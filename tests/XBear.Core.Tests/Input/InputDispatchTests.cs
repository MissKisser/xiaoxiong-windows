using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Input;

namespace XBear.Core.Tests.Input;

/// <summary>通道级投递测试，覆盖实际生效通路、降级行为与失败分类。</summary>
public sealed class InputDispatchTests
{
    private static readonly ScreenGeometry Guest = new(1080, 1920);

    [Fact]
    public async Task 未探测直接投递_先探测再用原生且坐标已换算()
    {
        var qmp = new RecordingQmpClient();
        var adb = new RecordingAdbClient();
        await using var channel = Build(qmp, adb, HostGeometry: new ScreenGeometry(1000, 1000));

        InputDispatchResult result = await channel.TouchAsync(new InputPoint(500, 500), TouchPhase.Down);

        Assert.Equal(InputChannelKind.Native, result.Channel);
        Assert.Equal(InputChannelKind.Native, channel.ActiveChannel);
        Assert.Null(result.NativeFailure);

        // 宿主画面 1000 见方换算到 guest 显示范围后，中心点落在中心像素附近而非原值。
        using JsonDocument doc = Serialize(SendEventArgument(qmp, 1));
        Assert.Equal(540, doc.RootElement.GetProperty("events")[0].GetProperty("data").GetProperty("value").GetInt32());
        Assert.Equal(960, doc.RootElement.GetProperty("events")[1].GetProperty("data").GetProperty("value").GetInt32());
        Assert.DoesNotContain(adb.ShellCommands, command => command.Contains("motionevent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 完整触摸_下发按下与抬起两步()
    {
        var qmp = new RecordingQmpClient();
        var adb = new RecordingAdbClient();
        await using var channel = Build(qmp, adb);

        await channel.ProbeAsync();
        int before = qmp.CountOf("input-send-event");
        await channel.TapAsync(new InputPoint(100, 200));

        Assert.Equal(before + 2, qmp.CountOf("input-send-event"));

        using JsonDocument down = Serialize(SendEventArgument(qmp, before));
        Assert.Equal(3, down.RootElement.GetProperty("events").GetArrayLength());

        using JsonDocument up = Serialize(SendEventArgument(qmp, before + 1));
        Assert.Equal(1, up.RootElement.GetProperty("events").GetArrayLength());
        Assert.False(up.RootElement.GetProperty("events")[0].GetProperty("data").GetProperty("down").GetBoolean());
    }

    [Fact]
    public async Task 滑动_插值为多步移动后抬起()
    {
        var qmp = new RecordingQmpClient();
        var adb = new RecordingAdbClient();
        await using var channel = Build(
            qmp,
            adb,
            stepInterval: TimeSpan.FromMilliseconds(20),
            threshold: 1);

        await channel.ProbeAsync();
        int before = qmp.CountOf("input-send-event");
        await channel.SwipeAsync(new InputPoint(0, 0), new InputPoint(100, 100), TimeSpan.FromMilliseconds(100));

        // 一步按下 + 五步移动 + 一步抬起。
        Assert.Equal(before + 7, qmp.CountOf("input-send-event"));
    }

    [Fact]
    public async Task 滑动_步长上限限制移动事件数量()
    {
        var qmp = new RecordingQmpClient();
        var adb = new RecordingAdbClient();
        await using var channel = Build(
            qmp,
            adb,
            stepInterval: TimeSpan.FromMilliseconds(1),
            threshold: 1);

        await channel.ProbeAsync();
        int before = qmp.CountOf("input-send-event");
        await channel.SwipeAsync(new InputPoint(0, 0), new InputPoint(10, 10), TimeSpan.FromSeconds(30));

        // 步长被限制为 32 步，宿主与实例之间不会被超长滑动打满。
        Assert.Equal(before + 34, qmp.CountOf("input-send-event"));
    }

    [Fact]
    public async Task 按键_走原生且下发按键事件()
    {
        var qmp = new RecordingQmpClient();
        var adb = new RecordingAdbClient();
        await using var channel = Build(qmp, adb);

        InputDispatchResult result = await channel.KeyAsync(KeyCodeMap.Get(AndroidKey.Back), KeyAction.Press);

        Assert.Equal(InputChannelKind.Native, result.Channel);
        using JsonDocument doc = Serialize(SendEventArgument(qmp, 1));
        JsonElement data = doc.RootElement.GetProperty("events")[0].GetProperty("data");
        Assert.True(data.GetProperty("down").GetBoolean());
    }

    [Fact]
    public async Task 投递成功后计数清零_失败不再沿用旧计数()
    {
        var qmp = new RecordingQmpClient();
        var adb = new RecordingAdbClient();
        await using var channel = Build(qmp, adb, threshold: 3);

        await channel.ProbeAsync();

        qmp.FailureSelector = command => command == "input-send-event"
            ? new InvalidOperationException("输入处理器缺失")
            : null;
        await Assert.ThrowsAsync<XBearException>(() =>
            channel.KeyAsync(KeyCodeMap.Get(AndroidKey.Home), KeyAction.Press));
        Assert.Equal(1, channel.ConsecutiveNativeFailures);

        qmp.FailureSelector = null;
        await channel.KeyAsync(KeyCodeMap.Get(AndroidKey.Home), KeyAction.Press);

        Assert.Equal(0, channel.ConsecutiveNativeFailures);
        Assert.Equal(InputChannelKind.Native, channel.ActiveChannel);
    }

    [Fact]
    public async Task 探测未耗尽阈值时投递失败_按协议失败上抛且不下发投屏命令()
    {
        var qmp = new RecordingQmpClient();
        var adb = new RecordingAdbClient();
        await using var channel = Build(qmp, adb, threshold: 3);

        await channel.ProbeAsync();
        int adbBefore = adb.ShellCommands.Count;

        // 探测之后才让注入失败，用于验证未达阈值时本次失败如实上抛而不降级。
        qmp.FailureSelector = command => command == "input-send-event"
            ? new InvalidOperationException("连接已断开")
            : null;

        XBearException error = await Assert.ThrowsAsync<XBearException>(() =>
            channel.KeyAsync(KeyCodeMap.Get(AndroidKey.Home), KeyAction.Press));

        Assert.Equal(ErrorCategory.Protocol, error.Category);
        Assert.Equal(InputChannelKind.Native, channel.ActiveChannel);
        Assert.Equal(adbBefore, adb.ShellCommands.Count);
        Assert.Equal(1, channel.ConsecutiveNativeFailures);
    }

    [Fact]
    public async Task 投递失败累计达阈值_降级到投屏并带回原生失败原因()
    {
        var qmp = new RecordingQmpClient();
        var adb = new RecordingAdbClient();
        await using var channel = Build(qmp, adb, threshold: 2);

        await channel.ProbeAsync();
        Assert.Equal(InputChannelKind.Native, channel.ActiveChannel);

        qmp.FailureSelector = command => command == "input-send-event"
            ? new InvalidOperationException("输入处理器缺失")
            : null;

        await Assert.ThrowsAsync<XBearException>(() =>
            channel.KeyAsync(KeyCodeMap.Get(AndroidKey.Home), KeyAction.Press));
        Assert.Equal(InputChannelKind.Native, channel.ActiveChannel);

        InputDispatchResult degraded = await channel.KeyAsync(KeyCodeMap.Get(AndroidKey.Home), KeyAction.Press);

        Assert.Equal(InputChannelKind.Projection, degraded.Channel);
        Assert.Equal(InputChannelKind.Projection, channel.ActiveChannel);
        Assert.False(string.IsNullOrWhiteSpace(degraded.NativeFailure));
        Assert.Contains("input keyevent 3", adb.ShellCommands);
    }

    [Fact]
    public async Task 降级后触摸_命令串来自投屏通路()
    {
        var qmp = new RecordingQmpClient();
        var adb = new RecordingAdbClient();
        await using var channel = Build(qmp, adb, threshold: 1);

        qmp.FailureSelector = command => command == "input-send-event"
            ? new InvalidOperationException("输入处理器缺失")
            : null;

        await channel.ProbeAsync();
        adb.ShellCommands.Clear();

        InputDispatchResult result = await channel.TapAsync(new InputPoint(300, 400));

        Assert.Equal(InputChannelKind.Projection, result.Channel);
        Assert.Equal(
            new[] { "input motionevent DOWN 300 400", "input motionevent UP 300 400" },
            adb.ShellCommands);
    }

    [Fact]
    public async Task 降级后滑动_走投屏的一次滑动命令()
    {
        var qmp = new RecordingQmpClient();
        var adb = new RecordingAdbClient();
        await using var channel = Build(qmp, adb, threshold: 1);

        qmp.FailureSelector = command => command == "input-send-event"
            ? new InvalidOperationException("输入处理器缺失")
            : null;

        await channel.ProbeAsync();
        adb.ShellCommands.Clear();

        InputDispatchResult result = await channel.SwipeAsync(
            new InputPoint(0, 0),
            new InputPoint(500, 500),
            TimeSpan.FromMilliseconds(600));

        Assert.Equal(InputChannelKind.Projection, result.Channel);
        Assert.Equal(new[] { "input swipe 0 0 500 500 600" }, adb.ShellCommands);
    }

    [Fact]
    public async Task 两条通路都失败_上抛并同时回报两条失败原因()
    {
        var qmp = new RecordingQmpClient
        {
            FailureSelector = command => command == "input-send-event"
                ? new InvalidOperationException("输入处理器缺失")
                : null,
        };
        var adb = new RecordingAdbClient { ShellFailure = new XBearException(ErrorCategory.Protocol, "adb 未连接") };
        await using var channel = Build(qmp, adb, threshold: 1);

        await channel.ProbeAsync();

        XBearException error = await Assert.ThrowsAsync<XBearException>(() =>
            channel.KeyAsync(KeyCodeMap.Get(AndroidKey.Home), KeyAction.Press));

        Assert.Equal(ErrorCategory.Protocol, error.Category);
        Assert.Contains("原生通路", error.Message, StringComparison.Ordinal);
        Assert.Contains("投屏通路", error.Message, StringComparison.Ordinal);
        Assert.Equal(InputChannelKind.Unavailable, channel.ActiveChannel);
    }

    [Fact]
    public async Task 探测判定两条通路都不可用_投递时按依赖缺失上抛()
    {
        var qmp = new RecordingQmpClient { ConnectFailure = new InvalidOperationException("端口未监听") };
        var adb = new RecordingAdbClient { ConnectFailure = new InvalidOperationException("端口未监听") };
        await using var channel = Build(qmp, adb, threshold: 1);

        XBearException error = await Assert.ThrowsAsync<XBearException>(() =>
            channel.TapAsync(new InputPoint(1, 1)));

        Assert.Equal(ErrorCategory.Dependency, error.Category);
        Assert.Contains("原生通路", error.Remediation!, StringComparison.Ordinal);
        Assert.Contains("投屏通路", error.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 无显示尺寸可用_按配置违规上抛而不猜分辨率()
    {
        var qmp = new RecordingQmpClient();
        var adb = new RecordingAdbClient();
        await using var channel = new ProbingInputChannel(qmp, adb, new InputChannelOptions
        {
            QmpPort = 45900,
            AdbPort = 5555,
            NativeFailureThreshold = 1,
            ScreenshotDirectory = Path.Combine(Path.GetTempPath(), "xbear-geometry-test"),
        });

        await channel.ProbeAsync();

        // 替身不产生截图文件，尺寸无从确定，此时必须报错而不是按某个默认分辨率注入。
        XBearException error = await Assert.ThrowsAsync<XBearException>(() =>
            channel.TouchAsync(new InputPoint(10, 10), TouchPhase.Down));

        Assert.Equal(ErrorCategory.Spec, error.Category);
    }

    [Fact]
    public async Task 调用方已提供显示尺寸_不再向QMP索取()
    {
        var qmp = new RecordingQmpClient();
        var adb = new RecordingAdbClient();
        await using var channel = Build(qmp, adb);

        ScreenGeometry? geometry = await channel.GetGeometryAsync();

        Assert.Equal(Guest, geometry);
        Assert.Equal(0, qmp.CountOf("screendump"));
    }

    [Fact]
    public async Task 未提供显示尺寸时向QMP索取_按截图头部得出尺寸()
    {
        string directory = Path.Combine(Path.GetTempPath(), "xbear-geometry-test");
        var qmp = new RecordingQmpClient
        {
            ScreendumpWriter = path => File.WriteAllText(path, "P6\n# 小熊生成的截图\n800 600\n255\n"),
        };
        var adb = new RecordingAdbClient();
        await using var channel = new ProbingInputChannel(qmp, adb, new InputChannelOptions
        {
            QmpPort = 45900,
            AdbPort = 5555,
            NativeFailureThreshold = 1,
            ScreenshotDirectory = directory,
        });

        ScreenGeometry? geometry = await channel.GetGeometryAsync();

        Assert.Equal(new ScreenGeometry(800, 600), geometry);
        Assert.Equal(1, qmp.CountOf("screendump"));

        // 取完尺寸后临时截图不应留在磁盘上。
        Assert.Empty(Directory.EnumerateFiles(directory, "*.ppm"));
    }

    [Fact]
    public async Task 取消令牌_投递时按取消原样上抛()
    {
        var qmp = new RecordingQmpClient();
        var adb = new RecordingAdbClient();
        await using var channel = Build(qmp, adb);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            channel.TapAsync(new InputPoint(1, 1), cts.Token));
    }

    [Fact]
    public async Task 释放后投递_抛出对象已释放异常()
    {
        var qmp = new RecordingQmpClient();
        var adb = new RecordingAdbClient();
        var channel = Build(qmp, adb);
        await channel.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            channel.TapAsync(new InputPoint(1, 1)));
    }

    private static ProbingInputChannel Build(
        RecordingQmpClient qmp,
        RecordingAdbClient adb,
        ScreenGeometry HostGeometry = default,
        int threshold = 1,
        TimeSpan? stepInterval = null) =>
        new(qmp, adb, new InputChannelOptions
        {
            QmpPort = 45900,
            AdbPort = 5555,
            NativeFailureThreshold = threshold,
            HostGeometry = HostGeometry,
            GuestGeometry = Guest,
            SwipeStepInterval = stepInterval ?? TimeSpan.FromMilliseconds(16),
        });

    private static object? SendEventArgument(RecordingQmpClient qmp, int occurrence)
    {
        int seen = -1;
        foreach ((string command, object? arguments) in qmp.Calls)
        {
            if (command != "input-send-event")
            {
                continue;
            }

            seen++;
            if (seen == occurrence)
            {
                return arguments;
            }
        }

        throw new InvalidOperationException($"未找到第 {occurrence} 次输入命令调用。");
    }

    private static JsonDocument Serialize(object? payload) =>
        JsonDocument.Parse(JsonSerializer.Serialize(payload));
}
