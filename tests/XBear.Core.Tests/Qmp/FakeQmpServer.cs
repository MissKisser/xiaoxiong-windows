using System.Net;
using System.Net.Sockets;
using System.Text;

namespace XBear.Core.Tests.Qmp;

/// <summary>内存内的假 QMP 服务端，只按客户端实现的协议规则应答，不依赖真实 QEMU。</summary>
internal sealed class FakeQmpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<FakeQmpConnection, Task> _handler;
    private readonly Task _acceptLoop;
    private int _connections;

    public FakeQmpServer(Func<FakeQmpConnection, Task> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>监听端口。</summary>
    public int Port { get; }

    /// <summary>已接受的连接数。</summary>
    public int ConnectionCount => Volatile.Read(ref _connections);

    /// <summary>取一个尚未被占用的本地端口，用于构造连接失败的场景。</summary>
    public static int ReserveClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
            // 监听结束属正常路径。
        }
        catch (SocketException)
        {
            // 监听结束属正常路径。
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            Interlocked.Increment(ref _connections);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var connection = new FakeQmpConnection(client, _cts.Token);
            try
            {
                await _handler(connection);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
            {
                // 连接结束时忽略。
            }
        }
    }
}

/// <summary>假 QMP 服务端的一条连接，按行读写 JSON 文本。</summary>
internal sealed class FakeQmpConnection
{
    private readonly TcpClient _client;
    private readonly CancellationToken _ct;
    private readonly List<string> _received = [];

    public FakeQmpConnection(TcpClient client, CancellationToken cancellationToken)
    {
        _client = client;
        _ct = cancellationToken;
    }

    /// <summary>客户端发来的命令序列。</summary>
    public IReadOnlyList<string> Received => _received;

    /// <summary>发送一条 QMP 消息。</summary>
    public async Task SendAsync(string json)
    {
        NetworkStream stream = _client.GetStream();
        byte[] payload = Encoding.UTF8.GetBytes(json + "\r\n");
        await stream.WriteAsync(payload, _ct);
        await stream.FlushAsync(_ct);
    }

    /// <summary>读取客户端的一条命令，返回去掉首尾空白后的原文。</summary>
    public async Task<string> ReceiveAsync()
    {
        NetworkStream stream = _client.GetStream();
        var builder = new StringBuilder();
        byte[] buffer = new byte[512];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, _ct);
            if (read == 0)
            {
                throw new IOException("客户端关闭了连接。");
            }

            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    string line = builder.ToString().Trim();
                    _received.Add(line);
                    return line;
                }

                builder.Append((char)buffer[i]);
            }
        }
    }

    /// <summary>读取客户端的一条命令并取出其中的 execute 字段名。</summary>
    public async Task<string> ReceiveCommandAsync()
    {
        string line = await ReceiveAsync();
        using var document = System.Text.Json.JsonDocument.Parse(line);
        return document.RootElement.GetProperty("execute").GetString()!;
    }

    /// <summary>关闭连接。</summary>
    public void Close() => _client.Close();
}