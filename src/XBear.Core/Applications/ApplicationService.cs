using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.Core.Applications;

/// <summary>
/// 应用操作的宿主侧目标：一个运行中的实例及其在宿主上映射的 adb 端口。
/// </summary>
/// <param name="InstanceId">实例标识，取值须落在应用记录契约的实例引用取值域内。</param>
/// <param name="AdbPort">该实例在宿主上映射的 adb 端口。</param>
public sealed record ApplicationTarget(string InstanceId, int AdbPort);

/// <summary>
/// 宿主侧应用管理服务：对指定运行实例列举已装应用、安装应用包、卸载应用与拉起应用。
/// 全部交互经 <see cref="IAdbClient"/> 完成，不新增依赖，连接按次创建并随次释放。
/// 产出的应用记录与操作记录字段严格取自实例内可观测的结果，取不到时缺省，不补造。
/// </summary>
public sealed class ApplicationService
{
    /// <summary>实例内的暂存目录。应用包先推送到这里，再交由包管理器安装。</summary>
    public const string GuestStagingDirectory = "/data/local/tmp";

    /// <summary>枚举第三方应用的包管理命令，只返回非系统应用。</summary>
    public const string PackageListCommand = "pm list packages -3";

    /// <summary>读取单个包详情的命令模板，占位符为包名。</summary>
    public const string PackageDumpCommandFormat = "dumpsys package {0}";

    /// <summary>安装命令模板，占位符为实例内暂存路径。保留应用数据的覆盖安装。</summary>
    public const string InstallCommandFormat = "pm install -r {0}";

    /// <summary>卸载命令模板，占位符为包名。</summary>
    public const string UninstallCommandFormat = "pm uninstall {0}";

    /// <summary>
    /// 拉起应用的命令模板，占位符为包名。按启动器分类在包内挑选入口组件，
    /// 不需要事先知道入口类名，因而不会与契约「解析不到入口组件时缺省」的要求冲突。
    /// </summary>
    public const string LaunchCommandFormat = "monkey -p {0} -c android.intent.category.LAUNCHER 1";

    /// <summary>删除实例内暂存文件的命令模板，占位符为实例内暂存路径。</summary>
    public const string RemoveStagedFileCommandFormat = "rm -f {0}";

    /// <summary>安装完成后让 guest 刷盘的命令。</summary>
    public const string FlushCommand = "sync";

    /// <summary>包列举输出中每行的前缀。</summary>
    private const string PackageListPrefix = "package:";

    /// <summary>包管理器成功时的输出标记。</summary>
    private const string SuccessMarker = "Success";

    /// <summary>包管理器失败时的输出标记。</summary>
    private const string FailureMarker = "Failure";

    /// <summary>失败归因中原始输出片段的长度上限，与契约的消息字段上限一致。</summary>
    private const int MaxFailureMessageLength = 512;

    /// <summary>版本名的长度上限，与契约一致，超出上限时按取不到处理。</summary>
    private const int MaxVersionNameLength = 128;

    /// <summary>版本号的上限，与契约一致。</summary>
    private const int MaxVersionCode = 2147483647;

    /// <summary>包名的长度上限，与契约一致。</summary>
    private const int MaxPackageNameLength = 255;

    /// <summary>操作标识的长度上限，与契约一致。</summary>
    private const int MaxOperationIdLength = 64;

    /// <summary>操作标识的固定前缀。</summary>
    private const string OperationIdPrefix = "appop-";

    /// <summary>实例内暂存文件名的固定前缀，配合唯一串避免并发安装互相覆盖。</summary>
    private const string StagedFileNamePrefix = "xbear-";

    /// <summary>纯 Java 应用在契约中的 ABI 取值，表示该应用无原生库。</summary>
    private const string NoNativeLibraryCpuAbi = "none";

    /// <summary>
    /// 实例未给出可识别失败码时的宿主侧回填码。
    /// 契约要求失败码取自实例回报，实例只给出散文式错误时不得替它编一个包管理器码，
    /// 故以宿主侧前缀如实标记「实例回报无法归类」。
    /// </summary>
    private const string UnclassifiedFailureCode = "HOST_GUEST_FAILURE_UNCLASSIFIED";

    /// <summary>ZIP 本地文件头魔数，应用包必须以此开头。</summary>
    private static readonly byte[] ZipLocalFileHeader = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>契约的 ABI 取值域，封闭枚举，新增取值须由规格侧递增主版本。</summary>
    private static readonly string[] ContractCpuAbis =
        ["x86_64", "x86", "arm64-v8a", "armeabi-v7a", "armeabi", NoNativeLibraryCpuAbi];

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>契约的包名取值域：至少两段，每段以小写字母开头。</summary>
    private static readonly Regex PackageNamePattern = new(
        @"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>契约的实例标识取值域。</summary>
    private static readonly Regex InstanceRefPattern = new(
        @"^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>契约的安装来源文件名取值域。</summary>
    private static readonly Regex ApkFileNamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._+-]*\.[aA][pP][kK]$",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>包详情中的版本号字段。</summary>
    private static readonly Regex VersionCodePattern = new(
        @"(?:^|\s)versionCode=(?<code>\d+)",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>包详情中的版本名字段。</summary>
    private static readonly Regex VersionNamePattern = new(
        @"(?:^|\s)versionName=(?<name>\S*)",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>包详情中的主 ABI 字段。</summary>
    private static readonly Regex PrimaryCpuAbiPattern = new(
        @"(?:^|\s)primaryCpuAbi=(?<abi>\S*)",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>包管理器失败回报中的方括号失败码。</summary>
    private static readonly Regex BracketedFailureCodePattern = new(
        @"Failure\s*\[(?<code>[A-Z][A-Z0-9_]*)",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>失败输出中独立出现的下划线大写码，命中不了方括号形式时兜底。</summary>
    private static readonly Regex SnakeCaseCodePattern = new(
        @"(?<![A-Za-z0-9_])(?<code>[A-Z][A-Z0-9]*_[A-Z0-9_]+)(?![A-Za-z0-9_])",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    private readonly Func<IAdbClient> _adbClientFactory;
    private readonly TimeSpan _connectTimeout;
    private readonly Func<string, string> _operationIdFactory;

    private int _operationSequence;

    /// <summary>
    /// 初始化应用管理服务。
    /// </summary>
    /// <param name="adbClientFactory">
    /// adb 客户端工厂，缺省时按实例新建客户端。每次操作各自创建并在结束时释放，
    /// 不跨调用复用，避免留下指向已回收端口的悬挂连接。
    /// </param>
    /// <param name="connectTimeout">连接与握手的等待上限，为 null 时使用客户端默认上限。</param>
    /// <param name="operationIdFactory">
    /// 操作标识发号器，入参为实例标识。缺省时按「前缀 + 实例标识 + 自增序号」发号；
    /// 失败重试时可由调用方显式传入标识，把同一次意图的多次尝试关联在一起。
    /// </param>
    public ApplicationService(
        Func<IAdbClient>? adbClientFactory = null,
        TimeSpan? connectTimeout = null,
        Func<string, string>? operationIdFactory = null)
    {
        _adbClientFactory = adbClientFactory ?? (static () => new Adb.AdbClient());
        _connectTimeout = connectTimeout ?? Adb.AdbClient.DefaultConnectTimeout;
        _operationIdFactory = operationIdFactory ?? NextOperationId;
    }

    /// <summary>
    /// 列出实例内已安装的第三方应用。包名来自包管理器的包列举，
    /// 版本名、版本号与主 ABI 来自包详情，取不到时按契约缺省，不以推断值填充。
    /// </summary>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用记录列表，按包名的字典序排列。</returns>
    /// <exception cref="XBearException">
    /// 目标实例非法时为 <see cref="ErrorCategory.Spec"/>；
    /// 与实例的交互失败为 <see cref="ErrorCategory.Protocol"/>。
    /// </exception>
    public async Task<IReadOnlyList<ApplicationSpec>> ListAsync(
        ApplicationTarget target,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);

        string output = await RunShellAsync(target, PackageListCommand, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> packages = ParsePackageList(output);

        var applications = new List<ApplicationSpec>(packages.Count);
        foreach (string packageName in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string dump = await RunShellAsync(
                    target,
                    Compose(PackageDumpCommandFormat, packageName),
                    cancellationToken)
                .ConfigureAwait(false);

            applications.Add(BuildApplicationRecord(target, packageName, dump));
        }

        return applications;
    }

    /// <summary>
    /// 安装一个应用包：先校验宿主上的应用包文件，把它推送到实例内的暂存路径，
    /// 交由包管理器覆盖安装，安装成功后清理暂存文件并让 guest 刷盘。
    /// 安装失败时同样清理暂存文件，并将包管理器的错误输出原样保留在操作记录中。
    /// </summary>
    /// <remarks>
    /// 包名须由调用方给出。应用包内的清单在实例侧只有安装那一刻才会被解析，
    /// 宿主侧的轻量实现没有可靠途径在不安装的前提下读出包名，
    /// 而契约要求操作记录的目标包名在发起时即可确定，不允许用模糊匹配的结果代替。
    /// 包管理器回报的失败码与输出原样保留在操作记录里，不翻译、不归并。
    /// </remarks>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="apkPath">宿主上的应用包文件路径。</param>
    /// <param name="packageName">应用在实例内的包名。</param>
    /// <param name="operationId">
    /// 操作标识，缺省时由服务端发号。失败重试时传入首次的标识即可把多次尝试关联在一起。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次安装的操作记录。</returns>
    /// <exception cref="XBearException">
    /// 应用包不存在、不可读或非 ZIP 归档时为 <see cref="ErrorCategory.Storage"/>；
    /// 包名或应用包文件名不符合契约取值域时为 <see cref="ErrorCategory.Spec"/>；
    /// 与实例的交互失败为 <see cref="ErrorCategory.Protocol"/>。
    /// </exception>
    public async Task<ApplicationOperation> InstallAsync(
        ApplicationTarget target,
        string apkPath,
        string packageName,
        string? operationId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        string package = NormalizePackageName(packageName);
        ApkPackage apk = InspectApkPackage(apkPath);
        string id = NormalizeOperationId(operationId, target.InstanceId);
        string stagedPath = BuildStagedPath();

        GuestOutcome outcome;
        DateTimeOffset occurredAt;
        var timer = Stopwatch.StartNew();
        try
        {
            await PushAsync(target, apk.HostPath, stagedPath, cancellationToken).ConfigureAwait(false);

            ShellExecutionResult execution = await RunShellCommandAsync(
                    target,
                    Compose(InstallCommandFormat, stagedPath),
                    cancellationToken)
                .ConfigureAwait(false);

            occurredAt = DateTimeOffset.Now;
            timer.Stop();
            outcome = InterpretPackageManagerOutput(execution.Output);

            await DiscardStagedFileAsync(target, stagedPath).ConfigureAwait(false);

            if (outcome.Succeeded)
            {
                // 安装成功后必须让 guest 刷盘：使新装的应用数据立即落盘。
                await RunShellAsync(target, FlushCommand, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await DiscardStagedFileAsync(target, stagedPath).ConfigureAwait(false);
            throw;
        }

        return BuildOperation(
            target,
            id,
            ApplicationOperationKind.Install,
            package,
            outcome,
            occurredAt,
            timer.Elapsed);
    }

    /// <summary>
    /// 从实例中卸载一个应用。成功后让 guest 刷盘，包管理器回报的失败码与输出原样保留在操作记录里。
    /// </summary>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="packageName">应用在实例内的包名。</param>
    /// <param name="operationId">
    /// 操作标识，缺省时由服务端发号。失败重试时传入首次的标识即可把多次尝试关联在一起。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次卸载的操作记录。</returns>
    /// <exception cref="XBearException">
    /// 包名不符合契约取值域时为 <see cref="ErrorCategory.Spec"/>；
    /// 与实例的交互失败为 <see cref="ErrorCategory.Protocol"/>。
    /// </exception>
    public async Task<ApplicationOperation> UninstallAsync(
        ApplicationTarget target,
        string packageName,
        string? operationId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        string package = NormalizePackageName(packageName);
        string id = NormalizeOperationId(operationId, target.InstanceId);

        var timer = Stopwatch.StartNew();
        ShellExecutionResult execution = await RunShellCommandAsync(
                target,
                Compose(UninstallCommandFormat, package),
                cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset occurredAt = DateTimeOffset.Now;
        timer.Stop();
        GuestOutcome outcome = InterpretPackageManagerOutput(execution.Output);

        if (outcome.Succeeded)
        {
            await RunShellAsync(target, FlushCommand, cancellationToken).ConfigureAwait(false);
        }

        return BuildOperation(
            target,
            id,
            ApplicationOperationKind.Uninstall,
            package,
            outcome,
            occurredAt,
            timer.Elapsed);
    }

    /// <summary>
    /// 拉起一个已安装的应用：按启动器分类在包内挑选入口组件并把它切换到前台。
    /// 契约要求入口组件取自实例侧的解析结果、不允许猜测类名，
    /// 因此这里走按包拉起的 monkey 通路，拉起成功与否以命令输出判定。
    /// </summary>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="packageName">应用在实例内的包名。</param>
    /// <param name="operationId">
    /// 操作标识，缺省时由服务端发号。失败重试时传入首次的标识即可把多次尝试关联在一起。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次拉起的操作记录。</returns>
    /// <exception cref="XBearException">
    /// 包名不符合契约取值域时为 <see cref="ErrorCategory.Spec"/>；
    /// 与实例的交互失败为 <see cref="ErrorCategory.Protocol"/>。
    /// </exception>
    public async Task<ApplicationOperation> LaunchAsync(
        ApplicationTarget target,
        string packageName,
        string? operationId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        string package = NormalizePackageName(packageName);
        string id = NormalizeOperationId(operationId, target.InstanceId);

        var timer = Stopwatch.StartNew();
        ShellExecutionResult execution = await RunShellCommandAsync(
                target,
                Compose(LaunchCommandFormat, package),
                cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset occurredAt = DateTimeOffset.Now;
        timer.Stop();
        GuestOutcome outcome = InterpretLaunchOutput(execution.ExitCode, execution.Output);

        return BuildOperation(
            target,
            id,
            ApplicationOperationKind.Launch,
            package,
            outcome,
            occurredAt,
            timer.Elapsed);
    }

    /// <summary>
    /// 解析包列举的输出，得到符合契约取值域的包名并按字典序排列。
    /// 实例内可能存在不满足契约包名约束的条目，这类条目写不进应用记录，按缺省丢弃。
    /// </summary>
    /// <param name="output">包列举命令的标准输出。</param>
    /// <returns>去重且有序的包名集合。</returns>
    private static IReadOnlyList<string> ParsePackageList(string output)
    {
        var packages = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith(PackageListPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string candidate = line[PackageListPrefix.Length..].Trim();
            if (!IsContractPackageName(candidate) || !seen.Add(candidate))
            {
                continue;
            }

            packages.Add(candidate);
        }

        packages.Sort(StringComparer.Ordinal);
        return packages;
    }

    /// <summary>
    /// 由包名与包详情输出组装应用记录。版本名、版本号与主 ABI 逐项独立取值，
    /// 某一项取不到只缺省该项，不牵连其余字段，也不以推断值补齐。
    /// </summary>
    /// <param name="target">目标实例。</param>
    /// <param name="packageName">应用包名。</param>
    /// <param name="dump">包详情命令的标准输出。</param>
    /// <returns>应用记录。</returns>
    private static ApplicationSpec BuildApplicationRecord(
        ApplicationTarget target,
        string packageName,
        string dump)
    {
        return new ApplicationSpec
        {
            PackageName = packageName,
            InstanceRef = target.InstanceId,
            InstallState = ApplicationInstallState.Installed,
            VersionName = ReadVersionName(dump),
            VersionCode = ReadVersionCode(dump),
            PrimaryCpuAbi = ReadPrimaryCpuAbi(dump)
        };
    }

    /// <summary>从包详情中读出版本名，缺失、为空或超出契约上限时返回 null。</summary>
    /// <param name="dump">包详情命令的标准输出。</param>
    /// <returns>版本名，取不到时为 null。</returns>
    private static string? ReadVersionName(string dump)
    {
        Match match = VersionNamePattern.Match(dump);
        if (!match.Success)
        {
            return null;
        }

        string value = match.Groups["name"].Value;
        if (value.Length is 0 or > MaxVersionNameLength || value == "null")
        {
            return null;
        }

        return value;
    }

    /// <summary>从包详情中读出版本号，缺失或不在契约取值范围内时返回 null。</summary>
    /// <param name="dump">包详情命令的标准输出。</param>
    /// <returns>版本号，取不到时为 null。</returns>
    private static int? ReadVersionCode(string dump)
    {
        Match match = VersionCodePattern.Match(dump);
        if (!match.Success
            || !int.TryParse(
                match.Groups["code"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int code))
        {
            return null;
        }

        return code is >= 1 and <= MaxVersionCode ? code : null;
    }

    /// <summary>
    /// 从包详情中读出实例实际承载该应用的 ABI。包管理器选出了空值表示该应用无原生库，
    /// 按契约记为纯 Java 应用；字段整体缺失表示实例未给出可观测来源，按缺省处理；
    /// 取值落在契约封闭枚举之外时同样按缺省处理，不擅自扩取值域。
    /// </summary>
    /// <param name="dump">包详情命令的标准输出。</param>
    /// <returns>ABI 取值，取不到时为 null。</returns>
    private static string? ReadPrimaryCpuAbi(string dump)
    {
        Match match = PrimaryCpuAbiPattern.Match(dump);
        if (!match.Success)
        {
            return null;
        }

        string value = match.Groups["abi"].Value;
        if (value.Length == 0 || value == "null")
        {
            return NoNativeLibraryCpuAbi;
        }

        return Array.IndexOf(ContractCpuAbis, value) >= 0 && value != NoNativeLibraryCpuAbi
            ? value
            : null;
    }

    /// <summary>
    /// 判定包管理器的输出。成功以成功标记为准，失败码取自输出本身，
    /// 工具层面未确认成功一律判为失败，与契约的「未确认成功即判失败」一致。
    /// </summary>
    /// <param name="output">包管理器命令的标准输出。</param>
    /// <returns>判定结论。</returns>
    private static GuestOutcome InterpretPackageManagerOutput(string output)
    {
        string trimmed = output.Trim();
        if (trimmed.Contains(SuccessMarker, StringComparison.Ordinal)
            && !trimmed.Contains(FailureMarker, StringComparison.Ordinal))
        {
            return GuestOutcome.Success();
        }

        return GuestOutcome.Failed(ExtractFailureCode(trimmed), trimmed);
    }

    /// <summary>
    /// 判定拉起命令的输出。以退出码为准并结合已知失败标记反判，
    /// 退出码为零且无已知失败标记判定为成功，避免依赖特定镜像 shell 包装器的成功文本标记。
    /// </summary>
    /// <param name="exitCode">拉起命令退出码。</param>
    /// <param name="output">拉起命令的标准输出。</param>
    /// <returns>判定结论。</returns>
    private static GuestOutcome InterpretLaunchOutput(int exitCode, string output)
    {
        string trimmed = output.Trim();
        if (exitCode != 0 || HasKnownLaunchFailureMarker(trimmed))
        {
            return GuestOutcome.Failed(ExtractFailureCode(trimmed), trimmed);
        }

        return GuestOutcome.Success();
    }

    private static bool HasKnownLaunchFailureMarker(string output)
    {
        return output.Contains("monkey aborted", StringComparison.OrdinalIgnoreCase)
            || output.Contains("No activities found", StringComparison.OrdinalIgnoreCase)
            || output.Contains("** Error", StringComparison.OrdinalIgnoreCase)
            || output.Contains("** Monkey aborted", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Failure [", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从实例输出中提取失败码。先取包管理器的方括号形式，
    /// 再退到输出中独立出现的下划线大写码，接着识别 monkey 拉起失败标记，最后如实标记为无法归类。
    /// </summary>
    /// <param name="output">实例命令的标准输出。</param>
    /// <returns>失败码。</returns>
    private static string ExtractFailureCode(string output)
    {
        Match bracketed = BracketedFailureCodePattern.Match(output);
        if (bracketed.Success)
        {
            return bracketed.Groups["code"].Value;
        }

        Match snake = SnakeCaseCodePattern.Match(output);
        if (snake.Success)
        {
            return snake.Groups["code"].Value;
        }

        if (output.Contains("No activities found", StringComparison.OrdinalIgnoreCase))
        {
            return "LAUNCH_FAILED_NO_ACTIVITIES";
        }

        if (output.Contains("monkey aborted", StringComparison.OrdinalIgnoreCase))
        {
            return "LAUNCH_FAILED_ABORTED";
        }

        if (output.Contains("** Error", StringComparison.OrdinalIgnoreCase))
        {
            return "LAUNCH_FAILED_ERROR";
        }

        return UnclassifiedFailureCode;
    }

    /// <summary>
    /// 由判定结论组装操作记录。失败必须带归因，成功不得带归因，与契约的条件约束一致。
    /// </summary>
    /// <param name="target">目标实例。</param>
    /// <param name="operationId">操作标识。</param>
    /// <param name="kind">操作类型。</param>
    /// <param name="packageName">操作目标的包名。</param>
    /// <param name="outcome">判定结论。</param>
    /// <param name="occurredAt">实例回报完成的时刻。</param>
    /// <param name="elapsed">端到端耗时。</param>
    /// <returns>操作记录。</returns>
    private static ApplicationOperation BuildOperation(
        ApplicationTarget target,
        string operationId,
        ApplicationOperationKind kind,
        string packageName,
        GuestOutcome outcome,
        DateTimeOffset occurredAt,
        TimeSpan elapsed)
    {
        var operation = new ApplicationOperation
        {
            Id = operationId,
            InstanceRef = target.InstanceId,
            Operation = kind,
            PackageName = packageName,
            Result = outcome.Succeeded ? ApplicationOperationResult.Success : ApplicationOperationResult.Failure,
            OccurredAt = FormatTimestamp(occurredAt),
            DurationMs = (int)Math.Clamp(Math.Round(elapsed.TotalMilliseconds), 0, int.MaxValue)
        };

        if (!outcome.Succeeded)
        {
            operation.FailureReason = new ApplicationOperationFailure
            {
                Category = ApplicationErrorCategory.Guest,
                Code = outcome.FailureCode ?? UnclassifiedFailureCode,
                Message = outcome.Message
            };
        }

        return operation;
    }

    /// <summary>把时刻格式化为契约要求的带时区偏移形式。</summary>
    /// <param name="moment">待格式化的时刻。</param>
    /// <returns>带时区偏移的日期时间文本。</returns>
    private static string FormatTimestamp(DateTimeOffset moment) =>
        moment.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>按模板与参数拼出命令，参数不参与任何文化相关的格式化。</summary>
    /// <param name="format">命令模板，占位符为 {0}。</param>
    /// <param name="argument">命令参数。</param>
    /// <returns>完整命令文本。</returns>
    private static string Compose(string format, string argument) =>
        string.Format(CultureInfo.InvariantCulture, format, argument);

    /// <summary>
    /// 在实例侧执行一条 shell 命令。
    /// adbd 在一条 shell 命令跑完后即关闭该连接，因此每条命令都单独建立并释放一次连接，
    /// 与宿主 adb 逐条执行命令的行为一致，也避免第二条命令落在已被关闭的连接上。
    /// </summary>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="command">命令与参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命令的标准输出。</returns>
    private async Task<string> RunShellAsync(
        ApplicationTarget target,
        string command,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
                target,
                client => client.ShellAsync(command, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// 执行 shell 命令并收集其输出。当命令以非零退出码结束时，
    /// 提取出实例回报的错误输出片段而不向外抛异常，以便原样传播包管理器或拉起工具的回报。
    /// 连接中断或超时等传输层故障仍正常外抛。
    /// </summary>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="command">命令与参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命令退出码与输出内容。</returns>
    private async Task<ShellExecutionResult> RunShellCommandAsync(
        ApplicationTarget target,
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            string output = await RunShellAsync(target, command, cancellationToken).ConfigureAwait(false);
            return new ShellExecutionResult(0, output);
        }
        catch (XBearException ex) when (ex.Category == ErrorCategory.Protocol && TryExtractCommandError(ex.Message, out string errorOutput, out int exitCode))
        {
            return new ShellExecutionResult(exitCode, errorOutput);
        }
    }

    /// <summary>
    /// 从 AdbClient 抛出的非零退出码异常消息中提取退出码与错误输出。
    /// </summary>
    /// <param name="message">异常消息。</param>
    /// <param name="errorOutput">提取到的错误输出。</param>
    /// <param name="exitCode">提取到的退出码。</param>
    /// <returns>成功提取时返回 true。</returns>
    private static bool TryExtractCommandError(string message, out string errorOutput, out int exitCode)
    {
        const string exitCodePrefix = "以退出码 ";
        const string errorOutputPrefix = "错误输出：";

        int codeStart = message.IndexOf(exitCodePrefix, StringComparison.Ordinal);
        int errorStart = message.IndexOf(errorOutputPrefix, StringComparison.Ordinal);

        if (codeStart >= 0 && errorStart > codeStart)
        {
            string between = message[(codeStart + exitCodePrefix.Length)..errorStart];
            int failIndex = between.IndexOf("失败", StringComparison.Ordinal);
            string codeSpan = (failIndex >= 0 ? between[..failIndex] : between).Trim(' ', '，', ',');
            if (int.TryParse(codeSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out exitCode))
            {
                errorOutput = message[(errorStart + errorOutputPrefix.Length)..].Trim();
                return true;
            }
        }

        exitCode = 0;
        errorOutput = string.Empty;
        return false;
    }

    /// <summary>shell 命令执行结果。</summary>
    /// <param name="ExitCode">退出码。</param>
    /// <param name="Output">标准输出或错误输出。</param>
    private readonly record struct ShellExecutionResult(int ExitCode, string Output);

    /// <summary>把宿主文件推送到实例。推送同样按次独占连接，sync 子协议的一次传输在结束后即结束。</summary>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="localPath">宿主文件路径。</param>
    /// <param name="remotePath">实例内目标路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示推送已结束的异步任务。</returns>
    private Task PushAsync(
        ApplicationTarget target,
        string localPath,
        string remotePath,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            target,
            async client =>
            {
                await client.PushAsync(localPath, remotePath, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// 删除实例内的暂存文件。清理是一次善后动作，失败只会在实例内留下一份应用包副本，
    /// 因此不向上抛出，也不改变已经确定的判定结论。
    /// </summary>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="stagedPath">实例内暂存路径。</param>
    /// <returns>表示清理已结束的异步任务。</returns>
    private async Task DiscardStagedFileAsync(ApplicationTarget target, string stagedPath)
    {
        try
        {
            await RunShellAsync(
                    target,
                    Compose(RemoveStagedFileCommandFormat, stagedPath),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is XBearException or OperationCanceledException)
        {
            // 暂存文件清理失败不影响本次操作的结论，也不影响其后的刷盘。
        }
    }

    /// <summary>
    /// 在 guest 上执行一次操作：按次建立连接并在结束时释放，不跨调用复用连接。
    /// </summary>
    /// <typeparam name="T">操作的返回类型。</typeparam>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="action">在已连接客户端上执行的操作。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作的结果。</returns>
    private async Task<T> ExecuteAsync<T>(
        ApplicationTarget target,
        Func<IAdbClient, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await using IAdbClient client = _adbClientFactory();
        try
        {
            await client.ConnectAsync(target.AdbPort, cancellationToken, _connectTimeout).ConfigureAwait(false);
            return await action(client).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消保持取消语义，不谎报为实例内交互失败。
            throw;
        }
        catch (XBearException ex)
        {
            throw new XBearException(
                ex.Category,
                $"实例 {target.InstanceId} 应用操作失败：{ex.Message}",
                string.IsNullOrWhiteSpace(ex.Remediation) ? DefaultRemediation(target) : ex.Remediation,
                ex);
        }
    }

    /// <summary>校验目标实例，并生成缺省处置建议。</summary>
    /// <param name="target">待校验的目标实例。</param>
    /// <exception cref="XBearException">实例标识不在契约取值域内时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private static void ValidateTarget(ApplicationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.InstanceId);

        if (target.AdbPort is < 1 or > 65535)
        {
            throw new ArgumentException(
                $"实例 {target.InstanceId} 的 adb 端口 {target.AdbPort} 不是合法端口。",
                nameof(target));
        }

        if (!InstanceRefPattern.IsMatch(target.InstanceId))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"实例标识 {target.InstanceId} 不在应用记录契约的取值域内。",
                "应用记录引用实例契约的标识，请改用只含小写字母、数字与连字符的实例标识。");
        }
    }

    /// <summary>
    /// 校验并归一化包名。包名直接进入实例侧命令与操作记录，
    /// 只接受契约允许的形态，杜绝命令注入与写不进契约的取值。
    /// </summary>
    /// <param name="packageName">待归一化的包名。</param>
    /// <returns>归一化后的包名。</returns>
    /// <exception cref="XBearException">包名不在契约取值域内时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private static string NormalizePackageName(string? packageName)
    {
        string package = (packageName ?? string.Empty).Trim();
        if (package.Length > MaxPackageNameLength || !IsContractPackageName(package))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"应用包名 {packageName} 不在应用记录契约的取值域内。",
                "请传入实例内实际注册的 Android 包名，形如 com.example.handyplayer。");
        }

        return package;
    }

    /// <summary>判断字符串是否为契约允许的包名。</summary>
    /// <param name="value">待判断的字符串。</param>
    /// <returns>符合包名取值域时为 true。</returns>
    private static bool IsContractPackageName(string value) => PackageNamePattern.IsMatch(value);

    /// <summary>
    /// 校验并归一化操作标识。缺省时按「前缀 + 实例标识 + 自增序号」发号，
    /// 显式给定且不合契约取值域时按配置错误拒绝，避免操作记录写不出去。
    /// </summary>
    /// <param name="operationId">调用方给定的操作标识，可为空。</param>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>归一化后的操作标识。</returns>
    /// <exception cref="XBearException">操作标识不在契约取值域内时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private string NormalizeOperationId(string? operationId, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return _operationIdFactory(instanceId);
        }

        string id = operationId.Trim();
        if (!IsContractOperationId(id))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"操作标识 {operationId} 不在应用记录契约的取值域内。",
                "操作标识须由小写字母、数字与连字符组成，且以字母或数字开头结尾。");
        }

        return id;
    }

    /// <summary>判断字符串是否为契约允许的操作标识。</summary>
    /// <param name="value">待判断的字符串。</param>
    /// <returns>符合操作标识取值域时为 true。</returns>
    private static bool IsContractOperationId(string value) => InstanceRefPattern.IsMatch(value);

    /// <summary>按「前缀 + 实例标识 + 自增序号」发号，并在超出契约长度上限时截断实例标识部分。</summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>操作标识。</returns>
    private string NextOperationId(string instanceId)
    {
        string sequence = Interlocked.Increment(ref _operationSequence)
            .ToString("D4", CultureInfo.InvariantCulture);

        int room = MaxOperationIdLength - OperationIdPrefix.Length - sequence.Length - 1;
        string trimmed = instanceId.Length > room ? instanceId[..Math.Max(room, 1)] : instanceId;
        trimmed = trimmed.Trim('-');
        if (trimmed.Length == 0)
        {
            trimmed = "inst";
        }

        return string.Concat(OperationIdPrefix, trimmed, "-", sequence);
    }

    /// <summary>
    /// 校验宿主上的应用包文件：确认存在、能读到 ZIP 归档魔数，
    /// 且文件名落在契约的来源取值域内。摘要与字节数一并算出，供安装来源使用。
    /// </summary>
    /// <param name="apkPath">宿主上的应用包文件路径。</param>
    /// <returns>应用包的内容身份。</returns>
    /// <exception cref="XBearException">
    /// 文件不存在、不可读或缺少 ZIP 魔数时抛出 <see cref="ErrorCategory.Storage"/>；
    /// 文件名不在契约取值域内时抛出 <see cref="ErrorCategory.Spec"/>。
    /// </exception>
    private static ApkPackage InspectApkPackage(string apkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);

        if (!File.Exists(apkPath))
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"应用包 {apkPath} 不存在。",
                "请先选择宿主上已下载完成的应用包 APK 文件，再执行安装。");
        }

        string fileName = Path.GetFileName(apkPath);
        if (!ApkFileNamePattern.IsMatch(fileName))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"应用包文件名 {fileName} 不在应用记录契约的取值域内。",
                "安装来源只记录文件名且须形如 App_1.0.apk，请改名后再安装。");
        }

        var info = new FileInfo(apkPath);
        string digest;
        try
        {
            using FileStream stream = new(
                apkPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: false);

            Span<byte> header = stackalloc byte[ZipLocalFileHeader.Length];
            if (stream.Read(header) != ZipLocalFileHeader.Length
                || !header.SequenceEqual(ZipLocalFileHeader))
            {
                throw new XBearException(
                    ErrorCategory.Storage,
                    $"应用包 {fileName} 的开头不是 ZIP 归档标记。",
                    "应用包必须是 ZIP 结构的 APK，请确认所选文件确实是应用包，必要时重新下载。");
            }

            stream.Position = 0;
            digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (XBearException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"应用包 {fileName} 无法读取。",
                "请确认所选文件未被占用且当前账户具备读取权限。",
                ex);
        }

        return new ApkPackage(apkPath, fileName, info.Length, digest);
    }

    /// <summary>
    /// 生成实例内的暂存路径。文件名带宿主侧唯一串，多个实例或多次安装并发时不会互相覆盖。
    /// </summary>
    /// <returns>实例内暂存路径。</returns>
    private static string BuildStagedPath() =>
        string.Concat(
            GuestStagingDirectory,
            "/",
            StagedFileNamePrefix,
            Guid.NewGuid().ToString("N"),
            ".apk");

    /// <summary>生成应用操作失败时的缺省处置建议。</summary>
    /// <param name="target">目标实例。</param>
    /// <returns>处置建议文本。</returns>
    private static string DefaultRemediation(ApplicationTarget target) =>
        $"请确认实例 {target.InstanceId} 已启动到调试通路就绪，再重试该应用操作。";

    /// <summary>宿主侧校验后的应用包内容身份。</summary>
    /// <param name="HostPath">宿主文件路径。</param>
    /// <param name="FileName">文件名的 basename 部分。</param>
    /// <param name="SizeBytes">文件字节数。</param>
    /// <param name="Sha256">文件内容摘要，小写十六进制。</param>
    private sealed record ApkPackage(string HostPath, string FileName, long SizeBytes, string Sha256);

    /// <summary>实例命令的判定结论。</summary>
    /// <param name="Succeeded">实例是否回报成功。</param>
    /// <param name="FailureCode">失败码，失败时非空。</param>
    /// <param name="Message">实例回报的关键输出片段，失败时非空。</param>
    private readonly record struct GuestOutcome(bool Succeeded, string? FailureCode, string? Message)
    {
        /// <summary>判定为成功。</summary>
        /// <returns>成功结论。</returns>
        public static GuestOutcome Success() => new(true, null, null);

        /// <summary>
        /// 判定为失败，并按契约的长度上限保留实例回报的输出片段。
        /// </summary>
        /// <param name="code">从实例输出中提取的失败码，可为空。</param>
        /// <param name="message">实例回报的输出原文。</param>
        /// <returns>失败结论。</returns>
        public static GuestOutcome Failed(string? code, string message) =>
            new(false, code ?? UnclassifiedFailureCode, CapMessage(message));
    }

    /// <summary>把实例输出片段裁到契约允许的长度，超出部分丢弃而不改写其内容。</summary>
    /// <param name="message">实例回报的输出原文。</param>
    /// <returns>裁剪后的输出片段。</returns>
    private static string CapMessage(string message) =>
        message.Length <= MaxFailureMessageLength ? message : message[..MaxFailureMessageLength];
}