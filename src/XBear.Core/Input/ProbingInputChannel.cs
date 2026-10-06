using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Input;

/// <summary>输入通道探测参数。</summary>
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
}

/// <summary>
/// 探测式输入通道。按原生优先、投屏降级的顺序确定实际生效通路：
/// 通路 A 通过 QMP 向 guest 私有输入后端注入触摸事件，
/// 需要镜像自带 ranchu 或 goldfish 类输入驱动；连续失败达到阈值后降级到通路 B，
/// 即 scrcpy 推流配合 <c>adb shell input</c> 注入。
/// 两条通路都不通时如实返回各自失败原因，不静默卡住。
/// </summary>
public sealed class ProbingInputChannel : IInputChannel
{
    private readonly IQmpClient _qmpClient;
    private readonly IAdbClient _adbClient;
    private readonly InputChannelOptions _options;
    private int _consecutiveNativeFailures;
    private string? _lastNativeFailure;
    private bool _disposed;

    /// <summary>
    /// 初始化探测式输入通道。
    /// </summary>
    /// <param name="qmpClient">QMP 客户端，用于原生通路注入。</param>
    /// <param name="adbClient">adb 客户端，用于投屏通路注入。</param>
    /// <param name="options">探测参数，为 null 时使用默认值。</param>
    public ProbingInputChannel(
        IQmpClient qmpClient,
        IAdbClient adbClient,
        InputChannelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(qmpClient);
        ArgumentNullException.ThrowIfNull(adbClient);

        _qmpClient = qmpClient;
        _adbClient = adbClient;
        _options = options ?? new InputChannelOptions();
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
    /// 构造一次触摸按下与释放的 QMP 事件载荷，供原生通路注入。
    /// </summary>
    /// <param name="x">触摸横坐标。</param>
    /// <param name="y">触摸纵坐标。</param>
    /// <param name="device">目标输入设备名，缺省为触屏设备。</param>
    /// <returns>QMP <c>input-send-event</c> 命令的参数对象。</returns>
    public static object BuildTouchEventPayload(int x, int y, string device = "touchscreen")
    {
        return new Dictionary<string, object>
        {
            ["events"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["type"] = "abs",
                    ["data"] = new Dictionary<string, object>
                    {
                        ["axis"] = "x",
                        ["value"] = x,
                    },
                },
                new Dictionary<string, object>
                {
                    ["type"] = "abs",
                    ["data"] = new Dictionary<string, object>
                    {
                        ["axis"] = "y",
                        ["value"] = y,
                    },
                },
                new Dictionary<string, object>
                {
                    ["type"] = "abs",
                    ["data"] = new Dictionary<string, object>
                    {
                        ["axis"] = "down",
                        ["value"] = 1,
                    },
                },
                new Dictionary<string, object>
                {
                    ["type"] = "abs",
                    ["data"] = new Dictionary<string, object>
                    {
                        ["axis"] = "down",
                        ["value"] = 0,
                    },
                },
            },
            ["device"] = device,
        };
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

    private async Task TryNativeAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.ProbeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        await _qmpClient.ConnectAsync(_options.QmpPort, linked.Token).ConfigureAwait(false);
        JsonElement result = await _qmpClient
            .ExecuteAsync("input-send-event", BuildTouchEventPayload(0, 0), linked.Token)
            .ConfigureAwait(false);

        ThrowIfQmpError(result);
    }

    private async Task TryProjectionAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.ProbeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        await _adbClient.ConnectAsync(_options.AdbPort, linked.Token).ConfigureAwait(false);

        if (!await _adbClient.IsRootAsync(linked.Token).ConfigureAwait(false))
        {
            throw new XBearException(
                ErrorCategory.Protocol,
                "实例未以 root 身份运行，投屏通路无法注入输入。",
                "请使用已 root 的镜像，或改用原生通路。");
        }

        // 取 input 命令的帮助输出，确认 shell 侧存在可用的注入入口。
        await _adbClient.ShellAsync("input --help", linked.Token).ConfigureAwait(false);
    }

    private static void ThrowIfQmpError(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("error", out JsonElement error))
        {
            string message = error.ValueKind == JsonValueKind.String
                ? error.GetString() ?? "未知错误"
                : error.ToString();

            throw new XBearException(
                ErrorCategory.Protocol,
                $"QMP 返回错误：{message}",
                "请确认镜像自带 ranchu 或 goldfish 类触摸输入驱动，否则请使用投屏通路。");
        }
    }

    private static string Describe(Exception ex) =>
        ex is XBearException xb && !string.IsNullOrWhiteSpace(xb.Remediation)
            ? $"{xb.Message} 处置建议：{xb.Remediation}"
            : ex.Message;
}