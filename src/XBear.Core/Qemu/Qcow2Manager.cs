using System.Diagnostics;
using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Qemu;

/// <summary>一次 qemu-img 调用的执行结果。</summary>
/// <param name="ExitCode">进程退出码，超时强杀时为 -1。</param>
/// <param name="StandardOutput">标准输出全文。</param>
/// <param name="StandardError">标准错误全文。</param>
/// <param name="TimedOut">是否因超时被强制终止。</param>
public sealed record QemuImgCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    /// <summary>标准输出与标准错误的合并文本，用于诊断信息。</summary>
    public string CombinedOutput =>
        string.IsNullOrWhiteSpace(StandardOutput)
            ? StandardError
            : string.IsNullOrWhiteSpace(StandardError)
                ? StandardOutput
                : StandardOutput + Environment.NewLine + StandardError;
}

/// <summary>执行 qemu-img 的边界，便于在受控环境中替换为替身实现。</summary>
public interface IQemuImgCommandRunner
{
    /// <summary>运行 qemu-img 直到其自行退出或超时。</summary>
    /// <param name="executablePath">可执行文件绝对路径。</param>
    /// <param name="arguments">参数序列。</param>
    /// <param name="timeout">等待退出的上限时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果，含退出码与合并输出。</returns>
    Task<QemuImgCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>用 qemu-img 维护 base 镜像与每实例 overlay 的链式关系。</summary>
public sealed class Qcow2Manager : IQcow2Manager
{
    /// <summary>单条 qemu-img 命令的默认执行上限时长。</summary>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 镜像导入（qemu-img convert）的执行上限时长。
    /// 转换耗时与源镜像体积和磁盘读写速度相关，比其余单步命令需要更长的等待。
    /// </summary>
    public static readonly TimeSpan DefaultImportTimeout = TimeSpan.FromMinutes(20);

    /// <summary>qcow2 格式名，用于格式探测结果的判定与命令行参数取值。</summary>
    public const string Qcow2Format = "qcow2";

    private const string RestoredLayerSuffix = "-restored.qcow2";
    private const string ImportingSuffix = ".importing";

    /// <summary>
    /// 允许直接充当 overlay 下层镜像的 base 格式。
    /// 仅收录支持 backing file 且宿主 QEMU 通常启用的格式，
    /// 其余格式必须先转换为 qcow2，避免把无法叠层的镜像交给 qemu-img create。
    /// </summary>
    private static readonly HashSet<string> SupportedBaseFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        Qcow2Format,
        "raw",
        "vmdk",
        "vhdx",
        "vpc",
    };

    private readonly string _qemuImgPath;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeSpan _importTimeout;
    private readonly IQemuImgCommandRunner _commandRunner;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, SemaphoreSlim> _importGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _liveTopsBySnapshot =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>构造磁盘管理器。</summary>
    /// <param name="paths">QEMU 可执行文件定位结果。</param>
    public Qcow2Manager(QemuPaths paths)
        : this(paths, null)
    {
    }

    /// <summary>构造磁盘管理器并指定命令执行上限时长。</summary>
    /// <param name="paths">QEMU 可执行文件定位结果。</param>
    /// <param name="commandTimeout">单条命令的执行上限时长，为 null 时使用 <see cref="DefaultCommandTimeout"/>。</param>
    /// <exception cref="XBearException">定位结果为空时抛出 <see cref="ErrorCategory.Dependency"/>。</exception>
    public Qcow2Manager(QemuPaths paths, TimeSpan? commandTimeout)
        : this(paths, commandTimeout, null)
    {
    }

    /// <summary>构造磁盘管理器并替换 qemu-img 执行边界与日志出口。</summary>
    /// <param name="paths">QEMU 可执行文件定位结果。</param>
    /// <param name="commandTimeout">单条命令的执行上限时长，为 null 时使用 <see cref="DefaultCommandTimeout"/>。</param>
    /// <param name="commandRunner">qemu-img 执行边界，为 null 时直接拉起 qemu-img 进程。</param>
    /// <param name="log">过程日志出口，为 null 时输出到 <see cref="Trace"/>。</param>
    /// <param name="importTimeout">镜像导入的执行上限时长，为 null 时使用 <see cref="DefaultImportTimeout"/>。</param>
    /// <exception cref="XBearException">定位结果为空时抛出 <see cref="ErrorCategory.Dependency"/>。</exception>
    public Qcow2Manager(
        QemuPaths paths,
        TimeSpan? commandTimeout,
        IQemuImgCommandRunner? commandRunner,
        Action<string>? log = null,
        TimeSpan? importTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _qemuImgPath = paths.QemuImgPath;
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
        _importTimeout = importTimeout ?? DefaultImportTimeout;
        _commandRunner = commandRunner ?? new ProcessQemuImgCommandRunner();
        _log = log ?? (message => Trace.WriteLine(message));
    }

    /// <summary>为实例创建可写 overlay，base 保持只读共享。</summary>
    /// <param name="baseImagePath">只读 base 镜像路径。</param>
    /// <param name="overlayPath">待创建的 overlay 路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="XBearException">镜像不存在、base 格式不受支持或 qemu-img 执行失败时抛出 <see cref="ErrorCategory.Storage"/>。</exception>
    public Task CreateOverlayAsync(
        string baseImagePath,
        string overlayPath,
        CancellationToken cancellationToken = default)
        => CreateLayerAsync(baseImagePath, overlayPath, cancellationToken);

    /// <summary>在当前 overlay 之上再叠一层，用于快照。</summary>
    /// <param name="currentTopPath">当前链顶 overlay 路径。</param>
    /// <param name="snapshotPath">待创建的快照层路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="XBearException">镜像不存在、base 格式不受支持或 qemu-img 执行失败时抛出 <see cref="ErrorCategory.Storage"/>。</exception>
    public async Task CreateSnapshotAsync(
        string currentTopPath,
        string snapshotPath,
        CancellationToken cancellationToken = default)
    {
        await CreateLayerAsync(currentTopPath, snapshotPath, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _liveTopsBySnapshot[NormalizePath(snapshotPath)] = NormalizePath(currentTopPath);
        }
    }

    /// <summary>将快照层复制为新的链顶，快照文件本身保持不变。</summary>
    /// <param name="snapshotPath">快照层路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="XBearException">快照不存在或 qemu-img 执行失败时抛出 <see cref="ErrorCategory.Storage"/>。</exception>
    public Task RestoreAsync(string snapshotPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshotPath);

        string? liveTop;
        lock (_gate)
        {
            _liveTopsBySnapshot.TryGetValue(NormalizePath(snapshotPath), out liveTop);
        }

        var destination = string.IsNullOrWhiteSpace(liveTop) || !File.Exists(liveTop)
            ? BuildFallbackRestoredPath(snapshotPath)
            : liveTop;

        return RestoreAsync(snapshotPath, destination, cancellationToken);
    }

    /// <summary>把快照层复制到指定的链顶位置。</summary>
    /// <param name="snapshotPath">快照层路径。</param>
    /// <param name="newTopPath">恢复后作为链顶的镜像路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="XBearException">快照不存在或 qemu-img 执行失败时抛出 <see cref="ErrorCategory.Storage"/>。</exception>
    public async Task RestoreAsync(
        string snapshotPath,
        string newTopPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshotPath);

        if (string.IsNullOrWhiteSpace(newTopPath))
        {
            throw new XBearException(ErrorCategory.Storage, "恢复目标镜像路径不能为空。");
        }

        if (!File.Exists(snapshotPath))
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"快照层不存在：{snapshotPath}",
                "请确认快照文件仍在原位置，或改用其他可用的快照层恢复。");
        }

        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(newTopPath));
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        await RunAsync(
            new[] { "convert", "-O", "qcow2", snapshotPath, newTopPath },
            snapshotPath,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>校验 overlay 的 backing file 指向仍有效。</summary>
    /// <param name="overlayPath">overlay 路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>镜像与 backing chain 均通过检查返回 true。</returns>
    /// <exception cref="XBearException">qemu-img 无法执行或超时未返回时抛出 <see cref="ErrorCategory.Storage"/>。</exception>
    public async Task<bool> ValidateChainAsync(
        string overlayPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(overlayPath);

        if (!File.Exists(overlayPath))
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"待校验的 overlay 不存在：{overlayPath}",
                "请确认实例镜像路径正确，或重新创建 overlay。");
        }

        var result = await ExecuteAsync(
            new[] { "check", overlayPath },
            _commandTimeout,
            cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"qemu-img check 执行超时：{overlayPath}",
                "请检查镜像所在存储设备的可用性后重试。");
        }

        return result.ExitCode == 0;
    }

    /// <summary>把任意格式的镜像导入为 qcow2 base，供后续实例在其上叠 overlay。</summary>
    /// <param name="sourceImagePath">源镜像路径，可为 ISO、raw 或 qcow2。</param>
    /// <param name="baseImagePath">待生成或已存在的 qcow2 base 镜像路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 本次真正执行了转换返回 true；目标已是可用的 qcow2 base、未重复转换返回 false。
    /// </returns>
    /// <exception cref="XBearException">源镜像不存在、路径冲突或转换失败时抛出 <see cref="ErrorCategory.Storage"/>。</exception>
    public async Task<bool> ImportBaseImageAsync(
        string sourceImagePath,
        string baseImagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceImagePath);

        if (string.IsNullOrWhiteSpace(baseImagePath))
        {
            throw new XBearException(ErrorCategory.Storage, "导入目标 base 镜像的路径不能为空。");
        }

        if (!File.Exists(sourceImagePath))
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"待导入的源镜像不存在：{sourceImagePath}",
                "请选择有效的镜像文件；Bliss OS 只发布 ISO 安装介质，需先导入为 qcow2 base 才能创建实例。");
        }

        if (string.Equals(NormalizePath(sourceImagePath), NormalizePath(baseImagePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"导入目标与源镜像是同一个路径：{baseImagePath}",
                "请指定一个与源镜像不同的 base 镜像输出路径。");
        }

        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(baseImagePath));
        if (!string.IsNullOrEmpty(baseDirectory))
        {
            Directory.CreateDirectory(baseDirectory);
        }

        var gate = GetImportGate(baseImagePath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(baseImagePath)
                && await IsQcow2BaseAsync(baseImagePath, cancellationToken).ConfigureAwait(false))
            {
                _log($"base 镜像已是可用的 qcow2，跳过重复转换：{baseImagePath}");
                return false;
            }

            return await ConvertToQcow2Async(sourceImagePath, baseImagePath, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 转换到临时路径再落位，保证中途失败不会让半成品占据 base 镜像路径。
    /// </summary>
    private async Task<bool> ConvertToQcow2Async(
        string sourceImagePath,
        string baseImagePath,
        CancellationToken cancellationToken)
    {
        var temporaryPath = BuildImportingPath(baseImagePath);
        var stopwatch = Stopwatch.StartNew();
        var published = false;

        _log($"开始把镜像导入为 qcow2 base：{sourceImagePath} → {baseImagePath}，"
             + $"源体积 {DescribeSize(sourceImagePath)}；转换耗时与镜像体积和磁盘速度相关，请稍候。");

        // 上一轮中断可能留下临时文件，先清掉再交给 qemu-img 写出完整产物。
        TryDeleteTemporary(temporaryPath);

        try
        {
            // -c 让 base 保持压缩：base 只读共享，压缩后占用磁盘更少。
            await RunAsync(
                new[] { "convert", "-O", "qcow2", "-c", sourceImagePath, temporaryPath },
                baseImagePath,
                cancellationToken,
                _importTimeout).ConfigureAwait(false);

            if (!File.Exists(temporaryPath))
            {
                throw new XBearException(
                    ErrorCategory.Storage,
                    $"镜像导入结束但未产出文件：{temporaryPath}",
                    "请检查目标目录的写入权限与剩余空间后重试。");
            }

            File.Move(temporaryPath, baseImagePath, overwrite: true);
            published = true;
        }
        catch (XBearException exception)
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"镜像导入为 qcow2 base 失败：{exception.Message}",
                "导入过程不会覆盖原有 base 镜像。请确认目标磁盘剩余空间不小于源镜像体积、源镜像可正常读取；"
                + "空间不足或转换超时后，释放空间或改到读写更快的分区重新导入即可。",
                exception);
        }
        finally
        {
            if (!published)
            {
                TryDeleteTemporary(temporaryPath);
            }
        }

        stopwatch.Stop();
        _log($"镜像导入完成：{baseImagePath}，耗时 {stopwatch.Elapsed.TotalSeconds:F1} 秒。");
        return true;
    }

    /// <summary>创建一层指向 base 的可写镜像。</summary>
    /// <param name="baseImagePath">下层镜像路径。</param>
    /// <param name="layerPath">待创建的上层镜像路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示创建结束的任务。</returns>
    /// <exception cref="XBearException">下层镜像不存在、格式探测失败或格式不受支持时抛出 <see cref="ErrorCategory.Storage"/>。</exception>
    private async Task CreateLayerAsync(
        string baseImagePath,
        string layerPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseImagePath);

        if (string.IsNullOrWhiteSpace(layerPath))
        {
            throw new XBearException(ErrorCategory.Storage, "待创建镜像的路径不能为空。");
        }

        if (!File.Exists(baseImagePath))
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"下层镜像不存在：{baseImagePath}",
                "请确认 base 镜像路径正确，或重新导入镜像后重试。");
        }

        var layerDirectory = Path.GetDirectoryName(Path.GetFullPath(layerPath));
        if (!string.IsNullOrEmpty(layerDirectory))
        {
            Directory.CreateDirectory(layerDirectory);
        }

        // 格式探测失败时不猜格式：create 的 -F 一旦写错，qemu-img 会在运行时才拒绝 backing file。
        var baseFormat = await ResolveBaseFormatAsync(baseImagePath, cancellationToken).ConfigureAwait(false);

        await RunAsync(
            new[] { "create", "-f", Qcow2Format, "-b", baseImagePath, "-F", baseFormat, layerPath },
            layerPath,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>探测 base 镜像的真实格式，供 create 的格式参数使用。</summary>
    /// <param name="baseImagePath">下层镜像路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>qemu-img 报告且受支持的格式名。</returns>
    /// <exception cref="XBearException">探测命令失败、输出无法解析或格式不受支持时抛出 <see cref="ErrorCategory.Storage"/>。</exception>
    private async Task<string> ResolveBaseFormatAsync(
        string baseImagePath,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(
            new[] { "info", "--output=json", baseImagePath },
            _commandTimeout,
            cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"探测 base 镜像格式超时：{baseImagePath}",
                "请检查镜像所在存储设备的可用性后重试。");
        }

        if (result.ExitCode != 0)
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"探测 base 镜像格式失败，退出码 {result.ExitCode}，镜像 {baseImagePath}。输出：{result.CombinedOutput}",
                "请确认 base 镜像完整可读；若为 ISO 等安装介质，请先导入为 qcow2 base 再创建 overlay。");
        }

        var format = ReadFormatOrNull(result.StandardOutput);
        if (string.IsNullOrWhiteSpace(format))
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"无法从 qemu-img info 输出中解析 base 镜像格式：{baseImagePath}。输出：{result.CombinedOutput}",
                "请确认当前 qemu-img 支持 --output=json 参数，必要时重新安装 QEMU。");
        }

        if (!SupportedBaseFormats.Contains(format))
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"base 镜像格式 {format} 暂不支持作为 overlay 的下层镜像：{baseImagePath}",
                "请先用 qemu-img convert -O qcow2 把该镜像转换为 qcow2，再作为 base 镜像使用。");
        }

        return format;
    }

    /// <summary>判断已有目标是否已经是可用的 qcow2 base，用于跳过重复转换。</summary>
    private async Task<bool> IsQcow2BaseAsync(string baseImagePath, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(
            new[] { "info", "--output=json", baseImagePath },
            _commandTimeout,
            cancellationToken).ConfigureAwait(false);

        return !result.TimedOut
            && result.ExitCode == 0
            && string.Equals(ReadFormatOrNull(result.StandardOutput), Qcow2Format, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>执行一条 qemu-img 命令并按失败原因抛出异常。</summary>
    /// <param name="arguments">qemu-img 参数序列。</param>
    /// <param name="subjectPath">用于异常信息的镜像路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="timeout">覆盖本次调用的执行上限时长，为 null 时使用管理器默认值。</param>
    /// <returns>表示执行结束的任务。</returns>
    /// <exception cref="XBearException">命令超时、无法启动或返回非零码时抛出 <see cref="ErrorCategory.Storage"/>。</exception>
    private async Task RunAsync(
        IReadOnlyList<string> arguments,
        string subjectPath,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var result = await ExecuteAsync(arguments, timeout ?? _commandTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"qemu-img 操作超时：{subjectPath}",
                "请检查镜像所在存储设备的可用性后重试。");
        }

        if (result.ExitCode != 0)
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"qemu-img 操作失败，退出码 {result.ExitCode}，目标 {subjectPath}。输出：{result.CombinedOutput}",
                "请检查镜像路径、磁盘剩余空间与镜像格式是否正确后重试。");
        }
    }

    /// <summary>调用 qemu-img 执行边界，并把无法拉起进程的情况转成统一异常。</summary>
    private async Task<QemuImgCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _commandRunner
                .RunAsync(_qemuImgPath, arguments, timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or InvalidOperationException)
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"无法执行 qemu-img：{exception.Message}",
                "请确认 QEMU 安装完整且 qemu-img.exe 路径正确。",
                exception);
        }
    }

    /// <summary>取同一目标路径上的导入锁，避免多个实例同时转换同一个 base 镜像。</summary>
    private SemaphoreSlim GetImportGate(string baseImagePath)
    {
        lock (_gate)
        {
            var key = NormalizePath(baseImagePath);
            if (!_importGates.TryGetValue(key, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _importGates[key] = gate;
            }

            return gate;
        }
    }

    /// <summary>由目标路径派生同目录的临时产物路径，保证落位是同卷替换。</summary>
    private static string BuildImportingPath(string baseImagePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(baseImagePath)) ?? string.Empty;
        var name = Path.GetFileName(baseImagePath) + ImportingSuffix;

        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }

    /// <summary>尽力清理临时产物，失败时不掩盖原始异常。</summary>
    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清理失败不应掩盖原始异常，残留文件会在下次导入前被重新清理。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上，交由用户处理文件占用。
        }
    }

    /// <summary>读取源镜像体积用于过程提示，读取失败不影响导入。</summary>
    private static string DescribeSize(string path)
    {
        try
        {
            return $"{new FileInfo(path).Length} 字节";
        }
        catch (IOException)
        {
            return "体积未知";
        }
        catch (UnauthorizedAccessException)
        {
            return "体积未知";
        }
    }

    /// <summary>从 qemu-img info 的 JSON 输出中取格式名，无法解析时返回 null。</summary>
    private static string? ReadFormatOrNull(string standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("format", out var element)
                && element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>由快照路径派生一个不覆盖原快照的恢复目标。</summary>
    /// <param name="snapshotPath">快照层路径。</param>
    /// <returns>恢复目标镜像路径。</returns>
    private static string BuildFallbackRestoredPath(string snapshotPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(snapshotPath)) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(snapshotPath);

        return string.IsNullOrEmpty(directory)
            ? stem + RestoredLayerSuffix
            : Path.Combine(directory, stem + RestoredLayerSuffix);
    }

    /// <summary>归一化路径用于字典键比较，避免大小写与相对路径造成重复登记。</summary>
    /// <param name="path">原始路径。</param>
    /// <returns>归一化后的路径文本。</returns>
    private static string NormalizePath(string path)
        => Path.GetFullPath(path.Trim());

    /// <summary>默认执行边界，直接调用真实 qemu-img 进程。</summary>
    private sealed class ProcessQemuImgCommandRunner : IQemuImgCommandRunner
    {
        /// <summary>运行 qemu-img 直到其自行退出或超时。</summary>
        /// <param name="executablePath">可执行文件绝对路径。</param>
        /// <param name="arguments">参数序列。</param>
        /// <param name="timeout">等待退出的上限时长。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>执行结果，含退出码与合并输出。</returns>
        public async Task<QemuImgCommandResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var result = await QemuCommandRunner
                .RunAsync(executablePath, arguments, timeout, cancellationToken)
                .ConfigureAwait(false);

            return new QemuImgCommandResult(
                result.ExitCode,
                result.StandardOutput,
                result.StandardError,
                result.TimedOut);
        }
    }
}