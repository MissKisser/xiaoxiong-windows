using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Qemu;

/// <summary>用 qemu-img 维护 base 镜像与每实例 overlay 的链式关系。</summary>
public sealed class Qcow2Manager : IQcow2Manager
{
    /// <summary>单条 qemu-img 命令的默认执行上限时长。</summary>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromMinutes(2);

    private const string RestoredLayerSuffix = "-restored.qcow2";

    private readonly string _qemuImgPath;
    private readonly TimeSpan _commandTimeout;
    private readonly object _gate = new();
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
    {
        ArgumentNullException.ThrowIfNull(paths);

        _qemuImgPath = paths.QemuImgPath;
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
    }

    /// <summary>为实例创建可写 overlay，base 保持只读共享。</summary>
    /// <param name="baseImagePath">只读 base 镜像路径。</param>
    /// <param name="overlayPath">待创建的 overlay 路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="XBearException">镜像不存在或 qemu-img 执行失败时抛出 <see cref="ErrorCategory.Storage"/>。</exception>
    public Task CreateOverlayAsync(
        string baseImagePath,
        string overlayPath,
        CancellationToken cancellationToken = default)
        => CreateLayerAsync(baseImagePath, overlayPath, cancellationToken);

    /// <summary>在当前 overlay 之上再叠一层，用于快照。</summary>
    /// <param name="currentTopPath">当前链顶 overlay 路径。</param>
    /// <param name="snapshotPath">待创建的快照层路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="XBearException">镜像不存在或 qemu-img 执行失败时抛出 <see cref="ErrorCategory.Storage"/>。</exception>
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

        var result = await QemuCommandRunner
            .RunAsync(_qemuImgPath, new[] { "check", overlayPath }, _commandTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"qemu-img check 执行超时：{overlayPath}",
                "请检查镜像所在存储设备的可用性后重试。");
        }

        return result.ExitCode == 0;
    }

    /// <summary>创建一层指向 base 的可写镜像。</summary>
    /// <param name="baseImagePath">下层镜像路径。</param>
    /// <param name="layerPath">待创建的上层镜像路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示创建结束的任务。</returns>
    /// <exception cref="XBearException">下层镜像不存在或 qemu-img 执行失败时抛出 <see cref="ErrorCategory.Storage"/>。</exception>
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

        await RunAsync(
            new[] { "create", "-f", "qcow2", "-b", baseImagePath, "-F", "qcow2", layerPath },
            layerPath,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>执行一条 qemu-img 命令并按失败原因抛出异常。</summary>
    /// <param name="arguments">qemu-img 参数序列。</param>
    /// <param name="subjectPath">用于异常信息的镜像路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示执行结束的任务。</returns>
    /// <exception cref="XBearException">命令超时、无法启动或返回非零码时抛出 <see cref="ErrorCategory.Storage"/>。</exception>
    private async Task RunAsync(
        IReadOnlyList<string> arguments,
        string subjectPath,
        CancellationToken cancellationToken)
    {
        QemuCommandResult result;
        try
        {
            result = await QemuCommandRunner
                .RunAsync(_qemuImgPath, arguments, _commandTimeout, cancellationToken)
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
}