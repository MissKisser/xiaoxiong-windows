using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Input;

namespace XBear.Core.Tests.Input;

/// <summary>宿主侧投递测试，锁定下发的 QMP 命令名与参数形状。</summary>
public sealed class QmpInputInjectorTests
{
    [Fact]
    public async Task 投递触摸_下发输入命令且命令名正确()
    {
        var qmp = new RecordingQmpClient();
        var injector = new QmpInputInjector(qmp, new InputChannelOptions { QmpPort = 45900 });

        await injector.TouchAsync(TouchPhase.Down, new InputPoint(640, 1280));

        Assert.Equal(1, qmp.CountOf("input-send-event"));
        Assert.Equal(1, qmp.ConnectCount);

        using JsonDocument doc = Serialize(qmp.ArgumentsOf(0));
        Assert.Equal(3, doc.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task 投递触摸_坐标已换算为实例像素()
    {
        var qmp = new RecordingQmpClient();
        var injector = new QmpInputInjector(qmp, new InputChannelOptions());

        await injector.TouchAsync(TouchPhase.Move, new InputPoint(1.4, 2.6));

        using JsonDocument doc = Serialize(qmp.ArgumentsOf(0));
        Assert.Equal(1, doc.RootElement.GetProperty("events")[0].GetProperty("data").GetProperty("value").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("events")[1].GetProperty("data").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task 投递按键_下发按键事件并取宿主侧事件码()
    {
        var qmp = new RecordingQmpClient();
        var injector = new QmpInputInjector(qmp, new InputChannelOptions());
        VirtualKey key = KeyCodeMap.Get(AndroidKey.VolumeUp);

        await injector.KeyAsync(key, KeyAction.Press);

        using JsonDocument doc = Serialize(qmp.ArgumentsOf(0));
        JsonElement data = doc.RootElement.GetProperty("events")[0].GetProperty("data");
        Assert.Equal("key", doc.RootElement.GetProperty("events")[0].GetProperty("type").GetString());
        Assert.True(data.GetProperty("down").GetBoolean());
        Assert.Equal(key.LinuxKeyCode, data.GetProperty("key").GetProperty("data").GetInt32());
    }

    [Fact]
    public async Task 指定设备名_参数中含该设备名()
    {
        var qmp = new RecordingQmpClient();
        var injector = new QmpInputInjector(
            qmp,
            new InputChannelOptions { QmpInputDeviceName = "touchscreen" });

        await injector.TouchAsync(TouchPhase.Down, new InputPoint(1, 2));

        using JsonDocument doc = Serialize(qmp.ArgumentsOf(0));
        Assert.Equal("touchscreen", doc.RootElement.GetProperty("device").GetString());
    }

    [Fact]
    public async Task 多次投递_每次都触达对端且复用已有连接()
    {
        var qmp = new RecordingQmpClient();
        var injector = new QmpInputInjector(qmp, new InputChannelOptions());

        await injector.TouchAsync(TouchPhase.Down, new InputPoint(1, 2));
        await injector.TouchAsync(TouchPhase.Up, new InputPoint(1, 2));

        Assert.Equal(2, qmp.ConnectCount);
        Assert.Equal(2, qmp.CountOf("input-send-event"));
    }

    [Fact]
    public async Task 重复连接被拒时复用已有连接并继续投递()
    {
        var qmp = new RecordingQmpClient
        {
            ConnectFailure = new XBearException(ErrorCategory.Protocol, "QMP 客户端已处于连接状态。"),
        };
        var injector = new QmpInputInjector(qmp, new InputChannelOptions());

        // 首次连接失败且此前并未连上，失败如实上抛。
        await Assert.ThrowsAsync<XBearException>(() =>
            injector.TouchAsync(TouchPhase.Down, new InputPoint(1, 2)));

        qmp.ConnectFailure = null;
        await injector.TouchAsync(TouchPhase.Down, new InputPoint(1, 2));

        qmp.ConnectFailure = new XBearException(ErrorCategory.Protocol, "QMP 客户端已处于连接状态。");
        await injector.TouchAsync(TouchPhase.Up, new InputPoint(1, 2));

        // 首次连接失败未产生注入，随后两次注入各自成一条命令。
        Assert.Equal(2, qmp.CountOf("input-send-event"));
    }

    [Fact]
    public void QMP回包含错误对象_归类为协议失败并带处置建议()
    {
        using JsonDocument doc = JsonDocument.Parse(
            "{\"error\":{\"class\":\"GenericError\",\"desc\":\"Input handler not found for event type abs\"}}");

        XBearException error = Assert.Throws<XBearException>(() =>
            QmpInputInjector.ThrowIfQmpError(doc.RootElement, "触摸按下"));

        Assert.Equal(ErrorCategory.Protocol, error.Category);
        Assert.Contains("Input handler not found", error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(error.Remediation));
    }

    [Fact]
    public void QMP回包为字符串错误_同样归类为协议失败()
    {
        using JsonDocument doc = JsonDocument.Parse("{\"error\":\"CommandNotFound\"}");

        XBearException error = Assert.Throws<XBearException>(() =>
            QmpInputInjector.ThrowIfQmpError(doc.RootElement, "触摸抬起"));

        Assert.Equal(ErrorCategory.Protocol, error.Category);
        Assert.Contains("CommandNotFound", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QMP命令失败_归类为协议失败而非裸异常()
    {
        var qmp = new RecordingQmpClient
        {
            FailureSelector = command => command == "input-send-event"
                ? new InvalidOperationException("连接已断开")
                : null,
        };
        var injector = new QmpInputInjector(qmp, new InputChannelOptions());

        XBearException error = await Assert.ThrowsAsync<XBearException>(() =>
            injector.TouchAsync(TouchPhase.Down, new InputPoint(1, 2)));

        Assert.Equal(ErrorCategory.Protocol, error.Category);
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public async Task 连接失败_按既有分类原样上抛()
    {
        var qmp = new RecordingQmpClient
        {
            ConnectFailure = new XBearException(ErrorCategory.Dependency, "宿主端口未监听"),
        };
        var injector = new QmpInputInjector(qmp, new InputChannelOptions());

        XBearException error = await Assert.ThrowsAsync<XBearException>(() =>
            injector.TouchAsync(TouchPhase.Down, new InputPoint(1, 2)));

        Assert.Equal(ErrorCategory.Dependency, error.Category);
    }

    [Fact]
    public async Task 取消令牌_取消时按取消原样上抛()
    {
        var qmp = new RecordingQmpClient();
        var injector = new QmpInputInjector(qmp, new InputChannelOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            injector.TouchAsync(TouchPhase.Down, new InputPoint(1, 2), cts.Token));
    }

    [Fact]
    public async Task 接受自定义编码器_注入器按其形状下发()
    {
        var qmp = new RecordingQmpClient();
        IQmpInputEventEncoder encoder = new QmpAbsInputEventEncoder();
        var injector = new QmpInputInjector(qmp, new InputChannelOptions(), encoder);

        await injector.TouchAsync(TouchPhase.Up, new InputPoint(0, 0));

        Assert.Equal(1, qmp.CountOf("input-send-event"));
    }

    private static JsonDocument Serialize(object? payload) =>
        JsonDocument.Parse(JsonSerializer.Serialize(payload));
}
