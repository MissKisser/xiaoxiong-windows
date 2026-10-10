using XBear.Core.Diagnostics;

namespace XBear.Core.Projection;

/// <summary>
/// 帧缓冲存储，用两块同样大小的 BGRA 缓冲实现双缓冲：
/// 取帧线程始终写入「在写」缓冲，整帧解码完成后在锁内交换两块缓冲并递增画面序号，
/// 消费方只在锁内复制「已发布」缓冲。因此消费方拿到的永远是完整的一帧，
/// 既不会读到半帧，也不会拿到引擎内部正在改写的数组。
/// </summary>
public sealed class ProjectionFrameStore
{
    private readonly object _gate = new();

    private byte[] _writeBuffer;
    private byte[] _publishedBuffer;
    private long _sequence;
    private DateTimeOffset? _publishedAt;

    /// <summary>
    /// 按给定画面尺寸创建帧缓冲存储。
    /// </summary>
    /// <param name="width">画面宽度，单位像素，必须为正。</param>
    /// <param name="height">画面高度，单位像素，必须为正。</param>
    public ProjectionFrameStore(int width, int height)
    {
        ValidateGeometry(width, height);
        Width = width;
        Height = height;
        _writeBuffer = new byte[checked(width * height * 4)];
        _publishedBuffer = new byte[_writeBuffer.Length];
    }

    /// <summary>当前画面宽度，单位像素。分辨率变化后为新的宽度。</summary>
    public int Width { get; private set; }

    /// <summary>当前画面高度，单位像素。分辨率变化后为新的高度。</summary>
    public int Height { get; private set; }

    /// <summary>已发布的画面序号，从 0 表示尚未发布过任何一帧。</summary>
    public long Sequence
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    /// <summary>最近一帧的解码完成时刻，尚未发布过任何一帧时为 null。</summary>
    public DateTimeOffset? PublishedAt
    {
        get
        {
            lock (_gate)
            {
                return _publishedAt;
            }
        }
    }

    /// <summary>当前画面尺寸。分辨率变化后为新的尺寸。</summary>
    public Abstractions.ScreenGeometry Geometry => new(Width, Height);

    /// <summary>
    /// 供取帧线程写入的一行像素区间。该区间指向内部在写缓冲，取帧线程独占使用。
    /// </summary>
    /// <param name="y">行下标，从 0 起。</param>
    /// <param name="xStart">该行起始列下标，从 0 起。</param>
    /// <param name="length">该区间的像素个数。</param>
    /// <returns>长度为长度乘四的 BGRA 像素区间。</returns>
    public Span<byte> GetWriteRowSpan(int y, int xStart, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfNegative(xStart);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        byte[] buffer = Volatile.Read(ref _writeBuffer);
        if (y >= Height || xStart + length > Width)
        {
            throw new XBearException(
                ErrorCategory.Protocol,
                $"待写入的帧缓冲区域越界：行 {y}、起始列 {xStart}、长度 {length}，画面为 {Width}×{Height}。",
                "请确认收到的画面更新矩形落在当前画面范围内。");
        }

        int offset = checked(((y * Width) + xStart) * 4);
        return buffer.AsSpan(offset, checked(length * 4));
    }

    /// <summary>
    /// 按新的画面尺寸重建帧缓冲。分辨率变化时由取帧线程调用，
    /// 重建后画面序号继续递增，已发布过的历史序号不复用。
    /// </summary>
    /// <param name="width">新的画面宽度，单位像素，必须为正。</param>
    /// <param name="height">新的画面高度，单位像素，必须为正。</param>
    public void ResetGeometry(int width, int height)
    {
        ValidateGeometry(width, height);

        lock (_gate)
        {
            if (width == Width && height == Height)
            {
                return;
            }

            Width = width;
            Height = height;
            _writeBuffer = new byte[checked(width * height * 4)];
            _publishedBuffer = new byte[_writeBuffer.Length];
            _publishedAt = null;
        }
    }

    /// <summary>
    /// 声明整帧解码完成，交换在写缓冲与已发布缓冲并递增画面序号。
    /// </summary>
    /// <param name="decodedAt">该帧解码完成的时刻。</param>
    /// <returns>递增后的画面序号。</returns>
    public long Publish(DateTimeOffset decodedAt)
    {
        lock (_gate)
        {
            byte[] published = _publishedBuffer;
            _publishedBuffer = _writeBuffer;
            _writeBuffer = published;

            _sequence++;
            _publishedAt = decodedAt;
            return _sequence;
        }
    }

    /// <summary>
    /// 复制最近一帧的像素，产出一份外部独占的快照。尚未发布过任何一帧时返回 null。
    /// </summary>
    /// <returns>最近一帧的快照，没有可用画面时为 null。</returns>
    public ProjectionFrame? CaptureLatest()
    {
        lock (_gate)
        {
            if (_publishedAt is null || _sequence == 0)
            {
                return null;
            }

            var pixels = new byte[_publishedBuffer.Length];
            _publishedBuffer.CopyTo(pixels, 0);
            return new ProjectionFrame(Width, Height, _sequence, _publishedAt.Value, pixels);
        }
    }

    /// <summary>
    /// 复制最近一帧的像素到调用方提供的数组，产出外部独占的快照。
    /// </summary>
    /// <param name="destination">接收像素的目标数组，长度须不小于当前画面像素总数。</param>
    /// <returns>最近一帧的快照，没有可用画面时为 null。</returns>
    public ProjectionFrame? CaptureLatestInto(byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate)
        {
            if (_publishedAt is null || _sequence == 0)
            {
                return null;
            }

            if (destination.Length < _publishedBuffer.Length)
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"目标像素数组长度不足：需要 {_publishedBuffer.Length}，实际为 {destination.Length}。",
                    "请按当前画面宽高乘四分配目标数组。");
            }

            _publishedBuffer.CopyTo(destination, 0);
            return new ProjectionFrame(Width, Height, _sequence, _publishedAt!.Value, destination);
        }
    }

    private static void ValidateGeometry(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"画面宽高必须为正整数，当前为 {width}×{height}。");
        }
    }
}
