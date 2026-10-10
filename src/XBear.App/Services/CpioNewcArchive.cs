using System.Globalization;
using System.IO;
using System.Text;
using XBear.Core.Diagnostics;

namespace XBear.App.Services;

/// <summary>
/// cpio(newc) 归档中的一个条目。
///
/// 头部字段全部原样保留：重打包时只有 <see cref="Content"/> 的长度会被重新计算，
/// 其余元数据（inode、权限、属主、时间戳、设备号）沿用原始值，
/// 因此未被定制的条目在解包重打包后与原始字节完全一致。
/// </summary>
public sealed class CpioNewcEntry
{
    /// <summary>条目名，归档内的相对路径。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>条目内容，目录条目为空数组。</summary>
    public byte[] Content { get; set; } = Array.Empty<byte>();

    /// <summary>inode 号。</summary>
    public uint Ino { get; set; }

    /// <summary>文件类型与权限位。</summary>
    public uint Mode { get; set; }

    /// <summary>属主 uid。</summary>
    public uint Uid { get; set; }

    /// <summary>属主 gid。</summary>
    public uint Gid { get; set; }

    /// <summary>硬链接计数，大于 1 表示与后续条目共享数据。</summary>
    public uint Nlink { get; set; }

    /// <summary>修改时间，Unix 时间戳。</summary>
    public uint Mtime { get; set; }

    /// <summary>设备号主号，仅设备条目有意义。</summary>
    public uint DevMajor { get; set; }

    /// <summary>设备号次号，仅设备条目有意义。</summary>
    public uint DevMinor { get; set; }

    /// <summary>特殊文件主设备号。</summary>
    public uint RdevMajor { get; set; }

    /// <summary>特殊文件次设备号。</summary>
    public uint RdevMinor { get; set; }

    /// <summary>头部校验和，newc 格式恒为 0。</summary>
    public uint Check { get; set; }
}

/// <summary>
/// cpio(newc) 格式的纯托管解包与重打包实现。
///
/// 选型理由：initrd 是产品的必经产物，而进程内解析可以在无外部工具、无特权、
/// 无临时目录的条件下完成定制与重打包；调用外部 cpio/gzip 会引入不可控的
/// 工具链依赖，且在受限环境下不可用。
///
/// 取舍：只实现 newc 一种格式。Android-x86 系镜像的 initramfs 全部使用 newc，
/// 为旧版 bin 格式保留解析分支只会增加无人验证的代码路径。
/// </summary>
public static class CpioNewcArchive
{
    /// <summary>newc 格式的魔数。</summary>
    public const string Magic = "070701";

    /// <summary>newc 头部固定长度：6 字节魔数加 13 个 8 字节十六进制字段。</summary>
    public const int HeaderLength = 110;

    /// <summary>归档结束标记的条目名。</summary>
    public const string TrailerName = "TRAILER!!!";

    /// <summary>名称字段的固定宽度。</summary>
    private const int FieldWidth = 8;

    /// <summary>
    /// 解析 newc 归档。
    /// </summary>
    /// <param name="data">归档字节内容。</param>
    /// <returns>按原始顺序排列的条目列表，含结束标记条目。</returns>
    /// <exception cref="XBearException">数据不是合法的 newc 归档时抛出。</exception>
    public static IReadOnlyList<CpioNewcEntry> Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var entries = new List<CpioNewcEntry>();
        int offset = 0;

        while (offset + HeaderLength <= data.Length)
        {
            if (!HasMagic(data, offset))
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"initrd 的 cpio 归档在偏移 {offset} 处缺少 newc 魔数。",
                    "确认该文件是完整的 newc 格式 initramfs，而非截断或非 cpio 的文件。");
            }

            uint fileSize = ReadField(data, offset + 6 + FieldWidth * 6);
            uint nameSize = ReadField(data, offset + 6 + FieldWidth * 11);

            int nameStart = offset + HeaderLength;
            if (nameSize == 0 || nameStart + nameSize > data.Length)
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    "initrd 的 cpio 条目名称长度越界。",
                    "确认该文件是完整的 newc 格式 initramfs。");
            }

            int bodyStart = AlignUp(nameStart + (int)nameSize);
            if (bodyStart < 0 || fileSize > int.MaxValue || bodyStart + fileSize > data.Length)
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    "initrd 的 cpio 条目内容长度越界。",
                    "确认该文件是完整的 newc 格式 initramfs。");
            }

            var entry = new CpioNewcEntry
            {
                Name = ReadName(data, nameStart, (int)nameSize),
                Ino = ReadField(data, offset + 6),
                Mode = ReadField(data, offset + 6 + FieldWidth),
                Uid = ReadField(data, offset + 6 + FieldWidth * 2),
                Gid = ReadField(data, offset + 6 + FieldWidth * 3),
                Nlink = ReadField(data, offset + 6 + FieldWidth * 4),
                Mtime = ReadField(data, offset + 6 + FieldWidth * 5),
                DevMajor = ReadField(data, offset + 6 + FieldWidth * 7),
                DevMinor = ReadField(data, offset + 6 + FieldWidth * 8),
                RdevMajor = ReadField(data, offset + 6 + FieldWidth * 9),
                RdevMinor = ReadField(data, offset + 6 + FieldWidth * 10),
                Check = ReadField(data, offset + 6 + FieldWidth * 12),
                Content = new byte[fileSize]
            };

            Buffer.BlockCopy(data, bodyStart, entry.Content, 0, (int)fileSize);
            entries.Add(entry);

            offset = AlignUp(bodyStart + (int)fileSize);
            if (entry.Name == TrailerName)
            {
                break;
            }
        }

        if (entries.Count == 0)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "initrd 的 cpio 归档中没有任何条目。",
                "确认该文件是完整的 newc 格式 initramfs。");
        }

        // 缺结束标记说明数据在解析途中被截断。静默返回残缺条目会让定制产出一个
        // 缺文件的 initrd，而它要等到启动时才会暴露，因此此处直接判为格式非法。
        if (entries[^1].Name != TrailerName)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "initrd 的 cpio 归档不完整，未读到结束标记。",
                "确认该文件是完整的 newc 格式 initramfs，而非截断或损坏的文件。");
        }

        return entries;
    }

    /// <summary>
    /// 把条目序列重打包为 newc 归档。名称字段补 NUL 结尾，头部与内容各自按 4 字节对齐。
    /// </summary>
    /// <param name="entries">条目列表，最后一项必须是结束标记条目。</param>
    /// <returns>归档字节内容。</returns>
    /// <exception cref="XBearException">条目列表为空或缺少结束标记时抛出。</exception>
    public static byte[] Build(IEnumerable<CpioNewcEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var materialised = entries.ToList();
        if (materialised.Count == 0)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "重打包 cpio 归档时条目列表为空。",
                "这是内部一致性错误，请反馈该问题。");
        }

        if (materialised[^1].Name != TrailerName)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "重打包 cpio 归档时缺少结束标记条目。",
                "这是内部一致性错误，请反馈该问题。");
        }

        using var buffer = new MemoryStream();

        foreach (CpioNewcEntry entry in materialised)
        {
            ArgumentNullException.ThrowIfNull(entry);

            byte[] nameBytes = Encoding.UTF8.GetBytes(entry.Name);
            int nameSize = nameBytes.Length + 1;
            int fileSize = entry.Content.Length;

            WriteMagic(buffer);
            WriteField(buffer, entry.Ino);
            WriteField(buffer, entry.Mode);
            WriteField(buffer, entry.Uid);
            WriteField(buffer, entry.Gid);
            WriteField(buffer, entry.Nlink);
            WriteField(buffer, entry.Mtime);
            WriteField(buffer, (uint)fileSize);
            WriteField(buffer, entry.DevMajor);
            WriteField(buffer, entry.DevMinor);
            WriteField(buffer, entry.RdevMajor);
            WriteField(buffer, entry.RdevMinor);
            WriteField(buffer, (uint)nameSize);
            WriteField(buffer, entry.Check);

            buffer.Write(nameBytes);
            buffer.WriteByte(0);
            WritePadding(buffer, HeaderLength + nameSize);

            buffer.Write(entry.Content, 0, fileSize);
            WritePadding(buffer, fileSize);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// 在条目列表中按名称查找条目，名称比较区分大小写，与归档内的原始写法一致。
    /// </summary>
    /// <param name="entries">条目列表。</param>
    /// <param name="name">目标条目名。</param>
    /// <returns>匹配条目，未找到时返回 null。</returns>
    public static CpioNewcEntry? Find(IEnumerable<CpioNewcEntry> entries, string name)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return entries.FirstOrDefault(entry =>
            string.Equals(entry.Name, name, StringComparison.Ordinal));
    }

    /// <summary>判断指定偏移处是否为 newc 魔数。</summary>
    /// <param name="data">归档字节内容。</param>
    /// <param name="offset">待检查的偏移。</param>
    /// <returns>该偏移处是否为 newc 魔数。</returns>
    private static bool HasMagic(byte[] data, int offset)
    {
        for (int i = 0; i < Magic.Length; i++)
        {
            if (data[offset + i] != (byte)Magic[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>读取定长十六进制字段。</summary>
    /// <param name="data">归档字节内容。</param>
    /// <param name="offset">字段起始偏移。</param>
    /// <returns>字段的无符号数值。</returns>
    private static uint ReadField(byte[] data, int offset)
    {
        ReadOnlySpan<char> text = Encoding.ASCII.GetString(data, offset, FieldWidth);
        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)
            ? value
            : 0u;
    }

    /// <summary>读取条目名，去掉结尾的 NUL。</summary>
    /// <param name="data">归档字节内容。</param>
    /// <param name="offset">名称字段起始偏移。</param>
    /// <param name="nameSize">名称字段长度，含结尾 NUL。</param>
    /// <returns>条目名。</returns>
    private static string ReadName(byte[] data, int offset, int nameSize)
    {
        int length = nameSize;
        while (length > 0 && data[offset + length - 1] == 0)
        {
            length--;
        }

        return Encoding.UTF8.GetString(data, offset, length);
    }

    /// <summary>写入 newc 魔数。</summary>
    /// <param name="buffer">目标流。</param>
    private static void WriteMagic(Stream buffer)
    {
        for (int i = 0; i < Magic.Length; i++)
        {
            buffer.WriteByte((byte)Magic[i]);
        }
    }

    /// <summary>按小写十六进制写入定长字段。</summary>
    /// <param name="buffer">目标流。</param>
    /// <param name="value">字段值。</param>
    private static void WriteField(Stream buffer, uint value)
    {
        byte[] text = Encoding.ASCII.GetBytes(
            value.ToString("x8", CultureInfo.InvariantCulture));
        buffer.Write(text, 0, text.Length);
    }

    /// <summary>写入把整体长度补齐到 4 字节倍数所需的 NUL。</summary>
    /// <param name="buffer">目标流。</param>
    /// <param name="consumed">当前已写入的字节数。</param>
    private static void WritePadding(Stream buffer, int consumed)
    {
        for (int i = consumed; i < AlignUp(consumed); i++)
        {
            buffer.WriteByte(0);
        }
    }

    /// <summary>把长度向上对齐到 4 字节倍数。</summary>
    /// <param name="value">待对齐的长度。</param>
    /// <returns>对齐后的长度。</returns>
    private static int AlignUp(int value) => (value + 3) & ~3;
}