using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using XBear.Core.Abstractions;
using XBear.Core.Projection;

namespace XBear.Core.Tests.Projection;

/// <summary>
/// 内存内假 VNC 服务端，讲 RFB 3.8 协议，用于投屏引擎单元与集成测试。
/// 支持配置初始分辨率、推送合成 Raw 帧、模拟分辨率变化及模拟异常断线。
/// </summary>
internal sealed class FakeVncServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<FakeVncConnection, Task> _handler;
    private readonly Task _acceptLoop;
    private int _connections;

    /// <summary>
    /// 创建假 VNC 服务端。
    /// </summary>
    /// <param name="handler">处理客户端连接的异步委托。</param>
    public FakeVncServer(Func<FakeVncConnection, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>服务端监听端口。</summary>
    public int Port { get; }

    /// <summary>已接收的连接总数。</summary>
    public int ConnectionCount => Volatile.Read(ref _connections);

    /// <summary>
    /// 创建一个按标准握手流程自动运行的假服务端。
    /// </summary>
    /// <param name="initialWidth">初始画面宽度。</param>
    /// <param name="initialHeight">初始画面高度。</param>
    /// <param name="desktopName">公布的桌面名。</param>
    /// <param name="onConnected">完成握手后交由测试控制的连接回调。</param>
    /// <returns>假 VNC 服务端。</returns>
    public static FakeVncServer CreateStandard(
        int initialWidth,
        int initialHeight,
        string desktopName = "xbear-test",
        Func<FakeVncConnection, Task>? onConnected = null)
    {
        return new FakeVncServer(async conn =>
        {
            await conn.SendVersionAsync();
            await conn.ReceiveVersionAsync();

            await conn.SendSecurityNoneAsync();
            byte chosen = await conn.ReceiveSecurityChoiceAsync();
            if (chosen != Rfb.SecurityNone)
            {
                await conn.SendSecurityResultFailureAsync("不支持该安全类型");
                return;
            }

            await conn.SendSecurityResultOkAsync();

            byte shared = await conn.ReceiveClientInitAsync();
            _ = shared;

            await conn.SendServerInitAsync(initialWidth, initialHeight, desktopName);

            await conn.ReceiveSetPixelFormatAsync();
            await conn.ReceiveSetEncodingsAsync();

            if (onConnected is not null)
            {
                await onConnected(conn);
            }

            try
            {
                while (!conn.CancellationToken.IsCancellationRequested)
                {
                    await conn.ReceiveFramebufferUpdateRequestAsync();
                }
            }
            catch (Exception)
            {
            }
        });
    }

    /// <summary>
    /// 释放假服务端。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
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
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
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
            var connection = new FakeVncConnection(client, _cts.Token);
            try
            {
                await _handler(connection).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
            {
            }
        }
    }
}

/// <summary>
/// 假 VNC 服务端的单条连接操作。
/// </summary>
internal sealed class FakeVncConnection
{
    private readonly TcpClient _client;
    private readonly CancellationToken _ct;

    public FakeVncConnection(TcpClient client, CancellationToken cancellationToken)
    {
        _client = client;
        _ct = cancellationToken;
    }

    /// <summary>连接关联的取消令牌。</summary>
    public CancellationToken CancellationToken => _ct;

    /// <summary>发送 RFB 3.8 版本号。</summary>
    public async Task SendVersionAsync()
    {
        await WriteExactAsync(Encoding.ASCII.GetBytes(Rfb.Version));
    }

    /// <summary>读取客户端版本号。</summary>
    public async Task<string> ReceiveVersionAsync()
    {
        byte[] buffer = await ReadExactAsync(Rfb.VersionLength);
        return Encoding.ASCII.GetString(buffer).TrimEnd('\r', '\n');
    }

    /// <summary>公布仅支持 None 安全类型。</summary>
    public async Task SendSecurityNoneAsync()
    {
        await WriteExactAsync([1, Rfb.SecurityNone]);
    }

    /// <summary>公布指定的安全类型列表。</summary>
    public async Task SendSecurityTypesAsync(params byte[] types)
    {
        var payload = new byte[1 + types.Length];
        payload[0] = (byte)types.Length;
        types.CopyTo(payload.AsSpan(1));
        await WriteExactAsync(payload);
    }

    /// <summary>公布安全认证被拒绝（类型数量为 0 加原因）。</summary>
    public async Task SendSecurityRejectedAsync(string reason)
    {
        byte[] reasonBytes = Encoding.UTF8.GetBytes(reason);
        var payload = new byte[1 + 4 + reasonBytes.Length];
        payload[0] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), (uint)reasonBytes.Length);
        reasonBytes.CopyTo(payload.AsSpan(5));
        await WriteExactAsync(payload);
    }

    /// <summary>读取客户端选择的安全类型。</summary>
    public async Task<byte> ReceiveSecurityChoiceAsync()
    {
        byte[] buffer = await ReadExactAsync(1);
        return buffer[0];
    }

    /// <summary>发送安全认证成功结果（4 字节 0）。</summary>
    public async Task SendSecurityResultOkAsync()
    {
        var result = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(result, Rfb.SecurityResultOk);
        await WriteExactAsync(result);
    }

    /// <summary>发送安全认证失败结果（4 字节 1 加原因）。</summary>
    public async Task SendSecurityResultFailureAsync(string reason)
    {
        byte[] reasonBytes = Encoding.UTF8.GetBytes(reason);
        var payload = new byte[4 + 4 + reasonBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), (uint)reasonBytes.Length);
        reasonBytes.CopyTo(payload.AsSpan(8));
        await WriteExactAsync(payload);
    }

    /// <summary>读取 ClientInit（1 字节 shared 标志）。</summary>
    public async Task<byte> ReceiveClientInitAsync()
    {
        byte[] buffer = await ReadExactAsync(1);
        return buffer[0];
    }

    /// <summary>发送 ServerInit 消息。</summary>
    public async Task SendServerInitAsync(int width, int height, string desktopName = "xbear")
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(desktopName);
        var payload = new byte[24 + nameBytes.Length];

        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), (ushort)height);

        payload[4] = 32; // bits-per-pixel
        payload[5] = 24; // depth
        payload[6] = 0;  // big-endian = false
        payload[7] = 1;  // true-colour = true
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(8, 2), 255);  // red-max
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(10, 2), 255); // green-max
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(12, 2), 255); // blue-max
        payload[14] = 16; // red-shift
        payload[15] = 8;  // green-shift
        payload[16] = 0;  // blue-shift

        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20, 4), (uint)nameBytes.Length);
        nameBytes.CopyTo(payload.AsSpan(24));

        await WriteExactAsync(payload);
    }

    /// <summary>读取客户端发来的 SetPixelFormat 消息（20 字节）。</summary>
    public async Task<byte[]> ReceiveSetPixelFormatAsync()
    {
        return await ReadExactAsync(20);
    }

    /// <summary>读取客户端发来的 SetEncodings 消息。</summary>
    public async Task<IReadOnlyList<int>> ReceiveSetEncodingsAsync()
    {
        byte[] header = await ReadExactAsync(4);
        int count = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        var encodings = new List<int>(count);

        if (count > 0)
        {
            byte[] body = await ReadExactAsync(count * 4);
            for (int i = 0; i < count; i++)
            {
                encodings.Add(BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(i * 4, 4)));
            }
        }

        return encodings;
    }

    /// <summary>读取客户端发来的 FramebufferUpdateRequest 消息（10 字节）。</summary>
    public async Task<(byte Incremental, int X, int Y, int Width, int Height)> ReceiveFramebufferUpdateRequestAsync()
    {
        byte[] buffer = await ReadExactAsync(10);
        byte incremental = buffer[1];
        int x = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2, 2));
        int y = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4, 2));
        int width = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(6, 2));
        int height = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(8, 2));
        return (incremental, x, y, width, height);
    }

    /// <summary>
    /// 发送一个完整的合成 Raw 帧矩形。
    /// </summary>
    /// <param name="width">矩形宽度。</param>
    /// <param name="height">矩形高度。</param>
    /// <param name="bgraPixels">BGRA 像素数组，长度为 width * height * 4。</param>
    /// <param name="x">矩形起始横坐标，默认为 0。</param>
    /// <param name="y">矩形起始纵坐标，默认为 0。</param>
    public async Task SendRawFrameAsync(
        int width,
        int height,
        byte[] bgraPixels,
        int x = 0,
        int y = 0)
    {
        ArgumentNullException.ThrowIfNull(bgraPixels);

        var header = new byte[4 + 12];
        header[0] = Rfb.ServerFramebufferUpdate;
        header[1] = 0; // padding
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), 1); // 1 个矩形

        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), (ushort)x);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), (ushort)y);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10, 2), (ushort)height);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12, 4), Rfb.EncodingRaw);

        await WriteExactAsync(header);
        await WriteExactAsync(bgraPixels);
    }

    /// <summary>
    /// 发送桌面尺寸变化伪编码（DesktopSize = -223）。
    /// </summary>
    /// <param name="newWidth">新的画面宽度。</param>
    /// <param name="newHeight">新的画面高度。</param>
    public async Task SendResolutionChangeAsync(int newWidth, int newHeight)
    {
        var header = new byte[4 + 12];
        header[0] = Rfb.ServerFramebufferUpdate;
        header[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), 1);

        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8, 2), (ushort)newWidth);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10, 2), (ushort)newHeight);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12, 4), Rfb.PseudoEncodingDesktopSize);

        await WriteExactAsync(header);
    }

    /// <summary>关闭当前客户端连接。</summary>
    public void Disconnect()
    {
        _client.Close();
    }

    private async Task WriteExactAsync(byte[] buffer)
    {
        NetworkStream stream = _client.GetStream();
        await stream.WriteAsync(buffer, _ct).ConfigureAwait(false);
        await stream.FlushAsync(_ct).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadExactAsync(int count)
    {
        var buffer = new byte[count];
        int offset = 0;
        NetworkStream stream = _client.GetStream();
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), _ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("客户端断开了连接。");
            }

            offset += read;
        }

        return buffer;
    }
}
