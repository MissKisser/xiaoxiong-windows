using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XBear.Core.Abstractions;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>端口分配器替身，按实例分配固定端口并记录释放次数。</summary>
internal sealed class StubPortAllocator : IPortAllocator
{
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);

    /// <summary>首个实例的 adb 端口，其后依次递增。</summary>
    public int BasePort { get; init; } = 15555;

    /// <summary>当前仍被占用的实例标识。</summary>
    public IReadOnlyCollection<string> Held => _held;

    /// <summary>释放调用次数。</summary>
    public int ReleaseCount { get; private set; }

    /// <summary>分配失败时抛出。</summary>
    public Exception? AcquireFailure { get; set; }

    /// <summary>
    /// 占用一组端口。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分配的端口组。</returns>
    public Task<AllocatedPorts> AcquireAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        if (AcquireFailure is not null)
        {
            throw AcquireFailure;
        }

        _held.Add(instanceId);
        int offset = _held.Count - 1;
        return Task.FromResult(new AllocatedPorts(BasePort + offset, BasePort + offset + 1, 5900 + offset));
    }

    /// <summary>
    /// 记录一次释放。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    public void Release(string instanceId)
    {
        ReleaseCount++;
        _held.Remove(instanceId);
    }

    /// <summary>
    /// 始终返回全部端口空闲。
    /// </summary>
    /// <param name="ports">待检端口组。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true。</returns>
    public Task<bool> AreAvailableAsync(AllocatedPorts ports, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}

/// <summary>参数生成器替身，只记录入参。</summary>
internal sealed class StubArgBuilder : IQemuArgBuilder
{
    /// <summary>最近一次使用的端口组。</summary>
    public AllocatedPorts LastPorts { get; private set; } = new(0, 0, 0);

    /// <summary>
    /// 生成固定参数。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="diskPath">可写磁盘镜像路径。</param>
    /// <param name="ports">端口组。</param>
    /// <returns>参数序列。</returns>
    public IReadOnlyList<string> BuildStartArguments(InstanceSpec spec, string diskPath, AllocatedPorts ports)
    {
        LastPorts = ports;
        return new[] { "-machine", "q35" };
    }
}

/// <summary>进程句柄替身，记录停止与释放调用。</summary>
internal sealed class StubQemuProcessHandle : QemuProcessHandle
{
    /// <summary>宿主进程标识。</summary>
    public int ProcessId => 4321;

    /// <summary>启动命令行。</summary>
    public string CommandLine => "qemu-system-x86_64 -machine q35";

    /// <summary>日志路径。</summary>
    public string LogFilePath => Path.Combine(Path.GetTempPath(), "xbear-stub-qemu.log");

    /// <summary>进程是否已退出。</summary>
    public bool HasExited => StopCount > 0;

    /// <summary>退出码。</summary>
    public int ExitCode => StopCount > 0 ? 0 : -1;

    /// <summary>停止调用次数。</summary>
    public int StopCount { get; private set; }

    /// <summary>释放调用次数。</summary>
    public int DisposeCount { get; private set; }

    /// <summary>
    /// 记录一次停止。
    /// </summary>
    /// <param name="timeout">等待超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        StopCount++;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 记录一次释放。
    /// </summary>
    /// <returns>异步任务。</returns>
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>QEMU 拉起器替身。</summary>
internal sealed class StubQemuLauncher : IQemuLauncher
{
    /// <summary>拉起的进程句柄。</summary>
    public List<StubQemuProcessHandle> Started { get; } = new();

    /// <summary>启动失败时抛出。</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>
    /// 返回一个进程句柄。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="diskPath">可写磁盘镜像路径。</param>
    /// <param name="ports">端口组。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>进程句柄。</returns>
    public Task<QemuProcessHandle> StartAsync(
        InstanceSpec spec,
        string diskPath,
        AllocatedPorts ports,
        CancellationToken cancellationToken = default)
    {
        if (StartFailure is not null)
        {
            throw StartFailure;
        }

        var handle = new StubQemuProcessHandle();
        Started.Add(handle);
        return Task.FromResult<QemuProcessHandle>(handle);
    }
}

/// <summary>overlay 管理器替身，写入占位文件使启动流程走完。</summary>
internal sealed class StubQcow2Manager : IQcow2Manager
{
    /// <summary>
    /// 创建一个占位 overlay。
    /// </summary>
    /// <param name="baseImagePath">base 镜像路径。</param>
    /// <param name="overlayPath">待创建的 overlay 路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task CreateOverlayAsync(
        string baseImagePath,
        string overlayPath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(overlayPath)!);
        File.WriteAllText(overlayPath, "stub overlay");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 不做实际快照。
    /// </summary>
    /// <param name="currentTopPath">链顶路径。</param>
    /// <param name="snapshotPath">快照层路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task CreateSnapshotAsync(
        string currentTopPath,
        string snapshotPath,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// 不做实际还原。
    /// </summary>
    /// <param name="snapshotPath">快照层路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task RestoreAsync(string snapshotPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// 始终返回链有效。
    /// </summary>
    /// <param name="overlayPath">overlay 路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true。</returns>
    public Task<bool> ValidateChainAsync(
        string overlayPath,
        CancellationToken cancellationToken = default) => Task.FromResult(true);
}

/// <summary>内存实例仓库，供装配测试使用。</summary>
internal sealed class StubInstanceRepository : IInstanceRepository
{
    private readonly Dictionary<string, InstanceSpec> _specs = new(StringComparer.Ordinal);

    /// <summary>
    /// 放入一个实例配置。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    public void Add(InstanceSpec spec) => _specs[spec.Id] = spec;

    /// <summary>
    /// 返回全部实例配置。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实例配置列表。</returns>
    public Task<IReadOnlyList<InstanceSpec>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InstanceSpec>>(_specs.Values.ToList());

    /// <summary>
    /// 按标识读取实例配置。
    /// </summary>
    /// <param name="id">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实例配置，不存在时返回 null。</returns>
    public Task<InstanceSpec?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_specs.TryGetValue(id, out InstanceSpec? spec) ? spec : null);

    /// <summary>
    /// 写入实例配置。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task SaveAsync(InstanceSpec spec, CancellationToken cancellationToken = default)
    {
        _specs[spec.Id] = spec;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 删除实例配置。
    /// </summary>
    /// <param name="id">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        _specs.Remove(id);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 始终返回 false。
    /// </summary>
    /// <param name="id">实例标识。</param>
    /// <returns>false。</returns>
    public Task<bool> IsTombstonedAsync(string id) => Task.FromResult(false);
}

/// <summary>QMP 客户端替身，可配置连接与命令执行的结果。</summary>
internal sealed class StubQmpClient : IQmpClient
{
    /// <summary>连接时抛出的异常，置空表示连接成功。</summary>
    public Exception? ConnectFailure { get; set; }

    /// <summary>命令执行时抛出的异常。</summary>
    public Exception? ExecuteFailure { get; set; }

    /// <summary>最近一次连接使用的端口。</summary>
    public int LastConnectPort { get; private set; }

    /// <summary>连接次数。</summary>
    public int ConnectCount { get; private set; }

    /// <summary>释放次数。</summary>
    public int DisposeCount { get; private set; }

    /// <summary>
    /// 记录一次连接。
    /// </summary>
    /// <param name="port">QMP 宿主端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>空能力集。</returns>
    public Task<IReadOnlySet<string>> ConnectAsync(int port, CancellationToken cancellationToken = default)
    {
        LastConnectPort = port;
        ConnectCount++;

        return ConnectFailure is not null
            ? throw ConnectFailure
            : Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// 返回预设的执行结果。
    /// </summary>
    /// <param name="command">命令名。</param>
    /// <param name="arguments">命令参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>空对象。</returns>
    public Task<System.Text.Json.JsonElement> ExecuteAsync(
        string command,
        object? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (ExecuteFailure is not null)
        {
            throw ExecuteFailure;
        }

        using var document = System.Text.Json.JsonDocument.Parse("{}");
        return Task.FromResult(document.RootElement.Clone());
    }

    /// <summary>
    /// 始终返回运行中。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true。</returns>
    public Task<bool> QueryRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    /// <summary>
    /// 始终返回关机成功。
    /// </summary>
    /// <param name="timeout">等待超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true。</returns>
    public Task<bool> RequestShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <summary>
    /// 记录一次释放。
    /// </summary>
    /// <returns>异步任务。</returns>
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>adb 客户端替身，可配置连接、root 检查与 shell 执行的结果。</summary>
internal sealed class StubAdbClient : IAdbClient
{
    /// <summary>连接时抛出的异常，置空表示连接成功。</summary>
    public Exception? ConnectFailure { get; set; }

    /// <summary>shell 执行时抛出的异常。</summary>
    public Exception? ShellFailure { get; set; }

    /// <summary>实例是否以 root 运行。</summary>
    public bool IsRoot { get; set; } = true;

    /// <summary>最近一次连接使用的端口。</summary>
    public int LastConnectPort { get; private set; }

    /// <summary>连接次数。</summary>
    public int ConnectCount { get; private set; }

    /// <summary>释放次数。</summary>
    public int DisposeCount { get; private set; }

    /// <summary>
    /// 记录一次连接。
    /// </summary>
    /// <param name="port">adb 宿主端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>协议版本。</returns>
    public Task<int> ConnectAsync(int port, CancellationToken cancellationToken = default)
    {
        LastConnectPort = port;
        ConnectCount++;

        return ConnectFailure is not null ? throw ConnectFailure : Task.FromResult(41);
    }

    /// <summary>
    /// 返回空输出。
    /// </summary>
    /// <param name="command">命令与参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>空串。</returns>
    public Task<string> ShellAsync(string command, CancellationToken cancellationToken = default) =>
        ShellFailure is not null ? throw ShellFailure : Task.FromResult(string.Empty);

    /// <summary>
    /// 返回预设的 root 状态。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否 root。</returns>
    public Task<bool> IsRootAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsRoot);

    /// <summary>
    /// 不做实际推送。
    /// </summary>
    /// <param name="localPath">宿主文件路径。</param>
    /// <param name="remotePath">实例内目标路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task PushAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// 不做实际拉取。
    /// </summary>
    /// <param name="remotePath">实例内源路径。</param>
    /// <param name="localPath">宿主目标路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task PullAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// 记录一次释放。
    /// </summary>
    /// <returns>异步任务。</returns>
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
