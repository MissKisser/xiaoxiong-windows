using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Input;
using XBear.Core.Projection;
using XBear.Core.Tests.Input;

namespace XBear.Core.Tests.Projection;

/// <summary>
/// 投屏输入注入器测试，覆盖帧缓冲像素坐标到 virtio 绝对坐标域映射、边界与超界钳制、按键按下/释放分离。
/// </summary>
public sealed class ProjectionInputInjectorTests
{
    private static readonly ScreenGeometry Geometry = new(1000, 500);

    [Fact]
    public void 坐标换算_首末像素与中心像素精确映射()
    {
        var qmp = new RecordingQmpClient();
        var injector = new ProjectionInputInjector(qmp, () => Geometry, pointerMax: 32767);

        // 左上角首像素 (0, 0) -> (0, 0)
        InputPoint p0 = injector.MapPoint(new InputPoint(0, 0));
        Assert.Equal(0, p0.X);
        Assert.Equal(0, p0.Y);

        // 右下角末像素 (999, 499) -> (32767, 32767)
        InputPoint pEnd = injector.MapPoint(new InputPoint(999, 499));
        Assert.Equal(32767, pEnd.X);
        Assert.Equal(32767, pEnd.Y);

        // 中心像素 (499.5, 249.5) -> (16384, 16384)
        InputPoint pMid = injector.MapPoint(new InputPoint(499.5, 249.5));
        Assert.InRange(pMid.X, 16380, 16388);
        Assert.InRange(pMid.Y, 16380, 16388);
    }

    [Theory]
    [InlineData(-100, -50, 0, 0)]
    [InlineData(2000, 1000, 32767, 32767)]
    [InlineData(-1, 600, 0, 32767)]
    public void 坐标换算_超界坐标精确钳制到边界(double inX, double inY, double expX, double expY)
    {
        var qmp = new RecordingQmpClient();
        var injector = new ProjectionInputInjector(qmp, () => Geometry, pointerMax: 32767);

        InputPoint mapped = injector.MapPoint(new InputPoint(inX, inY));
        Assert.Equal(expX, mapped.X);
        Assert.Equal(expY, mapped.Y);
    }

    [Fact]
    public async Task TouchAsync_按下与移动与抬起分别发对应事件()
    {
        var qmp = new RecordingQmpClient();
        var injector = new ProjectionInputInjector(qmp, () => Geometry, pointerMax: 32767);

        // Down
        await injector.TouchAsync(new InputPoint(0, 0), TouchPhase.Down);
        Assert.Equal(1, qmp.CountOf("input-send-event"));
        var downArgs = qmp.ArgumentsOf(0) as Dictionary<string, object>;
        Assert.NotNull(downArgs);
        var downEvents = downArgs["events"] as object[];
        Assert.NotNull(downEvents);
        Assert.Equal(3, downEvents.Length); // abs x, abs y, btn down

        // Move
        await injector.TouchAsync(new InputPoint(100, 50), TouchPhase.Move);
        Assert.Equal(2, qmp.CountOf("input-send-event"));
        var moveArgs = qmp.ArgumentsOf(1) as Dictionary<string, object>;
        var moveEvents = moveArgs!["events"] as object[];
        Assert.Equal(2, moveEvents!.Length); // abs x, abs y

        // Up
        await injector.TouchAsync(new InputPoint(100, 50), TouchPhase.Up);
        Assert.Equal(3, qmp.CountOf("input-send-event"));
        var upArgs = qmp.ArgumentsOf(2) as Dictionary<string, object>;
        var upEvents = upArgs!["events"] as object[];
        Assert.Single(upEvents!); // btn up
    }

    [Fact]
    public async Task TapAsync_发送Down加Up两阶段动作()
    {
        var qmp = new RecordingQmpClient();
        var injector = new ProjectionInputInjector(qmp, () => Geometry, pointerMax: 32767);

        await injector.TapAsync(new InputPoint(200, 100));
        Assert.Equal(1, qmp.CountOf("input-send-event"));

        var args = qmp.ArgumentsOf(0) as Dictionary<string, object>;
        var events = args!["events"] as object[];
        Assert.Equal(4, events!.Length); // abs x, abs y, btn down, btn up
    }

    [Fact]
    public async Task KeyAsync_按键按下与抬起独立投递且编码准确()
    {
        var qmp = new RecordingQmpClient();
        var injector = new ProjectionInputInjector(qmp, () => Geometry);

        // 注入 Android 返回键按下
        await injector.KeyAsync(AndroidKey.Back, KeyAction.Press);
        Assert.Equal(1, qmp.CountOf("input-send-event"));

        var pressArgs = qmp.ArgumentsOf(0) as Dictionary<string, object>;
        var pressEvents = pressArgs!["events"] as object[];
        Assert.NotNull(pressEvents);
        Assert.Single(pressEvents);

        // 注入 Android 返回键抬起
        await injector.KeyAsync(AndroidKey.Back, KeyAction.Release);
        Assert.Equal(2, qmp.CountOf("input-send-event"));

        var releaseArgs = qmp.ArgumentsOf(1) as Dictionary<string, object>;
        var releaseEvents = releaseArgs!["events"] as object[];
        Assert.NotNull(releaseEvents);
        Assert.Single(releaseEvents);

        // 序列化后断言 Linux KeyCode 158 存在且 down 为 true / false
        string jsonDown = JsonSerializer.Serialize(pressEvents[0]);
        string jsonUp = JsonSerializer.Serialize(releaseEvents[0]);

        Assert.Contains("\"down\":true", jsonDown);
        Assert.Contains("158", jsonDown);
        Assert.Contains("\"down\":false", jsonUp);
        Assert.Contains("158", jsonUp);
    }
}
