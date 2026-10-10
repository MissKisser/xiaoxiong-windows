using XBear.Core.Projection;

namespace XBear.Core.Tests.Projection;

/// <summary>
/// 帧率实测器测试，覆盖样本不足保持 null、受控节奏下的测算精度与复位行为。
/// </summary>
public sealed class FrameRateMeterTests
{
    [Fact]
    public void 样本少于两个时_实测帧率保持Null()
    {
        var meter = new FrameRateMeter(TimeSpan.FromSeconds(2));
        Assert.Null(meter.Measured);

        meter.Record(DateTimeOffset.UtcNow);
        Assert.Null(meter.Measured);
    }

    [Fact]
    public void 受控喂帧节奏下_实测帧率非空且量级正确()
    {
        var meter = new FrameRateMeter(TimeSpan.FromSeconds(2));
        DateTimeOffset start = DateTimeOffset.UtcNow;

        // 模拟 20 fps（每帧间隔 50ms），连续喂 21 帧（跨度 1.0 秒）
        for (int i = 0; i <= 20; i++)
        {
            meter.Record(start.AddMilliseconds(i * 50));
        }

        Assert.NotNull(meter.Measured);
        double fps = meter.Measured.Value;

        // 20 帧在 1.0 秒内，理论值为 20.0 fps，断言宽松区间 19.5 .. 20.5
        Assert.InRange(fps, 19.5, 20.5);
    }

    [Fact]
    public void 超出滚动窗口的旧样本被丢弃_仅按窗口内样本统计()
    {
        var meter = new FrameRateMeter(TimeSpan.FromSeconds(1));
        DateTimeOffset start = DateTimeOffset.UtcNow;

        // 窗口为 1 秒。在 t=0 到 t=500ms 喂 10 帧（速率 20fps）
        for (int i = 0; i < 10; i++)
        {
            meter.Record(start.AddMilliseconds(i * 50));
        }

        // 然后在 t=2000ms 到 t=2500ms 喂 5 帧（间隔 100ms，速率 10fps）
        DateTimeOffset later = start.AddSeconds(2);
        for (int i = 0; i < 5; i++)
        {
            meter.Record(later.AddMilliseconds(i * 100));
        }

        Assert.NotNull(meter.Measured);
        double fps = meter.Measured.Value;

        // 旧的 10 帧已落在 1 秒窗口外被丢弃，当前只剩 5 帧，跨度 400ms，4 / 0.4 = 10fps
        Assert.InRange(fps, 9.5, 10.5);
    }

    [Fact]
    public void Reset后清空样本并恢复为Null()
    {
        var meter = new FrameRateMeter();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        meter.Record(now);
        meter.Record(now.AddMilliseconds(50));
        Assert.NotNull(meter.Measured);

        meter.Reset();
        Assert.Null(meter.Measured);
        Assert.Equal(0, meter.SampleCount);
    }
}
