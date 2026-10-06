using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Input;

/// <summary>
/// 宿主侧投递器，走 QMP 输入命令向 guest 的输入后端注入事件。
/// 依赖镜像自带绝对坐标输入驱动；驱动缺失时 QEMU 会回找不到事件处理器，
/// 该失败按 <see cref="InputChannelOptions.NativeFailureThreshold"/> 计入并触发降级。
/// </summary>
public sealed class QmpInputInjector
{
    private readonly IQmpClient _qmpClient;
    private readonly InputChannelOptions _options;
    private readonly IQmpInputEventEncoder _encoder;
    private bool _connected;

    /// <summary>
    /// 初始化宿主侧投递器。
    /// </summary>
    /// <param name="qmpClient">QMP 客户端。</param>
    /// <param name="options">输入通道参数，提供端口、设备名与超时。</param>
    /// <param name="encoder">事件编码器，为 null 时使用默认的绝对坐标编码器。</param>
    public QmpInputInjector(
        IQmpClient qmpClient,
        InputChannelOptions options,
        IQmpInputEventEncoder? encoder = null)
    {
        ArgumentNullException.ThrowIfNull(qmpClient);
        ArgumentNullException.ThrowIfNull(options);

        _qmpClient = qmpClient;
        _options = options;
        _encoder = encoder ?? new QmpAbsInputEventEncoder();
    }

    /// <summary>
    /// 确保 QMP 连接就绪。已建立过连接时仍会再发起一次连接，这样重复探测能真实触达对端；
    /// 客户端在已连接状态下拒绝重复连接属于预期情况，复用既有连接继续即可。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <exception cref="XBearException">连接或握手失败时抛出，分类为 <see cref="ErrorCategory.Protocol"/>。</exception>
    public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _qmpClient.ConnectAsync(_options.QmpPort, cancellationToken).ConfigureAwait(false);
            _connected = true;
        }
        catch (XBearException) when (_connected)
        {
            // 已经连上过，重复连接被拒不影响后续投递。
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
        await SendAsync(_encoder.EncodeTouch(phase, point), Describe(phase), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>投递一次按键动作。</summary>
    /// <param name="key">按键编码。</param>
    /// <param name="action">按下或抬起。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public async Task KeyAsync(
        VirtualKey key,
        KeyAction action,
        CancellationToken cancellationToken = default)
    {
        await SendAsync(_encoder.EncodeKey(key, action), $"按键 {key.Key}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 执行一次输入命令，并把任何失败都归入 <see cref="ErrorCategory.Protocol"/>，
    /// 不向上抛裸异常，让调用方能按通路统一处理与降级。
    /// </summary>
    /// <param name="events">事件序列。</param>
    /// <param name="action">动作描述，用于错误文案。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <exception cref="XBearException">QMP 命令失败或回包含错误时抛出，分类为 <see cref="ErrorCategory.Protocol"/>。</exception>
    public async Task SendAsync(
        IReadOnlyList<object> events,
        string action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        using var timeout = new CancellationTokenSource(_options.DispatchTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        await EnsureConnectedAsync(linked.Token).ConfigureAwait(false);

        object payload = _encoder.EncodeCommand(_options.QmpInputDeviceName, events);

        try
        {
            JsonElement result = await _qmpClient.ExecuteAsync("input-send-event", payload, linked.Token).ConfigureAwait(false);
            ThrowIfQmpError(result, action);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消按取消原样上抛，不谎报为注入失败。
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new XBearException(ErrorCategory.Timeout, $"注入{action}超时。", "请检查实例状态与宿主端口后重试。", ex);
        }
        catch (XBearException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new XBearException(ErrorCategory.Protocol, $"注入{action}失败：{ex.Message}", inner: ex);
        }
    }

    /// <summary>
    /// 识别 QMP 回包中的错误对象。正式客户端在收到错误时已直接抛出，
    /// 此处兜住把错误作为返回内容交回的路径，使两条路径的失败分类一致。
    /// </summary>
    /// <param name="result">命令返回对象。</param>
    /// <param name="action">动作描述，用于错误文案。</param>
    /// <exception cref="XBearException">回包含错误时抛出，分类为 <see cref="ErrorCategory.Protocol"/>。</exception>
    public static void ThrowIfQmpError(JsonElement result, string action)
    {
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("error", out JsonElement error))
        {
            string message = error.ValueKind == JsonValueKind.String
                ? error.GetString() ?? "未知错误"
                : error.ToString();

            throw new XBearException(
                ErrorCategory.Protocol,
                $"注入{action}被拒：{message}",
                "请确认镜像自带绝对坐标或触摸输入驱动，否则请改用投屏通路。");
        }
    }

    private static string Describe(TouchPhase phase) => phase switch
    {
        TouchPhase.Down => "触摸按下",
        TouchPhase.Move => "触摸移动",
        TouchPhase.Up => "触摸抬起",
        _ => "触摸",
    };
}
