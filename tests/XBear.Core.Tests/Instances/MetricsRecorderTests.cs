using System.Text.Json;
using XBear.Core.Diagnostics;

namespace XBear.Core.Tests.Instances;

/// <summary>
/// 指标采集器测试，覆盖分阶段启动耗时、宿主内存协变量、内存采样序列与 JSON 落盘。
/// </summary>
public sealed class MetricsRecorderTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "xbear-metrics-test-" + Guid.NewGuid().ToString("N"));

    private readonly FakeTimeProvider _clock = new();

    /// <summary>初始化测试目录。</summary>
    public MetricsRecorderTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    /// <summary>清理测试目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private MetricsRecorder CreateRecorder(
        TimeSpan? samplingInterval = null,
        IHostMemorySampler? hostMemorySampler = null)
        => new(_tempDirectory, _clock, samplingInterval, hostMemorySampler: hostMemorySampler);

    [Fact]
    public void 进程拉起耗时单独记录且不等于冷启动耗时()
    {
        using var recorder = CreateRecorder();
        var handle = new FakeQemuProcessHandle();

        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));
        recorder.OnStarting("inst-01");

        _clock.Advance(TimeSpan.FromSeconds(2.08));
        recorder.OnProcessSpawned("inst-01", handle);

        InstanceMetricsSnapshot? snapshot = recorder.GetSnapshot("inst-01");
        Assert.NotNull(snapshot);
        Assert.Equal("inst-01", snapshot.InstanceId);
        Assert.NotNull(snapshot.Startup);
        Assert.True(snapshot.Startup.ProcessSpawned);
        Assert.Equal(2.08, snapshot.Startup.ProcessSpawnSeconds!.Value, 3);
        Assert.Equal(2080.0, snapshot.Startup.ProcessSpawnMs!.Value, 1);
    }

    /// <summary>
    /// 回归测试：进程拉起耗时只有秒级量级，把它写成「启动总耗时」会得出与真实冷启动无关的结论。
    /// 因此在调试通路确认就绪之前，冷启动字段必须保持为空。
    /// </summary>
    [Fact]
    public void 调试通路确认前不产出任何冷启动结论()
    {
        using var recorder = CreateRecorder();
        var handle = new FakeQemuProcessHandle();

        recorder.OnStarting("inst-01");
        _clock.Advance(TimeSpan.FromSeconds(2.08));
        recorder.OnProcessSpawned("inst-01", handle);

        InstanceMetricsSnapshot? snapshot = recorder.GetSnapshot("inst-01");
        Assert.NotNull(snapshot?.Startup);
        Assert.False(snapshot!.Startup!.DebugChannelReady);
        Assert.Null(snapshot.Startup.ReadyAt);
        Assert.Null(snapshot.Startup.ColdStartSeconds);
        Assert.Null(snapshot.Startup.ColdStartMs);
    }

    [Fact]
    public void 调试通路就绪后记录自发起算的冷启动耗时()
    {
        using var recorder = CreateRecorder();
        var handle = new FakeQemuProcessHandle();

        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));
        recorder.OnStarting("inst-01");

        _clock.Advance(TimeSpan.FromSeconds(2.1));
        recorder.OnProcessSpawned("inst-01", handle);

        _clock.Advance(TimeSpan.FromSeconds(60));
        recorder.OnDebugChannelReady("inst-01");

        InstanceMetricsSnapshot? snapshot = recorder.GetSnapshot("inst-01");
        Assert.NotNull(snapshot?.Startup);
        Assert.True(snapshot!.Startup!.DebugChannelReady);
        Assert.NotNull(snapshot.Startup.ReadyAt);
        Assert.Equal(62.1, snapshot.Startup.ColdStartSeconds!.Value, 3);
        Assert.Equal(62100.0, snapshot.Startup.ColdStartMs!.Value, 1);
        Assert.Equal(2.1, snapshot.Startup.ProcessSpawnSeconds!.Value, 3);
    }

    /// <summary>就绪探测超时时如实留空就绪时间点，并记录原因，不以任何形式补一个「大致成功」。</summary>
    [Fact]
    public void 就绪探测超时时就绪时间点留空且记录原因()
    {
        using var recorder = CreateRecorder();
        var handle = new FakeQemuProcessHandle();

        recorder.OnStarting("inst-01");
        recorder.OnProcessSpawned("inst-01", handle);
        recorder.OnDebugChannelUnavailable("inst-01", "调试通路在 120 秒内未就绪，共尝试 60 次。");

        InstanceMetricsSnapshot? snapshot = recorder.GetSnapshot("inst-01");
        Assert.NotNull(snapshot?.Startup);
        Assert.False(snapshot!.Startup!.DebugChannelReady);
        Assert.Null(snapshot.Startup.ReadyAt);
        Assert.Null(snapshot.Startup.ColdStartSeconds);
        Assert.Contains("未就绪", snapshot.Startup.Note, StringComparison.Ordinal);
    }

    /// <summary>已确认就绪之后再上报未就绪不得覆盖已记录的冷启动耗时。</summary>
    [Fact]
    public void 就绪后到达的未就绪上报不覆盖已记录的冷启动耗时()
    {
        using var recorder = CreateRecorder();
        var handle = new FakeQemuProcessHandle();

        recorder.OnStarting("inst-01");
        recorder.OnProcessSpawned("inst-01", handle);
        _clock.Advance(TimeSpan.FromSeconds(45));
        recorder.OnDebugChannelReady("inst-01");

        recorder.OnDebugChannelUnavailable("inst-01", "迟到的失败上报");

        InstanceMetricsSnapshot? snapshot = recorder.GetSnapshot("inst-01");
        Assert.NotNull(snapshot?.Startup);
        Assert.True(snapshot!.Startup!.DebugChannelReady);
        Assert.Equal(45.0, snapshot.Startup.ColdStartSeconds!.Value, 3);
    }

    [Fact]
    public void 启动失败_记录失败且不伪造就绪数据()
    {
        using var recorder = CreateRecorder();

        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));
        recorder.OnStarting("inst-01");

        _clock.Advance(TimeSpan.FromSeconds(2.0));
        recorder.OnStartupFailed("inst-01", "QEMU 退出码 1");

        InstanceMetricsSnapshot? snapshot = recorder.GetSnapshot("inst-01");
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.Startup);
        Assert.False(snapshot.Startup.ProcessSpawned);
        Assert.False(snapshot.Startup.DebugChannelReady);
        Assert.Null(snapshot.Startup.ProcessSpawnedAt);
        Assert.Null(snapshot.Startup.ReadyAt);
        Assert.Null(snapshot.Startup.ColdStartSeconds);
        Assert.Contains("退出码 1", snapshot.Startup.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// 回归测试：宿主空闲内存是多开冷启动数据是否可信的前提，
    /// 缺少该协变量时指标无法判断自身结论的有效性。
    /// </summary>
    [Fact]
    public void 发起启动时记录宿主物理内存协变量()
    {
        var memory = new FakeHostMemoryDetector(15.7) { AvailableMemoryGB = 2.85 };
        using var recorder = CreateRecorder(hostMemorySampler: memory);

        recorder.OnStarting("inst-01");

        InstanceMetricsSnapshot? snapshot = recorder.GetSnapshot("inst-01");
        Assert.NotNull(snapshot?.HostMemory);
        Assert.Equal(1, memory.SampleCount);
        Assert.Equal(15.7, snapshot!.HostMemory!.TotalPhysicalGB, 3);
        Assert.Equal(2.85, snapshot.HostMemory.AvailablePhysicalGB, 3);
        Assert.Equal((long)(2.85 * 1024 * 1024 * 1024), snapshot.HostMemory.AvailablePhysicalBytes);
        Assert.NotEqual(default, snapshot.HostMemory.SampledAt);
    }

    [Fact]
    public void 未装配宿主内存采样器时协变量字段整体省略()
    {
        using var recorder = CreateRecorder();

        recorder.OnStarting("inst-01");

        Assert.Null(recorder.GetSnapshot("inst-01")!.HostMemory);
    }

    [Fact]
    public void 内存采样_手动采样记录真实工作集与时间戳序列()
    {
        using var recorder = CreateRecorder(TimeSpan.Zero);
        var handle = new FakeQemuProcessHandle { FakeWorkingSet = 100 * 1024 * 1024 };

        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));
        recorder.OnStarting("inst-01");
        recorder.OnProcessSpawned("inst-01", handle);

        _clock.Advance(TimeSpan.FromSeconds(1));
        handle.FakeWorkingSet = 200 * 1024 * 1024;
        recorder.SampleMemory("inst-01");

        _clock.Advance(TimeSpan.FromSeconds(1));
        handle.FakeWorkingSet = 350 * 1024 * 1024;
        recorder.SampleMemory("inst-01");

        InstanceMetricsSnapshot? snapshot = recorder.GetSnapshot("inst-01");
        Assert.NotNull(snapshot);
        Assert.NotEmpty(snapshot.MemorySamples);
        Assert.Contains(snapshot.MemorySamples, s => s.WorkingSetBytes == 200 * 1024 * 1024);
        Assert.Contains(snapshot.MemorySamples, s => s.WorkingSetBytes == 350 * 1024 * 1024);
    }

    [Fact]
    public async Task 停止并落盘_生成合规metrics_json且摘要正确()
    {
        using var recorder = CreateRecorder(TimeSpan.Zero);
        var handle = new FakeQemuProcessHandle { FakeWorkingSet = 512 * 1024 * 1024 };

        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        recorder.OnStarting("inst-01");
        _clock.Advance(TimeSpan.FromSeconds(2.08));
        recorder.OnProcessSpawned("inst-01", handle);
        _clock.Advance(TimeSpan.FromSeconds(60));
        recorder.OnDebugChannelReady("inst-01");

        handle.FakeWorkingSet = 1024 * 1024 * 1024L;
        recorder.SampleMemory("inst-01");

        string filePath = await recorder.StopAndPersistAsync("inst-01");

        Assert.True(File.Exists(filePath));
        Assert.EndsWith("metrics.json", filePath);

        string json = await File.ReadAllTextAsync(filePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("inst-01", root.GetProperty("instanceId").GetString());
        var startup = root.GetProperty("startup");
        Assert.True(startup.GetProperty("processSpawned").GetBoolean());
        Assert.True(startup.GetProperty("debugChannelReady").GetBoolean());
        Assert.Equal(62.08, startup.GetProperty("coldStartSeconds").GetDouble(), 1);
        Assert.Equal(2.08, startup.GetProperty("processSpawnSeconds").GetDouble(), 1);

        var summary = root.GetProperty("summary");
        Assert.True(summary.GetProperty("sampleCount").GetInt32() >= 1);
        Assert.True(summary.GetProperty("maxWorkingSetBytes").GetInt64() >= 512 * 1024 * 1024);
    }

    /// <summary>
    /// 回归测试：落盘文档中不得再出现把进程拉起耗时称作「启动总耗时」的字段，
    /// 否则外部读到的数值仍会被误当成冷启动结论。
    /// </summary>
    [Fact]
    public async Task 落盘文档不再产出含义误导的启动耗时字段()
    {
        using var recorder = CreateRecorder(TimeSpan.Zero);
        var handle = new FakeQemuProcessHandle { FakeWorkingSet = 64 * 1024 * 1024 };

        recorder.OnStarting("inst-01");
        recorder.OnProcessSpawned("inst-01", handle);

        string filePath = await recorder.StopAndPersistAsync("inst-01");
        string json = await File.ReadAllTextAsync(filePath);
        using var doc = JsonDocument.Parse(json);
        var startup = doc.RootElement.GetProperty("startup");

        Assert.False(startup.TryGetProperty("success", out _));
        Assert.False(startup.TryGetProperty("durationSeconds", out _));
        Assert.False(startup.TryGetProperty("durationMs", out _));
        Assert.False(startup.TryGetProperty("readyAt", out _));
        Assert.True(startup.TryGetProperty("processSpawned", out _));
    }

    [Fact]
    public void 未采样到有效内存时_摘要为null不推导估算()
    {
        using var recorder = CreateRecorder(TimeSpan.Zero);
        var handle = new FakeQemuProcessHandle { FakeWorkingSet = null }; // 模拟无法读取内存

        recorder.OnStarting("inst-01");
        recorder.OnProcessSpawned("inst-01", handle);

        InstanceMetricsSnapshot? snapshot = recorder.GetSnapshot("inst-01");
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.MemorySamples);
        Assert.Null(snapshot.Summary);
    }

    [Fact]
    public async Task 周期采样定时器_按周期自动记录内存采样()
    {
        var handle = new FakeQemuProcessHandle { FakeWorkingSet = 128 * 1024 * 1024 };
        using var recorder = new MetricsRecorder(_tempDirectory, TimeProvider.System, TimeSpan.FromMilliseconds(20));

        recorder.OnStarting("inst-timer");
        recorder.OnProcessSpawned("inst-timer", handle);

        // 等待定时器触发多次
        await Task.Delay(80);

        handle.FakeWorkingSet = 256 * 1024 * 1024;
        await Task.Delay(80);

        string filePath = await recorder.StopAndPersistAsync("inst-timer");
        Assert.True(File.Exists(filePath));

        var snapshot = recorder.GetSnapshot("inst-timer");
        Assert.NotNull(snapshot);
        Assert.True(snapshot.MemorySamples.Count >= 2);
    }
}