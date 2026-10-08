using System.Text.Json;
using XBear.Core.Diagnostics;

namespace XBear.Core.Tests.Instances;

/// <summary>
/// 指标采集器测试，覆盖启动耗时、内存采样序列、JSON落盘与聚合摘要。
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

    [Fact]
    public void 启动耗时_以可注入时间源记录真实间隔()
    {
        using var recorder = new MetricsRecorder(_tempDirectory, _clock);
        var handle = new FakeQemuProcessHandle();

        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));
        recorder.OnStarting("inst-01");

        _clock.Advance(TimeSpan.FromSeconds(3.5));
        recorder.OnRunning("inst-01", handle);

        InstanceMetricsSnapshot? snapshot = recorder.GetSnapshot("inst-01");
        Assert.NotNull(snapshot);
        Assert.Equal("inst-01", snapshot.InstanceId);
        Assert.NotNull(snapshot.Startup);
        Assert.True(snapshot.Startup.Success);
        Assert.Equal(3.5, snapshot.Startup.DurationSeconds);
        Assert.Equal(3500.0, snapshot.Startup.DurationMs);
    }

    [Fact]
    public void 启动失败_记录失败且不伪造就绪数据()
    {
        using var recorder = new MetricsRecorder(_tempDirectory, _clock);

        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));
        recorder.OnStarting("inst-01");

        _clock.Advance(TimeSpan.FromSeconds(2.0));
        recorder.OnStartupFailed("inst-01");

        InstanceMetricsSnapshot? snapshot = recorder.GetSnapshot("inst-01");
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.Startup);
        Assert.False(snapshot.Startup.Success);
        Assert.Null(snapshot.Startup.ReadyAt);
        Assert.Null(snapshot.Startup.DurationSeconds);
    }

    [Fact]
    public void 内存采样_手动采样记录真实工作集与时间戳序列()
    {
        using var recorder = new MetricsRecorder(_tempDirectory, _clock, TimeSpan.Zero);
        var handle = new FakeQemuProcessHandle { FakeWorkingSet = 100 * 1024 * 1024 };

        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));
        recorder.OnStarting("inst-01");
        recorder.OnRunning("inst-01", handle);

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
        using var recorder = new MetricsRecorder(_tempDirectory, _clock, TimeSpan.Zero);
        var handle = new FakeQemuProcessHandle { FakeWorkingSet = 512 * 1024 * 1024 };

        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        recorder.OnStarting("inst-01");
        _clock.Advance(TimeSpan.FromSeconds(4.2));
        recorder.OnRunning("inst-01", handle);

        handle.FakeWorkingSet = 1024 * 1024 * 1024L;
        recorder.SampleMemory("inst-01");

        string filePath = await recorder.StopAndPersistAsync("inst-01");

        Assert.True(File.Exists(filePath));
        Assert.EndsWith("metrics.json", filePath);

        string json = await File.ReadAllTextAsync(filePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("inst-01", root.GetProperty("instanceId").GetString());
        Assert.True(root.GetProperty("startup").GetProperty("success").GetBoolean());
        Assert.Equal(4.2, root.GetProperty("startup").GetProperty("durationSeconds").GetDouble(), 1);

        var summary = root.GetProperty("summary");
        Assert.True(summary.GetProperty("sampleCount").GetInt32() >= 1);
        Assert.True(summary.GetProperty("maxWorkingSetBytes").GetInt64() >= 512 * 1024 * 1024);
    }

    [Fact]
    public void 未采样到有效内存时_摘要为null不推导估算()
    {
        using var recorder = new MetricsRecorder(_tempDirectory, _clock, TimeSpan.Zero);
        var handle = new FakeQemuProcessHandle { FakeWorkingSet = null }; // 模拟无法读取内存

        recorder.OnStarting("inst-01");
        recorder.OnRunning("inst-01", handle);

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
        recorder.OnRunning("inst-timer", handle);

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
