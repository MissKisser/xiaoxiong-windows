using XBear.Core.Diagnostics;

namespace XBear.Core.Projection;

/// <summary>
/// RFB 像素格式。描述服务端按什么位深、什么通道次序把像素写到原始编码矩形里。
/// 投屏取帧统一协商为 32 位真彩色、小端、红移 16、绿移 8、蓝移 0，
/// 这样原始编码的字节排列即 BGRA，无需逐像素换算。
/// </summary>
internal readonly struct RfbPixelFormat
{
    /// <summary>投屏取帧协商的像素格式：32 位真彩色、小端，字节排列为 BGRA。</summary>
    public static RfbPixelFormat ProjectionBgra32 => new(
        bitsPerPixel: 32,
        depth: 24,
        bigEndian: false,
        trueColour: true,
        redMax: 255,
        greenMax: 255,
        blueMax: 255,
        redShift: 16,
        greenShift: 8,
        blueShift: 0);

    public int BitsPerPixel { get; }

    public int Depth { get; }

    public bool BigEndian { get; }

    public bool TrueColour { get; }

    public int RedMax { get; }

    public int GreenMax { get; }

    public int BlueMax { get; }

    public int RedShift { get; }

    public int GreenShift { get; }

    public int BlueShift { get; }

    public RfbPixelFormat(
        int bitsPerPixel,
        int depth,
        bool bigEndian,
        bool trueColour,
        int redMax,
        int greenMax,
        int blueMax,
        int redShift,
        int greenShift,
        int blueShift)
    {
        BitsPerPixel = bitsPerPixel;
        Depth = depth;
        BigEndian = bigEndian;
        TrueColour = trueColour;
        RedMax = redMax;
        GreenMax = greenMax;
        BlueMax = blueMax;
        RedShift = redShift;
        GreenShift = greenShift;
        BlueShift = blueShift;
    }

    /// <summary>单个像素占用的字节数。</summary>
    public int BytesPerPixel => BitsPerPixel / 8;

    /// <summary>
    /// 判断该像素格式能否直接按字节复制到 BGRA 帧缓冲，
    /// 即字节序为蓝、绿、红、透明且不透明通道固定填满。
    /// </summary>
    /// <returns>可走整行直接复制时返回 true。</returns>
    public bool IsDirectBgraBytes() =>
        BitsPerPixel == 32
        && !BigEndian
        && TrueColour
        && RedMax == 255
        && GreenMax == 255
        && BlueMax == 255
        && RedShift == 16
        && GreenShift == 8
        && BlueShift == 0;

    /// <summary>
    /// 把一行原始像素解码为一行 BGRA 像素。
    /// </summary>
    /// <param name="source">该行的原始像素，长度至少为长度乘每像素字节数。</param>
    /// <param name="destination">该行的 BGRA 目标像素，长度为长度乘四。</param>
    /// <param name="pixelCount">该行的像素个数。</param>
    public void DecodeRow(ReadOnlySpan<byte> source, Span<byte> destination, int pixelCount)
    {
        if (IsDirectBgraBytes())
        {
            DecodeDirectRow(source, destination, pixelCount);
            return;
        }

        if (BitsPerPixel is not (8 or 16 or 32) || !TrueColour)
        {
            throw new XBearException(
                ErrorCategory.Protocol,
                $"不支持的像素格式：每像素 {BitsPerPixel} 位，真彩色为 {TrueColour}。",
                "请确认实例的 VNC 服务端支持 32 位真彩色取帧。");
        }

        DecodeShiftedRow(source, destination, pixelCount);
    }

    /// <summary>
    /// 把一行 BGRA 像素编码为该像素格式的原始像素，供测试替身按协商结果回送画面。
    /// </summary>
    /// <param name="source">该行的 BGRA 像素。</param>
    /// <param name="destination">该行的目标原始像素。</param>
    /// <param name="pixelCount">该行的像素个数。</param>
    public void EncodeRow(ReadOnlySpan<byte> source, Span<byte> destination, int pixelCount)
    {
        if (IsDirectBgraBytes())
        {
            for (int index = 0; index < pixelCount; index++)
            {
                int offset = index * 4;
                destination[offset] = source[offset];
                destination[offset + 1] = source[offset + 1];
                destination[offset + 2] = source[offset + 2];
                destination[offset + 3] = 0;
            }

            return;
        }

        if (BitsPerPixel is not (8 or 16 or 32) || !TrueColour)
        {
            throw new XBearException(
                ErrorCategory.Protocol,
                $"不支持的像素格式：每像素 {BitsPerPixel} 位，真彩色为 {TrueColour}。");
        }

        int bytesPerPixel = BytesPerPixel;
        int redScale = ScaleFor(RedMax);
        int greenScale = ScaleFor(GreenMax);
        int blueScale = ScaleFor(BlueMax);

        for (int index = 0; index < pixelCount; index++)
        {
            int offset = index * 4;
            uint value = ((uint)(source[offset + 2] >> redScale) << RedShift)
                       | ((uint)(source[offset + 1] >> greenScale) << GreenShift)
                       | (uint)(source[offset] >> blueScale) << BlueShift;

            int target = index * bytesPerPixel;
            if (BigEndian)
            {
                for (int byteIndex = 0; byteIndex < bytesPerPixel; byteIndex++)
                {
                    destination[target + byteIndex] = (byte)(value >> (8 * (bytesPerPixel - 1 - byteIndex)));
                }
            }
            else
            {
                for (int byteIndex = 0; byteIndex < bytesPerPixel; byteIndex++)
                {
                    destination[target + byteIndex] = (byte)(value >> (8 * byteIndex));
                }
            }
        }
    }

    private static void DecodeDirectRow(ReadOnlySpan<byte> source, Span<byte> destination, int pixelCount)
    {
        ReadOnlySpan<byte> colours = source[..(pixelCount * 4)];
        colours.CopyTo(destination[..(pixelCount * 4)]);

        // 字节序已是不透明通道在末位，直接复制后需要把不透明通道统一填满。
        for (int offset = 3; offset < pixelCount * 4; offset += 4)
        {
            destination[offset] = 0xFF;
        }
    }

    private void DecodeShiftedRow(ReadOnlySpan<byte> source, Span<byte> destination, int pixelCount)
    {
        int bytesPerPixel = BytesPerPixel;
        int redScale = ScaleFor(RedMax);
        int greenScale = ScaleFor(GreenMax);
        int blueScale = ScaleFor(BlueMax);

        for (int index = 0; index < pixelCount; index++)
        {
            int offset = index * bytesPerPixel;
            uint value = 0;
            for (int byteIndex = 0; byteIndex < bytesPerPixel; byteIndex++)
            {
                uint part = source[offset + byteIndex];
                value |= BigEndian
                    ? part << (8 * (bytesPerPixel - 1 - byteIndex))
                    : part << (8 * byteIndex);
            }

            int red = (int)((value >> RedShift) & (uint)RedMax) >> redScale;
            int green = (int)((value >> GreenShift) & (uint)GreenMax) >> greenScale;
            int blue = (int)((value >> BlueShift) & (uint)BlueMax) >> blueScale;

            int target = index * 4;
            destination[target] = (byte)Math.Clamp(blue, 0, 255);
            destination[target + 1] = (byte)Math.Clamp(green, 0, 255);
            destination[target + 2] = (byte)Math.Clamp(red, 0, 255);
            destination[target + 3] = 0xFF;
        }
    }

    /// <summary>
    /// 由通道上界算出把通道值右移到 8 位所需的移位数，上界为 255 时不移位。
    /// </summary>
    /// <param name="channelMax">通道上界。</param>
    /// <returns>右移位数。</returns>
    private static int ScaleFor(int channelMax)
    {
        int shift = 0;
        int max = channelMax;
        while (max > 0 && (max & 1) == 0)
        {
            max >>= 1;
            shift++;
        }

        return shift;
    }
}
