using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Input;

/// <summary>输入通道参数。</summary>
public sealed class InputChannelOptions
{
    /// <summary>原生通路允许的连续失败次数，达到该次数后降级，默认 3 次。</summary>
    public int NativeFailureThreshold { get; init; } = 3;

    /// <summary>QMP 宿主端口，探测原生通路时使用。</summary>
    public int QmpPort { get; init; }

    /// <summary>adb 宿主端口，探测投屏通路时使用。</summary>
    public int AdbPort { get; init; }

    /// <summary>单次探测的等待上限，超时按该通路失败处理。</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>单次注入的等待上限，超时按该通路失败处理。</summary>
    public TimeSpan DispatchTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 原生通路指定的目标输入设备名。为空表示不下发该字段，由 QEMU 把事件路由到当前控制台；
    /// 实测中按设备名定位即便对真实存在的输入设备也可能被拒，故默认不指定。
    /// </summary>
    public string? QmpInputDeviceName { get; init; }

    /// <summary>调用方的画面尺寸，用于把触摸点换算到 guest 显示分辨率，未知时按不缩放处理。</summary>
    public ScreenGeometry HostGeometry { get; init; }

    /// <summary>调用方已知的 guest 显示尺寸，为空时从 QMP 查询显示尺寸。</summary>
    public ScreenGeometry? GuestGeometry { get; init; }

    /// <summary>为确定 guest 显示尺寸而截图时写入的目录，默认为系统临时目录。</summary>
    public string ScreenshotDirectory { get; init; } = Path.GetTempPath();

    /// <summary>滑动被拆成移动事件时的步长间隔。</summary>
    public TimeSpan SwipeStepInterval { get; init; } = TimeSpan.FromMilliseconds(16);
}

/// <summary>
/// 探测式输入通道。既按原生优先、投屏降级的顺序确定实际生效通路，也向实例实际投递触摸与按键：
/// 通路 A 通过 QMP 向 guest 私有输入后端注入事件，需要镜像自带 ranchu 或 goldfish 类输入驱动；
/// 通路 B 即 scrcpy 推流配合实例内输入命令注入。
/// 投递阶段沿用探测阶段的判定口径：原生通路成功就用原生，连续失败达到阈值后降级到投屏通路并如实回报原因，
/// 阈值以内不上抛也不静默吞掉，两条通路都不通时按输入通道不可用上抛。
/// </summary>
public sealed class ProbingInputChannel : IInputChannel
{
    /// <summary>单次滑动最多拆出的移动事件数，防止超长时长把宿主与实例之间的往返打满。</summary>
    private const int MaxSwipeSteps = 32;

    private readonly IQmpClient _qmpClient;
    private readonly IAdbClient _adbClient;
    private readonly InputChannelOptions _options;
    private readonly QmpInputInjector _nativeInjector;
    private readonly AdbInputInjector _projectionInjector;
    private readonly QmpDisplayGeometrySource _geometrySource;
    private readonly ICoordinateMapper _coordinateMapper;
    private int _consecutiveNativeFailures;
    private string? _lastNativeFailure;
    private bool _disposed;

    /// <summary>
    /// 初始化探测式输入通道。
    /// </summary>
    /// <param name="qmpClient">QMP 客户端，用于原生通路注入。</param>
    /// <param name="adbClient">adb 客户端，用于投屏通路注入。</param>
    /// <param name="options">探测与投递参数，为 null 时使用默认值。</param>
    /// <param name="encoder">原生通路的事件编码器，为 null 时使用默认的绝对坐标编码器。</param>
    /// <param name="coordinateMapper">坐标映射组件，为 null 时使用默认的线性映射。</param>
    public ProbingInputChannel(
        IQmpClient qmpClient,
        IAdbClient adbClient,
        InputChannelOptions? options = null,
        IQmpInputEventEncoder? encoder = null,
        ICoordinateMapper? coordinateMapper = null)
    {
        ArgumentNullException.ThrowIfNull(qmpClient);
        ArgumentNullException.ThrowIfNull(adbClient);

        _qmpClient = qmpClient;
        _adbClient = adbClient;
        _options = options ?? new InputChannelOptions();
        _nativeInjector = new QmpInputInjector(qmpClient, _options, encoder);
        _projectionInjector = new AdbInputInjector(adbClient, _options);
        _coordinateMapper = coordinateMapper ?? new CoordinateMapper();
        _geometrySource = new QmpDisplayGeometrySource(qmpClient, _options, () => _options.GuestGeometry);
    }

    /// <summary>当前实际生效的输入通路。</summary>
    public InputChannelKind ActiveChannel { get; private set; } = InputChannelKind.Unknown;

    /// <summary>
    /// 原生通路跨调用累计的连续失败次数，成功一次即清零；
    /// 达到阈值后后续探测不再重试原生通路。
    /// </summary>
    public int ConsecutiveNativeFailures => _consecutiveNativeFailures;

    /// <summary>
    /// 探测可用输入通路。原生通路连续失败达到阈值前不降级，
    /// 避免偶发失败误判导致本可用的原生通路被弃用；累计失败达到阈值后，
    /// 后续探测不再重试原生通路，直接按降级逻辑走投屏通路。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>探测结论，含每条通路的失败原因。</returns>
    public async Task<InputProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        int threshold = Math.Max(1, _options.NativeFailureThreshold);
        string? nativeFailure = _lastNativeFailure;

        // 门控按跨调用累计的失败次数判定：达到阈值说明原生通路已被判定不可用，不再重复消耗时间重试。
        if (_consecutiveNativeFailures < threshold)
        {
            for (int attempt = 0; attempt < threshold; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await TryNativeAsync(cancellationToken).ConfigureAwait(false);
                    _consecutiveNativeFailures = 0;
                    _lastNativeFailure = null;
                    ActiveChannel = InputChannelKind.Native;
                    return new InputProbeResult(InputChannelKind.Native);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _consecutiveNativeFailures++;
                    nativeFailure = _lastNativeFailure = Describe(ex);
                }
            }
        }

        try
        {
            await TryProjectionAsync(cancellationToken).ConfigureAwait(false);
            ActiveChannel = InputChannelKind.Projection;
            return new InputProbeResult(InputChannelKind.Projection, nativeFailure);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ActiveChannel = InputChannelKind.Unavailable;
            return new InputProbeResult(
                InputChannelKind.Unavailable,
                nativeFailure ?? "原生通路未完成探测。",
                Describe(ex));
        }
    }

    /// <summary>
    /// 取 guest 显示尺寸。调用方已给出尺寸时直接返回，否则向 QEMU 索取当前画面并从其头部读出宽高。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>guest 显示尺寸，无法确定时为 null。</returns>
    public async Task<ScreenGeometry?> GetGeometryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_options.GuestGeometry is { } configured)
        {
            return configured;
        }

        return await _geometrySource.TryGetAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 投递单步触摸。坐标先由调用方坐标系换算到 guest 显示分辨率并夹取，再按当前通路注入。
    /// </summary>
    /// <param name="point">触摸点，取值为调用方坐标系下的坐标。</param>
    /// <param name="phase">触摸阶段。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>投递结论，含实际生效通路与降级原因。</returns>
    public async Task<InputDispatchResult> TouchAsync(
        InputPoint point,
        TouchPhase phase,
        CancellationToken cancellationToken = default)
    {
        InputPoint guestPoint = await ToGuestPointAsync(point, cancellationToken).ConfigureAwait(false);

        return await DispatchAsync(
            token => _nativeInjector.TouchAsync(phase, guestPoint, token),
            token => _projectionInjector.TouchAsync(phase, guestPoint, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 投递一次完整触摸，由按下与抬起两步组成。任一步失败都按该通路的失败处理。
    /// </summary>
    /// <param name="point">触摸点，取值为调用方坐标系下的坐标。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>投递结论，含实际生效通路与降级原因。</returns>
    public async Task<InputDispatchResult> TapAsync(InputPoint point, CancellationToken cancellationToken = default)
    {
        InputPoint guestPoint = await ToGuestPointAsync(point, cancellationToken).ConfigureAwait(false);

        return await DispatchAsync(
            async token =>
            {
                await _nativeInjector.TouchAsync(TouchPhase.Down, guestPoint, token).ConfigureAwait(false);
                await _nativeInjector.TouchAsync(TouchPhase.Up, guestPoint, token).ConfigureAwait(false);
            },
            async token =>
            {
                await _projectionInjector.TouchAsync(TouchPhase.Down, guestPoint, token).ConfigureAwait(false);
                await _projectionInjector.TouchAsync(TouchPhase.Up, guestPoint, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 投递带时长的滑动。原生通路按步长插值为若干移动事件后抬起，
    /// 投屏通路由实例内的滑动命令一次完成，时长随之透传。
    /// </summary>
    /// <param name="from">起点，取值为调用方坐标系下的坐标。</param>
    /// <param name="to">终点，取值为调用方坐标系下的坐标。</param>
    /// <param name="duration">滑动时长，必须为正值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>投递结论，含实际生效通路与降级原因。</returns>
    public async Task<InputDispatchResult> SwipeAsync(
        InputPoint from,
        InputPoint to,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "滑动时长必须为正值。",
                "请在界面或调用方指定一个大于零的滑动时长。");
        }

        InputPoint guestFrom = await ToGuestPointAsync(from, cancellationToken).ConfigureAwait(false);
        InputPoint guestTo = await ToGuestPointAsync(to, cancellationToken).ConfigureAwait(false);

        return await DispatchAsync(
            token => SwipeNativeAsync(guestFrom, guestTo, duration, token),
            token => _projectionInjector.SwipeAsync(guestFrom, guestTo, duration, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 投递按键。原生通路可区分按下与抬起，投屏通路的按键命令一次性送达，两种动作发出的命令相同。
    /// </summary>
    /// <param name="key">按键及其在两条通路下的编码。</param>
    /// <param name="action">按下或抬起。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>投递结论，含实际生效通路与降级原因。</returns>
    public async Task<InputDispatchResult> KeyAsync(
        VirtualKey key,
        KeyAction action,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return await DispatchAsync(
            token => _nativeInjector.KeyAsync(key, action, token),
            token => _projectionInjector.KeyAsync(key, action, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 构造一次完整触摸的 QMP 事件序列：按下为两个绝对坐标事件加按下键，抬起为抬起键。
    /// </summary>
    /// <param name="x">触摸横坐标。</param>
    /// <param name="y">触摸纵坐标。</param>
    /// <param name="device">目标输入设备名，为空时不下发该字段。</param>
    /// <returns>QMP <c>input-send-event</c> 命令的参数对象。</returns>
    public static object BuildTouchEventPayload(int x, int y, string? device = null)
    {
        var encoder = new QmpAbsInputEventEncoder();
        var point = new InputPoint(x, y);
        var events = new List<object>(encoder.EncodeTouch(TouchPhase.Down, point));
        events.AddRange(encoder.EncodeTouch(TouchPhase.Up, point));
        return encoder.EncodeCommand(device, events);
    }

    /// <summary>
    /// 释放通道所持有的 QMP 与 adb 客户端。
    /// </summary>
    /// <returns>异步任务。</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _qmpClient.DisposeAsync().ConfigureAwait(false);
        await _adbClient.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 按当前生效通路投递一次动作。原生通路成功即返回；失败时累计连续失败次数，
    /// 未达阈值按原样上抛本次失败而不静默降级，达到阈值则本次直接改走投屏通路并在结论里带回原生失败原因；
    /// 投屏通路再失败时把两条通路的失败原因一并上抛，并归类为输入通道不可用。
    /// </summary>
    /// <param name="nativeAction">原生通路的投递动作。</param>
    /// <param name="projectionAction">投屏通路的投递动作。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>投递结论。</returns>
    /// <exception cref="XBearException">尚未探测出可用通路，或两条通路都投递失败时抛出。</exception>
    private async Task<InputDispatchResult> DispatchAsync(
        Func<CancellationToken, Task> nativeAction,
        Func<CancellationToken, Task> projectionAction,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (ActiveChannel == InputChannelKind.Unknown)
        {
            InputProbeResult probed = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (probed.Channel == InputChannelKind.Unavailable)
            {
                throw new XBearException(
                    ErrorCategory.Dependency,
                    "两条输入通路均不可用，无法投递输入。",
                    $"原生通路：{probed.NativeFailure ?? "未完成探测"}；投屏通路：{probed.ProjectionFailure ?? "未完成探测"}");
            }
        }

        int threshold = Math.Max(1, _options.NativeFailureThreshold);

        if (ActiveChannel == InputChannelKind.Native)
        {
            try
            {
                await nativeAction(cancellationToken).ConfigureAwait(false);
                _consecutiveNativeFailures = 0;
                _lastNativeFailure = null;
                return new InputDispatchResult(InputChannelKind.Native);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _consecutiveNativeFailures++;
                _lastNativeFailure = Describe(ex);

                if (_consecutiveNativeFailures < threshold)
                {
                    // 未达阈值说明原生通路仍被判定可用，本次失败如实上抛，不把偶发失败当作通路不可用。
                    throw ex is XBearException xb
                        ? xb
                        : new XBearException(ErrorCategory.Protocol, $"注入失败：{_lastNativeFailure}", inner: ex);
                }
            }
        }

        try
        {
            await projectionAction(cancellationToken).ConfigureAwait(false);
            ActiveChannel = InputChannelKind.Projection;
            return new InputDispatchResult(InputChannelKind.Projection, _lastNativeFailure);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            string projectionFailure = Describe(ex);
            ActiveChannel = InputChannelKind.Unavailable;
            throw new XBearException(
                ErrorCategory.Protocol,
                $"注入失败，原生通路：{_lastNativeFailure ?? "未尝试"}；投屏通路：{projectionFailure}",
                "请确认实例处于运行态且输入通道可用，必要时停止后重新启动实例。",
                ex);
        }
    }

    /// <summary>
    /// 原生通路的滑动：按下后按步长插值发出移动事件，到点后抬起。
    /// </summary>
    /// <param name="from">起点。</param>
    /// <param name="to">终点。</param>
    /// <param name="duration">滑动时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task SwipeNativeAsync(
        InputPoint from,
        InputPoint to,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        await _nativeInjector.TouchAsync(TouchPhase.Down, from, cancellationToken).ConfigureAwait(false);

        int steps = ComputeSwipeSteps(duration);
        for (int step = 1; step <= steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double ratio = step / (double)steps;
            var interpolated = new InputPoint(
                from.X + (to.X - from.X) * ratio,
                from.Y + (to.Y - from.Y) * ratio);
            await _nativeInjector.TouchAsync(TouchPhase.Move, interpolated, cancellationToken).ConfigureAwait(false);

            if (step < steps && _options.SwipeStepInterval > TimeSpan.Zero)
            {
                await Task.Delay(_options.SwipeStepInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        await _nativeInjector.TouchAsync(TouchPhase.Up, to, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按步长间隔把滑动时长换算成移动事件数，并限制上限。
    /// </summary>
    /// <param name="duration">滑动时长。</param>
    /// <returns>移动事件数，至少为 1。</returns>
    private int ComputeSwipeSteps(TimeSpan duration)
    {
        TimeSpan interval = _options.SwipeStepInterval <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(16)
            : _options.SwipeStepInterval;

        double steps = Math.Round(duration.TotalMilliseconds / interval.TotalMilliseconds, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(steps, 1, MaxSwipeSteps);
    }

    /// <summary>
    /// 把调用方坐标系下的触摸点换算到 guest 显示分辨率。拿不到显示尺寸时按配置违规上抛，
    /// 而不是拿一个猜出来的分辨率去注入。
    /// </summary>
    /// <param name="point">调用方坐标系下的点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>guest 显示分辨率范围内的点。</returns>
    /// <exception cref="XBearException">无法确定 guest 显示尺寸时抛出，分类为 <see cref="ErrorCategory.Spec"/>。</exception>
    private async Task<InputPoint> ToGuestPointAsync(InputPoint point, CancellationToken cancellationToken)
    {
        ScreenGeometry? guest = await GetGeometryAsync(cancellationToken).ConfigureAwait(false);
        if (guest is not { } geometry)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "无法确定 guest 显示尺寸，触摸坐标无法换算。",
                "请确认实例显示已就绪，或由调用方直接提供显示尺寸。");
        }

        return _coordinateMapper.Map(point, _options.HostGeometry, geometry);
    }

    private async Task TryNativeAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.ProbeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var encoder = new QmpAbsInputEventEncoder();
        var point = new InputPoint(0, 0);
        var events = new List<object>(encoder.EncodeTouch(TouchPhase.Down, point));
        events.AddRange(encoder.EncodeTouch(TouchPhase.Up, point));

        await _nativeInjector.SendAsync(events, "触摸探测", linked.Token).ConfigureAwait(false);
    }

    private async Task TryProjectionAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.ProbeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        await _projectionInjector.EnsureConnectedAsync(linked.Token).ConfigureAwait(false);

        // 取 input 命令的帮助输出，确认 shell 侧存在可用的注入入口。
        await _adbClient.ShellAsync("input --help", linked.Token).ConfigureAwait(false);
    }

    private static string Describe(Exception ex) =>
        ex is XBearException xb && !string.IsNullOrWhiteSpace(xb.Remediation)
            ? $"{xb.Message} 处置建议：{xb.Remediation}"
            : ex.Message;
}
