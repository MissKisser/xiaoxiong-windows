using System.Diagnostics;
using XBear.Core.Abstractions;
using XBear.Core.Adb;
using XBear.Core.Diagnostics;
using XBear.Core.Instances;
using XBear.Core.Spec;
using XBear.Core.Tests.Adb;

namespace XBear.Core.Tests.Instances;

/// <summary>
/// 启动后的净桌面初始化测试。
/// 刚启动的实例存在锁屏阻断窗口合成、通知栏抢占焦点与桌面选择器叠层三类问题，
/// 启动流程必须在调试通路就绪后把桌面整理成干净可驱动状态；
/// 而该步骤属尽力而为：任何一步失败都不得判启动失败，且整体必须受等待上限约束。
/// </summary>
public sealed class InstanceManagerCleanDesktopTests : IDisposable
{
    private const string ImageRef = "bliss-os-17-x86_64";
    private const string InstanceId = "inst-clean-01";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "xbear-mgr-clean-" + Guid.NewGuid().ToString("N"));

    private readonly string _imagesRoot;
    private readonly string _instancesRoot;
    private readonly InMemoryInstanceRepository _repository = new();
    private readonly FakeQemuArgBuilder _argBuilder = new();
    private readonly FakeQcow2Manager _qcow2 = new();
    private readonly List<string> _shellCommands = new();

    /// <summary>初始化测试环境并登记一个可用实例。</summary>
    public InstanceManagerCleanDesktopTests()
    {
        _imagesRoot = Path.Combine(_root, "images");
        _instancesRoot = Path.Combine(_root, "instances");
        Directory.CreateDirectory(_imagesRoot);
        Directory.CreateDirectory(_instancesRoot);
        File.WriteAllText(Path.Combine(_imagesRoot, ImageRef + ".qcow2"), "base");

        _repository.Add(new InstanceSpec
        {
            Id = InstanceId,
            DisplayName = "净桌面测试实例",
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
    /// 按给定端口装配管理器。端口分配器把首个实例的 adb 端口固定为指定值。
    /// 调试通路探测按真实生产配置启用：净桌面初始化只在启动阶段确认过 adbd 通的实例上发起。
    /// </summary>
    /// <param name="adbPort">分配给该实例的 adb 端口。</param>
    /// <param name="probeTimeout">调试通路探测的等待上限，为零时关闭探测。</param>
    /// <param name="options">净桌面初始化配置，缺省为生产默认配置（开启，5 秒上限）。</param>
    /// <param name="adbClientFactory">adb 客户端工厂，缺省时使用真实客户端连向指定端口。</param>
    /// <returns>被测管理器。</returns>
    private InstanceManager CreateManager(
        int adbPort,
        TimeSpan? probeTimeout = null,
        CleanDesktopOptions? options = null,
        Func<IAdbClient>? adbClientFactory = null)
    {
        var ports = new FakePortAllocator(adbPort);
        return new InstanceManager(
            _repository,
            ports,
            _argBuilder,
            new FakeQemuLauncher(),
            _qcow2,
            _imagesRoot,
            _instancesRoot,
            specValidator: null,
            imageCatalog: null,
            qmpClientFactory: null,
            adbClientFactory: adbClientFactory ?? (() => new AdbClient(adbPort)),
            metricsRecorder: null,
            densityAdvisor: null,
            debugChannelProbeTimeout: probeTimeout ?? TimeSpan.FromMilliseconds(500),
            debugChannelProbeInterval: TimeSpan.FromMilliseconds(100),
            debugChannelAttemptTimeout: TimeSpan.FromMilliseconds(200),
            cleanDesktopOptions: options);
    }

    /// <summary>记录 guest 侧收到的 shell 命令，便于断言命令序列内容与顺序。</summary>
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

    /// <summary>探路就绪后应下发的完整命令序列，即固化序列加末尾一次刷盘。</summary>
    private static IReadOnlyList<string> ExpectedCommands =>
        [.. InstanceManager.CleanDesktopCommands, InstanceManager.CleanDesktopFlushCommand];

    [Fact]
    public async Task 调试通路就绪后按序下发净桌面命令并以sync收尾()
    {
        await using var server = new FakeAdbdServer
        {
            ShellHandler = command => RecordShell(command),
        };

        InstanceManager manager = CreateManager(server.Port);

        await manager.StartAsync(InstanceId);

        Assert.Equal(ExpectedCommands, ShellCommands);
        Assert.Equal(InstanceState.Running, manager.GetState(InstanceId));
    }

    [Fact]
    public async Task 关闭净桌面初始化则不下发任何命令()
    {
        await using var server = new FakeAdbdServer
        {
            ShellHandler = command => RecordShell(command),
        };

        InstanceManager manager = CreateManager(server.Port, options: new CleanDesktopOptions { Enabled = false });

        await manager.StartAsync(InstanceId);

        Assert.Empty(ShellCommands);
        Assert.Equal(InstanceState.Running, manager.GetState(InstanceId));
    }

    /// <summary>等待上限为零与关闭开关等价，同样不下发任何命令。</summary>
    [Fact]
    public async Task 等待上限为零则不下发任何命令()
    {
        await using var server = new FakeAdbdServer
        {
            ShellHandler = command => RecordShell(command),
        };

        InstanceManager manager = CreateManager(
            server.Port,
            options: new CleanDesktopOptions { Timeout = TimeSpan.Zero });

        await manager.StartAsync(InstanceId);

        Assert.Empty(ShellCommands);
        Assert.Equal(InstanceState.Running, manager.GetState(InstanceId));
    }

    /// <summary>
    /// 命令之间彼此独立：某条失败只少解决一个叠层问题，其余命令仍须继续下发，
    /// 且启动结果不受影响。
    /// </summary>
    [Fact]
    public async Task 单条命令失败仍继续下发其余命令且不影响启动()
    {
        string failing = InstanceManager.CleanDesktopCommands[2];

        await using var server = new FakeAdbdServer
        {
            ShellHandler = command =>
                RecordShell(command) with { ExitCode = string.Equals(command, failing, StringComparison.Ordinal) ? 1 : 0 },
        };

        InstanceManager manager = CreateManager(server.Port);

        await manager.StartAsync(InstanceId);

        Assert.Equal(ExpectedCommands, ShellCommands);
        Assert.Equal(InstanceState.Running, manager.GetState(InstanceId));
    }

    /// <summary>
    /// 非零退出码之外，传输层异常同样按单步失败处理：
    /// 后续命令仍须继续下发，且启动结果不受影响。
    /// </summary>
    [Fact]
    public async Task 单条命令抛异常仍继续下发其余命令且不影响启动()
    {
        string failing = InstanceManager.CleanDesktopCommands[1];
        var client = new RecordingAdbClient(failing);
        InstanceManager manager = CreateManager(5555, adbClientFactory: () => client);

        await manager.StartAsync(InstanceId);

        Assert.Equal(ExpectedCommands, client.Commands);
        Assert.Equal(InstanceState.Running, manager.GetState(InstanceId));
    }

    /// <summary>
    /// 调试通路未就绪时不得下发初始化命令：此时 guest 可能仍在启动，连上去只会白等一个等待上限。
    /// 替身在探测期内连接失败、其后即可连上，因此若缺少就绪准入，
    /// 序列仍会连上并下发命令，本断言不会因「连不上」而虚假通过。
    /// </summary>
    [Fact]
    public async Task 调试通路未就绪则不执行净桌面初始化()
    {
        var client = new RecordingAdbClient
        {
            // 探测上限内的全部连接尝试都失败，之后的连接才会成功。
            FailLeadingConnects = int.MaxValue,
        };

        InstanceManager manager = CreateManager(
            5555,
            probeTimeout: TimeSpan.FromMilliseconds(250),
            adbClientFactory: () => client);

        await manager.StartAsync(InstanceId);

        Assert.Empty(client.Commands);
        Assert.Equal(InstanceState.Running, manager.GetState(InstanceId));

        // 探测在上限内至少重试过两次，连接次数停在探测自身的重试上而非序列所需的连接数。
        Assert.InRange(client.ConnectCount, 2, InstanceManager.CleanDesktopCommands.Count);
    }

    /// <summary>guest 侧不回包时整体等待上限必须生效，启动不得被拖住。</summary>
    [Fact]
    public async Task 净桌面初始化超时不阻塞启动()
    {
        await using var server = new FakeAdbdServer { StallShell = true };

        InstanceManager manager = CreateManager(
            server.Port,
            options: new CleanDesktopOptions { Timeout = TimeSpan.FromMilliseconds(200) });

        var elapsed = Stopwatch.StartNew();
        await manager.StartAsync(InstanceId);
        elapsed.Stop();

        Assert.Equal(InstanceState.Running, manager.GetState(InstanceId));

        // 启动只受净桌面初始化上限约束，不会因为 guest 不回包而一直挂着。
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"启动耗时 {elapsed.Elapsed} 超出预期。");
    }
}

/// <summary>
/// 记录连接次数与下发命令的 adb 客户端替身。
/// 可指定「前若干次连接失败」与「指定命令失败」，用于验证净桌面初始化只在调试通路就绪后发起，
/// 且单步失败不影响其余步骤。
/// </summary>
internal sealed class RecordingAdbClient : IAdbClient
{
    private readonly string? _failingCommand;
    private readonly List<string> _commands = new();
    private int _connectCount;

    /// <summary>
    /// 初始化客户端替身。
    /// </summary>
    /// <param name="failingCommand">执行时抛出异常的命令，为空时全部命令均成功。</param>
    public RecordingAdbClient(string? failingCommand = null)
    {
        _failingCommand = failingCommand;
    }

    /// <summary>
    /// 开头若干次连接握手失败的次数，超出后连接一律成功。
    /// 该取值用于模拟「探测期内 adbd 未就绪、之后已可用」：
    /// 探测耗尽后若缺少就绪准入，净桌面初始化仍能连上并下发命令。
    /// </summary>
    public int FailLeadingConnects { get; set; }

    /// <summary>连接尝试次数，净桌面初始化的每条命令都会各自建立一次连接。</summary>
    public int ConnectCount => Volatile.Read(ref _connectCount);

    /// <summary>按下发顺序记录的 shell 命令。</summary>
    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_commands)
            {
                return _commands.ToArray();
            }
        }
    }

    /// <summary>
    /// 记录一次连接尝试，按配置决定连接成功或失败。
    /// </summary>
    /// <param name="port">adb 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="timeout">本次连接等待上限。</param>
    /// <returns>协议版本号。</returns>
    /// <exception cref="XBearException">本次连接落在 <see cref="FailLeadingConnects"/> 范围内时抛出。</exception>
    public Task<int> ConnectAsync(
        int port,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        int attempt = Interlocked.Increment(ref _connectCount);

        return attempt <= FailLeadingConnects
            ? Task.FromException<int>(new XBearException(ErrorCategory.Protocol, "adbd 未响应"))
            : Task.FromResult(0x29);
    }

    /// <summary>
    /// 记录一条命令，命中失败命令时抛出异常。
    /// </summary>
    /// <param name="command">命令与参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>空标准输出。</returns>
    /// <exception cref="XBearException">命令命中 <c>failingCommand</c> 时抛出。</exception>
    public Task<string> ShellAsync(string command, CancellationToken cancellationToken = default)
    {
        lock (_commands)
        {
            _commands.Add(command);
        }

        if (_failingCommand is not null && string.Equals(command, _failingCommand, StringComparison.Ordinal))
        {
            throw new XBearException(ErrorCategory.Protocol, $"命令 {command} 的传输中断。");
        }

        return Task.FromResult(string.Empty);
    }

    /// <summary>模拟实例以 root 身份运行。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>恒为 true。</returns>
    public Task<bool> IsRootAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    /// <summary>本替身不支持文件推送。</summary>
    /// <param name="localPath">宿主文件路径。</param>
    /// <param name="remotePath">实例内路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>不返回的异步任务。</returns>
    public Task PushAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("该替身不支持文件推送。"));

    /// <summary>本替身不支持文件拉取。</summary>
    /// <param name="remotePath">实例内路径。</param>
    /// <param name="localPath">宿主文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>不返回的异步任务。</returns>
    public Task PullAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("该替身不支持文件拉取。"));

    /// <summary>本替身无需释放任何资源。</summary>
    /// <returns>表示释放完成的异步结果。</returns>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
