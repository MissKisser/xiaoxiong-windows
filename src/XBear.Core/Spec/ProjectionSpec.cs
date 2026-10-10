using System.Text.Json;
using System.Text.Json.Serialization;
using XBear.Core.Serialization;

namespace XBear.Core.Spec;

/// <summary>
/// 投屏会话，字段与 spec/schema/projection.schema.json 严格对应。
/// 投屏负责呈现画面、输入通道负责操控，二者彼此独立，投屏会话建立与否不决定输入通道是否可用。
/// 取帧与注入的实现方式不进契约，只允许写进 platformConfig 逃生舱。
/// </summary>
public sealed class ProjectionSpec
{
    /// <summary>投屏契约自身的版本，与规格层版本同源。</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0.0";

    /// <summary>投屏会话的全局唯一标识，会话结束后进墓碑记录不得自动复用。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>被投屏实例的标识，引用实例契约的 id。</summary>
    [JsonPropertyName("instanceRef")]
    public string InstanceRef { get; set; } = string.Empty;

    /// <summary>呈现画面的语义，是投屏会话唯一不可省略的部分。</summary>
    [JsonPropertyName("video")]
    public ProjectionVideo Video { get; set; } = new();

    /// <summary>本会话承载的输入语义，缺省表示这份投屏只看不送输入。</summary>
    [JsonPropertyName("input")]
    public ProjectionInput? Input { get; set; }

    /// <summary>投屏会话状态，只能沿 pending → active → stopped 单向推进。</summary>
    [JsonPropertyName("state")]
    public ProjectionState State { get; set; } = ProjectionState.Pending;

    /// <summary>首次进入 active 的时刻，仍处于 pending 时为 null。</summary>
    [JsonPropertyName("startedAt")]
    public string? StartedAt { get; set; }

    /// <summary>进入 stopped 的时刻，仍处于 pending 或 active 时为 null。</summary>
    [JsonPropertyName("endedAt")]
    public string? EndedAt { get; set; }

    /// <summary>平台特有字段逃生舱，通用契约不解析其内部结构。</summary>
    [JsonPropertyName("platformConfig")]
    public JsonElement? PlatformConfig { get; set; }

    /// <summary>
    /// 判断该会话是否正在呈现画面。只有 active 会话的画面可见。
    /// </summary>
    /// <returns>处于 active 时为 true。</returns>
    public bool IsPresenting() => State == ProjectionState.Active;

    /// <summary>
    /// 判断该会话是否已终止。实例停止时处于 active 的会话自动转 stopped。
    /// </summary>
    /// <returns>处于 stopped 时为 true。</returns>
    public bool IsTerminated() => State == ProjectionState.Stopped;
}

/// <summary>投屏呈现画面的宽高与呈现节奏。</summary>
public sealed class ProjectionVideo
{
    /// <summary>呈现画面的宽度，单位像素。</summary>
    [JsonPropertyName("width")]
    public int Width { get; set; }

    /// <summary>呈现画面的高度，单位像素。</summary>
    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>呈现节奏，目标与实测分列，缺省表示本会话未登记帧率。</summary>
    [JsonPropertyName("fps")]
    public ProjectionFrameRate? Fps { get; set; }
}

/// <summary>
/// 呈现节奏。目标只声明意图，实测只登记真实观测，两者不得互相推导。
/// </summary>
public sealed class ProjectionFrameRate
{
    /// <summary>目标帧率，单位 fps，不作为验收依据。</summary>
    [JsonPropertyName("target")]
    public int Target { get; set; }

    /// <summary>
    /// 实测帧率，单位 fps。只能来自真实观测，尚未测量时保持 null，
    /// 不得由目标帧率推导，也不得用 0 或默认值占位。
    /// 契约要求该字段一旦声明 fps 就必须存在，因此写出时保留显式 null 而不省略。
    /// </summary>
    [JsonPropertyName("measured")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Measured { get; set; }
}

/// <summary>投屏会话承载的输入语义。通道说清投递通路，设备说清承载什么设备。</summary>
public sealed class ProjectionInput
{
    /// <summary>输入投递所走的通道，缺省表示本会话未声明投递通路。</summary>
    [JsonPropertyName("channel")]
    public ProjectionInputChannel? Channel { get; set; }

    /// <summary>本会话承载的输入设备类型，契约要求取值互不重复。</summary>
    [JsonPropertyName("devices")]
    public List<ProjectionInputDeviceKind>? Devices { get; set; }

    /// <summary>指针的坐标与事件约定。</summary>
    [JsonPropertyName("pointer")]
    public ProjectionPointer? Pointer { get; set; }

    /// <summary>键盘的按键编码表约定。</summary>
    [JsonPropertyName("keyboard")]
    public ProjectionKeyboard? Keyboard { get; set; }
}

/// <summary>
/// 指针的坐标域与上界。坐标域决定宿主投递的是像素坐标还是归一化坐标，
/// 两端必须从同一份会话描述上取得一致结论，任一端不得私自换算。
/// </summary>
public sealed class ProjectionPointer
{
    /// <summary>指针坐标域，缺省表示本会话未声明坐标域。</summary>
    [JsonPropertyName("domain")]
    public ProjectionPointerDomain? Domain { get; set; }

    /// <summary>坐标域上界，仅在绝对值域时有意义。</summary>
    [JsonPropertyName("max")]
    public int? Max { get; set; }

    /// <summary>
    /// 判断是否使用设备原生绝对坐标域。使用绝对值域时宿主须按当前画面分辨率线性映射后再投递。
    /// </summary>
    /// <returns>使用绝对值域时为 true。</returns>
    public bool IsAbsoluteDomain() => Domain == ProjectionPointerDomain.Absolute;
}

/// <summary>键盘的按键编码表约定。两端必须约定同一张编码表，编码表本身不入契约。</summary>
public sealed class ProjectionKeyboard
{
    /// <summary>
    /// 按键编码表。契约取值域为 usb-hid 与 linux-evdev，
    /// 字面量含连字符故以字符串承载，各端按本字段自行解析编码表。
    /// </summary>
    [JsonPropertyName("layout")]
    public string? Layout { get; set; }
}

/// <summary>
/// 投屏会话状态。pending 会话已建立但尚未开始取帧，画面不可见；
/// stopped 会话已终止，画面不再更新。
/// </summary>
[JsonConverter(typeof(LowerCaseEnumConverter<ProjectionState>))]
public enum ProjectionState
{
    /// <summary>会话已建立但尚未开始取帧。</summary>
    Pending = 0,

    /// <summary>正在呈现画面。</summary>
    Active = 1,

    /// <summary>会话已终止，画面不再更新。</summary>
    Stopped = 2
}

/// <summary>输入投递所走的通道，取值域与镜像契约的实测输入通道一致。</summary>
[JsonConverter(typeof(LowerCaseEnumConverter<ProjectionInputChannel>))]
public enum ProjectionInputChannel
{
    /// <summary>原生输入。</summary>
    Native = 0,

    /// <summary>经投屏注入。</summary>
    Scrcpy = 1,

    /// <summary>本会话不投递输入。</summary>
    None = 2
}

/// <summary>投屏会话承载的输入设备类型，与投递通路是两个维度。</summary>
[JsonConverter(typeof(LowerCaseEnumConverter<ProjectionInputDeviceKind>))]
public enum ProjectionInputDeviceKind
{
    /// <summary>指针，涵盖触摸与鼠标。</summary>
    Pointer = 0,

    /// <summary>键盘。</summary>
    Keyboard = 1
}

/// <summary>指针坐标域，决定投递指针事件时使用的坐标取值空间。</summary>
[JsonConverter(typeof(LowerCaseEnumConverter<ProjectionPointerDomain>))]
public enum ProjectionPointerDomain
{
    /// <summary>设备原生绝对坐标域，宿主须按当前画面分辨率线性映射到该域后再投递。</summary>
    Absolute = 0,

    /// <summary>归一化坐标域，由接收端按画面分辨率换算。</summary>
    Normalized = 1
}
