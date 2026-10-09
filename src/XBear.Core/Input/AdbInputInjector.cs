using System.Globalization;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Input;

/// <summary>
/// 投屏侧投递器，走实例内的输入命令注入触摸与按键。
/// 该通路依赖实例内存在输入命令，且实例需以足够权限运行才能注入；
/// 按键只能整体送达一次，输入命令不区分按下与抬起，两种动作发出的命令相同。
/// </summary>
public sealed class AdbInputInjector
{
    private readonly IAdbClient _adbClient;
    private readonly InputChannelOptions _options;
    private bool _connected;

    /// <summary>
    /// 初始化投屏侧投递器。
    /// </summary>
    /// <param name="adbClient">adb 客户端。</param>
    /// <param name="options">输入通道参数，提供端口与超时。</param>
    public AdbInputInjector(IAdbClient adbClient, InputChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(adbClient);
        ArgumentNullException.ThrowIfNull(options);

        _adbClient = adbClient;
        _options = options;
    }

    /// <summary>
    /// 确保 adb 连接就绪并具备注入所需权限。已建立过连接时仍会再发起一次连接，
    /// 这样重复投递能真实触达实例；客户端在已连接状态下拒绝重复连接属于预期情况，复用既有连接继续即可。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <exception cref="XBearException">连接失败或权限不足时抛出，分类为 <see cref="ErrorCategory.Protocol"/>。</exception>
    public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _adbClient.ConnectAsync(_options.AdbPort, cancellationToken).ConfigureAwait(false);
            _connected = true;
        }
        catch (XBearException) when (_connected)
        {
            // 已经连上过，重复连接被拒不影响后续投递。
        }

        if (!await _adbClient.IsRootAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new XBearException(
                ErrorCategory.Protocol,
                "实例未以足够权限运行，投屏通路无法注入输入。",
                "请使用已开启足够权限的镜像，或改用原生通路。");
        }
    }

    /// <summary>投递一次触摸阶段。</summary>
    /// <param name="phase">触摸阶段。</param>
    /// <param name="point">已换算到 guest 显示分辨率的点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public async Task TouchAsync(
        TouchPhase phase,
        InputPoint point,
        CancellationToken cancellationToken = default)
    {
        await RunAsync(BuildTouchCommand(phase, point), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>投递一次按键动作。</summary>
    /// <param name="key">按键编码，取其中的实例侧按键码。</param>
    /// <param name="action">按下或抬起，输入命令不区分两者。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public async Task KeyAsync(
        VirtualKey key,
        KeyAction action,
        CancellationToken cancellationToken = default)
    {
        await RunAsync(BuildKeyCommand(key), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>投递一次带时长的滑动，由实例内的滑动命令一次完成。</summary>
    /// <param name="from">起点。</param>
    /// <param name="to">终点。</param>
    /// <param name="duration">滑动时长，必须为正值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public async Task SwipeAsync(
        InputPoint from,
        InputPoint to,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        await RunAsync(BuildSwipeCommand(from, to, duration), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>构造单步触摸的输入命令串。</summary>
    /// <param name="phase">触摸阶段。</param>
    /// <param name="point">已换算到 guest 显示分辨率的点。</param>
    /// <returns>输入命令串。</returns>
    /// <exception cref="XBearException">阶段取值非法时抛出，分类为 <see cref="ErrorCategory.Spec"/>。</exception>
    public static string BuildTouchCommand(TouchPhase phase, InputPoint point)
    {
        string stage = phase switch
        {
            TouchPhase.Down => "DOWN",
            TouchPhase.Move => "MOVE",
            TouchPhase.Up => "UP",
            _ => throw new XBearException(ErrorCategory.Spec, $"未知的触摸阶段：{phase}。"),
        };

        int x = (int)Math.Clamp(Math.Round(point.X, MidpointRounding.AwayFromZero), 0, int.MaxValue);
        int y = (int)Math.Clamp(Math.Round(point.Y, MidpointRounding.AwayFromZero), 0, int.MaxValue);
        return FormattableString.Invariant($"input motionevent {stage} {x} {y}");
    }

    /// <summary>构造带时长滑动的输入命令串。</summary>
    /// <param name="from">起点。</param>
    /// <param name="to">终点。</param>
    /// <param name="duration">滑动时长，必须为正。</param>
    /// <returns>输入命令串。</returns>
    /// <exception cref="XBearException">时长非正时抛出，分类为 <see cref="ErrorCategory.Spec"/>。</exception>
    public static string BuildSwipeCommand(InputPoint from, InputPoint to, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "滑动时长必须为正值。",
                "请在界面或调用方指定一个大于零的滑动时长。");
        }

        int milliseconds = (int)Math.Clamp(Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero), 1, int.MaxValue);
        int x1 = (int)Math.Clamp(Math.Round(from.X, MidpointRounding.AwayFromZero), 0, int.MaxValue);
        int y1 = (int)Math.Clamp(Math.Round(from.Y, MidpointRounding.AwayFromZero), 0, int.MaxValue);
        int x2 = (int)Math.Clamp(Math.Round(to.X, MidpointRounding.AwayFromZero), 0, int.MaxValue);
        int y2 = (int)Math.Clamp(Math.Round(to.Y, MidpointRounding.AwayFromZero), 0, int.MaxValue);
        return FormattableString.Invariant($"input swipe {x1} {y1} {x2} {y2} {milliseconds}");
    }

    /// <summary>构造按键的输入命令串。</summary>
    /// <param name="key">按键编码，取其中的实例侧按键码。</param>
    /// <returns>输入命令串。</returns>
    public static string BuildKeyCommand(VirtualKey key) =>
        FormattableString.Invariant($"input keyevent {key.AndroidKeyCode.ToString(CultureInfo.InvariantCulture)}");

    private async Task RunAsync(string command, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.DispatchTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        await EnsureConnectedAsync(linked.Token).ConfigureAwait(false);

        try
        {
            await _adbClient.ShellAsync(command, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new XBearException(ErrorCategory.Timeout, $"注入超时：{command}", inner: ex);
        }
        catch (XBearException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new XBearException(ErrorCategory.Protocol, $"注入失败：{command}", inner: ex);
        }
    }
}
