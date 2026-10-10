using XBear.Core.Abstractions;

namespace XBear.Core.Projection;

/// <summary>
/// 一帧投屏画面的不可变快照。像素按每像素四字节的 BGRA 顺序排列，
/// 行优先、自上而下，行内从左到右，与界面层写位图时所需的排列一致。
/// 快照的像素数组由帧缓冲产出方独占复制，外部无法改动投屏引擎内部的缓冲。
/// </summary>
public sealed class ProjectionFrame
{
    /// <summary>
    /// 由一份 BGRA 像素数组构造快照。
    /// </summary>
    /// <param name="width">画面宽度，单位像素。</param>
    /// <param name="height">画面高度，单位像素。</param>
    /// <param name="sequence">画面序号，从 1 起单调递增。</param>
    /// <param name="capturedAt">该帧解码完成的时刻。</param>
    /// <param name="pixels">BGRA 像素数组，长度为宽乘高乘四，调用方须保证独占该数组。</param>
    public ProjectionFrame(
        int width,
        int height,
        long sequence,
        DateTimeOffset capturedAt,
        byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "画面宽高必须为正整数。");
        }

        int expected = checked(width * height * 4);
        if (pixels.Length != expected)
        {
            throw new ArgumentException(
                $"像素数组长度应为 {expected}，实际为 {pixels.Length}。",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        Sequence = sequence;
        CapturedAt = capturedAt;
        Pixels = pixels;
    }

    /// <summary>画面宽度，单位像素。</summary>
    public int Width { get; }

    /// <summary>画面高度，单位像素。</summary>
    public int Height { get; }

    /// <summary>画面序号，从 1 起单调递增，分辨率变化后继续递增不复用。</summary>
    public long Sequence { get; }

    /// <summary>该帧解码完成的时刻。</summary>
    public DateTimeOffset CapturedAt { get; }

    /// <summary>BGRA 像素数组，长度为宽乘高乘四。</summary>
    public byte[] Pixels { get; }

    /// <summary>该帧对应的画面尺寸。</summary>
    public ScreenGeometry Geometry => new(Width, Height);

    /// <summary>
    /// 取指定像素处的 BGRA 分量。
    /// </summary>
    /// <param name="x">横坐标下标，从 0 起。</param>
    /// <param name="y">纵坐标下标，从 0 起。</param>
    /// <returns>依次为蓝、绿、红与不透明度的分量值。</returns>
    public (byte B, byte G, byte R, byte A) GetPixel(int x, int y)
    {
        if (x < 0 || x >= Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"横坐标 {x} 超出画面宽度 {Width}。");
        }

        if (y < 0 || y >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y), $"纵坐标 {y} 超出画面高度 {Height}。");
        }

        int offset = checked(((y * Width) + x) * 4);
        return (Pixels[offset], Pixels[offset + 1], Pixels[offset + 2], Pixels[offset + 3]);
    }
}
