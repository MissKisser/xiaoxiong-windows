using System.Buffers.Binary;
using System.Text;
using XBear.Core.Adb;
using XBear.Core.Diagnostics;

namespace XBear.Core.Tests.Adb;

/// <summary>adb 客户端测试，全部针对内存内的假 adbd，不需要真实 Android 实例。</summary>
public sealed class AdbClientTests : IDisposable
{
    private readonly List<string> _temporaryFiles = [];

    [Fact]
    public async Task ConnectAsync_请求帧使用四字节小端长度前缀且解析出版本()
    {
        await using var server = new FakeAdbdServer { ProtocolVersion = 0x29 };
        await using var client = new AdbClient();

        int version = await client.ConnectAsync(server.Port);

        Assert.Equal(0x29, version);
        byte[] frame = server.RawRequests[0];
        int declared = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(0, 4));
        Assert.Equal(frame.Length - 4, declared);
        Assert.Equal(16, declared);
        Assert.Equal("0010host:version", Encoding.ASCII.GetString(frame, 4, frame.Length - 4));
    }

    [Fact]
    public async Task ConnectAsync_紧凑回包同样能解析出版本()
    {
        await using var server = new FakeAdbdServer { ProtocolVersion = 0x29, PadVersionResponse = false };
        await using var client = new AdbClient();

        Assert.Equal(0x29, await client.ConnectAsync(server.Port));
    }

    [Fact]
    public async Task ConnectAsync_连接被拒时抛协议错误()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await using var client = new AdbClient();
        XBearException error = await Assert.ThrowsAsync<XBearException>(() => client.ConnectAsync(port));

        Assert.Equal(ErrorCategory.Protocol, error.Category);
    }

    [Fact]
    public async Task ShellAsync_返回标准输出()
    {
        await using var server = new FakeAdbdServer
        {
            ShellHandler = command => new ShellResponse($"[{command}] hello\n", 0),
        };

        await using var client = new AdbClient();
        await client.ConnectAsync(server.Port);

        Assert.Equal("[echo hi] hello\n", await client.ShellAsync("echo hi"));
        Assert.Contains(server.Requests, request => request.StartsWith("shell:echo hi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShellAsync_非零退出码抛协议错误并携带错误输出()
    {
        await using var server = new FakeAdbdServer
        {
            ShellHandler = _ => new ShellResponse("No such file or directory\n", 127),
        };

        await using var client = new AdbClient();
        await client.ConnectAsync(server.Port);

        XBearException error = await Assert.ThrowsAsync<XBearException>(() => client.ShellAsync("ls /nope"));

        Assert.Equal(ErrorCategory.Protocol, error.Category);
        Assert.Contains("No such file or directory", error.Message);
        Assert.Contains("127", error.Message);
    }

    [Theory]
    [InlineData("0\n", true)]
    [InlineData("1000\n", false)]
    public async Task IsRootAsync_按id输出判断是否为root(string output, bool expected)
    {
        await using var server = new FakeAdbdServer
        {
            ShellHandler = _ => new ShellResponse(output, 0),
        };

        await using var client = new AdbClient();
        await client.ConnectAsync(server.Port);

        Assert.Equal(expected, await client.IsRootAsync());
    }

    [Fact]
    public async Task PushAsync_按sync子协议写入实例内文件()
    {
        string localPath = CreateTempFile("hello adb\0binary\n");
        await using var server = new FakeAdbdServer();
        await using var client = new AdbClient();
        await client.ConnectAsync(server.Port);

        await client.PushAsync(localPath, "/data/local/tmp/hello.txt");

        Assert.Equal(await File.ReadAllBytesAsync(localPath), server.GetFile("/data/local/tmp/hello.txt"));
        Assert.Contains("sync:", server.Requests);
    }

    [Fact]
    public async Task PullAsync_读取实例内文件并写入宿主()
    {
        byte[] content = Encoding.UTF8.GetBytes("round trip 内容");
        await using var server = new FakeAdbdServer();
        server.SetFile("/data/local/tmp/a.txt", content);

        await using var client = new AdbClient();
        await client.ConnectAsync(server.Port);
        string localPath = ReserveTempPath();
        _temporaryFiles.Add(localPath);

        await client.PullAsync("/data/local/tmp/a.txt", localPath);

        Assert.Equal(content, await File.ReadAllBytesAsync(localPath));
    }

    [Fact]
    public async Task Push与Pull_二进制内容逐字节一致()
    {
        var random = new Random(20260907);
        var content = new byte[200 * 1024];
        random.NextBytes(content);
        for (int i = 0; i < content.Length; i += 7)
        {
            content[i] = 0;
        }

        string localPath = CreateTempFile(content);

        await using var server = new FakeAdbdServer();
        await using var client = new AdbClient();
        await client.ConnectAsync(server.Port);

        await client.PushAsync(localPath, "/data/local/tmp/big.bin");

        string pulledPath = ReserveTempPath();
        _temporaryFiles.Add(pulledPath);
        await client.PullAsync("/data/local/tmp/big.bin", pulledPath);

        Assert.Equal(content, await File.ReadAllBytesAsync(pulledPath));
    }

    [Fact]
    public async Task PullAsync_远端文件不存在时抛协议错误()
    {
        await using var server = new FakeAdbdServer();
        await using var client = new AdbClient();
        await client.ConnectAsync(server.Port);
        string localPath = ReserveTempPath();
        _temporaryFiles.Add(localPath);

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => client.PullAsync("/data/local/tmp/missing", localPath));

        Assert.Equal(ErrorCategory.Protocol, error.Category);
        Assert.Contains("missing", error.Message);
    }

    [Fact]
    public async Task ShellAsync_取消令牌可中断阻塞读取()
    {
        await using var server = new FakeAdbdServer { StallShell = true };
        await using var client = new AdbClient();
        await client.ConnectAsync(server.Port);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ShellAsync("sleep 100", cts.Token));
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

    private string ReserveTempPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xbear-adb-{Guid.NewGuid():N}.bin");
        _temporaryFiles.Add(path);
        return path;
    }

    private string CreateTempFile(string content)
    {
        string path = ReserveTempPath();
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string CreateTempFile(byte[] content)
    {
        string path = ReserveTempPath();
        File.WriteAllBytes(path, content);
        return path;
    }
}