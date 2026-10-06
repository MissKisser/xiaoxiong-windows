using XBear.Core.Spec;

namespace XBear.Core.Abstractions;

/// <summary>按实例配置生成 QEMU 命令行参数。</summary>
public interface IQemuArgBuilder
{
    /// <summary>生成启动参数序列，不含可执行文件路径。</summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="diskPath">实例可写磁盘镜像路径。</param>
    /// <param name="ports">本次分配到的宿主端口。</param>
    IReadOnlyList<string> BuildStartArguments(
        InstanceSpec spec,
        string diskPath,
        AllocatedPorts ports);
}

/// <summary>拉起并托管 QEMU 子进程。</summary>
public interface IQemuLauncher
{
    /// <summary>启动实例进程并开始捕获日志。</summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="diskPath">实例可写磁盘镜像路径。</param>
    /// <param name="ports">本次分配到的宿主端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<QemuProcessHandle> StartAsync(
        InstanceSpec spec,
        string diskPath,
        AllocatedPorts ports,
        CancellationToken cancellationToken = default);
}

/// <summary>运行中的 QEMU 子进程句柄。</summary>
public interface QemuProcessHandle : IAsyncDisposable
{
    /// <summary>宿主进程标识。</summary>
    int ProcessId { get; }

    /// <summary>启动时使用的完整命令行。</summary>
    string CommandLine { get; }

    /// <summary>标准输出与标准错误的合并日志路径。</summary>
    string LogFilePath { get; }

    /// <summary>进程是否已退出。</summary>
    bool HasExited { get; }

    /// <summary>异常退出时的退出码，正常退出为 0。</summary>
    int ExitCode { get; }

    /// <summary>终止进程并等待其完全退出。</summary>
    /// <param name="timeout">等待超时，超时后强杀。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>管理 base 镜像与每实例 overlay 的链式关系。</summary>
public interface IQcow2Manager
{
    /// <summary>为实例创建可写 overlay，base 保持只读共享。</summary>
    /// <param name="baseImagePath">只读 base 镜像路径。</param>
    /// <param name="overlayPath">待创建的 overlay 路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task CreateOverlayAsync(string baseImagePath, string overlayPath, CancellationToken cancellationToken = default);

    /// <summary>在当前 overlay 之上再叠一层，用于快照。</summary>
    /// <param name="currentTopPath">当前链顶 overlay 路径。</param>
    /// <param name="snapshotPath">待创建的快照层路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task CreateSnapshotAsync(string currentTopPath, string snapshotPath, CancellationToken cancellationToken = default);

    /// <summary>将链顶切换为指定快照层。</summary>
    /// <param name="snapshotPath">快照层路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RestoreAsync(string snapshotPath, CancellationToken cancellationToken = default);

    /// <summary>校验 overlay 的 backing file 指向仍有效。</summary>
    /// <param name="overlayPath">overlay 路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> ValidateChainAsync(string overlayPath, CancellationToken cancellationToken = default);
}