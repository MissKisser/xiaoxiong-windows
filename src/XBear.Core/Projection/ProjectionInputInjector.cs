using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Input;

namespace XBear.Core.Projection;

/// <summary>
/// 投屏输入注入器：把投屏画面坐标（帧缓冲像素坐标系）换算到实例输入设备的坐标域
/// （如 virtio-tablet 的 0..32767 绝对坐标域），并经 QMP input-send-event 命令注入事件。
/// 键盘事件同样经 QMP 事件编码器注入，按下与抬起保持独立投递。
/// </summary>
public sealed class ProjectionInputInjector
{
    /// <summary>virtio-tablet 的默认坐标域上界。</summary>
    public const int DefaultPointerMax = 32767;

    private static readonly TimeSpan DefaultDispatchTimeout = TimeSpan.FromSeconds(5);

    private readonly IQmpClient _qmpClient;
    private readonly ICoordinateMapper _coordinateMapper;
    private readonly IQmpInputEventEncoder _encoder;
    private readonly Func<ScreenGeometry> _currentGeometry;
    private readonly int _pointerMax;
    private readonly string? _deviceName;
    private readonly TimeSpan _dispatchTimeout;

    /// <summary>
    /// 初始化投屏输入注入器。
    /// </summary>
    /// <param name="qmpClient">已连接的 QMP 客户端。</param>
    /// <param name="currentGeometry">获取当前画面尺寸的委托。</param>
    /// <param name="pointerMax">绝对坐标域上界，默认为 32767。</param>
    /// <param name="deviceName">输入设备名，为空时由 QEMU 路由到当前控制台。</param>
    /// <param name="coordinateMapper">坐标映射组件，为空时使用默认线性映射。</param>
    /// <param name="encoder">QMP 事件编码器，为空时使用默认绝对坐标编码器。</param>
    /// <param name="dispatchTimeout">单次注入超时时间。</param>
    public ProjectionInputInjector(
        IQmpClient qmpClient,
        Func<ScreenGeometry> currentGeometry,
        int pointerMax = DefaultPointerMax,
        string? deviceName = null,
        ICoordinateMapper? coordinateMapper = null,
        IQmpInputEventEncoder? encoder = null,
        TimeSpan? dispatchTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(qmpClient);
        ArgumentNullException.ThrowIfNull(currentGeometry);

        if (pointerMax <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pointerMax), "指针坐标域上界必须为正整数。");
        }

        _qmpClient = qmpClient;
        _currentGeometry = currentGeometry;
        _pointerMax = pointerMax;
        _deviceName = deviceName;
        _coordinateMapper = coordinateMapper ?? new CoordinateMapper();
        _encoder = encoder ?? new QmpAbsInputEventEncoder();
        _dispatchTimeout = dispatchTimeout is { } t && t > TimeSpan.Zero ? t : DefaultDispatchTimeout;
    }

    /// <summary>
    /// 将帧缓冲像素坐标换算为输入设备的绝对坐标。
    /// </summary>
    /// <param name="framebufferPoint">帧缓冲像素坐标。</param>
    /// <returns>换算并夹取到 0..pointerMax 范围内的整数坐标。</returns>
    public InputPoint MapPoint(InputPoint framebufferPoint)
    {
        ScreenGeometry source = _currentGeometry();
        if (!source.IsValid)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "当前画面尺寸无效，无法换算指针坐标。",
                "请确认投屏会话已取到首帧画面后重试。");
        }

        // virtio-tablet 的绝对坐标域为 0..pointerMax，像素跨度为 pointerMax + 1。
        var target = new ScreenGeometry(_pointerMax + 1, _pointerMax + 1);
        return _coordinateMapper.Map(framebufferPoint, source, target);
    }

    /// <summary>
    /// 投递单步指针动作（按下、移动或抬起）。
    /// </summary>
    /// <param name="framebufferPoint">帧缓冲像素坐标。</param>
    /// <param name="phase">触摸阶段。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task TouchAsync(
        InputPoint framebufferPoint,
        TouchPhase phase,
        CancellationToken cancellationToken = default)
    {
        InputPoint mapped = MapPoint(framebufferPoint);
        IReadOnlyList<object> events = _encoder.EncodeTouch(phase, mapped);
        await SendAsync(events, Describe(phase), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 投递一次完整点击（按下与抬起两步）。
    /// </summary>
    /// <param name="framebufferPoint">帧缓冲像素坐标。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task TapAsync(
        InputPoint framebufferPoint,
        CancellationToken cancellationToken = default)
    {
        InputPoint mapped = MapPoint(framebufferPoint);
        var events = new List<object>(_encoder.EncodeTouch(TouchPhase.Down, mapped));
        events.AddRange(_encoder.EncodeTouch(TouchPhase.Up, mapped));
        await SendAsync(events, "指针点击", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 投递一次按键动作（按下或抬起）。
    /// </summary>
    /// <param name="key">按键及其编码。</param>
    /// <param name="action">按下或抬起。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task KeyAsync(
        VirtualKey key,
        KeyAction action,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<object> events = _encoder.EncodeKey(key, action);
        await SendAsync(events, $"按键 {key.Key}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 投递一次标准 Android 按键动作。
    /// </summary>
    /// <param name="key">Android 按键标识。</param>
    /// <param name="action">按下或抬起。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task KeyAsync(
        AndroidKey key,
        KeyAction action,
        CancellationToken cancellationToken = default)
    {
        if (!KeyCodeMap.TryGet(key, out VirtualKey virtualKey))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"未知的按键标识：{key}。",
                "请确认该按键已在 KeyCodeMap 中定义。");
        }

        await KeyAsync(virtualKey, action, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(
        IReadOnlyList<object> events,
        string action,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_dispatchTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        object payload = _encoder.EncodeCommand(_deviceName, events);

        try
        {
            var result = await _qmpClient
                .ExecuteAsync("input-send-event", payload, linked.Token)
                .ConfigureAwait(false);
            QmpInputInjector.ThrowIfQmpError(result, action);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new XBearException(
                ErrorCategory.Timeout,
                $"注入{action}超时。",
                "请检查实例状态与 QMP 端口后重试。",
                ex);
        }
        catch (XBearException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new XBearException(
                ErrorCategory.Protocol,
                $"注入{action}失败：{ex.Message}",
                "请确认实例处于运行态并具备可用的输入后端。",
                ex);
        }
    }

    private static string Describe(TouchPhase phase) => phase switch
    {
        TouchPhase.Down => "指针按下",
        TouchPhase.Move => "指针移动",
        TouchPhase.Up => "指针抬起",
        _ => "指针",
    };
}
