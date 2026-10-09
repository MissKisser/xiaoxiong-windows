using System.Globalization;
using System.Text;
using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Adb;
using XBear.Core.Qmp;

namespace XBear.Core.Diagnostics;

/// <summary>诊断包中单个采集步骤的结论。</summary>
public enum DiagnosticsStepOutcome
{
    /// <summary>已取得证据。</summary>
    Succeeded = 0,

    /// <summary>已取得证据，但因超过体积上限而被裁剪。</summary>
    Truncated = 1,

    /// <summary>未能取得证据，失败原因记录在步骤说明中。</summary>
    Failed = 2,

    /// <summary>前置条件不满足而未执行，例如实例根本没有分配到该端口。</summary>
    Skipped = 3
}

/// <summary>
/// 单个采集步骤的结果。
/// </summary>
/// <param name="Name">步骤名称，取值为导出器公开的步骤名常量之一。</param>
/// <param name="Outcome">步骤结论。</param>
/// <param name="Detail">人类可读的说明，包含失败原因或裁剪情况。</param>
/// <param name="ArtifactRelativePath">产物在诊断包目录内的相对路径，未产出产物时为 null。</param>
public sealed record DiagnosticsStepResult(
    string Name,
    DiagnosticsStepOutcome Outcome,
    string Detail,
    string? ArtifactRelativePath = null);

/// <summary>整份诊断包的导出结果。</summary>
/// <param name="PackageDirectory">本次导出的诊断包目录绝对路径。</param>
/// <param name="Steps">各步骤结果，按执行顺序排列。</param>
public sealed record InstanceDiagnosticsResult(
    string PackageDirectory,
    IReadOnlyList<DiagnosticsStepResult> Steps)
{
    /// <summary>是否存在未取得证据的步骤。</summary>
    public bool HasFailure => Steps.Any(step => step.Outcome == DiagnosticsStepOutcome.Failed);

    /// <summary>按名称查找步骤结果。</summary>
    /// <param name="name">步骤名称。</param>
    /// <returns>匹配的结果，不存在时返回 null。</returns>
    public DiagnosticsStepResult? Find(string name) =>
        Steps.FirstOrDefault(step => string.Equals(step.Name, name, StringComparison.Ordinal));
}

/// <summary>单次导出的采集参数，用于给每个步骤设定资源上限。</summary>
public sealed record InstanceDiagnosticsOptions
{
    /// <summary>默认采集参数。</summary>
    public static InstanceDiagnosticsOptions Default { get; } = new();

    /// <summary>单个步骤的时限，超时按失败记录而不中断整体导出。</summary>
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>QEMU 日志最多保留的字节数，超出部分只保留靠后的内容。</summary>
    public int MaxLogBytes { get; init; } = 256 * 1024;

    /// <summary>屏幕截图最多保留的字节数，超出上限的截图视为不可解码而整体丢弃。</summary>
    public int MaxScreenshotBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>QMP 状态快照序列化后的字节上限。</summary>
    public int MaxSnapshotBytes { get; init; } = 256 * 1024;
}

/// <summary>一次实例诊断包导出的输入。</summary>
public sealed record InstanceDiagnosticsRequest
{
    /// <summary>实例标识，用于产物目录命名与清单记录。</summary>
    public required string InstanceId { get; init; }

    /// <summary>实例显示名，可为空。</summary>
    public string? InstanceDisplayName { get; init; }

    /// <summary>运行中的 QEMU 进程句柄，实例尚未启动时为 null。</summary>
    public QemuProcessHandle? Process { get; init; }

    /// <summary>本次分配到的宿主端口，未分配到的端口以 0 表示。</summary>
    public required AllocatedPorts Ports { get; init; }

    /// <summary>QMP 客户端工厂，为 null 时使用真实客户端。</summary>
    public Func<int, IQmpClient>? QmpClientFactory { get; init; }

    /// <summary>adb 客户端工厂，为 null 时使用真实客户端。</summary>
    public Func<IAdbClient>? AdbClientFactory { get; init; }
}

/// <summary>
/// 实例诊断包导出器：把启动命令行、QEMU 日志、QMP 状态、QMP 截图与 adb 探测结果收进一个目录。
/// </summary>
/// <remarks>
/// 该能力用于「进程活着但画面全黑」这类无法靠肉眼判断的故障，因此最优先的属性是可用性：
/// 任一步骤失败都会被记录下来并继续执行后续步骤，只要诊断包目录本身可写就一定产出清单。
/// 每个步骤默认自带时限且所有读取都有字节上限，不会因日志过大或 QMP 无响应而拖垮宿主。
/// </remarks>
public sealed class InstanceDiagnosticsExporter
{
    /// <summary>启动命令行与进程状态步骤名。</summary>
    public const string ProcessStepName = "process";

    /// <summary>QEMU 日志步骤名。</summary>
    public const string QemuLogStepName = "qemu-log";

    /// <summary>QMP 状态快照步骤名。</summary>
    public const string QmpSnapshotStepName = "qmp-snapshot";

    /// <summary>QMP 屏幕截图步骤名。</summary>
    public const string QmpScreenshotStepName = "qmp-screenshot";

    /// <summary>adb 探测步骤名。</summary>
    public const string AdbProbeStepName = "adb-probe";

    /// <summary>导出清单步骤名。</summary>
    public const string ManifestStepName = "manifest";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly (string Command, string Label)[] SnapshotQueries =
    [
        ("query-status", "运行状态"),
        ("query-name", "实例名称"),
        ("query-kvm", "硬件加速状态"),
        ("query-memory-size-summary", "内存摘要"),
    ];

    private readonly InstanceDiagnosticsOptions _options;

    /// <summary>
    /// 构造导出器。
    /// </summary>
    /// <param name="options">采集参数，为 null 时使用 <see cref="InstanceDiagnosticsOptions.Default"/>。</param>
    public InstanceDiagnosticsExporter(InstanceDiagnosticsOptions? options = null)
    {
        _options = options ?? InstanceDiagnosticsOptions.Default;
    }

    /// <summary>
    /// 为一个实例导出诊断包目录。
    /// </summary>
    /// <param name="baseDirectory">诊断包根目录，不存在时创建。</param>
    /// <param name="request">本次导出的输入。</param>
    /// <param name="cancellationToken">取消令牌，由调用方取消时中止整体导出。</param>
    /// <returns>诊断包目录路径与各步骤结果。除调用方取消外不因单步失败而抛出。</returns>
    /// <exception cref="ArgumentException">根目录或实例标识为空时抛出。</exception>
    /// <exception cref="ArgumentNullException">请求对象为 null 时抛出。</exception>
    /// <exception cref="XBearException">诊断包根目录无法创建时抛出，分类为 <see cref="ErrorCategory.Storage"/>。</exception>
    /// <exception cref="OperationCanceledException">调用方取消时抛出。</exception>
    public async Task<InstanceDiagnosticsResult> ExportAsync(
        string baseDirectory,
        InstanceDiagnosticsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceId);

        try
        {
            Directory.CreateDirectory(baseDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"无法创建诊断包根目录 {baseDirectory}：{exception.Message}",
                "请改用其他目录，或检查该目录的写入权限。",
                exception);
        }

        string packageDirectory = AllocatePackageDirectory(baseDirectory, request.InstanceId);
        var steps = new List<DiagnosticsStepResult>();

        steps.Add(await RunStepAsync(
            ProcessStepName,
            token => WriteProcessAsync(request, packageDirectory, token),
            cancellationToken).ConfigureAwait(false));

        steps.Add(await RunStepAsync(
            QemuLogStepName,
            token => CopyQemuLogAsync(request, packageDirectory, token),
            cancellationToken).ConfigureAwait(false));

        steps.Add(await RunStepAsync(
            QmpSnapshotStepName,
            token => CaptureQmpSnapshotAsync(request, packageDirectory, token),
            cancellationToken).ConfigureAwait(false));

        steps.Add(await RunStepAsync(
            QmpScreenshotStepName,
            token => CaptureQmpScreenshotAsync(request, packageDirectory, token),
            cancellationToken).ConfigureAwait(false));

        steps.Add(await RunStepAsync(
            AdbProbeStepName,
            token => ProbeAdbAsync(request, packageDirectory, token),
            cancellationToken).ConfigureAwait(false));

        steps.Add(await RunStepAsync(
            ManifestStepName,
            token => WriteManifestAsync(request, packageDirectory, steps, token),
            cancellationToken).ConfigureAwait(false));

        return new InstanceDiagnosticsResult(packageDirectory, steps);
    }

    /// <summary>
    /// 为本次导出分配一个尚不存在的子目录，已存在的同名目录按序号向后取名而不覆盖。
    /// </summary>
    /// <param name="baseDirectory">诊断包根目录。</param>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>新建的空目录绝对路径。</returns>
    private static string AllocatePackageDirectory(string baseDirectory, string instanceId)
    {
        string leaf = $"xbear-diagnostics-{SanitizeFileName(instanceId)}-{DateTime.Now:yyyyMMdd-HHmmss}";
        for (int attempt = 0; ; attempt++)
        {
            string candidate = Path.Combine(baseDirectory, attempt == 0 ? leaf : $"{leaf}-{attempt + 1}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }
    }

    /// <summary>
    /// 把实例标识转换为合法文件名。</summary>
    /// <param name="value">原始标识。</param>
    /// <returns>仅含合法文件名字符的文本。</returns>
    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    /// <summary>
    /// 为步骤产物解析一个不冲突的路径，已被占用的名称按序号向后取名而不覆盖。
    /// </summary>
    /// <param name="directory">诊断包目录。</param>
    /// <param name="fileName">期望的产物文件名。</param>
    /// <returns>未被占用的绝对路径。</returns>
    private static string ResolveUniquePath(string directory, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int attempt = 0; ; attempt++)
        {
            string candidate = Path.Combine(
                directory,
                attempt == 0 ? fileName : $"{stem}-{attempt + 1}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 执行一个采集步骤，把任何可预期失败都转成步骤结论而不向外抛。
    /// </summary>
    /// <param name="name">步骤名称。</param>
    /// <param name="body">步骤主体，收到带时限的令牌。</param>
    /// <param name="cancellationToken">调用方令牌。</param>
    /// <returns>步骤结论。</returns>
    /// <exception cref="OperationCanceledException">调用方取消时抛出。</exception>
    private async Task<DiagnosticsStepResult> RunStepAsync(
        string name,
        Func<CancellationToken, Task<DiagnosticsStepResult>> body,
        CancellationToken cancellationToken)
    {
        using var stepToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stepToken.CancelAfter(_options.StepTimeout);

        try
        {
            return await body(stepToken.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消属于整体中止语义，不按单步失败吞掉。
            throw;
        }
        catch (OperationCanceledException)
        {
            return new DiagnosticsStepResult(
                name,
                DiagnosticsStepOutcome.Failed,
                $"超过单步时限 {_options.StepTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} 秒仍未完成。");
        }
        catch (Exception exception)
        {
            return new DiagnosticsStepResult(name, DiagnosticsStepOutcome.Failed, Describe(exception));
        }
    }

    private static string Describe(Exception exception) => exception switch
    {
        XBearException typed => string.IsNullOrEmpty(typed.Remediation)
            ? typed.Message
            : $"{typed.Message} 建议：{typed.Remediation}",
        _ => $"{exception.GetType().Name}：{exception.Message}",
    };

    private static IQmpClient CreateQmpClient(InstanceDiagnosticsRequest request, int port) =>
        (request.QmpClientFactory ?? (static p => new QmpClient()))(port);

    private static IAdbClient CreateAdbClient(InstanceDiagnosticsRequest request) =>
        (request.AdbClientFactory ?? (static () => new AdbClient()))();

    /// <summary>
    /// 写入启动命令行与进程状态。
    /// </summary>
    private static async Task<DiagnosticsStepResult> WriteProcessAsync(
        InstanceDiagnosticsRequest request,
        string packageDirectory,
        CancellationToken cancellationToken)
    {
        if (request.Process is not { } handle)
        {
            return new DiagnosticsStepResult(
                ProcessStepName,
                DiagnosticsStepOutcome.Skipped,
                "实例尚未拉起，没有进程句柄可采集命令行。");
        }

        var lines = new StringBuilder()
            .Append("process_id: ").Append(handle.ProcessId).AppendLine()
            .Append("has_exited: ").Append(handle.HasExited ? "true" : "false").AppendLine()
            .Append("exit_code: ").Append(handle.ExitCode).AppendLine()
            .Append("adb_port: ").Append(request.Ports.Adb).AppendLine()
            .Append("qmp_port: ").Append(request.Ports.Qmp).AppendLine()
            .Append("vnc_port: ").Append(request.Ports.Vnc).AppendLine()
            .AppendLine()
            .Append("command_line:")
            .AppendLine()
            .AppendLine(DiagnosticTextRedactor.Redact(handle.CommandLine));

        string fileName = ResolveUniquePath(packageDirectory, "process.txt");
        await File.WriteAllTextAsync(fileName, lines.ToString(), Utf8NoBom, cancellationToken).ConfigureAwait(false);

        return new DiagnosticsStepResult(
            ProcessStepName,
            DiagnosticsStepOutcome.Succeeded,
            handle.HasExited
                ? $"已采集命令行原文，进程已退出（退出码 {handle.ExitCode}）。"
                : "已采集命令行原文，进程仍在运行。",
            Path.GetFileName(fileName));
    }

    /// <summary>
    /// 截取 QEMU 日志尾部写入诊断包。日志可能持续增长，故只保留末尾的有界字节数。
    /// </summary>
    private async Task<DiagnosticsStepResult> CopyQemuLogAsync(
        InstanceDiagnosticsRequest request,
        string packageDirectory,
        CancellationToken cancellationToken)
    {
        if (request.Process is not { } handle)
        {
            return new DiagnosticsStepResult(
                QemuLogStepName,
                DiagnosticsStepOutcome.Skipped,
                "实例尚未拉起，没有日志路径可采集。");
        }

        string sourcePath = handle.LogFilePath;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return new DiagnosticsStepResult(
                QemuLogStepName,
                DiagnosticsStepOutcome.Skipped,
                $"QEMU 日志文件不存在：{DiagnosticTextRedactor.Redact(sourcePath)}");
        }

        long totalLength;
        byte[] tail;
        string fileName = ResolveUniquePath(packageDirectory, "qemu-log.log");

        try
        {
            (totalLength, tail) = await ReadTailAsync(sourcePath, _options.MaxLogBytes, cancellationToken)
                .ConfigureAwait(false);

            // 日志在写入过程中仍可能被 QEMU 追加，此刻的读只保证拿到已刷出的那一段。
            await File.WriteAllTextAsync(
                fileName,
                DiagnosticTextRedactor.DecodeAndRedact(tail),
                Utf8NoBom,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DiagnosticsStepResult(
                QemuLogStepName,
                DiagnosticsStepOutcome.Failed,
                $"读取 QEMU 日志失败：{exception.Message}");
        }

        bool truncated = tail.Length < totalLength;
        string detail = truncated
            ? $"日志共 {totalLength} 字节，已保留末尾 {tail.Length} 字节，丢弃开头 {totalLength - tail.Length} 字节。"
            : $"日志共 {totalLength} 字节，已完整保留。";

        return new DiagnosticsStepResult(
            QemuLogStepName,
            truncated ? DiagnosticsStepOutcome.Truncated : DiagnosticsStepOutcome.Succeeded,
            detail,
            Path.GetFileName(fileName));
    }

    /// <summary>
    /// 读取源文件末尾不超过上限的字节，返回原文长度与实际取到的尾部内容。
    /// </summary>
    private static async Task<(long TotalLength, byte[] Tail)> ReadTailAsync(
        string sourcePath,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        // QEMU 仍持有日志的写入句柄，必须共享写入权限才能读到已刷出的内容。
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            useAsync: true);

        long totalLength = source.Length;
        int wanted = (int)Math.Min(totalLength, maxBytes);
        if (totalLength > maxBytes)
        {
            source.Seek(totalLength - maxBytes, SeekOrigin.Begin);
        }

        var tail = new byte[wanted];
        int filled = 0;
        while (filled < wanted)
        {
            int read = await source
                .ReadAsync(tail.AsMemory(filled, wanted - filled), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return (totalLength, filled == wanted ? tail : tail[..filled]);
    }

    /// <summary>
    /// 采集 QMP 状态快照。每条查询独立容错，个别查询失败只在其结果里记错误。
    /// </summary>
    private async Task<DiagnosticsStepResult> CaptureQmpSnapshotAsync(
        InstanceDiagnosticsRequest request,
        string packageDirectory,
        CancellationToken cancellationToken)
    {
        int port = request.Ports.Qmp;
        if (port is < 1 or > 65535)
        {
            return new DiagnosticsStepResult(
                QmpSnapshotStepName,
                DiagnosticsStepOutcome.Skipped,
                $"没有可用的 QMP 端口（当前为 {port}），跳过状态采集。");
        }

        IQmpClient client = CreateQmpClient(request, port);
        var queries = new Dictionary<string, object?>(StringComparer.Ordinal);
        int succeeded = 0;
        List<string> failures = [];

        await using (client.ConfigureAwait(false))
        {
            IReadOnlySet<string> capabilities = await client.ConnectAsync(port, cancellationToken).ConfigureAwait(false);
            queries["capabilities"] = capabilities.OrderBy(name => name, StringComparer.Ordinal).ToArray();

            foreach ((string command, string label) in SnapshotQueries)
            {
                try
                {
                    JsonElement result = await client.ExecuteAsync(command, null, cancellationToken).ConfigureAwait(false);
                    queries[command] = result;
                    succeeded++;
                }
                catch (XBearException exception)
                {
                    queries[command] = new Dictionary<string, string>(StringComparer.Ordinal) { ["error"] = exception.Message };
                    failures.Add($"{label}（{command}）：{exception.Message}");
                }
            }
        }

        string fileName = ResolveUniquePath(packageDirectory, "qmp-snapshot.json");
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["queries"] = queries,
            ["failures"] = failures,
        };

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
        bool truncated = json.Length > _options.MaxSnapshotBytes;
        if (truncated)
        {
            // 快照只保留定长结论，超限时不再写入各查询的原始回包。
            json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["queries"] = $"共 {SnapshotQueries.Length} 条，超出体积上限已省略原始回包",
                ["failures"] = failures,
            });
        }

        await File.WriteAllBytesAsync(fileName, json, cancellationToken).ConfigureAwait(false);

        string detail = failures.Count == 0
            ? $"{SnapshotQueries.Length} 条状态查询全部成功。"
            : $"{succeeded} 条状态查询成功，{failures.Count} 条失败：{string.Join("；", failures)}";

        return new DiagnosticsStepResult(
            QmpSnapshotStepName,
            succeeded > 0 ? DiagnosticsStepOutcome.Succeeded : DiagnosticsStepOutcome.Failed,
            truncated ? $"{detail} 快照超出体积上限，已省略原始回包。" : detail,
            Path.GetFileName(fileName));
    }

    /// <summary>
    /// 通过 QMP 的 screendump 取回当前显示内容，并把产物收进诊断包。
    /// </summary>
    private async Task<DiagnosticsStepResult> CaptureQmpScreenshotAsync(
        InstanceDiagnosticsRequest request,
        string packageDirectory,
        CancellationToken cancellationToken)
    {
        int port = request.Ports.Qmp;
        if (port is < 1 or > 65535)
        {
            return new DiagnosticsStepResult(
                QmpScreenshotStepName,
                DiagnosticsStepOutcome.Skipped,
                $"没有可用的 QMP 端口（当前为 {port}），跳过截图。");
        }

        string temporaryPath = Path.Combine(Path.GetTempPath(), $"xbear-screendump-{Guid.NewGuid():N}.ppm");
        try
        {
            IQmpClient client = CreateQmpClient(request, port);
            await using (client.ConfigureAwait(false))
            {
                await client.ConnectAsync(port, cancellationToken).ConfigureAwait(false);
                await client
                    .ExecuteAsync("screendump", new { filename = temporaryPath }, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!File.Exists(temporaryPath))
            {
                return new DiagnosticsStepResult(
                    QmpScreenshotStepName,
                    DiagnosticsStepOutcome.Failed,
                    "screendump 已应答但没有产出文件。");
            }

            string targetPath = ResolveUniquePath(packageDirectory, "screen.ppm");
            long size = new FileInfo(temporaryPath).Length;
            long copied = await CopyBoundedAsync(temporaryPath, targetPath, _options.MaxScreenshotBytes, cancellationToken)
                .ConfigureAwait(false);

            if (copied < size)
            {
                // 被裁掉的 PPM 头部尺寸与实际字节数不符，任何解码器都会报错，留着只会误导排查。
                File.Delete(targetPath);
                return new DiagnosticsStepResult(
                    QmpScreenshotStepName,
                    DiagnosticsStepOutcome.Truncated,
                    $"截图共 {size} 字节，超过上限 {_options.MaxScreenshotBytes} 字节，已整体丢弃以免留下无法解码的残缺文件。");
            }

            return new DiagnosticsStepResult(
                QmpScreenshotStepName,
                DiagnosticsStepOutcome.Succeeded,
                $"已取回屏幕截图，{size} 字节。",
                Path.GetFileName(targetPath));
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    /// <summary>
    /// 以固定缓冲把源文件复制到目标路径，超过上限即停止并如实返回已复制字节数。
    /// </summary>
    private static async Task<long> CopyBoundedAsync(
        string sourcePath,
        string targetPath,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        const int BufferSize = 81920;

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            useAsync: true);
        await using var target = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: true);

        byte[] buffer = new byte[BufferSize];
        long copied = 0;
        while (copied < maxBytes)
        {
            int read = await source
                .ReadAsync(buffer.AsMemory(0, (int)Math.Min(BufferSize, maxBytes - copied)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
        }

        return copied;
    }

    /// <summary>
    /// 探测实例的 adbd 是否可用，并记录观测到的连接状态。
    /// </summary>
    /// <remarks>
    /// 本产品直连 adbd 端口而不经过 adb 可执行文件，因此没有设备列表可读，
    /// 状态由传输层与 shell 层两级探测推导：连不上记为 unreachable，
    /// 传输层握手成功但 shell 无响应记为 offline，shell 可响应记为 device。
    /// </remarks>
    private async Task<DiagnosticsStepResult> ProbeAdbAsync(
        InstanceDiagnosticsRequest request,
        string packageDirectory,
        CancellationToken cancellationToken)
    {
        int port = request.Ports.Adb;
        if (port is < 1 or > 65535)
        {
            return new DiagnosticsStepResult(
                AdbProbeStepName,
                DiagnosticsStepOutcome.Skipped,
                $"没有可用的 adb 端口（当前为 {port}），跳过探测。");
        }

        string state = "unknown";
        string detail = "尚未完成探测。";
        int? protocolVersion = null;
        bool? isRoot = null;
        var outcome = DiagnosticsStepOutcome.Failed;

        IAdbClient client = CreateAdbClient(request);
        await using (client.ConfigureAwait(false))
        {
            try
            {
                protocolVersion = await client.ConnectAsync(port, cancellationToken).ConfigureAwait(false);
            }
            catch (XBearException exception)
            {
                state = "unreachable";
                detail = $"无法连接 adbd 端口 {port}：{exception.Message}";
                outcome = DiagnosticsStepOutcome.Failed;
            }

            if (state == "unknown")
            {
                try
                {
                    isRoot = await client.IsRootAsync(cancellationToken).ConfigureAwait(false);
                    state = "device";
                    outcome = DiagnosticsStepOutcome.Succeeded;
                    detail = $"adbd 可响应 shell，实例以 {(isRoot == true ? "root" : "非 root")} 身份运行。";
                }
                catch (XBearException exception)
                {
                    state = "offline";
                    outcome = DiagnosticsStepOutcome.Failed;
                    detail = $"传输层握手成功但 shell 无响应，实例内服务可能尚未就绪：{exception.Message}";
                }
            }
        }

        var lines = new StringBuilder()
            .Append("state: ").Append(state).AppendLine()
            .Append("adb_port: ").Append(port).AppendLine()
            .Append("protocol_version: ")
            .AppendLine(protocolVersion is { } version ? "0x" + version.ToString("x8", CultureInfo.InvariantCulture) : "(未取得)")
            .Append("root: ")
            .AppendLine(isRoot is { } root ? (root ? "true" : "false") : "(未取得)")
            .Append("detail: ").AppendLine(detail);

        string fileName = ResolveUniquePath(packageDirectory, "adb-probe.txt");
        await File.WriteAllTextAsync(fileName, lines.ToString(), Utf8NoBom, cancellationToken).ConfigureAwait(false);

        return new DiagnosticsStepResult(
            AdbProbeStepName,
            outcome,
            $"状态 {state}：{detail}",
            Path.GetFileName(fileName));
    }

    /// <summary>
    /// 写入人类可读的导出清单，说明包的来源与各步骤结论。
    /// </summary>
    private static async Task<DiagnosticsStepResult> WriteManifestAsync(
        InstanceDiagnosticsRequest request,
        string packageDirectory,
        IReadOnlyList<DiagnosticsStepResult> steps,
        CancellationToken cancellationToken)
    {
        int failed = steps.Count(step => step.Outcome == DiagnosticsStepOutcome.Failed);
        int truncated = steps.Count(step => step.Outcome == DiagnosticsStepOutcome.Truncated);

        var lines = new StringBuilder()
            .AppendLine("小熊模拟器 实例诊断包")
            .AppendLine("导出时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Append("实例标识: ").AppendLine(request.InstanceId)
            .Append("实例显示名: ").AppendLine(string.IsNullOrWhiteSpace(request.InstanceDisplayName) ? "(未提供)" : request.InstanceDisplayName)
            .Append("宿主系统: ").AppendLine(Environment.OSVersion.ToString())
            .Append("运行时版本: ").AppendLine(Environment.Version.ToString())
            .Append("进程位数: ").Append(Environment.Is64BitProcess ? "64 位" : "32 位").AppendLine()
            .Append("逻辑处理器: ").AppendLine(Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture))
            .AppendLine("隐私说明: 未采集用户名、计算机名与局域网地址；绝对路径中的用户目录已替换为占位符。")
            .Append("总体结论: ").AppendLine(failed == 0 && truncated == 0
                ? "全部步骤均已取得证据。"
                : $"{failed} 个步骤未取得证据，{truncated} 个步骤被裁剪，详见下表。")
            .AppendLine()
            .AppendLine("步骤结果")
            .AppendLine("--------");

        foreach (DiagnosticsStepResult step in steps)
        {
            lines.Append('[')
                .Append(DescribeOutcome(step.Outcome))
                .Append("] ")
                .Append(step.Name)
                .Append(" — ")
                .AppendLine(step.Detail);
        }

        lines.AppendLine()
            .AppendLine("说明: 未取得证据的步骤表示当时宿主侧观测不到该信息，不代表实例一定没有启动。");

        string fileName = ResolveUniquePath(packageDirectory, "manifest.txt");
        await File.WriteAllTextAsync(fileName, lines.ToString(), Utf8NoBom, cancellationToken).ConfigureAwait(false);

        return new DiagnosticsStepResult(
            ManifestStepName,
            DiagnosticsStepOutcome.Succeeded,
            "已写出导出清单。",
            Path.GetFileName(fileName));
    }

    private static string DescribeOutcome(DiagnosticsStepOutcome outcome) => outcome switch
    {
        DiagnosticsStepOutcome.Succeeded => "成功",
        DiagnosticsStepOutcome.Truncated => "裁剪",
        DiagnosticsStepOutcome.Failed => "失败",
        _ => "跳过",
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 临时文件清理失败不影响诊断结论，下次开机由系统回收。
        }
    }
}