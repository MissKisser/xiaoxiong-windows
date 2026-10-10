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
    public async Task ConnectAsync_请求帧使用十六进制ASCII长度前缀且解析出版本()
    {
        await using var server = new FakeAdbdServer { ProtocolVersion = 0x29 };
        await using var client = new AdbClient(server.Port);

        int version = await client.ConnectAsync(5555);

        Assert.Equal(0x29, version);
        byte[] frame = server.RawRequests[0];
        Assert.Equal(16, frame.Length);
        Assert.Equal("000chost:version", Encoding.ASCII.GetString(frame));
    }

    [Fact]
    public async Task ConnectAsync_紧凑回包同样能解析出版本()
    {
        await using var server = new FakeAdbdServer { ProtocolVersion = 0x29, PadVersionResponse = false };
        await using var client = new AdbClient(server.Port);

        Assert.Equal(0x29, await client.ConnectAsync(5555));
    }

    [Fact]
    public async Task ConnectAsync_连接被拒时抛协议错误()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await using var client = new AdbClient(port);
        XBearException error = await Assert.ThrowsAsync<XBearException>(() => client.ConnectAsync(5555));

        Assert.Equal(ErrorCategory.Protocol, error.Category);
    }

    [Fact]
    public async Task ShellAsync_返回标准输出()
    {
        await using var server = new FakeAdbdServer
        {
            ShellHandler = command => new ShellResponse($"[{command}] hello\n", 0),
        };

        await using var client = new AdbClient(server.Port);
        await client.ConnectAsync(5555);

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

        await using var client = new AdbClient(server.Port);
        await client.ConnectAsync(5555);

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

        await using var client = new AdbClient(server.Port);
        await client.ConnectAsync(5555);

        Assert.Equal(expected, await client.IsRootAsync());
    }

    [Fact]
    public async Task PushAsync_按sync子协议写入实例内文件()
    {
        string localPath = CreateTempFile("hello adb\0binary\n");
        await using var server = new FakeAdbdServer();
        await using var client = new AdbClient(server.Port);
        await client.ConnectAsync(5555);

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

        await using var client = new AdbClient(server.Port);
        await client.ConnectAsync(5555);
        string localPath = ReserveTempPath();
        _temporaryFiles.Add(localPath);

        await client.PullAsync("/data/local/tmp/a.txt", localPath);

        Assert.Equal(content, await File.ReadAllBytesAsync(localPath));
    }

    [Fact]
    public async Task StatAsync_存在的文件返回元信息()
    {
        byte[] content = Encoding.UTF8.GetBytes("stat 内容");
        await using var server = new FakeAdbdServer();
        server.SetFile("/sdcard/Download/test.txt", content);

        await using var client = new AdbClient(server.Port);
        await client.ConnectAsync(5555);

        var info = await client.StatAsync("/sdcard/Download/test.txt");

        Assert.NotNull(info);
        Assert.Equal(content.Length, info.Size);
        Assert.True(info.IsRegularFile);
    }

    [Fact]
    public async Task StatAsync_不存在的文件返回空()
    {
        await using var server = new FakeAdbdServer();
        await using var client = new AdbClient(server.Port);
        await client.ConnectAsync(5555);

        var info = await client.StatAsync("/sdcard/Download/missing.bin");

        Assert.Null(info);
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
        await using var client = new AdbClient(server.Port);
        await client.ConnectAsync(5555);

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
        await using var client = new AdbClient(server.Port);
        await client.ConnectAsync(5555);
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
        await using var client = new AdbClient(server.Port);
        await client.ConnectAsync(5555);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ShellAsync("sleep 100", cts.Token));
    }

    /// <summary>
    /// 回归测试：宿主转发会在宿主侧立即接受连接，adbd 未就绪时客户端会永久阻塞在握手上，
    /// 必须由等待上限给出可识别的超时结论而不是挂死。
    /// </summary>
    [Fact]
    public async Task ConnectAsync_握手无响应时按等待上限抛超时而不是挂死()
    {
        await using var server = new FakeAdbdServer { StallHandshake = true };
        await using var client = new AdbClient(server.Port);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        XBearException error = await Assert.ThrowsAsync<XBearException>(() =>
            client.ConnectAsync(5555, timeout: TimeSpan.FromMilliseconds(300)));
        stopwatch.Stop();

        Assert.Equal(ErrorCategory.Timeout, error.Category);
        Assert.False(string.IsNullOrWhiteSpace(error.Remediation));
        Assert.Contains("超时", error.Message, StringComparison.Ordinal);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"等待上限未生效，连接实际耗时 {stopwatch.Elapsed}。");
    }

    /// <summary>等待上限写在客户端构造参数上时同样生效。</summary>
    [Fact]
    public async Task ConnectAsync_客户端级等待上限同样生效()
    {
        await using var server = new FakeAdbdServer { StallHandshake = true };
        await using var client = new AdbClient(server.Port, TimeSpan.FromMilliseconds(300));

        XBearException error = await Assert.ThrowsAsync<XBearException>(() => client.ConnectAsync(5555));

        Assert.Equal(ErrorCategory.Timeout, error.Category);
    }

    /// <summary>默认等待上限必须存在，否则调用方在默认路径上依旧会被永久阻塞。</summary>
    [Fact]
    public void 客户端具备有限的默认连接等待上限()
    {
        Assert.True(AdbClient.DefaultConnectTimeout > TimeSpan.Zero);
        Assert.True(AdbClient.DefaultConnectTimeout <= TimeSpan.FromSeconds(30));
    }

    /// <summary>调用方主动取消必须保持取消语义，不得被包装成超时或协议错误。</summary>
    [Fact]
    public async Task ConnectAsync_调用方取消保持取消语义()
    {
        await using var server = new FakeAdbdServer { StallHandshake = true };
        await using var client = new AdbClient(server.Port);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ConnectAsync(5555, cts.Token, TimeSpan.FromSeconds(30)));
    }

    /// <summary>连接被拒是协议层失败而非超时，两类结论不得混淆。</summary>
    [Fact]
    public async Task ConnectAsync_连接被拒仍归类为协议错误()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await using var client = new AdbClient(port);
        XBearException error = await Assert.ThrowsAsync<XBearException>(() => client.ConnectAsync(5555));

        Assert.Equal(ErrorCategory.Protocol, error.Category);
    }

    /// <summary>握手中途连接被关闭时给出协议层结论，不谎报成连接失败或挂死。</summary>
    [Fact]
    public async Task ConnectAsync_握手阶段连接被关闭按协议错误上报()
    {
        await using var server = new FakeAdbdServer { StallHandshake = true };
        await using var client = new AdbClient(server.Port);

        Task<int> connect = client.ConnectAsync(5555, timeout: TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        await server.DisposeAsync();

        XBearException error = await Assert.ThrowsAsync<XBearException>(() => connect);
        Assert.Equal(ErrorCategory.Protocol, error.Category);
        Assert.Contains("提前关闭", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (string file in _temporaryFiles)
        {
            if (File.Exists(file))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                }
            }
        }
    }

    private string CreateTempFile(string content) => CreateTempFile(Encoding.UTF8.GetBytes(content));

    private string CreateTempFile(byte[] content)
    {
        string path = ReserveTempPath();
        File.WriteAllBytes(path, content);
        _temporaryFiles.Add(path);
        return path;
    }

    private static string ReserveTempPath() =>
        Path.Combine(Path.GetTempPath(), "xbear-adb-test-" + Guid.NewGuid().ToString("N"));
}
