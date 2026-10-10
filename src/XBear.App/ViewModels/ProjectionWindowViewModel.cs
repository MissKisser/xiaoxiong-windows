using CommunityToolkit.Mvvm.ComponentModel;
using XBear.App.Presentation;
using XBear.Core.Abstractions;
using XBear.Core.Input;
using XBear.Core.Projection;
using XBear.Core.Spec;

namespace XBear.App.ViewModels;

/// <summary>
/// 投屏窗口视图模型。承接投屏服务的画面到达、分辨率变化与会话状态变化通知，
/// 把画面快照、实测帧率与输入投递收敛到一处，窗口只负责呈现画面与采集宿主输入事件。
///
/// 画面到达事件由取帧线程触发，属性更新必须切回界面线程，
/// 否则绑定会在非界面线程上被改写。统一经构造时注入的编组委托完成切换。
/// </summary>
public sealed partial class ProjectionWindowViewModel : ObservableObject
{
    private readonly IProjectionService _projection;
    private readonly TerminologyCatalog _terms;
    private readonly Action<Action> _postToUi;
    private readonly CancellationTokenSource _lifetime = new();

    private int _frameWidth;
    private int _frameHeight;
    private bool _pointerPressed;

    [ObservableProperty]
    private string _windowTitle = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _resolutionText = string.Empty;

    [ObservableProperty]
    private string _frameRateText = string.Empty;

    [ObservableProperty]
    private ProjectionFrame? _latestFrame;

    [ObservableProperty]
    private bool _hasFrame;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _failureText = string.Empty;

    /// <summary>是否有失败原因需要呈现。</summary>
    public bool HasFailure => !string.IsNullOrEmpty(FailureText);

    /// <summary>
    /// 构造投屏窗口视图模型并订阅投屏服务通知。
    /// </summary>
    /// <param name="projection">投屏服务。</param>
    /// <param name="instanceId">被投屏实例标识。</param>
    /// <param name="instanceName">被投屏实例显示名，用于窗口标题。</param>
    /// <param name="terms">界面文案术语来源。</param>
    /// <param name="postToUi">把动作编组到界面线程的委托，为空时在当前线程直接执行。</param>
    public ProjectionWindowViewModel(
        IProjectionService projection,
        string instanceId,
        string instanceName,
        TerminologyCatalog terms,
        Action<Action>? postToUi = null)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(terms);

        _projection = projection;
        _terms = terms;
        _postToUi = postToUi ?? (action => action());

        InstanceId = instanceId;
        InstanceName = instanceName;

        _windowTitle = $"{instanceName} · {terms.Projection}";
        _statusText = $"{terms.ProjectionSession}：{ProjectionStateText.Describe(ProjectionState.Pending)}";

        ProjectionSession? session = projection.GetSession(instanceId);
        if (session is not null)
        {
            _frameWidth = session.Width;
            _frameHeight = session.Height;
            IsActive = session.State == ProjectionState.Active;
        }

        RefreshResolutionText();
        RefreshFrameRateText();

        _projection.FrameArrived += OnFrameArrived;
        _projection.ResolutionChanged += OnResolutionChanged;
        _projection.SessionStateChanged += OnSessionStateChanged;
    }

    /// <summary>被投屏实例标识。</summary>
    public string InstanceId { get; }

    /// <summary>被投屏实例显示名。</summary>
    public string InstanceName { get; }

    /// <summary>有新画面可呈现时触发，窗口据此取最新快照并刷新位图。</summary>
    public event EventHandler? FrameReady;

    /// <summary>画面尺寸变化时触发，窗口据此重建位图并调整外框。</summary>
    public event EventHandler<ScreenGeometry>? FrameSizeChanged;

    /// <summary>会话终止时触发，窗口据此收敛状态行。</summary>
    public event EventHandler<string>? Failed;

    /// <summary>当前画面宽度，单位像素；尚未取到首帧时为 0。</summary>
    public int FrameWidth => _frameWidth;

    /// <summary>当前画面高度，单位像素；尚未取到首帧时为 0。</summary>
    public int FrameHeight => _frameHeight;

    /// <summary>
    /// 把宿主呈现面上的指针位置换算为画面像素坐标。落在留边区域时返回 false，不投递输入。
    /// </summary>
    /// <param name="surfaceX">呈现面横坐标。</param>
    /// <param name="surfaceY">呈现面纵坐标。</param>
    /// <param name="surfaceWidth">呈现面宽度。</param>
    /// <param name="surfaceHeight">呈现面高度。</param>
    /// <param name="point">换算得到的画面像素坐标。</param>
    /// <returns>该位置对应画面内一点时返回 true。</returns>
    public bool TryMapPoint(
        double surfaceX,
        double surfaceY,
        double surfaceWidth,
        double surfaceHeight,
        out InputPoint point)
    {
        var frame = new ScreenGeometry(_frameWidth, _frameHeight);
        ProjectionSurfaceRect rect = ProjectionViewport.Fit(frame, surfaceWidth, surfaceHeight);
        InputPoint? mapped = ProjectionViewport.ToFramePoint(frame, rect, surfaceX, surfaceY);

        point = mapped ?? default;
        return mapped is not null;
    }

    /// <summary>
    /// 投递一次指针按下。
    /// </summary>
    /// <param name="point">画面像素坐标。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task PointerDownAsync(InputPoint point, CancellationToken cancellationToken = default)
    {
        _pointerPressed = true;
        return InjectAsync(injector => injector.TouchAsync(point, TouchPhase.Down, cancellationToken));
    }

    /// <summary>
    /// 投递一次指针移动。未处于按下状态时不投递，悬停不应被当成拖拽。
    /// </summary>
    /// <param name="point">画面像素坐标。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public async Task PointerMoveAsync(InputPoint point, CancellationToken cancellationToken = default)
    {
        if (!_pointerPressed)
        {
            return;
        }

        await InjectAsync(injector => injector.TouchAsync(point, TouchPhase.Move, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 投递一次指针抬起。未处于按下状态时不投递。
    /// </summary>
    /// <param name="point">画面像素坐标。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public async Task PointerUpAsync(InputPoint point, CancellationToken cancellationToken = default)
    {
        if (!_pointerPressed)
        {
            return;
        }

        _pointerPressed = false;
        await InjectAsync(injector => injector.TouchAsync(point, TouchPhase.Up, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 投递一次滚轮。实例的输入后端不承载滚轮，滚轮以方向键表达：
    /// 正向滚动上移一格，负向滚动下移一格，按下与抬起为两次独立投递。
    /// </summary>
    /// <param name="delta">滚轮增量，正值表示向上。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task WheelAsync(int delta, CancellationToken cancellationToken = default)
    {
        AndroidKey key = delta >= 0 ? AndroidKey.ArrowUp : AndroidKey.ArrowDown;
        return KeyTapAsync(key, cancellationToken);
    }

    /// <summary>
    /// 投递一次按键按下。
    /// </summary>
    /// <param name="key">实例按键标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task KeyDownAsync(AndroidKey key, CancellationToken cancellationToken = default) =>
        InjectAsync(injector => injector.KeyAsync(
            ProjectionKeyMap.Encode(key),
            KeyAction.Press,
            cancellationToken));

    /// <summary>
    /// 投递一次按键抬起。
    /// </summary>
    /// <param name="key">实例按键标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task KeyUpAsync(AndroidKey key, CancellationToken cancellationToken = default) =>
        InjectAsync(injector => injector.KeyAsync(
            ProjectionKeyMap.Encode(key),
            KeyAction.Release,
            cancellationToken));

    /// <summary>
    /// 取最新的画面快照。帧缓冲在锁内被复制为外部独占数组，窗口可安全地直接拷贝像素。
    /// </summary>
    /// <returns>最新画面快照，尚未取到首帧或会话已终止时为 null。</returns>
    public ProjectionFrame? CaptureFrame() => _projection.GetLatestFrame(InstanceId);

    /// <summary>
    /// 终止投屏会话并退订全部通知。会话终止后本视图模型不再接受输入投递。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);

        _projection.FrameArrived -= OnFrameArrived;
        _projection.ResolutionChanged -= OnResolutionChanged;
        _projection.SessionStateChanged -= OnSessionStateChanged;

        _pointerPressed = false;
        await _projection.StopAsync(InstanceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task KeyTapAsync(AndroidKey key, CancellationToken cancellationToken)
    {
        await KeyDownAsync(key, cancellationToken).ConfigureAwait(false);
        await KeyUpAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 经投屏输入注入器投递一次动作。注入失败不在窗口内抛出，
    /// 而是把失败原因呈现到界面上，让用户看到会话仍在但这次投递没有生效。
    /// </summary>
    /// <param name="dispatch">投递动作。</param>
    /// <returns>异步任务。</returns>
    private async Task InjectAsync(Func<ProjectionInputInjector, Task> dispatch)
    {
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        ProjectionInputInjector? injector = _projection.GetInputInjector(InstanceId);
        if (injector is null)
        {
            PresentFailure($"{_terms.ProjectionSession}已结束，{_terms.InputChannel}投递不再可用。");
            return;
        }

        try
        {
            await dispatch(injector).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PresentFailure(ex.Message);
        }
    }

    private void OnFrameArrived(object? sender, ProjectionFrameArrivedEventArgs e)
    {
        if (!string.Equals(e.InstanceId, InstanceId, StringComparison.Ordinal))
        {
            return;
        }

        PostToUi(() =>
        {
            RefreshFrameRateText();

            if (e.Geometry.Width != _frameWidth || e.Geometry.Height != _frameHeight)
            {
                _frameWidth = e.Geometry.Width;
                _frameHeight = e.Geometry.Height;
                RefreshResolutionText();
                FrameSizeChanged?.Invoke(this, e.Geometry);
            }

            LatestFrame = _projection.GetLatestFrame(InstanceId);
            HasFrame = LatestFrame is not null;
            FrameReady?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnResolutionChanged(object? sender, ProjectionResolutionChangedEventArgs e)
    {
        if (!string.Equals(e.InstanceId, InstanceId, StringComparison.Ordinal))
        {
            return;
        }

        PostToUi(() =>
        {
            bool resized = e.Current.Width != _frameWidth || e.Current.Height != _frameHeight;

            _frameWidth = e.Current.Width;
            _frameHeight = e.Current.Height;
            RefreshResolutionText();

            if (resized)
            {
                FrameSizeChanged?.Invoke(this, e.Current);
            }
        });
    }

    private void OnSessionStateChanged(object? sender, ProjectionSessionStateChangedEventArgs e)
    {
        if (!string.Equals(e.InstanceId, InstanceId, StringComparison.Ordinal))
        {
            return;
        }

        PostToUi(() =>
        {
            IsActive = e.CurrentState == ProjectionState.Active;
            StatusText = $"{_terms.ProjectionSession}：{ProjectionStateText.Describe(e.CurrentState)}";

            if (e.Error is not null)
            {
                PresentFailureDirect(e.Error.Message);
            }
        });
    }

    private void RefreshResolutionText() =>
        ResolutionText = _frameWidth > 0 && _frameHeight > 0
            ? $"{_terms.Resolution}：{_frameWidth}×{_frameHeight}"
            : $"{_terms.Resolution}：等待首帧";

    /// <summary>
    /// 刷新实测帧率文案。实测值为空时如实显示尚未产生，不得用目标帧率顶替。
    /// </summary>
    private void RefreshFrameRateText()
    {
        string target = _projection.GetSession(InstanceId)?.TargetFps is { } value
            ? $"{value} fps"
            : "未声明";

        FrameRateText = _projection.GetMeasuredFps(InstanceId) is { } measured
            ? $"{_terms.FrameRate}：实测 {measured:0.0} fps（目标 {target}）"
            : $"{_terms.FrameRate}：实测尚未产生（目标 {target}）";
    }

    private void PresentFailure(string message) => PostToUi(() => PresentFailureDirect(message));

    private void PresentFailureDirect(string message)
    {
        FailureText = message;
        OnPropertyChanged(nameof(HasFailure));
        Failed?.Invoke(this, message);
    }

    private void PostToUi(Action action)
    {
        try
        {
            _postToUi(action);
        }
        catch (TaskCanceledException)
        {
            // 窗口已关闭，界面编组不再受理投递。
        }
    }
}

/// <summary>投屏会话状态在界面上的中文表述。</summary>
public static class ProjectionStateText
{
    /// <summary>
    /// 取会话状态的中文表述。
    /// </summary>
    /// <param name="state">投屏会话状态。</param>
    /// <returns>状态的中文表述。</returns>
    public static string Describe(ProjectionState state) =>
        state switch
        {
            ProjectionState.Pending => "建立中",
            ProjectionState.Active => "呈现中",
            _ => "已结束",
        };
}