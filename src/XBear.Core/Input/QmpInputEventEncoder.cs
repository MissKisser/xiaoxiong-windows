using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Input;

/// <summary>
/// 把语义化的触摸与按键编码成 QMP 输入命令的参数。抽成接口是为了让事件形状成为可替换的一处，
/// 不同构建的 QEMU 对事件结构的要求存在差异，改形状时只替换实现并由测试锁住序列化结果。
/// </summary>
public interface IQmpInputEventEncoder
{
    /// <summary>编码一次触摸阶段。</summary>
    /// <param name="phase">触摸阶段。</param>
    /// <param name="point">已换算到 guest 显示分辨率的点。</param>
    /// <returns>按顺序发出的事件对象。</returns>
    IReadOnlyList<object> EncodeTouch(TouchPhase phase, InputPoint point);

    /// <summary>编码一次按键动作。</summary>
    /// <param name="key">按键编码。</param>
    /// <param name="action">按下或抬起。</param>
    /// <returns>按顺序发出的事件对象。</returns>
    IReadOnlyList<object> EncodeKey(VirtualKey key, KeyAction action);

    /// <summary>组装输入命令的参数对象。</summary>
    /// <param name="deviceName">目标输入设备名，为空时由 QEMU 路由到当前控制台。</param>
    /// <param name="events">事件序列。</param>
    /// <returns>输入命令的参数对象。</returns>
    object EncodeCommand(string? deviceName, IReadOnlyList<object> events);
}

/// <summary>
/// 默认编码器，用绝对坐标轴事件加按键事件表达触摸。
/// 形状依据是 QEMU 的 qapi/input.json 中 InputEvent 的 abs、btn、key 三个分支，
/// 以及本机 <c>query-qmp-schema</c> 与 <c>input-send-event</c> 的实测回包，逐条确认如下：
/// 事件类型只接受 abs、btn、key，传入 touch 会被拒为「Parameter 'type' does not accept value 'touch'」，
/// 因此触摸用 abs 表达坐标、用 btn 表达按下与抬起，不使用已被取代的 touch 类型；
/// abs 的 data 为 axis 与 value 两个字段；
/// btn 的 data 必须带 button 字段，只传 down 会被拒为「Parameter 'events[0].data.button' is missing」；
/// key 的 data 里键值本身是嵌套对象，data 为 down 与 key，key 取形如 type 为 number、data 为事件码的对象，
/// 直接把键值写成字符串会被拒为「Invalid parameter type for 'events[0].data.key', expected: object」。
/// 至于实例是否真的有绝对坐标输入后端，由 QEMU 在运行时判定，找不到处理器时回
/// 「Input handler not found for event type abs」，该失败会被输入通道按失败阈值如实计入并降级。
/// </summary>
public sealed class QmpAbsInputEventEncoder : IQmpInputEventEncoder
{
    /// <summary>表达触摸所用的按键名，取鼠标左键，与绝对坐标输入后端的常规约定一致。</summary>
    private const string TouchButton = "left";

    /// <summary>
    /// 编码一次触摸阶段。
    /// </summary>
    /// <param name="phase">触摸阶段。</param>
    /// <param name="point">已换算到 guest 显示分辨率的点。</param>
    /// <returns>按下为坐标加按下键，移动为坐标，抬起为抬起键。</returns>
    /// <exception cref="XBearException">阶段取值非法时抛出，分类为 <see cref="ErrorCategory.Spec"/>。</exception>
    public IReadOnlyList<object> EncodeTouch(TouchPhase phase, InputPoint point)
    {
        return phase switch
        {
            TouchPhase.Down => new object[] { Axis("x", point.X), Axis("y", point.Y), Button(true) },
            TouchPhase.Move => new object[] { Axis("x", point.X), Axis("y", point.Y) },
            TouchPhase.Up => new object[] { Button(false) },
            _ => throw new XBearException(ErrorCategory.Spec, $"未知的触摸阶段：{phase}。"),
        };
    }

    /// <summary>
    /// 编码一次按键动作。
    /// </summary>
    /// <param name="key">按键编码，取其中的宿主侧输入事件码。</param>
    /// <param name="action">按下或抬起。</param>
    /// <returns>单个按键事件对象。</returns>
    public IReadOnlyList<object> EncodeKey(VirtualKey key, KeyAction action)
    {
        object payload = new Dictionary<string, object>
        {
            ["type"] = "key",
            ["data"] = new Dictionary<string, object>
            {
                ["down"] = action == KeyAction.Press,
                ["key"] = new Dictionary<string, object>
                {
                    ["type"] = "number",
                    ["data"] = key.LinuxKeyCode,
                },
            },
        };

        return new[] { payload };
    }

    /// <summary>
    /// 组装输入命令的参数对象。设备名为空时不写入该字段，由 QEMU 决定事件去向；
    /// 这是因为按设备名定位在实测中对真实存在的输入设备也会回 DeviceNotFound，不可依赖。
    /// </summary>
    /// <param name="deviceName">目标输入设备名，为空时不下发。</param>
    /// <param name="events">事件序列。</param>
    /// <returns>输入命令的参数对象。</returns>
    public object EncodeCommand(string? deviceName, IReadOnlyList<object> events)
    {
        var payload = new Dictionary<string, object>
        {
            ["events"] = events.ToArray(),
        };

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            payload["device"] = deviceName;
        }

        return payload;
    }

    private static object Axis(string axis, double value) =>
        new Dictionary<string, object>
        {
            ["type"] = "abs",
            ["data"] = new Dictionary<string, object>
            {
                ["axis"] = axis,
                ["value"] = (int)Math.Round(value, MidpointRounding.AwayFromZero),
            },
        };

    private static object Button(bool down) =>
        new Dictionary<string, object>
        {
            ["type"] = "btn",
            ["data"] = new Dictionary<string, object>
            {
                ["button"] = TouchButton,
                ["down"] = down,
            },
        };
}
