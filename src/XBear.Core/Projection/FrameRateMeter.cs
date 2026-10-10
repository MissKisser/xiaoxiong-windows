using XBear.Core.Diagnostics;

namespace XBear.Core.Projection;

/// <summary>
/// 帧率实测器。以每个画面更新完成时刻为样本，保留最近一个滚动窗口内的样本，
/// 按「窗口内样本数量减一除以首末样本的时间跨度」算出实测帧率。
///
/// 只登记真实观测：样本不足两个时实测值保持 null，既不由目标帧率推导，
/// 也不以 0 或任何默认值占位。窗口取 2 秒，既能覆盖若干秒的稳定节奏，
/// 又短到画面在旋转、加载等场景下明显变慢时能较快反映出来。
/// </summary>
public sealed class FrameRateMeter
{
    /// <summary>滚动窗口时长。</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _samples = new();
    private readonly object _gate = new();
    private double? _measured;

    /// <summary>
    /// 创建帧率实测器。
    /// </summary>
    /// <param name="window">滚动窗口时长，为 null 或非正值时使用默认窗口。</param>
    public FrameRateMeter(TimeSpan? window = null)
    {
        _window = window is { } configured && configured > TimeSpan.Zero ? configured : DefaultWindow;
    }

    /// <summary>滚动窗口时长。</summary>
    public TimeSpan Window => _window;

    /// <summary>
    /// 实测帧率，单位 fps。样本不足两个或时间跨度为零时为 null。
    /// </summary>
    public double? Measured
    {
        get
        {
            lock (_gate)
            {
                return _measured;
            }
        }
    }

    /// <summary>当前窗口内的样本数。</summary>
    public int SampleCount
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count;
            }
        }
    }

    /// <summary>
    /// 登记一个画面更新完成的时刻，并把滚动窗口之外的样本丢弃。
    /// </summary>
    /// <param name="decodedAt">该帧解码完成的时刻。</param>
    public void Record(DateTimeOffset decodedAt)
    {
        lock (_gate)
        {
            _samples.Enqueue(decodedAt);

            DateTimeOffset earliest = decodedAt - _window;
            while (_samples.Count > 0 && _samples.Peek() < earliest)
            {
                _samples.Dequeue();
            }

            _measured = ComputeMeasured();
        }
    }

    /// <summary>
    /// 清空窗口内全部样本并把实测值复位为 null，用于会话终止等需要如实清零的场合。
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
            _measured = null;
        }
    }

    private double? ComputeMeasured()
    {
        if (_samples.Count < 2)
        {
            return null;
        }

        DateTimeOffset first = _samples.Peek();
        DateTimeOffset last = _samples.Last();
        double spanSeconds = (last - first).TotalSeconds;
        if (spanSeconds <= 0)
        {
            return null;
        }

        return (_samples.Count - 1) / spanSeconds;
    }
}

/// <summary>
/// 帧率实测器参数。
/// </summary>
public sealed class FrameRateMeterOptions
{
    /// <summary>滚动窗口时长，为空或非正值时使用 <see cref="FrameRateMeter.DefaultWindow"/>。</summary>
    public TimeSpan? Window { get; init; }
}
