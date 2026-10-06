using System.Buffers.Binary;
using System.Text;
using XBear.Core.Adb;

namespace XBear.Core.Tests.Adb;

/// <summary>
/// sync 子协议的黄金向量测试：直接断言写上 socket 的原始字节，不依赖假服务端的解析逻辑。
/// 期望字节按协议文档手工推导并硬编码，因此假服务端与客户端同时写错时这些用例仍会转红。
/// </summary>
public sealed class SyncGoldenVectorTests : IDisposable
{
    /// <summary>协议规定的单块数据上限。</summary>
    private const int MaxChunk = 64 * 1024;

    /// <summary>客户端推送时使用的十进制权限位，对应八进制 0644。</summary>
    private const uint DefaultMode = 420;

    private readonly List<string> _temporaryFiles = [];

    [Fact]
    public void SEND帧_命令字在前长度在后且长度覆盖路径与权限位()
    {
        byte[] frame = AdbClient.BuildSendHeader("/data/local/tmp/a.txt", DefaultMode);

        Assert.Equal(
            new byte[]
            {
                0x53, 0x45, 0x4E, 0x44,
                0x19, 0x00, 0x00, 0x00,
                (byte)'/', (byte)'d', (byte)'a', (byte)'t', (byte)'a', (byte)'/', (byte)'l', (byte)'o',
                (byte)'c', (byte)'a', (byte)'l', (byte)'/', (byte)'t', (byte)'m', (byte)'p', (byte)'/',
                (byte)'a', (byte)'.', (byte)'t', (byte)'x', (byte)'t', (byte)',', (byte)'4', (byte)'2', (byte)'0',
            },
            frame);
    }

    [Fact]
    public void SEND帧_长度字段等于路径与权限位拼接后的字节数()
    {
        const string RemotePath = "/sdcard/大文件.bin";

        byte[] frame = AdbClient.BuildSendHeader(RemotePath, DefaultMode);

        int declared = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4, 4));
        string combined = string.Concat(RemotePath, ",", DefaultMode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Encoding.UTF8.GetByteCount(combined), declared);
        Assert.Equal(combined, Encoding.UTF8.GetString(frame, 8, declared));
    }

    [Fact]
    public void DATA帧_命令字在前长度等于块大小()
    {
        byte[] frame = AdbClient.BuildDataHeader(17);

        Assert.Equal(new byte[] { 0x44, 0x41, 0x54, 0x41, 0x11, 0x00, 0x00, 0x00 }, frame);
    }

    [Fact]
    public void DONE帧_长度字段为小端最后修改时间()
    {
        byte[] frame = AdbClient.BuildDoneHeader(0x12345678);

        Assert.Equal(new byte[] { 0x44, 0x4F, 0x4E, 0x45, 0x78, 0x56, 0x34, 0x12 }, frame);
    }

    [Fact]
    public void 长度前置的错误布局_无法通过标准向量校验()
    {
        // 旧实现把长度写在命令字之前，等价于「payloadLen | SEND | mode | pathLen | path」。
        byte[] wrong = BuildLengthPrefixedSendHeader("/data/local/tmp/a.txt", DefaultMode);
        byte[] correct = AdbClient.BuildSendHeader("/data/local/tmp/a.txt", DefaultMode);

        Assert.NotEqual(correct, wrong);
        Assert.NotEqual(Encoding.ASCII.GetBytes("SEND"), wrong.AsSpan(0, 4).ToArray());
        Assert.Equal(Encoding.ASCII.GetBytes("SEND"), correct.AsSpan(0, 4).ToArray());

        // 标准服务端只会读前 4 字节当命令字，读到的是长度而非 SEND。
        Assert.NotEqual("SEND", Encoding.ASCII.GetString(wrong, 0, 4));
    }

    [Fact]
    public async Task PushAsync_线上SEND帧逐字节匹配黄金向量()
    {
        byte[] content = Encoding.UTF8.GetBytes("golden");
        string localPath = CreateTempFile(content);
        await using var server = new FakeAdbdServer();
        await using var client = new AdbClient();
        await client.ConnectAsync(server.Port);

        await client.PushAsync(localPath, "/data/local/tmp/a.txt");

        IReadOnlyList<byte[]> sends = server.SyncFramesOf("SEND");
        byte[] frame = Assert.Single(sends);
        Assert.Equal(
            new byte[]
            {
                0x53, 0x45, 0x4E, 0x44,
                0x19, 0x00, 0x00, 0x00,
                (byte)'/', (byte)'d', (byte)'a', (byte)'t', (byte)'a', (byte)'/', (byte)'l', (byte)'o',
                (byte)'c', (byte)'a', (byte)'l', (byte)'/', (byte)'t', (byte)'m', (byte)'p', (byte)'/',
                (byte)'a', (byte)'.', (byte)'t', (byte)'x', (byte)'t', (byte)',', (byte)'4', (byte)'2', (byte)'0',
            },
            frame);
    }

    [Fact]
    public async Task PushAsync_线上DATA帧逐字节匹配黄金向量()
    {
        byte[] content = [0x00, 0x01, 0x02, 0xFD, 0xFE, 0xFF];
        string localPath = CreateTempFile(content);
        await using var server = new FakeAdbdServer();
        await using var client = new AdbClient();
        await client.ConnectAsync(server.Port);

        await client.PushAsync(localPath, "/data/local/tmp/a.bin");

        IReadOnlyList<byte[]> datas = server.SyncFramesOf("DATA");
        byte[] frame = Assert.Single(datas);
        Assert.Equal(
            new byte[] { 0x44, 0x41, 0x54, 0x41, 0x06, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0xFD, 0xFE, 0xFF },
            frame);
    }

    [Fact]
    public async Task PushAsync_线上DONE帧命令字与长度位置正确()
    {
        string localPath = CreateTempFile(Encoding.UTF8.GetBytes("x"));
        await using var server = new FakeAdbdServer();
        await using var client = new AdbClient();
        await client.ConnectAsync(server.Port);

        await client.PushAsync(localPath, "/data/local/tmp/a.txt");

        byte[] done = Assert.Single(server.SyncFramesOf("DONE"));
        Assert.Equal(8, done.Length);
        Assert.Equal(new byte[] { 0x44, 0x4F, 0x4E, 0x45 }, done.AsSpan(0, 4).ToArray());
        Assert.Equal(done, AdbClient.BuildDoneHeader(server.GetMtime("/data/local/tmp/a.txt")!.Value));
    }

    [Fact]
    public async Task PushAsync_跨越块边界的文件每块都不超过64KB()
    {
        const int Total = (MaxChunk * 2) + 1234;
        var content = new byte[Total];
        new Random(20260907).NextBytes(content);
        string localPath = CreateTempFile(content);

        await using var server = new FakeAdbdServer();
        await using var client = new AdbClient();
        await client.ConnectAsync(server.Port);

        await client.PushAsync(localPath, "/data/local/tmp/big.bin");

        IReadOnlyList<byte[]> datas = server.SyncFramesOf("DATA");
        Assert.Equal(3, datas.Count);
        int observed = 0;
        foreach (byte[] frame in datas)
        {
            Assert.Equal("DATA", Encoding.ASCII.GetString(frame, 0, 4));
            int length = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4, 4));
            Assert.InRange(length, 1, MaxChunk);
            Assert.Equal(8 + length, frame.Length);
            observed += length;
        }

        Assert.Equal(Total, observed);
        Assert.Equal(content, server.GetFile("/data/local/tmp/big.bin"));
    }

    [Fact]
    public async Task PushAsync_命令字顺序为SEND后接DATA块与DONE()
    {
        string localPath = CreateTempFile(new byte[MaxChunk + 5]);
        await using var server = new FakeAdbdServer();
        await using var client = new AdbClient();
        await client.ConnectAsync(server.Port);

        await client.PushAsync(localPath, "/data/local/tmp/x.bin");

        Assert.Equal(new[] { "SEND", "DATA", "DATA", "DONE" }, server.SyncCommands);
    }

    [Fact]
    public async Task PullAsync_线上RECV帧命令字在前长度等于路径字节数()
    {
        byte[] content = Encoding.UTF8.GetBytes("pulled");
        await using var server = new FakeAdbdServer();
        server.SetFile("/data/local/tmp/b.txt", content);
        await using var client = new AdbClient();
        await client.ConnectAsync(server.Port);
        string localPath = ReserveTempPath();

        await client.PullAsync("/data/local/tmp/b.txt", localPath);

        byte[] recv = Assert.Single(server.SyncFramesOf("RECV"));
        Assert.Equal(new byte[] { 0x52, 0x45, 0x43, 0x56 }, recv.AsSpan(0, 4).ToArray());
        Assert.Equal(21, BinaryPrimitives.ReadInt32LittleEndian(recv.AsSpan(4, 4)));
        Assert.Equal("/data/local/tmp/b.txt", Encoding.UTF8.GetString(recv, 8, 21));
        Assert.Equal(content, await File.ReadAllBytesAsync(localPath));
    }

    [Fact]
    public async Task Push与Pull_小文件往返二进制完全一致()
    {
        var content = new byte[512];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i % 256);
        }

        string localPath = CreateTempFile(content);
        await using var server = new FakeAdbdServer();
        await using var client = new AdbClient();
        await client.ConnectAsync(server.Port);

        await client.PushAsync(localPath, "/data/local/tmp/rt.bin");

        string pulledPath = ReserveTempPath();
        await client.PullAsync("/data/local/tmp/rt.bin", pulledPath);

        Assert.Equal(content, await File.ReadAllBytesAsync(pulledPath));
    }

    [Fact]
    public async Task PushAsync_权限位以十进制ASCII拼在路径之后()
    {
        string localPath = CreateTempFile(Encoding.UTF8.GetBytes("m"));
        await using var server = new FakeAdbdServer();
        await using var client = new AdbClient();
        await client.ConnectAsync(server.Port);

        await client.PushAsync(localPath, "/data/local/tmp/m.txt");

        Assert.Equal(420u, server.GetMode("/data/local/tmp/m.txt"));
        byte[] send = Assert.Single(server.SyncFramesOf("SEND"));
        Assert.EndsWith(",420", Encoding.UTF8.GetString(send, 8, send.Length - 8), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (string path in _temporaryFiles)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>构造旧实现的错误布局：长度字段在命令字之前，权限位与路径各有独立长度。</summary>
    /// <param name="remotePath">实例内目标路径。</param>
    /// <param name="mode">十进制权限位。</param>
    /// <returns>旧布局的帧字节。</returns>
    private static byte[] BuildLengthPrefixedSendHeader(string remotePath, uint mode)
    {
        byte[] pathBytes = Encoding.UTF8.GetBytes(remotePath);
        int payloadLength = 12 + pathBytes.Length;
        var frame = new byte[4 + payloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), (uint)payloadLength);
        Encoding.ASCII.GetBytes("SEND").CopyTo(frame, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), mode);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12, 4), (uint)pathBytes.Length);
        pathBytes.CopyTo(frame, 16);
        return frame;
    }

    private string ReserveTempPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xbear-adb-vec-{Guid.NewGuid():N}.bin");
        _temporaryFiles.Add(path);
        return path;
    }

    private string CreateTempFile(byte[] content)
    {
        string path = ReserveTempPath();
        File.WriteAllBytes(path, content);
        return path;
    }
}
