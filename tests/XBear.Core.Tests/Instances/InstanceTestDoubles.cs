using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.Core.Tests.Instances;

/// <summary>端口分配器替身，记录占用与释放用于断言端口不泄漏。</summary>
internal sealed class FakePortAllocator : IPortAllocator
{
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private readonly int _basePort;

    /// <summary>
    /// 初始化替身。
    /// </summary>
    /// <param name="basePort">首个实例的 adb 端口，其后依次递增。</param>
    public FakePortAllocator(int basePort = 15555)
    {
        _basePort = basePort;
    }

    /// <summary>当前仍被占用的实例标识。</summary>
    public IReadOnlyCollection<string> Held => _held;

    /// <summary>分配失败时抛出。</summary>
    public Exception? AcquireFailure { get; set; }

    /// <summary>释放调用次数。</summary>
    public int ReleaseCount { get; private set; }

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
        return Task.FromResult(new AllocatedPorts(_basePort + offset, _basePort + offset + 1, 5900 + offset));
    }

    /// <summary>
    /// 释放此前占用的端口组。
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

/// <summary>参数生成器替身，只记录入参用于断言。</summary>
internal sealed class FakeQemuArgBuilder : IQemuArgBuilder
{
    /// <summary>最近一次生成的参数序列。</summary>
    public IReadOnlyList<string> LastArguments { get; private set; } = Array.Empty<string>();

    /// <summary>最近一次使用的 overlay 路径。</summary>
    public string LastDiskPath { get; private set; } = string.Empty;

    /// <summary>最近一次使用的端口组，尚未生成时为空端口。</summary>
    public AllocatedPorts LastPorts { get; private set; } = new(0, 0, 0);

    /// <summary>最近一次收到的镜像清单，尚未生成时为空。</summary>
    public ImageSpec? LastImage { get; private set; }

    /// <summary>
    /// 生成固定的参数序列。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="image">实例引用的镜像清单。</param>
    /// <param name="diskPath">可写磁盘镜像路径。</param>
    /// <param name="ports">端口组。</param>
    /// <returns>参数序列。</returns>
    public IReadOnlyList<string> BuildStartArguments(
        InstanceSpec spec,
        ImageSpec? image,
        string diskPath,
        AllocatedPorts ports)
    {
        LastDiskPath = diskPath;
        LastPorts = ports;
        LastImage = image;
        LastArguments = new[] { "-machine", "q35", "-m", spec.Resources.MemoryMB.ToString(), "-qmp", $"tcp:127.0.0.1:{ports.Qmp}" };
        return LastArguments;
    }
}

/// <summary>进程句柄替身，记录停止与释放调用。</summary>
internal sealed class FakeQemuProcessHandle : QemuProcessHandle
{
    /// <summary>宿主进程标识。</summary>
    public int ProcessId => 4321;

    /// <summary>启动命令行。</summary>
    public string CommandLine => "qemu-system-x86_64 -machine q35";

    /// <summary>日志路径。</summary>
    public string LogFilePath => Path.Combine(Path.GetTempPath(), "xbear-fake-qemu.log");

    /// <summary>进程是否已退出。</summary>
    public bool HasExited => StopCount > 0;

    /// <summary>异常退出时的退出码。</summary>
    public int ExitCode => StopCount > 0 ? 0 : -1;

    /// <summary>停止调用次数。</summary>
    public int StopCount { get; private set; }

    /// <summary>释放调用次数。</summary>
    public int DisposeCount { get; private set; }

    /// <summary>停止时抛出。</summary>
    public Exception? StopFailure { get; set; }

    /// <summary>工作集内存提供者，供测试模拟采样。</summary>
    public Func<long?>? WorkingSetProvider { get; set; }

    /// <summary>固定工作集内存数值，供测试模拟采样。</summary>
    public long? FakeWorkingSet { get; set; }

    /// <inheritdoc />
    public long? GetWorkingSetBytes() => WorkingSetProvider?.Invoke() ?? FakeWorkingSet;

    /// <summary>
    /// 记录一次停止调用。
    /// </summary>
    /// <param name="timeout">等待超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        StopCount++;
        return StopFailure is not null ? throw StopFailure : Task.CompletedTask;
    }

    /// <summary>
    /// 记录一次释放调用。
    /// </summary>
    /// <returns>异步任务。</returns>
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>QEMU 拉起器替身，可配置启动失败。</summary>
internal sealed class FakeQemuLauncher : IQemuLauncher
{
    private int _sequence;

    /// <summary>拉起的进程句柄，按顺序返回。</summary>
    public List<FakeQemuProcessHandle> Started { get; } = new();

    /// <summary>启动失败时抛出。</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>拉起成功时的回调，可用于在进程创建完成后推进假时钟。</summary>
    public Action? OnStart { get; set; }

    /// <summary>最近一次启动使用的端口组，尚未启动时为空端口。</summary>
    public AllocatedPorts LastPorts { get; private set; } = new(0, 0, 0);

    /// <summary>最近一次收到的镜像清单，尚未启动时为空。</summary>
    public ImageSpec? LastImage { get; private set; }

    /// <summary>
    /// 返回一个进程句柄，或抛出预设的失败。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="image">实例引用的镜像清单。</param>
    /// <param name="diskPath">可写磁盘镜像路径。</param>
    /// <param name="ports">端口组。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>进程句柄。</returns>
    public Task<QemuProcessHandle> StartAsync(
        InstanceSpec spec,
        ImageSpec? image,
        string diskPath,
        AllocatedPorts ports,
        CancellationToken cancellationToken = default)
    {
        if (StartFailure is not null)
        {
            throw StartFailure;
        }

        LastPorts = ports;
        LastImage = image;
        var handle = new FakeQemuProcessHandle();
        Started.Add(handle);
        _sequence++;
        OnStart?.Invoke();
        return Task.FromResult<QemuProcessHandle>(handle);
    }

    /// <summary>已拉起的进程数量。</summary>
    /// <returns>数量。</returns>
    public int StartCount => _sequence;
}

/// <summary>overlay 管理器替身，可配置创建失败。</summary>
internal sealed class FakeQcow2Manager : IQcow2Manager
{
    /// <summary>已创建的 overlay 路径。</summary>
    public List<string> CreatedOverlays { get; } = new();

    /// <summary>创建失败时抛出。</summary>
    public Exception? CreateFailure { get; set; }

    /// <summary>
    /// 创建一个假的 overlay 文件。
    /// </summary>
    /// <param name="baseImagePath">base 镜像路径。</param>
    /// <param name="overlayPath">overlay 路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task CreateOverlayAsync(string baseImagePath, string overlayPath, CancellationToken cancellationToken = default)
    {
        if (CreateFailure is not null)
        {
            throw CreateFailure;
        }

        CreatedOverlays.Add(overlayPath);
        Directory.CreateDirectory(Path.GetDirectoryName(overlayPath)!);
        File.WriteAllText(overlayPath, "fake overlay");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 始终返回链有效。
    /// </summary>
    /// <param name="overlayPath">overlay 路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true。</returns>
    public Task<bool> ValidateChainAsync(string overlayPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}

/// <summary>内存实例仓库，供生命周期测试使用。</summary>
internal sealed class InMemoryInstanceRepository : IInstanceRepository
{
    private readonly Dictionary<string, InstanceSpec> _specs = new(StringComparer.Ordinal);

    /// <summary>
    /// 放入一个实例配置。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    public void Add(InstanceSpec spec) => _specs[spec.Id] = spec;

    /// <summary>返回全部实例配置。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实例配置列表。</returns>
    public Task<IReadOnlyList<InstanceSpec>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InstanceSpec>>(_specs.Values.ToList());

    /// <summary>按标识读取实例配置。</summary>
    /// <param name="id">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实例配置，不存在时返回 null。</returns>
    public Task<InstanceSpec?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_specs.TryGetValue(id, out InstanceSpec? spec) ? spec : null);

    /// <summary>写入实例配置。</summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task SaveAsync(InstanceSpec spec, CancellationToken cancellationToken = default)
    {
        _specs[spec.Id] = spec;
        return Task.CompletedTask;
    }

    /// <summary>删除实例配置。</summary>
    /// <param name="id">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        _specs.Remove(id);
        return Task.CompletedTask;
    }

    /// <summary>始终返回 false。</summary>
    /// <param name="id">实例标识。</param>
    /// <returns>false。</returns>
    public Task<bool> IsTombstonedAsync(string id) => Task.FromResult(false);
}

/// <summary>假时间源，供测试精确推进时钟。</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;

    public void SetUtcNow(DateTimeOffset time) => _now = time;
}

/// <summary>假宿主内存检测器，供测试模拟任意宿主物理内存容量。</summary>
internal sealed class FakeHostMemoryDetector : IHostMemoryDetector, IHostMemorySampler
{
    public double MemoryGB { get; set; }

    public FakeHostMemoryDetector(double memoryGB = 16.0)
    {
        MemoryGB = memoryGB;
    }

    /// <summary>可用物理内存（GB），未显式设置时按总量的一半模拟。</summary>
    public double AvailableMemoryGB { get; set; } = double.NaN;

    /// <summary>采样次数，用于断言指标确实采到了宿主内存协变量。</summary>
    public int SampleCount { get; private set; }

    public double GetTotalPhysicalMemoryGB() => MemoryGB;

    public HostMemorySample? Sample()
    {
        SampleCount++;
        double available = double.IsNaN(AvailableMemoryGB) ? MemoryGB / 2.0 : AvailableMemoryGB;
        return new HostMemorySample
        {
            TotalPhysicalBytes = (long)(MemoryGB * 1024 * 1024 * 1024),
            AvailablePhysicalBytes = (long)(available * 1024 * 1024 * 1024),
            TotalPhysicalGB = MemoryGB,
            AvailablePhysicalGB = available,
        };
    }
}

/// <summary>adb 客户端替身，供生命周期测试控制调试通路就绪探测的结果。</summary>
internal sealed class FakeAdbClient : IAdbClient
{
    /// <summary>连接时抛出的异常，置空表示连接成功。</summary>
    public Exception? ConnectFailure { get; set; }

    /// <summary>连接回调，入参为目标端口，可用于在探测期间推进假时钟。</summary>
    public Action<int>? OnConnect { get; set; }

    /// <summary>连接尝试次数。</summary>
    public int ConnectCount { get; private set; }

    /// <summary>最近一次连接收到的等待上限，尚未连接时为空。</summary>
    public TimeSpan? LastTimeout { get; private set; }

    /// <summary>
    /// 记录一次连接尝试。
    /// </summary>
    /// <param name="port">adb 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="timeout">本次连接等待上限。</param>
    /// <returns>协议版本。</returns>
    public Task<int> ConnectAsync(int port, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        ConnectCount++;
        LastTimeout = timeout;
        OnConnect?.Invoke(port);
        return ConnectFailure is not null ? throw ConnectFailure : Task.FromResult(41);
    }

    /// <inheritdoc />
    public Task<string> ShellAsync(string command, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

    /// <inheritdoc />
    public Task<bool> IsRootAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    /// <inheritdoc />
    public Task PushAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task PullAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}