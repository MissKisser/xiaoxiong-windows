using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.Core.Tests.Instances;

/// <summary>
/// 实例管理器与指标采集、多开密度评估的集成测试。
/// </summary>
public sealed class InstanceManagerIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "xbear-mgr-integ-" + Guid.NewGuid().ToString("N"));

    private readonly InMemoryInstanceRepository _repository = new();
    private readonly FakePortAllocator _ports = new();
    private readonly FakeQemuArgBuilder _argBuilder = new();
    private readonly FakeQemuLauncher _launcher = new();
    private readonly FakeQcow2Manager _qcow2 = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly FakeHostMemoryDetector _memoryDetector = new(32.0);
    private readonly MetricsRecorder _metricsRecorder;
    private readonly InstanceDensityAdvisor _densityAdvisor;
    private readonly InstanceManager _manager;

    /// <summary>初始化集成测试环境。</summary>
    public InstanceManagerIntegrationTests()
    {
        string imagesRoot = Path.Combine(_root, "images");
        string instancesRoot = Path.Combine(_root, "instances");
        Directory.CreateDirectory(imagesRoot);
        Directory.CreateDirectory(instancesRoot);
        File.WriteAllText(Path.Combine(imagesRoot, "bliss-os-17-x86_64.qcow2"), "base");

        _metricsRecorder = new MetricsRecorder(instancesRoot, _clock, TimeSpan.Zero);
        _densityAdvisor = new InstanceDensityAdvisor(_memoryDetector);

        _manager = new InstanceManager(
            _repository,
            _ports,
            _argBuilder,
            _launcher,
            _qcow2,
            imagesRoot,
            instancesRoot,
            specValidator: null,
            qmpClientFactory: null,
            adbClientFactory: null,
            metricsRecorder: _metricsRecorder,
            densityAdvisor: _densityAdvisor);

        _repository.Add(new InstanceSpec
        {
            Id = "inst-integ-01",
            DisplayName = "集成测试实例",
            ImageRef = "bliss-os-17-x86_64",
        });
    }

    /// <summary>清理测试目录。</summary>
    public void Dispose()
    {
        _metricsRecorder.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task 启动并停止实例_按阶段记录耗时与采样并在停止时落盘()
    {
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 8, 14, 0, 0, TimeSpan.Zero));

        // 启动实例
        await _manager.StartAsync("inst-integ-01");

        Assert.Equal(InstanceState.Running, _manager.GetState("inst-integ-01"));
        Assert.Single(_launcher.Started);

        FakeQemuProcessHandle handle = _launcher.Started[0];
        handle.FakeWorkingSet = 256 * 1024 * 1024; // 256 MB

        _clock.Advance(TimeSpan.FromSeconds(5.0));
        _metricsRecorder.SampleMemory("inst-integ-01");

        // 停止实例
        await _manager.StopAsync("inst-integ-01");

        Assert.Equal(InstanceState.Stopped, _manager.GetState("inst-integ-01"));

        // 验证 metrics.json 已经落盘
        string metricsPath = _manager.GetMetricsFilePath("inst-integ-01");
        Assert.True(File.Exists(metricsPath));

        string content = await File.ReadAllTextAsync(metricsPath);
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.Equal("inst-integ-01", root.GetProperty("instanceId").GetString());
        Assert.True(root.GetProperty("startup").GetProperty("processSpawned").GetBoolean());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("summary").ValueKind);
    }

    /// <summary>
    /// 回归测试：本集成场景未配置调试通路就绪探测，
    /// 此时不得把「进程刚拉起」当成冷启动完成写入就绪结论。
    /// </summary>
    [Fact]
    public async Task 未配置就绪探测时落盘文档不含冷启动结论()
    {
        await _manager.StartAsync("inst-integ-01");
        string metricsPath = await _metricsRecorder.StopAndPersistAsync("inst-integ-01");

        string content = await File.ReadAllTextAsync(metricsPath);
        using var doc = JsonDocument.Parse(content);
        var startup = doc.RootElement.GetProperty("startup");

        Assert.True(startup.GetProperty("processSpawned").GetBoolean());
        Assert.False(startup.GetProperty("debugChannelReady").GetBoolean());
        Assert.False(startup.TryGetProperty("readyAt", out _));
        Assert.False(startup.TryGetProperty("coldStartSeconds", out _));
    }

    [Fact]
    public void 密度建议与劣化评估API_可通过InstanceManager正常调用()
    {
        // 32GB 宿主物理内存
        DensityAdvice advice = _manager.GetDensityAdvice(currentCount: 3);
        Assert.Equal(32.0, advice.HostMemoryGB);
        Assert.Equal("主流", advice.Tier);
        Assert.Equal(3, advice.RecommendedLimit);
        Assert.True(advice.IsExceeded);

        RegressionEvaluation reg = _manager.EvaluateRegression(100, 125);
        Assert.True(reg.IsAcceptable);
        Assert.Equal(25.0, reg.DegradationPercent);
    }
}
