using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using XBear.Core.Diagnostics;

namespace XBear.App.Services;

/// <summary>
/// initrd 外层压缩格式。initramfs 本体始终是 cpio 归档，外层只是压缩封装。
/// </summary>
public enum InitrdCompression
{
    /// <summary>未压缩，文件内容直接是 cpio 归档。</summary>
    None = 0,

    /// <summary>gzip 压缩，Android-x86 系镜像的常规形态。</summary>
    GZip = 1
}

/// <summary>
/// initrd 的压缩封装层处理：探测外层格式、解压与按原格式回压。
///
/// 刻意只支持 gzip 与未压缩两种形态：.NET 8 的基类库不含 zstd 编解码器，
/// 引入外部原生依赖会带来分发与运行时加载风险，而 Android-x86/Bliss 系镜像的
/// initrd 全部为 gzip。遇到 zstd 等其他格式时明确报错，而不是产出启动不了的镜像。
///
/// gzip 解压不走 GZipStream：该 API 对截断的输入会静默返回已解出的部分数据，
/// 定制链路会据此产出一个缺文件的 initrd，而问题要到启动时才暴露。
/// 这里改为自行定位 deflate 数据段并校验尾部 CRC32 与长度，让截断在导入阶段就被拦下。
/// </summary>
public static class InitrdImageCodec
{
    /// <summary>gzip 魔数。</summary>
    private static readonly byte[] GZipMagic = { 0x1f, 0x8b };

    /// <summary>gzip 固定头部长度：魔数、压缩方法、标志位、时间戳、附加标志、操作系统。</summary>
    private const int GZipHeaderLength = 10;

    /// <summary>gzip 固定尾部长度：CRC32 与原始长度。</summary>
    private const int GZipTrailerLength = 8;

    /// <summary>gzip 的压缩方法字段取值，8 表示 deflate。</summary>
    private const byte GZipCompressionMethodDeflate = 8;

    /// <summary>gzip 标志位：存在附加字段。</summary>
    private const byte GZipFlagExtraField = 0x04;

    /// <summary>gzip 标志位：存在原始文件名。</summary>
    private const byte GZipFlagOriginalName = 0x08;

    /// <summary>gzip 标志位：存在原始注释。</summary>
    private const byte GZipFlagComment = 0x10;

    /// <summary>gzip 标志位：存在头部 CRC16。</summary>
    private const byte GZipFlagHeaderCrc = 0x02;

    /// <summary>CRC32 多项式（反射形式）。</summary>
    private const uint Crc32Polynomial = 0xedb88320u;

    /// <summary>CRC32 的初始值。</summary>
    private const uint Crc32Initial = 0xffffffffu;

    /// <summary>
    /// 探测 initrd 文件的外层压缩格式。
    /// </summary>
    /// <param name="data">文件字节内容。</param>
    /// <returns>探测到的压缩格式。</returns>
    /// <exception cref="XBearException">识别出 gzip 与未压缩之外的格式时抛出。</exception>
    public static InitrdCompression Detect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (StartsWith(data, GZipMagic))
        {
            return InitrdCompression.GZip;
        }

        if (StartsWith(data, System.Text.Encoding.ASCII.GetBytes(CpioNewcArchive.Magic)))
        {
            return InitrdCompression.None;
        }

        throw new XBearException(
            ErrorCategory.Spec,
            "initrd 的外层压缩格式无法识别，仅支持 gzip 与未压缩的 newc 归档。",
            "确认该 initrd 是 gzip 或未压缩的 cpio(newc) 归档；zstd 等其他压缩封装暂不支持。");
    }

    /// <summary>
    /// 按探测到的格式解压，得到 cpio 归档字节。
    /// </summary>
    /// <param name="data">initrd 文件字节内容。</param>
    /// <param name="compression">外层压缩格式。</param>
    /// <returns>解压后的 cpio 归档字节。</returns>
    /// <exception cref="XBearException">解压失败或完整性校验不通过时抛出。</exception>
    public static byte[] Decompress(byte[] data, InitrdCompression compression)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (compression == InitrdCompression.None)
        {
            return data;
        }

        try
        {
            return InflateGZip(data);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"解压 initrd 失败：{ex.Message}",
                "确认该 initrd 文件完整且未被截断，必要时重新从镜像中提取。",
                ex);
        }
    }

    /// <summary>
    /// 按指定格式把 cpio 归档压回 initrd 文件字节。
    /// </summary>
    /// <param name="archive">cpio 归档字节。</param>
    /// <param name="compression">目标压缩格式。</param>
    /// <returns>initrd 文件字节。</returns>
    public static byte[] Compress(byte[] archive, InitrdCompression compression)
    {
        ArgumentNullException.ThrowIfNull(archive);

        if (compression == InitrdCompression.None)
        {
            return archive;
        }

        using var target = new MemoryStream();

        // 不写原始文件名与时间戳：同一份定制策略对同一份原版 initrd
        // 必须产出字节一致的产物，幂等判定才能建立在内容比较上。
        using (var gzip = new GZipStream(target, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(archive, 0, archive.Length);
        }

        return target.ToArray();
    }

    /// <summary>
    /// 解压 gzip 并校验尾部 CRC32 与长度。
    /// </summary>
    /// <param name="data">gzip 文件字节内容。</param>
    /// <returns>解压后的字节。</returns>
    private static byte[] InflateGZip(byte[] data)
    {
        if (data.Length < GZipHeaderLength + GZipTrailerLength)
        {
            throw new InvalidDataException("gzip 数据长度不足，无法容纳头部与尾部。");
        }

        if (data[2] != GZipCompressionMethodDeflate)
        {
            throw new InvalidDataException($"gzip 压缩方法 {data[2]} 不受支持，仅支持 deflate。");
        }

        int deflateStart = SkipGZipOptionalFields(data);
        int deflateLength = data.Length - GZipTrailerLength - deflateStart;

        if (deflateLength <= 0)
        {
            throw new InvalidDataException("gzip 缺少 deflate 数据段。");
        }

        byte[] inflated;
        using (var source = new MemoryStream(data, deflateStart, deflateLength, writable: false))
        using (var deflate = new DeflateStream(source, CompressionMode.Decompress))
        using (var target = new MemoryStream())
        {
            deflate.CopyTo(target);
            inflated = target.ToArray();
        }

        uint declaredCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - GZipTrailerLength, 4));
        uint declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 4, 4));

        if (declaredSize != (uint)inflated.Length)
        {
            throw new InvalidDataException(
                $"gzip 声明的原始长度 {declaredSize} 与实际解出的 {inflated.Length} 不一致，数据不完整。");
        }

        uint actualCrc = ComputeCrc32(inflated);
        if (actualCrc != declaredCrc)
        {
            throw new InvalidDataException("gzip 校验和不匹配，数据已损坏。");
        }

        return inflated;
    }

    /// <summary>
    /// 跳过 gzip 头部中的可选字段，定位 deflate 数据段起点。
    /// </summary>
    /// <param name="data">gzip 文件字节内容。</param>
    /// <returns>deflate 数据段的起始偏移。</returns>
    private static int SkipGZipOptionalFields(byte[] data)
    {
        byte flags = data[3];
        int offset = GZipHeaderLength;

        if ((flags & GZipFlagExtraField) != 0)
        {
            if (offset + 2 > data.Length)
            {
                throw new InvalidDataException("gzip 附加字段长度越界。");
            }

            int extraLength = data[offset] | (data[offset + 1] << 8);
            offset += 2 + extraLength;
        }

        if ((flags & GZipFlagOriginalName) != 0)
        {
            offset = SkipGZipZeroTerminated(data, offset, "文件名");
        }

        if ((flags & GZipFlagComment) != 0)
        {
            offset = SkipGZipZeroTerminated(data, offset, "注释");
        }

        if ((flags & GZipFlagHeaderCrc) != 0)
        {
            offset += 2;
        }

        if (offset >= data.Length)
        {
            throw new InvalidDataException("gzip 可选字段长度越界。");
        }

        return offset;
    }

    /// <summary>跳过以 NUL 结尾的 gzip 可选字符串字段。</summary>
    /// <param name="data">gzip 文件字节内容。</param>
    /// <param name="offset">字段起始偏移。</param>
    /// <param name="fieldName">字段名，用于错误描述。</param>
    /// <returns>字段之后的偏移。</returns>
    private static int SkipGZipZeroTerminated(byte[] data, int offset, string fieldName)
    {
        while (offset < data.Length && data[offset] != 0)
        {
            offset++;
        }

        if (offset >= data.Length)
        {
            throw new InvalidDataException($"gzip {fieldName}字段缺少结尾 NUL。");
        }

        return offset + 1;
    }

    /// <summary>
    /// 计算 gzip 使用的 CRC32 校验和。
    /// </summary>
    /// <param name="data">待校验字节。</param>
    /// <returns>CRC32 校验和。</returns>
    private static uint ComputeCrc32(byte[] data)
    {
        uint crc = Crc32Initial;

        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Crc32Polynomial : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>判断数据是否以指定前缀开头。</summary>
    /// <param name="data">待判断的数据。</param>
    /// <param name="prefix">前缀。</param>
    /// <returns>是否以指定前缀开头。</returns>
    private static bool StartsWith(byte[] data, byte[] prefix)
    {
        if (data.Length < prefix.Length)
        {
            return false;
        }

        for (int i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }
}