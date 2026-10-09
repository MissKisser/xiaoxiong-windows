using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.Core.Tests.Instances;

/// <summary>
/// 启动序列的分阶段指标测试：进程拉起与调试通路就绪是两个量级相差一个数量级以上的阶段，
/// 冷启动耗时必须以 adb 通路可连接为终点，未确认就绪时不得产出任何就绪结论。
/// </summary>
public sealed class InstanceManagerStartupMetricTests : IDisposable
{
    private const string ImageRef = "bliss-os-17-x86_64";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "xbear-mgr-metric-" + Guid.NewGuid().ToString("N"));

    private readonly InMemoryInstanceRepository _repository = new();
    private readonly FakePortAllocator _ports = new();
    private readonly FakeQemuArgBuilder _argBuilder = new();
    private readonly FakeQemuLauncher _launcher = new();
    private readonly FakeQcow2Manager _qcow2 = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly FakeHostMemoryDetector _memory = new(15.7) { AvailableMemoryGB = 2.85 };
    private readonly FakeAdbClient _adb = new();
    private readonly MetricsRecorder _metrics;
    private readonly string _instancesRoot;
    private InstanceManager _manager = default!;

    /// <summary>初始化测试环境并登记一个可用实例。</summary>
    public InstanceManagerStartupMetricTests()
    {
        string imagesRoot = Path.Combine(_root, "images");
        _instancesRoot = Path.Combine(_root, "instances");
        Directory.CreateDirectory(imagesRoot);
        Directory.CreateDirectory(_instancesRoot);
        File.WriteAllText(Path.Combine(imagesRoot, ImageRef + ".qcow2"), "base");

        _metrics = new MetricsRecorder(_instancesRoot, _clock, TimeSpan.Zero, hostMemorySampler: _memory);
        _repository.Add(NewSpec("inst-01"));
    }

    /// <summary>清理测试目录。</summary>
    public void Dispose()
    {
        _metrics.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static InstanceSpec NewSpec(string id) => new()
    {
        Id = id,
        DisplayName = "测试实例",
        ImageRef = ImageRef,
    };

    private InstanceManager CreateManager(
        IImageCatalog? imageCatalog = null,
        TimeSpan? probeTimeout = null,
        TimeSpan? probeInterval = null,
        TimeSpan? attemptTimeout = null)
        => _manager = new(
            _repository,
            _ports,
            _argBuilder,
            _launcher,
            _qcow2,
            Path.Combine(_root, "images"),
            _instancesRoot,
            specValidator: null,
            imageCatalog: imageCatalog,
            qmpClientFactory: null,
            adbClientFactory: () => _adb,
            metricsRecorder: _metrics,
            densityAdvisor: null,
            debugChannelProbeTimeout: probeTimeout,
            debugChannelProbeInterval: probeInterval,
            debugChannelAttemptTimeout: attemptTimeout);

    private InstanceStartupMetric StartupOf(string instanceId)
    {
        InstanceMetricsSnapshot? snapshot = _metrics.GetSnapshot(instanceId);
        Assert.NotNull(snapshot?.Startup);
        return snapshot!.Startup!;
    }

    [Fact]
    public async Task 进程拉起即记录耗时且不冒充冷启动()
    {
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 8, 16, 0, 0, TimeSpan.Zero));
        _launcher.OnStart = () => _clock.Advance(TimeSpan.FromSeconds(2.08));

        await CreateManager().StartAsync("inst-01");

        InstanceStartupMetric startup = StartupOf("inst-01");
        Assert.True(startup.ProcessSpawned);
        Assert.Equal(2.08, startup.ProcessSpawnSeconds!.Value, 3);
        Assert.False(startup.DebugChannelReady);
        Assert.Null(startup.ColdStartSeconds);
    }

    /// <summary>冷启动耗时的终点是 adb 通路可连接，而不是 QEMU 进程被创建出来。</summary>
    [Fact]
    public async Task 调试通路就绪后记录冷启动耗时()
    {
        _adb.OnConnect = _ => _clock.Advance(TimeSpan.FromSeconds(62.4));

        await CreateManager(probeTimeout: TimeSpan.FromSeconds(5)).StartAsync("inst-01");

        InstanceStartupMetric startup = StartupOf("inst-01");
        Assert.True(startup.DebugChannelReady);
        Assert.NotNull(startup.ReadyAt);
        Assert.Equal(62.4, startup.ColdStartSeconds!.Value, 3);
        Assert.Equal(62400.0, startup.ColdStartMs!.Value, 1);
    }

    /// <summary>单次连接尝试必须带等待上限，否则 adbd 未就绪时探测本身会挂死。</summary>
    [Fact]
    public async Task 每次连接尝试都带等待上限()
    {
        await CreateManager(
                probeTimeout: TimeSpan.FromSeconds(5),
                attemptTimeout: TimeSpan.FromMilliseconds(200))
            .StartAsync("inst-01");

        Assert.Equal(1, _adb.ConnectCount);
        Assert.Equal(TimeSpan.FromMilliseconds(200), _adb.LastTimeout);
    }

    /// <summary>
    /// 回归测试：探测超时不得被写成「启动成功」，
    /// 就绪时间点与冷启动耗时必须如实留空并给出原因。
    /// </summary>
    [Fact]
    public async Task 探测超时就绪时间点留空并记录原因()
    {
        _adb.ConnectFailure = new XBearException(ErrorCategory.Protocol, "adbd 未响应");

        await CreateManager(
                probeTimeout: TimeSpan.FromMilliseconds(250),
                probeInterval: TimeSpan.FromMilliseconds(40))
            .StartAsync("inst-01");

        InstanceStartupMetric startup = StartupOf("inst-01");
        Assert.True(startup.ProcessSpawned);
        Assert.False(startup.DebugChannelReady);
        Assert.Null(startup.ReadyAt);
        Assert.Null(startup.ColdStartSeconds);
        Assert.Contains("未就绪", startup.Note, StringComparison.Ordinal);
        Assert.True(_adb.ConnectCount >= 2, $"探测应在超时前重试过，实际尝试 {_adb.ConnectCount} 次。");

        // 调试通路未就绪不代表实例没起来，进程已经在跑就不该被拆掉。
        Assert.Equal(InstanceState.Running, _manager.GetState("inst-01"));
    }

    /// <summary>未启用探测时不得发起任何连接，同样如实留空就绪时间点。</summary>
    [Fact]
    public async Task 未启用探测时不发起连接且就绪时间点留空()
    {
        await CreateManager().StartAsync("inst-01");

        Assert.Equal(0, _adb.ConnectCount);
        InstanceStartupMetric startup = StartupOf("inst-01");
        Assert.True(startup.ProcessSpawned);
        Assert.False(startup.DebugChannelReady);
        Assert.Null(startup.ReadyAt);
        Assert.Contains("未配置调试通路就绪探测", startup.Note, StringComparison.Ordinal);
    }

    /// <summary>多开数据的可信度依赖宿主空闲内存，因此每次启动都必须采样一次。</summary>
    [Fact]
    public async Task 启动时记录宿主空闲内存协变量()
    {
        await CreateManager().StartAsync("inst-01");

        InstanceMetricsSnapshot? snapshot = _metrics.GetSnapshot("inst-01");
        Assert.NotNull(snapshot?.HostMemory);
        Assert.Equal(1, _memory.SampleCount);
        Assert.Equal(2.85, snapshot!.HostMemory!.AvailablePhysicalGB, 3);
    }

    /// <summary>
    /// 回归测试：拉起器必须拿到镜像清单，
    /// 否则镜像保真度通道永远不会被评估、保真度证据与实际启动参数会长期脱节。
    /// </summary>
    [Fact]
    public async Task 镜像清单按引用同时接线到参数生成器与拉起器()
    {
        var image = new ImageSpec
        {
            Id = ImageRef,
            Verified = new ImageVerification { Fidelity = new FidelitySet { P2SystemWrite = VerificationState.Pass } },
        };

        await CreateManager(imageCatalog: new ImageCatalog(new[] { image })).StartAsync("inst-01");

        Assert.Same(image, _argBuilder.LastImage);
        Assert.Same(image, _launcher.LastImage);
    }

    [Fact]
    public async Task 镜像引用未登记时按镜像未知处理()
    {
        var other = new ImageSpec { Id = "another-image" };

        await CreateManager(imageCatalog: new ImageCatalog(new[] { other })).StartAsync("inst-01");

        Assert.Null(_argBuilder.LastImage);
        Assert.Null(_launcher.LastImage);
    }

    [Fact]
    public async Task 未装配镜像目录时按镜像未知处理()
    {
        await CreateManager().StartAsync("inst-01");

        Assert.Null(_argBuilder.LastImage);
        Assert.Null(_launcher.LastImage);
    }

    /// <summary>启动失败时不得留下任何就绪结论，同时端口必须回收。</summary>
    [Fact]
    public async Task 启动失败时不产出就绪结论且端口回收()
    {
        _launcher.StartFailure = new XBearException(ErrorCategory.Process, "QEMU 退出码 1");

        await Assert.ThrowsAsync<XBearException>(() => CreateManager().StartAsync("inst-01"));

        InstanceStartupMetric startup = StartupOf("inst-01");
        Assert.False(startup.ProcessSpawned);
        Assert.False(startup.DebugChannelReady);
        Assert.Null(startup.ReadyAt);
        Assert.Null(startup.ColdStartSeconds);
        Assert.Empty(_ports.Held);
    }
}