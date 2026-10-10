using System.IO;
using System.Text;
using XBear.App.Services;
using XBear.Core.Diagnostics;

namespace XBear.App.Tests;

/// <summary>
/// CpioNewcArchive 与 InitrdImageCodec 的格式行为测试。
/// 全部使用进程内构造的样本，不依赖真实 initrd 文件。
/// </summary>
public class CpioNewcArchiveTests
{
    /// <summary>
    /// 解析后重打包，字节内容必须与原包完全一致。
    /// 这是整个定制链路可信的前提：任何未改动的条目都必须原样透传。
    /// </summary>
    [Fact]
    public void RoundTripPreservesArchiveBytes()
    {
        byte[] archive = SampleArchive();

        byte[] rebuilt = CpioNewcArchive.Build(CpioNewcArchive.Parse(archive));

        Assert.Equal(archive, rebuilt);
    }

    /// <summary>
    /// 往返两次结果稳定，说明重打包的输出能被同一套解析器再次读回。
    /// </summary>
    [Fact]
    public void RoundTripIsIdempotentAcrossTwoPasses()
    {
        byte[] once = CpioNewcArchive.Build(CpioNewcArchive.Parse(SampleArchive()));
        byte[] twice = CpioNewcArchive.Build(CpioNewcArchive.Parse(once));

        Assert.Equal(once, twice);
    }

    /// <summary>
    /// 条目顺序、名称与内容在往返后保持不变。
    /// </summary>
    [Fact]
    public void RoundTripPreservesEntryOrderAndContent()
    {
        IReadOnlyList<CpioNewcEntry> parsed = CpioNewcArchive.Parse(SampleArchive());
        IReadOnlyList<CpioNewcEntry> rebuilt = CpioNewcArchive.Parse(CpioNewcArchive.Build(parsed));

        Assert.Equal(parsed.Count, rebuilt.Count);
        for (int i = 0; i < parsed.Count; i++)
        {
            Assert.Equal(parsed[i].Name, rebuilt[i].Name);
            Assert.Equal(parsed[i].Content, rebuilt[i].Content);
        }
    }

    /// <summary>
    /// 头部元数据在往返后保持不变：头部字段由内容长度之外的信息构成，
    /// 任何一项被重算都会静默改变文件权限或属主。
    /// </summary>
    [Fact]
    public void RoundTripPreservesHeaderMetadata()
    {
        IReadOnlyList<CpioNewcEntry> parsed = CpioNewcArchive.Parse(SampleArchive());
        IReadOnlyList<CpioNewcEntry> rebuilt = CpioNewcArchive.Parse(CpioNewcArchive.Build(parsed));

        for (int i = 0; i < parsed.Count; i++)
        {
            Assert.Equal(parsed[i].Ino, rebuilt[i].Ino);
            Assert.Equal(parsed[i].Mode, rebuilt[i].Mode);
            Assert.Equal(parsed[i].Uid, rebuilt[i].Uid);
            Assert.Equal(parsed[i].Gid, rebuilt[i].Gid);
            Assert.Equal(parsed[i].Nlink, rebuilt[i].Nlink);
            Assert.Equal(parsed[i].Mtime, rebuilt[i].Mtime);
            Assert.Equal(parsed[i].DevMajor, rebuilt[i].DevMajor);
            Assert.Equal(parsed[i].DevMinor, rebuilt[i].DevMinor);
            Assert.Equal(parsed[i].RdevMajor, rebuilt[i].RdevMajor);
            Assert.Equal(parsed[i].RdevMinor, rebuilt[i].RdevMinor);
        }
    }

    /// <summary>
    /// 内容长度不是 4 的倍数时，名称与内容各自按 4 字节对齐。
    /// 该对齐是 newc 格式的硬性要求，写错会导致内核解析错位。
    /// </summary>
    [Fact]
    public void OddLengthNamesAndBodiesAreFourByteAligned()
    {
        var entries = new List<CpioNewcEntry>
        {
            Entry("a", new byte[] { 1, 2, 3 }),
            Entry("odd-name", new byte[] { 4, 5, 6, 7, 8 }),
            Entry("TRAILER!!!", Array.Empty<byte>())
        };

        byte[] archive = CpioNewcArchive.Build(entries);

        // 总长度必须是 4 的倍数，且解析结果与写入内容一致。
        Assert.Equal(0, archive.Length % 4);
        IReadOnlyList<CpioNewcEntry> parsed = CpioNewcArchive.Parse(archive);
        Assert.Equal(new byte[] { 1, 2, 3 }, parsed[0].Content);
        Assert.Equal(new byte[] { 4, 5, 6, 7, 8 }, parsed[1].Content);
    }

    /// <summary>
    /// 非 newc 数据必须报错而不是静默产出错误归档。
    /// </summary>
    [Fact]
    public void NonNewcDataThrowsSpecException()
    {
        XBearException error = Assert.Throws<XBearException>(
            () => CpioNewcArchive.Parse(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 }));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.NotNull(error.Remediation);
    }

    /// <summary>
    /// 声明的长度超出实际数据时必须报错，避免按越界长度构造出损坏归档。
    /// </summary>
    [Fact]
    public void TruncatedArchiveThrowsSpecException()
    {
        byte[] full = SampleArchive();
        var truncated = new byte[full.Length - 40];
        Array.Copy(full, truncated, truncated.Length);

        Assert.Throws<XBearException>(() => CpioNewcArchive.Parse(truncated));
    }

    /// <summary>
    /// 缺少结束标记的条目列表不允许重打包，否则产出的归档无法被内核终止解析。
    /// </summary>
    [Fact]
    public void BuildWithoutTrailerThrowsSpecException()
    {
        var entries = new List<CpioNewcEntry> { Entry("init", Encoding.UTF8.GetBytes("x")) };

        XBearException error = Assert.Throws<XBearException>(() => CpioNewcArchive.Build(entries));

        Assert.Equal(ErrorCategory.Spec, error.Category);
    }

    /// <summary>
    /// 空条目列表不允许重打包。
    /// </summary>
    [Fact]
    public void BuildWithNoEntriesThrowsSpecException()
    {
        Assert.Throws<XBearException>(
            () => CpioNewcArchive.Build(Array.Empty<CpioNewcEntry>()));
    }

    /// <summary>
    /// 目录条目（内容为空、模式带目录位）必须能原样往返。
    /// </summary>
    [Fact]
    public void DirectoryEntriesRoundTrip()
    {
        var entries = new List<CpioNewcEntry>
        {
            new CpioNewcEntry { Name = "scripts", Mode = 0x41ed, Content = Array.Empty<byte>() },
            new CpioNewcEntry { Name = "scripts/0-auto-detect", Mode = 0x81ed, Content = Encoding.UTF8.GetBytes("# script") },
            Entry("TRAILER!!!", Array.Empty<byte>())
        };

        IReadOnlyList<CpioNewcEntry> parsed = CpioNewcArchive.Parse(CpioNewcArchive.Build(entries));

        Assert.Equal(0x41edu, parsed[0].Mode);
        Assert.Empty(parsed[0].Content);
        Assert.Equal(Encoding.UTF8.GetBytes("# script"), parsed[1].Content);
    }

    /// <summary>
    /// 按名称查找条目，缺失时返回 null。
    /// </summary>
    [Fact]
    public void FindReturnsEntryOrNull()
    {
        IReadOnlyList<CpioNewcEntry> parsed = CpioNewcArchive.Parse(SampleArchive());

        Assert.NotNull(CpioNewcArchive.Find(parsed, "init"));
        Assert.Null(CpioNewcArchive.Find(parsed, "not-present"));
    }

    /// <summary>
    /// gzip 与未压缩两种封装都能被探测，且压缩往返后归档字节一致。
    /// </summary>
    [Fact]
    public void CompressionEnvelopeRoundTripsForBothFormats()
    {
        byte[] archive = SampleArchive();

        Assert.Equal(
            InitrdCompression.None,
            InitrdImageCodec.Detect(archive));

        byte[] compressed = InitrdImageCodec.Compress(archive, InitrdCompression.GZip);
        Assert.Equal(InitrdCompression.GZip, InitrdImageCodec.Detect(compressed));
        Assert.Equal(
            archive,
            InitrdImageCodec.Decompress(compressed, InitrdCompression.GZip));
    }

    /// <summary>
    /// 相同归档重复压缩必须产出相同字节：否则无法用内容比较来判定幂等。
    /// </summary>
    [Fact]
    public void CompressionIsDeterministic()
    {
        byte[] archive = SampleArchive();

        Assert.Equal(
            InitrdImageCodec.Compress(archive, InitrdCompression.GZip),
            InitrdImageCodec.Compress(archive, InitrdCompression.GZip));
    }

    /// <summary>
    /// 未压缩格式的压缩操作是恒等变换。
    /// </summary>
    [Fact]
    public void UncompressedEnvelopePassesThroughUnchanged()
    {
        byte[] archive = SampleArchive();

        Assert.Equal(archive, InitrdImageCodec.Compress(archive, InitrdCompression.None));
        Assert.Equal(archive, InitrdImageCodec.Decompress(archive, InitrdCompression.None));
    }

    /// <summary>
    /// zstd 等未支持的压缩封装必须明确报错，不得产出启动不了的 initrd。
    /// </summary>
    [Fact]
    public void UnsupportedCompressionThrowsSpecException()
    {
        byte[] zstd = { 0x28, 0xb5, 0x2f, 0xfd, 0x00, 0x00 };

        XBearException error = Assert.Throws<XBearException>(() => InitrdImageCodec.Detect(zstd));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.Contains("zstd", error.Remediation!);
    }

    /// <summary>
    /// 声称是 gzip 但内容损坏时，解压必须报错而不是返回垃圾数据。
    /// </summary>
    [Fact]
    public void CorruptGzipThrowsSpecException()
    {
        byte[] broken = { 0x1f, 0x8b, 0x08, 0x00, 0x01, 0x02, 0x03, 0x04 };

        XBearException error = Assert.Throws<XBearException>(
            () => InitrdImageCodec.Decompress(broken, InitrdCompression.GZip));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.NotNull(error.Remediation);
    }

    /// <summary>
    /// 截断的 gzip 必须报错。
    ///
    /// 这条断言针对的是一个具体后果：基类库的 gzip API 对截断输入会静默返回
    /// 已解出的部分数据，若沿用该行为，定制链路会产出一个缺文件的 initrd，
    /// 而问题要到启动时才暴露。
    /// </summary>
    [Fact]
    public void TruncatedGzipThrowsInsteadOfReturningPartialData()
    {
        byte[] archive = SampleArchive();
        byte[] compressed = InitrdImageCodec.Compress(archive, InitrdCompression.GZip);

        // 分别在数据段与尾部截断，覆盖两种最常见的损坏形态。
        foreach (int cut in new[] { 20, compressed.Length - GZipTrailerLength - 1 })
        {
            var truncated = new byte[compressed.Length - cut];
            Array.Copy(compressed, truncated, truncated.Length);

            XBearException error = Assert.Throws<XBearException>(
                () => InitrdImageCodec.Decompress(truncated, InitrdCompression.GZip));

            Assert.Equal(ErrorCategory.Spec, error.Category);
        }
    }

    /// <summary>
    /// 只剩 gzip 头部的空壳必须报错。
    /// </summary>
    [Fact]
    public void GzipHeaderWithoutBodyThrowsSpecException()
    {
        byte[] shell = { 0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0a };

        Assert.Throws<XBearException>(
            () => InitrdImageCodec.Decompress(shell, InitrdCompression.GZip));
    }

    /// <summary>
    /// 尾部校验和被篡改时必须报错：长度正确但内容已损坏的组合同样不能放行。
    /// </summary>
    [Fact]
    public void TamperedGzipPayloadThrowsSpecException()
    {
        byte[] archive = SampleArchive();
        byte[] compressed = InitrdImageCodec.Compress(archive, InitrdCompression.GZip);

        // 翻转原始数据首字节。压缩后的字节可能不变，因此改为篡改尾部校验和，
        // 确保一定构造出「长度对不上内容」的损坏数据。
        compressed[^8] ^= 0xff;

        Assert.Throws<XBearException>(
            () => InitrdImageCodec.Decompress(compressed, InitrdCompression.GZip));
    }

    /// <summary>gzip 尾部长度，与生产代码的解析口径保持一致。</summary>
    private const int GZipTrailerLength = 8;

    /// <summary>
    /// 构造一个包含目录、脚本与二进制文件的最小归档，字段取值刻意各不相同以暴露错位。
    /// </summary>
    private static byte[] SampleArchive()
    {
        var entries = new List<CpioNewcEntry>
        {
            new CpioNewcEntry
            {
                Name = "bin",
                Ino = 1,
                Mode = 0x41ed,
                Uid = 0,
                Gid = 0,
                Nlink = 2,
                Mtime = 1700000000,
                Content = Array.Empty<byte>()
            },
            new CpioNewcEntry
            {
                Name = "init",
                Ino = 2,
                Mode = 0x81ed,
                Uid = 0,
                Gid = 0,
                Nlink = 1,
                Mtime = 1700000001,
                Content = Encoding.UTF8.GetBytes("#!/bin/busybox sh\nmount_data\nmount_sdcard\n")
            },
            new CpioNewcEntry
            {
                Name = "scripts/0-auto-detect",
                Ino = 3,
                Mode = 0x81ed,
                Uid = 0,
                Gid = 0,
                Nlink = 1,
                Mtime = 1700000002,
                Content = Encoding.UTF8.GetBytes("auto_detect()\n{\n\techo hi\n}\n")
            },
            new CpioNewcEntry
            {
                Name = "bin/busybox",
                Ino = 4,
                Mode = 0x81ed,
                Uid = 0,
                Gid = 0,
                Nlink = 1,
                Mtime = 1700000003,
                Content = new byte[] { 0x7f, 0x45, 0x4c, 0x46, 0x02, 0x01, 0x01, 0x00, 0xff }
            },
            Entry(CpioNewcArchive.TrailerName, Array.Empty<byte>())
        };

        return CpioNewcArchive.Build(entries);
    }

    /// <summary>构造一个带默认元数据的条目。</summary>
    /// <param name="name">条目名。</param>
    /// <param name="content">条目内容。</param>
    /// <returns>构造好的条目。</returns>
    private static CpioNewcEntry Entry(string name, byte[] content) => new()
    {
        Name = name,
        Mode = 0x81a4,
        Nlink = 1,
        Mtime = 1700000000,
        Content = content
    };
}