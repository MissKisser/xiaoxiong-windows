using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Input;

namespace XBear.Core.Tests.Input;

/// <summary>
/// QMP 事件编码测试。序列化结果被逐一断言，任何对事件形状的改动都必须先改这里，
/// 避免改形状后靠真实实例才发现 QEMU 不认。
/// </summary>
public sealed class QmpInputEventEncoderTests
{
    private readonly QmpAbsInputEventEncoder _encoder = new();

    [Fact]
    public void 触摸按下_发出坐标轴与按下键事件()
    {
        IReadOnlyList<object> events = _encoder.EncodeTouch(TouchPhase.Down, new InputPoint(120, 340));

        Assert.Equal(3, events.Count);
        using JsonDocument doc = Serialize(events);

        Assert.Equal("abs", doc.RootElement[0].GetProperty("type").GetString());
        Assert.Equal("x", doc.RootElement[0].GetProperty("data").GetProperty("axis").GetString());
        Assert.Equal(120, doc.RootElement[0].GetProperty("data").GetProperty("value").GetInt32());
        Assert.Equal("y", doc.RootElement[1].GetProperty("data").GetProperty("axis").GetString());
        Assert.Equal(340, doc.RootElement[1].GetProperty("data").GetProperty("value").GetInt32());
        Assert.Equal("btn", doc.RootElement[2].GetProperty("type").GetString());
        Assert.Equal("left", doc.RootElement[2].GetProperty("data").GetProperty("button").GetString());
        Assert.True(doc.RootElement[2].GetProperty("data").GetProperty("down").GetBoolean());
    }

    [Fact]
    public void 触摸移动_只发坐标轴事件()
    {
        IReadOnlyList<object> events = _encoder.EncodeTouch(TouchPhase.Move, new InputPoint(50, 60));

        Assert.Equal(2, events.Count);
        using JsonDocument doc = Serialize(events);
        Assert.Equal("abs", doc.RootElement[0].GetProperty("type").GetString());
        Assert.Equal("abs", doc.RootElement[1].GetProperty("type").GetString());
    }

    [Fact]
    public void 触摸抬起_只发抬起键事件()
    {
        IReadOnlyList<object> events = _encoder.EncodeTouch(TouchPhase.Up, new InputPoint(50, 60));

        Assert.Single(events);
        using JsonDocument doc = Serialize(events);
        Assert.Equal("btn", doc.RootElement[0].GetProperty("type").GetString());
        Assert.False(doc.RootElement[0].GetProperty("data").GetProperty("down").GetBoolean());
    }

    [Fact]
    public void 编码不使用已被取代的触摸类型()
    {
        var events = new List<object>();
        events.AddRange(_encoder.EncodeTouch(TouchPhase.Down, new InputPoint(1, 2)));
        events.AddRange(_encoder.EncodeTouch(TouchPhase.Move, new InputPoint(3, 4)));
        events.AddRange(_encoder.EncodeTouch(TouchPhase.Up, new InputPoint(5, 6)));

        using JsonDocument doc = Serialize(events);
        Assert.DoesNotContain(
            doc.RootElement.EnumerateArray(),
            element => element.GetProperty("type").GetString() == "touch");
    }

    [Fact]
    public void 按键按下_键值为嵌套对象并标记按下()
    {
        VirtualKey key = KeyCodeMap.Get(AndroidKey.Back);
        IReadOnlyList<object> events = _encoder.EncodeKey(key, KeyAction.Press);

        Assert.Single(events);
        using JsonDocument doc = Serialize(events);
        JsonElement data = doc.RootElement[0].GetProperty("data");

        Assert.Equal("key", doc.RootElement[0].GetProperty("type").GetString());
        Assert.True(data.GetProperty("down").GetBoolean());
        Assert.Equal("number", data.GetProperty("key").GetProperty("type").GetString());
        Assert.Equal(key.LinuxKeyCode, data.GetProperty("key").GetProperty("data").GetInt32());
    }

    [Fact]
    public void 按键抬起_键值不变仅标记抬起()
    {
        VirtualKey key = KeyCodeMap.Get(AndroidKey.Enter);
        IReadOnlyList<object> events = _encoder.EncodeKey(key, KeyAction.Release);

        using JsonDocument doc = Serialize(events);
        Assert.False(doc.RootElement[0].GetProperty("data").GetProperty("down").GetBoolean());
        Assert.Equal(key.LinuxKeyCode, doc.RootElement[0].GetProperty("data").GetProperty("key").GetProperty("data").GetInt32());
    }

    [Fact]
    public void 组装命令_默认不带设备名()
    {
        object payload = _encoder.EncodeCommand(null, _encoder.EncodeTouch(TouchPhase.Down, new InputPoint(1, 2)));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.False(doc.RootElement.TryGetProperty("device", out _));
        Assert.Equal(3, doc.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public void 组装命令_指定设备名时写入该字段()
    {
        object payload = _encoder.EncodeCommand("touchscreen", _encoder.EncodeTouch(TouchPhase.Down, new InputPoint(1, 2)));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.Equal("touchscreen", doc.RootElement.GetProperty("device").GetString());
    }

    [Fact]
    public void 实现编码器接口_默认实现与组件一致()
    {
        IQmpInputEventEncoder encoder = new QmpAbsInputEventEncoder();

        IReadOnlyList<object> events = encoder.EncodeTouch(TouchPhase.Up, new InputPoint(0, 0));

        Assert.Single(events);
    }

    [Fact]
    public void 未定义的触摸阶段_按配置违规上抛()
    {
        XBearException error = Assert.Throws<XBearException>(() =>
            _encoder.EncodeTouch((TouchPhase)99, new InputPoint(1, 2)));

        Assert.Equal(ErrorCategory.Spec, error.Category);
    }

    [Fact]
    public void 按键表覆盖全部按键标识且两侧编码均非零()
    {
        foreach (AndroidKey key in Enum.GetValues<AndroidKey>())
        {
            Assert.True(KeyCodeMap.TryGet(key, out VirtualKey virtualKey), $"按键 {key} 缺少编码。");
            Assert.NotEqual(0, virtualKey.AndroidKeyCode);
            Assert.NotEqual(0, virtualKey.LinuxKeyCode);
        }
    }

    [Fact]
    public void 未知按键标识_取编码时按未命中处理()
    {
        Assert.False(KeyCodeMap.TryGet((AndroidKey)999, out _));
    }

    private static JsonDocument Serialize(IReadOnlyList<object> events) =>
        JsonDocument.Parse(JsonSerializer.Serialize(events.ToArray()));
}
