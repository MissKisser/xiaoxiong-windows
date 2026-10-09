using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Input;

namespace XBear.Core.Tests.Input;

/// <summary>投屏侧投递测试，锁定实例内输入命令串的形状。</summary>
public sealed class AdbInputInjectorTests
{
    [Theory]
    [InlineData(TouchPhase.Down, "input motionevent DOWN 120 340")]
    [InlineData(TouchPhase.Move, "input motionevent MOVE 120 340")]
    [InlineData(TouchPhase.Up, "input motionevent UP 120 340")]
    public void 构造触摸命令_按阶段给出对应串(TouchPhase phase, string expected)
    {
        Assert.Equal(expected, AdbInputInjector.BuildTouchCommand(phase, new InputPoint(120, 340)));
    }

    [Fact]
    public void 构造滑动命令_含起止点与时长毫秒()
    {
        string command = AdbInputInjector.BuildSwipeCommand(
            new InputPoint(100, 200),
            new InputPoint(300, 400),
            TimeSpan.FromMilliseconds(750));

        Assert.Equal("input swipe 100 200 300 400 750", command);
    }

    [Fact]
    public void 构造滑动命令_时长不足一毫秒时取最小正值()
    {
        string command = AdbInputInjector.BuildSwipeCommand(
            new InputPoint(0, 0),
            new InputPoint(10, 10),
            TimeSpan.FromTicks(1));

        Assert.Equal("input swipe 0 0 10 10 1", command);
    }

    [Fact]
    public void 构造滑动命令_时长非正时按配置违规上抛()
    {
        XBearException error = Assert.Throws<XBearException>(() =>
            AdbInputInjector.BuildSwipeCommand(new InputPoint(0, 0), new InputPoint(1, 1), TimeSpan.Zero));

        Assert.Equal(ErrorCategory.Spec, error.Category);
    }

    [Fact]
    public void 构造按键命令_使用实例侧按键码()
    {
        VirtualKey back = KeyCodeMap.Get(AndroidKey.Back);
        VirtualKey enter = KeyCodeMap.Get(AndroidKey.Enter);

        Assert.Equal("input keyevent 4", AdbInputInjector.BuildKeyCommand(back));
        Assert.Equal("input keyevent 66", AdbInputInjector.BuildKeyCommand(enter));
    }

    [Fact]
    public async Task 投递触摸_发出对应阶段的命令串()
    {
        var adb = new RecordingAdbClient();
        var injector = new AdbInputInjector(adb, new InputChannelOptions { AdbPort = 5555 });

        await injector.TouchAsync(TouchPhase.Down, new InputPoint(12, 34));

        Assert.Equal(new[] { "input motionevent DOWN 12 34" }, adb.ShellCommands);
        Assert.Equal(1, adb.ConnectCount);
    }

    [Fact]
    public async Task 重复连接被拒时复用已有连接并继续投递()
    {
        var adb = new RecordingAdbClient();
        var injector = new AdbInputInjector(adb, new InputChannelOptions());

        await injector.TouchAsync(TouchPhase.Down, new InputPoint(1, 1));

        adb.ConnectFailure = new XBearException(ErrorCategory.Protocol, "adb 客户端已处于连接状态。");
        await injector.TouchAsync(TouchPhase.Up, new InputPoint(1, 1));

        Assert.Equal(2, adb.ShellCommands.Count);
    }

    [Fact]
    public async Task 投递滑动_命令串含时长()
    {
        var adb = new RecordingAdbClient();
        var injector = new AdbInputInjector(adb, new InputChannelOptions());

        await injector.SwipeAsync(new InputPoint(1, 2), new InputPoint(3, 4), TimeSpan.FromMilliseconds(300));

        Assert.Equal(new[] { "input swipe 1 2 3 4 300" }, adb.ShellCommands);
    }

    [Fact]
    public async Task 投递按键_按下与抬起发出相同命令()
    {
        var adb = new RecordingAdbClient();
        var injector = new AdbInputInjector(adb, new InputChannelOptions());
        VirtualKey home = KeyCodeMap.Get(AndroidKey.Home);

        await injector.KeyAsync(home, KeyAction.Press);
        await injector.KeyAsync(home, KeyAction.Release);

        Assert.Equal(new[] { "input keyevent 3", "input keyevent 3" }, adb.ShellCommands);
    }

    [Fact]
    public async Task 实例权限不足_按协议失败上抛且不下发命令()
    {
        var adb = new RecordingAdbClient { IsRoot = false };
        var injector = new AdbInputInjector(adb, new InputChannelOptions());

        XBearException error = await Assert.ThrowsAsync<XBearException>(() =>
            injector.TouchAsync(TouchPhase.Down, new InputPoint(1, 1)));

        Assert.Equal(ErrorCategory.Protocol, error.Category);
        Assert.Empty(adb.ShellCommands);
    }

    [Fact]
    public async Task shell失败_归类为协议失败而非裸异常()
    {
        var adb = new RecordingAdbClient { ShellFailure = new InvalidOperationException("管道已断开") };
        var injector = new AdbInputInjector(adb, new InputChannelOptions());

        XBearException error = await Assert.ThrowsAsync<XBearException>(() =>
            injector.TouchAsync(TouchPhase.Down, new InputPoint(1, 1)));

        Assert.Equal(ErrorCategory.Protocol, error.Category);
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public async Task 取消令牌_取消时按取消原样上抛()
    {
        var adb = new RecordingAdbClient();
        var injector = new AdbInputInjector(adb, new InputChannelOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            injector.TouchAsync(TouchPhase.Down, new InputPoint(1, 1), cts.Token));
    }
}
