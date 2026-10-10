using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using XBear.Core.Abstractions;
using XBear.Core.Adb;
using XBear.Core.Instances;
using XBear.Core.Spec;
using XBear.Core.Tests.Adb;

namespace XBear.Core.Tests.Instances;

/// <summary>
/// 停止前的数据刷盘测试。
/// guest 内刚写入的文件若未刷盘就随 QEMU 退出，会退化为 0 字节，
/// 因此停止流程必须在 QMP quit 之前下发一次 sync；而刷盘是尽力而为，任何失败都不能挡住停止。
/// </summary>
public sealed class InstanceManagerFlushTests : IDisposable
{
    private const string ImageRef = "bliss-os-17-x86_64";
    private const string InstanceId = "inst-flush-01";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "xbear-mgr-flush-" + Guid.NewGuid().ToString("N"));

    private readonly string _imagesRoot;
    private readonly string _instancesRoot;
    private readonly InMemoryInstanceRepository _repository = new();
    private readonly FakeQemuArgBuilder _argBuilder = new();
    private readonly FakeQcow2Manager _qcow2 = new();
    private readonly List<string> _shellCommands = new();

    /// <summary>初始化测试环境并登记一个可用实例。</summary>
    public InstanceManagerFlushTests()
    {
        _imagesRoot = Path.Combine(_root, "images");
        _instancesRoot = Path.Combine(_root, "instances");
        Directory.CreateDirectory(_imagesRoot);
        Directory.CreateDirectory(_instancesRoot);
        File.WriteAllText(Path.Combine(_imagesRoot, ImageRef + ".qcow2"), "base");

        _repository.Add(new InstanceSpec
        {
            Id = InstanceId,
            DisplayName = "刷盘测试实例",
            ImageRef = ImageRef,
        });
    }

    /// <summary>清理测试目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// 取一个当前没有进程监听的宿主端口，用于模拟 adbd 未就绪。
    /// </summary>
    /// <returns>空闲端口号。</returns>
    private static int ReserveClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// 按给定端口装配管理器。端口分配器把首个实例的 adb 端口固定为指定值。
    /// 调试通路探测按真实生产配置启用：刷盘只在启动阶段确认过 adbd 通的实例上发起。
    /// 净桌面初始化在本套件中关闭，使 guest 侧收到的 shell 命令只可能来自停止前刷盘，
    /// 从而让「是否下发过 sync」的断言不受启动序列干扰。
    /// </summary>
    /// <param name="adbPort">分配给该实例的 adb 端口。</param>
    /// <param name="flushTimeout">刷盘等待上限，为 <see cref="TimeSpan.Zero"/> 时不刷盘。</param>
    /// <param name="probeTimeout">
    /// 调试通路探测的等待上限，为 <see cref="TimeSpan.Zero"/> 时关闭探测，
    /// 此时该实例不会有「调试通路已就绪」的结论，停止时也就不会发起刷盘。
    /// </param>
    /// <param name="flushProbe">停止时刻探测刷盘是否已下发的回调。</param>
    /// <param name="launcher">记录停止时刻刷盘先后顺序的拉起器。</param>
    /// <returns>被测管理器。</returns>
    private InstanceManager CreateManager(
        int adbPort,
        TimeSpan? flushTimeout = null,
        TimeSpan? probeTimeout = null,
        Func<bool>? flushProbe = null,
        IQemuLauncher? launcher = null)
    {
        var ports = new FakePortAllocator(adbPort);
        return new InstanceManager(
            _repository,
            ports,
            _argBuilder,
            launcher ?? new FlushOrderLauncher(flushProbe ?? (static () => false)),
            _qcow2,
            _imagesRoot,
            _instancesRoot,
            specValidator: null,
            imageCatalog: null,
            qmpClientFactory: null,
            adbClientFactory: () => new AdbClient(adbPort),
            metricsRecorder: null,
            densityAdvisor: null,
            debugChannelProbeTimeout: probeTimeout ?? TimeSpan.FromMilliseconds(500),
            debugChannelProbeInterval: TimeSpan.FromMilliseconds(100),
            debugChannelAttemptTimeout: TimeSpan.FromMilliseconds(200),
            shutdownFlushTimeout: flushTimeout,
            cleanDesktopOptions: new CleanDesktopOptions { Enabled = false });
    }

    /// <summary>记录 guest 侧收到的 shell 命令，便于断言刷盘命令确实下发。</summary>
    /// <param name="command">客户端下发的命令。</param>
    /// <returns>固定的成功应答。</returns>
    private ShellResponse RecordShell(string command)
    {
        lock (_shellCommands)
        {
            _shellCommands.Add(command);
        }

        return new ShellResponse(string.Empty, 0);
    }

    /// <summary>读取 guest 侧收到的 shell 命令快照。</summary>
    /// <returns>命令序列，按下发顺序排列。</returns>
    private IReadOnlyList<string> ShellCommands
    {
        get
        {
            lock (_shellCommands)
            {
                return _shellCommands.ToArray();
            }
        }
    }

    /// <summary>判定刷盘命令是否已经下发到 guest。</summary>
    /// <returns>已下发返回 true。</returns>
    private bool HasFlushCommand()
    {
        lock (_shellCommands)
        {
            return _shellCommands.Contains(InstanceManager.ShutdownFlushCommand);
        }
    }

    [Fact]
    public async Task 停止时_adb通路可用则向guest下发sync()
    {
        await using var server = new FakeAdbdServer
        {
            ShellHandler = command => RecordShell(command),
        };

        InstanceManager manager = CreateManager(server.Port);

        await manager.StartAsync(InstanceId);
        await manager.StopAsync(InstanceId);

        Assert.Contains(InstanceManager.ShutdownFlushCommand, ShellCommands);
        Assert.Equal(InstanceState.Stopped, manager.GetState(InstanceId));
    }

    [Fact]
    public async Task 停止时_sync先于Qemu进程终止下发()
    {
        await using var server = new FakeAdbdServer
        {
            ShellHandler = command => RecordShell(command),
        };

        var launcher = new FlushOrderLauncher(() =>
        {
            lock (_shellCommands)
            {
                return _shellCommands.Contains(InstanceManager.ShutdownFlushCommand);
            }
        });

        InstanceManager manager = CreateManager(server.Port, launcher: launcher);

        await manager.StartAsync(InstanceId);
        await manager.StopAsync(InstanceId);

        Assert.True(launcher.Handle.FlushObservedBeforeStop);
        Assert.Equal(1, launcher.Handle.StopCount);
    }

    [Fact]
    public async Task 停止时_adb通路不可用则跳过刷盘并正常停止()
    {
        var launcher = new FlushOrderLauncher(HasFlushCommand);
        InstanceManager manager = CreateManager(ReserveClosedPort(), launcher: launcher);

        await manager.StartAsync(InstanceId);
        await manager.StopAsync(InstanceId);

        // 刷盘被跳过，但停止必须照常完成：状态收敛、进程被终止。
        Assert.Equal(InstanceState.Stopped, manager.GetState(InstanceId));
        Assert.Equal(1, launcher.Handle.StopCount);
        Assert.False(launcher.Handle.FlushObservedBeforeStop);
        Assert.Empty(ShellCommands);
    }

    [Fact]
    public async Task 停止时_刷盘超时不阻塞停止()
    {
        await using var server = new FakeAdbdServer { StallShell = true };
        var launcher = new FlushOrderLauncher(HasFlushCommand);
        InstanceManager manager = CreateManager(
            server.Port,
            flushTimeout: TimeSpan.FromMilliseconds(200),
            launcher: launcher);

        await manager.StartAsync(InstanceId);

        var elapsed = Stopwatch.StartNew();
        await manager.StopAsync(InstanceId);
        elapsed.Stop();

        Assert.Equal(InstanceState.Stopped, manager.GetState(InstanceId));
        Assert.Equal(1, launcher.Handle.StopCount);

        // 停止只受刷盘上限约束，不会因为 guest 不回包而一直挂着。
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"停止耗时 {elapsed.Elapsed} 超出预期。");
    }

    [Fact]
    public async Task 停止时_未配置调试通路探测则不发起刷盘()
    {
        await using var server = new FakeAdbdServer
        {
            ShellHandler = command => RecordShell(command),
        };

        // 未配置探测就没有「调试通路已就绪」的结论，据此跳过刷盘，不去连可能并无意义的 adbd。
        InstanceManager manager = CreateManager(server.Port, probeTimeout: TimeSpan.Zero);

        await manager.StartAsync(InstanceId);
        await manager.StopAsync(InstanceId);

        Assert.Empty(ShellCommands);
        Assert.Equal(InstanceState.Stopped, manager.GetState(InstanceId));
    }

    [Fact]
    public async Task 停止时_sync命令失败不影响停止结果()
    {
        await using var server = new FakeAdbdServer
        {
            ShellHandler = command =>
                RecordShell(command) with { ExitCode = 1 },
        };

        InstanceManager manager = CreateManager(server.Port);

        await manager.StartAsync(InstanceId);
        await manager.StopAsync(InstanceId);

        Assert.Contains(InstanceManager.ShutdownFlushCommand, ShellCommands);
        Assert.Equal(InstanceState.Stopped, manager.GetState(InstanceId));
    }

    [Fact]
    public async Task 停止时_未配置刷盘则不下发sync()
    {
        await using var server = new FakeAdbdServer
        {
            ShellHandler = command => RecordShell(command),
        };

        InstanceManager manager = CreateManager(server.Port, flushTimeout: TimeSpan.Zero);

        await manager.StartAsync(InstanceId);
        await manager.StopAsync(InstanceId);

        Assert.Empty(ShellCommands);
        Assert.Equal(InstanceState.Stopped, manager.GetState(InstanceId));
    }
}

/// <summary>
/// 记录停止时刻刷盘命令是否已经下发的进程句柄替身。
/// 刷盘必须在进程终止之前完成，QEMU 一旦退出 guest 就没有机会再写回页缓存。
/// </summary>
internal sealed class FlushOrderProcessHandle : QemuProcessHandle
{
    private readonly Func<bool> _flushObserved;

    /// <summary>
    /// 初始化句柄替身。
    /// </summary>
    /// <param name="flushObserved">探测刷盘命令是否已下发的回调。</param>
    public FlushOrderProcessHandle(Func<bool> flushObserved)
    {
        _flushObserved = flushObserved;
    }

    /// <summary>宿主进程标识。</summary>
    public int ProcessId => 9999;

    /// <summary>启动命令行。</summary>
    public string CommandLine => "qemu-system-x86_64 -machine q35";

    /// <summary>日志路径。</summary>
    public string LogFilePath => Path.Combine(Path.GetTempPath(), "xbear-flush-order.log");

    /// <summary>进程是否已退出。</summary>
    public bool HasExited => StopCount > 0;

    /// <summary>退出码。</summary>
    public int ExitCode => 0;

    /// <summary>停止调用次数。</summary>
    public int StopCount { get; private set; }

    /// <summary>进入终止流程时是否已经观察到刷盘命令下发。</summary>
    public bool FlushObservedBeforeStop { get; private set; }

    /// <summary>
    /// 记录一次终止调用，并冻结该时刻的刷盘观测结果。
    /// </summary>
    /// <param name="timeout">等待超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        StopCount++;
        FlushObservedBeforeStop = _flushObserved();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>返回可记录刷盘先后顺序的进程句柄的拉起器替身。</summary>
internal sealed class FlushOrderLauncher : IQemuLauncher
{
    private readonly Func<bool> _flushObserved;

    /// <summary>
    /// 初始化拉起器替身。
    /// </summary>
    /// <param name="flushObserved">探测刷盘命令是否已下发的回调。</param>
    public FlushOrderLauncher(Func<bool> flushObserved)
    {
        _flushObserved = flushObserved;
        Handle = new FlushOrderProcessHandle(flushObserved);
    }

    /// <summary>拉起返回的进程句柄。</summary>
    public FlushOrderProcessHandle Handle { get; }

    /// <summary>
    /// 返回同一个进程句柄。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="image">镜像清单。</param>
    /// <param name="diskPath">overlay 路径。</param>
    /// <param name="ports">端口组。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>进程句柄。</returns>
    public Task<QemuProcessHandle> StartAsync(
        InstanceSpec spec,
        ImageSpec? image,
        string diskPath,
        AllocatedPorts ports,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<QemuProcessHandle>(Handle);
}