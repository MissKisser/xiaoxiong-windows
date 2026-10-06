using XBear.Core.Spec;

namespace XBear.Core.Abstractions;

/// <summary>QMP 轻客户端，JSON over stdio。</summary>
public interface IQmpClient : IAsyncDisposable
{
    /// <summary>连接 QMP 并完成握手，返回协商出的能力集。</summary>
    /// <param name="port">QMP 宿主端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlySet<string>> ConnectAsync(int port, CancellationToken cancellationToken = default);

    /// <summary>执行一条 QMP 命令并返回 return 对象。</summary>
    /// <param name="command">命令名。</param>
    /// <param name="arguments">命令参数，可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<System.Text.Json.JsonElement> ExecuteAsync(
        string command,
        object? arguments = null,
        CancellationToken cancellationToken = default);

    /// <summary>查询实例运行状态。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> QueryRunningAsync(CancellationToken cancellationToken = default);

    /// <summary>请求 guest 正常关机。</summary>
    /// <param name="timeout">等待关机完成的超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> RequestShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>adb 轻客户端，直连 TCP，不起 adb.exe 子进程。</summary>
public interface IAdbClient : IAsyncDisposable
{
    /// <summary>连接实例并执行握手，取得协议版本。</summary>
    /// <param name="port">实例在宿主上映射的 adb 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<int> ConnectAsync(int port, CancellationToken cancellationToken = default);

    /// <summary>以 root 身份执行 shell 命令。</summary>
    /// <param name="command">命令与参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标准输出，失败时抛出 <see cref="Diagnostics.ErrorCategory.Protocol"/>。</returns>
    Task<string> ShellAsync(string command, CancellationToken cancellationToken = default);

    /// <summary>检查实例是否已以 root 身份运行。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> IsRootAsync(CancellationToken cancellationToken = default);

    /// <summary>推送文件到实例。</summary>
    /// <param name="localPath">宿主文件路径。</param>
    /// <param name="remotePath">实例内目标路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task PushAsync(string localPath, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>从实例拉取文件。</summary>
    /// <param name="remotePath">实例内源路径。</param>
    /// <param name="localPath">宿主目标路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task PullAsync(string remotePath, string localPath, CancellationToken cancellationToken = default);
}

/// <summary>输入通道，向实例投递触摸与按键。</summary>
public interface IInputChannel : IAsyncDisposable
{
    /// <summary>当前实际生效的输入通路。</summary>
    InputChannelKind ActiveChannel { get; }

    /// <summary>探测可用输入通路，按原生优先、投屏降级的顺序选定。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>探测结论，含失败原因，用于向用户如实告知而非静默卡住。</returns>
    Task<InputProbeResult> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 取 guest 显示尺寸。优先用调用方已知的尺寸，其次从 QMP 截图命令回读的图像尺寸得到，
    /// 都没有时返回 null，由调用方决定后续行为。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>guest 显示尺寸，无法确定时为 null。</returns>
    Task<ScreenGeometry?> GetGeometryAsync(CancellationToken cancellationToken = default);

    /// <summary>投递单步触摸，可只发按下、只发移动或只发抬起。</summary>
    /// <param name="point">触摸点，取值为调用方坐标系下的坐标。</param>
    /// <param name="phase">触摸阶段。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>投递结论，含实际生效通路与降级原因。</returns>
    Task<InputDispatchResult> TouchAsync(
        InputPoint point,
        TouchPhase phase,
        CancellationToken cancellationToken = default);

    /// <summary>投递一次完整触摸，由按下与抬起两步组成。</summary>
    /// <param name="point">触摸点，取值为调用方坐标系下的坐标。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>投递结论，含实际生效通路与降级原因。</returns>
    Task<InputDispatchResult> TapAsync(InputPoint point, CancellationToken cancellationToken = default);

    /// <summary>投递带时长的滑动，从起点经若干移动步到达终点后抬起。</summary>
    /// <param name="from">起点，取值为调用方坐标系下的坐标。</param>
    /// <param name="to">终点，取值为调用方坐标系下的坐标。</param>
    /// <param name="duration">滑动时长，必须为正值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>投递结论，含实际生效通路与降级原因。</returns>
    Task<InputDispatchResult> SwipeAsync(
        InputPoint from,
        InputPoint to,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    /// <summary>投递按键。</summary>
    /// <param name="key">按键及其在两条通路下的编码。</param>
    /// <param name="action">按下或抬起。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>投递结论，含实际生效通路与降级原因。</returns>
    Task<InputDispatchResult> KeyAsync(
        VirtualKey key,
        KeyAction action,
        CancellationToken cancellationToken = default);
}

/// <summary>输入通路种类。</summary>
public enum InputChannelKind
{
    /// <summary>尚未探测。</summary>
    Unknown = 0,

    /// <summary>原生 QMP 输入。</summary>
    Native = 1,

    /// <summary>scrcpy 投屏 + input 注入。</summary>
    Projection = 2,

    /// <summary>两条通路均不可用，该镜像不可交互。</summary>
    Unavailable = 3
}

/// <summary>输入通路探测结论。</summary>
/// <param name="Channel">实际生效的通路。</param>
/// <param name="NativeFailure">原生通路的失败原因，可用时为空。</param>
/// <param name="ProjectionFailure">投屏通路的失败原因，可用时为空。</param>
public sealed record InputProbeResult(
    InputChannelKind Channel,
    string? NativeFailure = null,
    string? ProjectionFailure = null);

/// <summary>画面中的一个点，单位与调用方的坐标系一致，尚未换算到 guest 显示分辨率。</summary>
/// <param name="X">横坐标，允许超出画面范围，越界部分由坐标映射夹取。</param>
/// <param name="Y">纵坐标，允许超出画面范围，越界部分由坐标映射夹取。</param>
public readonly record struct InputPoint(double X, double Y);

/// <summary>画面尺寸，单位为像素，可表示宿主画面尺寸或 guest 显示尺寸。</summary>
/// <param name="Width">宽度像素。</param>
/// <param name="Height">高度像素。</param>
public readonly record struct ScreenGeometry(int Width, int Height)
{
    /// <summary>是否为可用于坐标换算的正尺寸。</summary>
    public bool IsValid => Width > 0 && Height > 0;
}

/// <summary>一次触摸的阶段。</summary>
public enum TouchPhase
{
    /// <summary>按下。</summary>
    Down = 0,

    /// <summary>移动。</summary>
    Move = 1,

    /// <summary>抬起。</summary>
    Up = 2
}

/// <summary>按键动作。</summary>
public enum KeyAction
{
    /// <summary>按下。</summary>
    Press = 0,

    /// <summary>抬起。</summary>
    Release = 1
}

/// <summary>按键标识，取实例侧的按键名，与界面上的按键一一对应。</summary>
public enum AndroidKey
{
    /// <summary>返回键。</summary>
    Back = 0,

    /// <summary>主页键。</summary>
    Home,

    /// <summary>任务切换键。</summary>
    AppSwitch,

    /// <summary>回车键。</summary>
    Enter,

    /// <summary>删除键。</summary>
    Delete,

    /// <summary>菜单键。</summary>
    Menu,

    /// <summary>制表键。</summary>
    Tab,

    /// <summary>空格键。</summary>
    Space,

    /// <summary>方向键中心。</summary>
    DpadCenter,

    /// <summary>方向键上。</summary>
    ArrowUp,

    /// <summary>方向键下。</summary>
    ArrowDown,

    /// <summary>方向键左。</summary>
    ArrowLeft,

    /// <summary>方向键右。</summary>
    ArrowRight,

    /// <summary>音量加键。</summary>
    VolumeUp,

    /// <summary>音量减键。</summary>
    VolumeDown,

    /// <summary>电源键。</summary>
    Power
}

/// <summary>
/// 一个按键在两条输入通路下的编码。实例侧按键码供投屏通路的输入命令使用，
/// 宿主侧输入事件码供原生通路的输入事件使用，两者由同一张按键表产出，避免两处各写一份映射。
/// </summary>
/// <param name="Key">按键标识。</param>
/// <param name="AndroidKeyCode">实例侧按键码。</param>
/// <param name="LinuxKeyCode">宿主侧输入事件码。</param>
public readonly record struct VirtualKey(AndroidKey Key, int AndroidKeyCode, int LinuxKeyCode);

/// <summary>一次输入投递的结论。</summary>
/// <param name="Channel">实际生效的通路。</param>
/// <param name="NativeFailure">原生通路的失败原因，可用时为空。</param>
/// <param name="ProjectionFailure">投屏通路的失败原因，可用时为空。</param>
public sealed record InputDispatchResult(
    InputChannelKind Channel,
    string? NativeFailure = null,
    string? ProjectionFailure = null);

/// <summary>设备标识生成器，每实例产出唯一且不复用的标识。</summary>
public interface IDeviceIdentityFactory
{
    /// <summary>生成一组全新标识。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<DeviceIdentity> GenerateAsync(CancellationToken cancellationToken = default);
}

/// <summary>实例配置仓库。</summary>
public interface IInstanceRepository
{
    /// <summary>列出全部实例配置。</summary>
    Task<IReadOnlyList<InstanceSpec>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>按标识读取单个实例配置。</summary>
    /// <param name="id">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<InstanceSpec?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>写入实例配置。</summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SaveAsync(InstanceSpec spec, CancellationToken cancellationToken = default);

    /// <summary>删除实例配置，并把其标识移入墓碑以阻止复用。</summary>
    /// <param name="id">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>查询标识是否已被墓碑记录占用。</summary>
    /// <param name="id">实例标识。</param>
    Task<bool> IsTombstonedAsync(string id);
}