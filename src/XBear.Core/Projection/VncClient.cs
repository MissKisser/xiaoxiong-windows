using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Projection;

/// <summary>RFB 消息类型与服务端消息常量。</summary>
internal static class Rfb
{
    /// <summary>投屏取帧使用的 RFB 协议版本号。</summary>
    public const string Version = "RFB 003.008\n";

    /// <summary>版本号在协议里的字节长度，含行尾符。</summary>
    public const int VersionLength = 12;

    /// <summary>不进行认证的安全类型。本地实例的 VNC 服务端只开放该类型。</summary>
    public const byte SecurityNone = 1;

    /// <summary>会话共享标志：与其他连接共享同一画面。</summary>
    public const byte SharedFlag = 1;

    /// <summary>帧缓冲更新请求：仅请求发生变化的部分。</summary>
    public const byte FramebufferUpdateRequestIncremental = 3;

    /// <summary>帧缓冲更新请求：请求整个画面。</summary>
    public const byte FramebufferUpdateRequestFull = 0;

    /// <summary>服务端消息：帧缓冲更新。</summary>
    public const byte ServerFramebufferUpdate = 0;

    /// <summary>服务端消息：调色板条目。</summary>
    public const byte ServerSetColourMapEntries = 1;

    /// <summary>服务端消息：响铃。</summary>
    public const byte ServerBell = 2;

    /// <summary>服务端消息：服务端剪贴板文本。</summary>
    public const byte ServerCutText = 3;

    /// <summary>编码：原始像素。</summary>
    public const int EncodingRaw = 0;

    /// <summary>伪编码：桌面尺寸变化。</summary>
    public const int PseudoEncodingDesktopSize = -223;

    /// <summary>伪编码：扩展桌面尺寸变化。</summary>
    public const int PseudoEncodingExtendedDesktopSize = -308;

    /// <summary>扩展桌面尺寸伪编码的单屏条目字节数：坐标与宽高各两字节，标志四字节。</summary>
    public const int ExtendedDesktopScreenEntryBytes = 12;

    /// <summary>扩展桌面尺寸伪编码允许的最大屏幕数，用于挡住畸形长度。</summary>
    public const int MaxExtendedDesktopScreens = 16;

    /// <summary>安全握手成功的结果值。</summary>
    public const uint SecurityResultOk = 0;
}

/// <summary>取帧参数。</summary>
public sealed class VncClientOptions
{
    /// <summary>单次连接与取帧的等待上限，为空时使用客户端默认上限。</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>是否与其他连接共享同一画面，默认共享。</summary>
    public bool Shared { get; init; } = true;
}

/// <summary>一次分辨率变化的记录。</summary>
/// <param name="Previous">变化前的画面尺寸。</param>
/// <param name="Current">变化后的画面尺寸。</param>
public readonly record struct VncResolutionChanged(ScreenGeometry Previous, ScreenGeometry Current);

/// <summary>取帧循环观察到的新一帧。</summary>
/// <param name="Sequence">该帧的画面序号。</param>
/// <param name="Geometry">该帧的画面尺寸。</param>
/// <param name="DecodedAt">该帧解码完成的时刻。</param>
public readonly record struct VncFrameDecoded(long Sequence, ScreenGeometry Geometry, DateTimeOffset DecodedAt);

/// <summary>
/// RFB 3.8 取帧客户端：完成协议版本与安全类型协商，按 32 位真彩色 BGRA 取原始像素，
/// 并在增量请求循环中持续把画面更新解码进帧缓冲。
/// 全部网络操作异步进行，任何一步都能被取消令牌打断。
/// </summary>
public sealed class VncClient : IAsyncDisposable
{
    private const int ReadBufferSize = 64 * 1024;
    private const int MaxSecurityReasonLength = 4096;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly VncClientOptions _options;
    private readonly TimeSpan _timeout;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private byte[] _readBuffer;
    private byte[] _pixelBuffer;
    private int _disposed;

    /// <summary>
    /// 创建取帧客户端。
    /// </summary>
    /// <param name="options">取帧参数，为空时使用默认参数。</param>
    public VncClient(VncClientOptions? options = null)
    {
        _options = options ?? new VncClientOptions();
        _timeout = _options.Timeout is { } configured && configured > TimeSpan.Zero
            ? configured
            : DefaultTimeout;
        _readBuffer = new byte[ReadBufferSize];
        _pixelBuffer = new byte[ReadBufferSize];
    }

    /// <summary>画面尺寸变化时触发，实例旋转屏幕或改变显示分辨率时发生。</summary>
    public event EventHandler<VncResolutionChanged>? ResolutionChanged;

    /// <summary>每一帧解码完成时触发。</summary>
    public event EventHandler<VncFrameDecoded>? FrameDecoded;

    /// <summary>
    /// 连接实例的 VNC 端口，完成版本与安全类型协商、像素格式与编码协商，
    /// 并按服务端公布的画面尺寸初始化帧缓冲。
    /// </summary>
    /// <param name="port">实例在宿主上映射的 VNC 端口。</param>
    /// <param name="frames">承载本次投屏取帧的帧缓冲。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务端公布的桌面名与画面尺寸。</returns>
    /// <exception cref="XBearException">
    /// 连接失败为 <see cref="ErrorCategory.Protocol"/>；
    /// 服务端要求本端不支持的安全类型时同样为 <see cref="ErrorCategory.Protocol"/>，
    /// 失败信息中带上对端给出的原因，便于直接呈现给用户。
    /// </exception>
    public async Task<VncServerInfo> ConnectAsync(
        int port,
        ProjectionFrameStore frames,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(frames);
        ValidatePort(port);

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using CancellationTokenSource timeout = CreateTimeoutToken(cancellationToken);
            await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw ProtocolError(
                $"连接实例 VNC 端口 {port} 超时。",
                "请确认实例已完全启动并取得 VNC 端口后重试。");
        }
        catch (OperationCanceledException)
        {
            tcp.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            tcp.Dispose();
            throw ProtocolError(
                $"无法连接实例 VNC 端口 {port}：{ex.Message}",
                "请确认实例处于运行态，且其暴露级别允许本机访问该端口。",
                ex);
        }

        _tcp = tcp;
        _stream = tcp.GetStream();

        try
        {
            return await HandshakeAsync(frames, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TeardownAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 持续请求画面更新并把原始像素解码进帧缓冲，直到取消令牌被触发或连接中断。
    /// </summary>
    /// <param name="frames">承载本次投屏取帧的帧缓冲。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>取帧循环结束时的任务。</returns>
    /// <exception cref="XBearException">
    /// 连接中断或协议不符时为 <see cref="ErrorCategory.Protocol"/>。
    /// </exception>
    public async Task RunFrameLoopAsync(
        ProjectionFrameStore frames,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(frames);

        NetworkStream stream = RequireStream();

        await WriteAsync(
            BuildFramebufferUpdateRequest(frames.Geometry, Rfb.FramebufferUpdateRequestFull),
            cancellationToken).ConfigureAwait(false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte messageType;
            try
            {
                messageType = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException ex)
            {
                throw Disconnected(ex);
            }

            switch (messageType)
            {
                case Rfb.ServerFramebufferUpdate:
                {
                    long previousSequence = frames.Sequence;
                    long sequence = await ReadFramebufferUpdateAsync(stream, frames, cancellationToken)
                        .ConfigureAwait(false);
                    if (sequence > previousSequence)
                    {
                        FrameDecoded?.Invoke(
                            this,
                            new VncFrameDecoded(sequence, frames.Geometry, DateTimeOffset.UtcNow));
                    }
                    break;
                }

                case Rfb.ServerSetColourMapEntries:
                    await SkipColourMapEntriesAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case Rfb.ServerBell:
                    // 响铃不带后续数据，忽略即可。
                    break;

                case Rfb.ServerCutText:
                    await SkipServerCutTextAsync(cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw ProtocolError(
                        $"收到未知的 VNC 服务端消息类型 {messageType}。",
                        "请确认实例的 VNC 服务端实现符合 RFB 3.8 协议。");
            }

            await WriteAsync(
                BuildFramebufferUpdateRequest(frames.Geometry, Rfb.FramebufferUpdateRequestIncremental),
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 释放套接字与缓冲，不改变服务端状态。
    /// </summary>
    /// <returns>表示释放完成的异步结果。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await TeardownAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task<VncServerInfo> HandshakeAsync(
        ProjectionFrameStore frames,
        CancellationToken cancellationToken)
    {
        NetworkStream stream = RequireStream();

        byte[] serverVersion = await ReadExactlyAsync(Rfb.VersionLength, cancellationToken).ConfigureAwait(false);
        string version = Encoding.ASCII.GetString(serverVersion).TrimEnd('\r', '\n');
        if (!version.StartsWith("RFB ", StringComparison.Ordinal))
        {
            throw ProtocolError(
                $"VNC 服务端返回的协议版本标识非法：{version}。",
                "请确认实例的 VNC 服务端已启动。");
        }

        await WriteAsync(Encoding.ASCII.GetBytes(Rfb.Version), cancellationToken).ConfigureAwait(false);
        await NegotiateSecurityAsync(cancellationToken).ConfigureAwait(false);

        await WriteAsync(
            [_options.Shared ? Rfb.SharedFlag : (byte)0],
            cancellationToken).ConfigureAwait(false);

        VncServerInfo info = await ReadServerInitAsync(frames, cancellationToken).ConfigureAwait(false);

        await WriteAsync(BuildSetPixelFormat(RfbPixelFormat.ProjectionBgra32), cancellationToken)
            .ConfigureAwait(false);
        await WriteAsync(BuildSetEncodings(), cancellationToken).ConfigureAwait(false);

        return info;
    }

    private async Task NegotiateSecurityAsync(CancellationToken cancellationToken)
    {
        byte count = await ReadByteAsync(cancellationToken).ConfigureAwait(false);

        if (count == 0)
        {
            string reason = await ReadReasonAsync(cancellationToken).ConfigureAwait(false);
            throw ProtocolError(
                $"VNC 服务端拒绝了本次连接：{reason}",
                "请确认实例的 VNC 服务端允许本机连接。");
        }

        byte[] types = await ReadExactlyAsync(count, cancellationToken).ConfigureAwait(false);

        if (Array.IndexOf(types, Rfb.SecurityNone) < 0)
        {
            throw ProtocolError(
                $"VNC 服务端只接受 {DescribeSecurityTypes(types)}，本端仅支持不认证（None）方式。",
                "请在实例参数中为 VNC 配置不认证访问，或改用带认证的取帧通道。");
        }

        await WriteAsync([Rfb.SecurityNone], cancellationToken).ConfigureAwait(false);

        byte[] resultBytes = await ReadExactlyAsync(4, cancellationToken).ConfigureAwait(false);
        uint result = BinaryPrimitives.ReadUInt32BigEndian(resultBytes);
        if (result == Rfb.SecurityResultOk)
        {
            return;
        }

        string failure = await ReadReasonAsync(cancellationToken).ConfigureAwait(false);
        throw ProtocolError(
            $"VNC 安全握手未通过：{failure}",
            "请确认实例的 VNC 服务端允许不认证连接。");
    }

    private async Task<VncServerInfo> ReadServerInitAsync(
        ProjectionFrameStore frames,
        CancellationToken cancellationToken)
    {
        byte[] header = await ReadExactlyAsync(24, cancellationToken).ConfigureAwait(false);

        int width = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
        int height = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        ValidateFrameGeometry(width, height);

        uint nameLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
        if (nameLength > MaxSecurityReasonLength * 1024)
        {
            throw ProtocolError(
                $"VNC 服务端公布的桌面名长度异常：{nameLength}。",
                "请确认实例的 VNC 服务端实现符合 RFB 3.8 协议。");
        }

        byte[] nameBytes = nameLength == 0
            ? []
            : await ReadExactlyAsync((int)nameLength, cancellationToken).ConfigureAwait(false);

        frames.ResetGeometry(width, height);

        return new VncServerInfo(
            new ScreenGeometry(width, height),
            Encoding.UTF8.GetString(nameBytes));
    }

    private async Task<long> ReadFramebufferUpdateAsync(
        NetworkStream stream,
        ProjectionFrameStore frames,
        CancellationToken cancellationToken)
    {
        byte[] prefix = await ReadExactlyAsync(3, cancellationToken).ConfigureAwait(false);
        int rectangleCount = BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(1, 2));

        bool hasPixelUpdate = false;
        bool resolutionChanged = false;
        var previous = frames.Geometry;

        for (int index = 0; index < rectangleCount; index++)
        {
            byte[] rectangleHeader = await ReadExactlyAsync(12, cancellationToken).ConfigureAwait(false);
            int x = BinaryPrimitives.ReadUInt16BigEndian(rectangleHeader.AsSpan(0, 2));
            int y = BinaryPrimitives.ReadUInt16BigEndian(rectangleHeader.AsSpan(2, 2));
            int width = BinaryPrimitives.ReadUInt16BigEndian(rectangleHeader.AsSpan(4, 2));
            int height = BinaryPrimitives.ReadUInt16BigEndian(rectangleHeader.AsSpan(6, 2));
            int encoding = BinaryPrimitives.ReadInt32BigEndian(rectangleHeader.AsSpan(8, 4));

            switch (encoding)
            {
                case Rfb.EncodingRaw:
                    await ReadRawRectangleAsync(frames, x, y, width, height, cancellationToken)
                        .ConfigureAwait(false);
                    hasPixelUpdate = true;
                    break;

                case Rfb.PseudoEncodingDesktopSize:
                    ApplyResolutionChange(frames, width, height, ref resolutionChanged);
                    break;

                case Rfb.PseudoEncodingExtendedDesktopSize:
                {
                    ScreenGeometry announced = await ReadExtendedDesktopSizeAsync(
                        frames,
                        width,
                        height,
                        cancellationToken).ConfigureAwait(false);

                    ApplyResolutionChange(frames, announced.Width, announced.Height, ref resolutionChanged);
                    break;
                }

                default:
                    throw ProtocolError(
                        $"VNC 服务端下发了未协商的编码 {encoding}。",
                        "请确认实例的 VNC 服务端只使用原始编码与桌面尺寸伪编码。");
            }
        }

        long sequence = frames.Sequence;
        if (hasPixelUpdate)
        {
            sequence = frames.Publish(DateTimeOffset.UtcNow);
        }

        if (resolutionChanged)
        {
            ResolutionChanged?.Invoke(this, new VncResolutionChanged(previous, frames.Geometry));
        }

        return sequence;
    }

    /// <summary>
    /// 读取扩展桌面尺寸伪编码的载荷。载荷为「屏幕数」加「每屏 12 字节」，
    /// 屏幕数可为零且可大于一，因此必须按实际屏幕数逐屏消费，不能按固定长度跳读：
    /// 少读会让后续矩形与像素全部错位，投屏会立刻中断。
    /// </summary>
    /// <param name="frames">承载画面的帧缓冲。</param>
    /// <param name="headerWidth">矩形头给出的宽度。</param>
    /// <param name="headerHeight">矩形头给出的高度。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析出的呈现画幅；首帧尺寸未变时为 null。</returns>
    private async Task<ScreenGeometry> ReadExtendedDesktopSizeAsync(
        ProjectionFrameStore frames,
        int headerWidth,
        int headerHeight,
        CancellationToken cancellationToken)
    {
        byte[] countBytes = await ReadExactlyAsync(1, cancellationToken).ConfigureAwait(false);
        int screenCount = countBytes[0];

        // 上限只用于挡住畸形长度；正常实现为 1。
        if (screenCount > Rfb.MaxExtendedDesktopScreens)
        {
            throw ProtocolError(
                $"VNC 服务端下发的扩展桌面尺寸屏幕数为 {screenCount}，超出可接受范围。",
                "请确认实例的 VNC 服务端实现符合 RFB 3.8 的扩展桌面尺寸伪编码。");
        }

        int announcedWidth = headerWidth;
        int announcedHeight = headerHeight;

        for (int screen = 0; screen < screenCount; screen++)
        {
            byte[] entry = await ReadExactlyAsync(Rfb.ExtendedDesktopScreenEntryBytes, cancellationToken)
                .ConfigureAwait(false);

            // 每屏布局为 x、y、宽、高各两字节，标志四字节。
            int screenWidth = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(4, 2));
            int screenHeight = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(6, 2));

            if (screen == 0 && screenWidth > 0 && screenHeight > 0)
            {
                announcedWidth = screenWidth;
                announcedHeight = screenHeight;
            }
        }

        ValidateFrameGeometry(announcedWidth, announcedHeight);
        return new ScreenGeometry(announcedWidth, announcedHeight);
    }

    private async Task ReadRawRectangleAsync(
        ProjectionFrameStore frames,
        int x,
        int y,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        if (width == 0 || height == 0)
        {
            return;
        }

        if (x < 0 || y < 0 || x + width > frames.Width || y + height > frames.Height)
        {
            throw ProtocolError(
                $"VNC 服务端下发的画面更新矩形超出画面范围：({x}, {y}) {width}×{height}，画面为 {frames.Width}×{frames.Height}。",
                "请确认实例的显示分辨率与 VNC 服务端一致。");
        }

        RfbPixelFormat format = RfbPixelFormat.ProjectionBgra32;
        int bytesPerPixel = format.BytesPerPixel;
        int rowBytes = checked(width * bytesPerPixel);
        EnsurePixelCapacity(rowBytes);

        byte[] rowSource = _pixelBuffer;

        for (int row = 0; row < height; row++)
        {
            await ReadExactlyIntoAsync(rowSource.AsMemory(0, rowBytes), cancellationToken).ConfigureAwait(false);
            DecodeRowToFrames(frames, rowSource, rowBytes, y + row, x, width, format);
        }
    }

    private static void DecodeRowToFrames(
        ProjectionFrameStore frames,
        byte[] rowSource,
        int rowBytes,
        int y,
        int x,
        int width,
        RfbPixelFormat format)
    {
        Span<byte> destination = frames.GetWriteRowSpan(y, x, width);
        format.DecodeRow(rowSource.AsSpan(0, rowBytes), destination, width);
    }

    private static void ApplyResolutionChange(
        ProjectionFrameStore frames,
        int width,
        int height,
        ref bool resolutionChanged)
    {
        ValidateFrameGeometry(width, height);
        if (width == frames.Width && height == frames.Height)
        {
            return;
        }

        frames.ResetGeometry(width, height);
        resolutionChanged = true;
    }

    /// <summary>
    /// 按服务端给出的尺寸重建帧缓冲并标记分辨率已变化。
    /// </summary>
    /// <param name="frames">承载画面的帧缓冲。</param>
    /// <param name="width">新的宽度。</param>
    /// <param name="height">新的高度。</param>
    private static void ApplyResolutionChange(ProjectionFrameStore frames, int width, int height)
    {
        bool changed = false;
        ApplyResolutionChange(frames, width, height, ref changed);
    }

    private async Task SkipColourMapEntriesAsync(CancellationToken cancellationToken)
    {
        byte[] header = await ReadExactlyAsync(5, cancellationToken).ConfigureAwait(false);
        int count = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3, 2));

        long payloadBytes = (long)count * 6;
        if (payloadBytes > 0)
        {
            await SkipAsync(payloadBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SkipServerCutTextAsync(CancellationToken cancellationToken)
    {
        byte[] header = await ReadExactlyAsync(7, cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(3, 4));
        if (length > 0)
        {
            await SkipAsync(length, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> ReadReasonAsync(CancellationToken cancellationToken)
    {
        byte[] lengthBytes = await ReadExactlyAsync(4, cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        if (length > MaxSecurityReasonLength)
        {
            return $"（失败原因长度 {length} 超出上限，未读取内容）";
        }

        byte[] payload = length == 0
            ? []
            : await ReadExactlyAsync((int)length, cancellationToken).ConfigureAwait(false);

        return Encoding.UTF8.GetString(payload);
    }

    private async Task SkipAsync(long bytes, CancellationToken cancellationToken)
    {
        long remaining = bytes;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(remaining, _readBuffer.Length);
            await ReadExactlyAsync(chunk, cancellationToken).ConfigureAwait(false);
            remaining -= chunk;
        }
    }

    private async Task<byte> ReadByteAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = await ReadExactlyAsync(1, cancellationToken).ConfigureAwait(false);
        return buffer[0];
    }

    private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken cancellationToken)
    {
        var result = new byte[count];
        await ReadExactlyIntoAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task ReadExactlyIntoAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read;
            try
            {
                read = await RequireStream()
                    .ReadAsync(destination[offset..], cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
            {
                throw Disconnected(ex);
            }

            if (read == 0)
            {
                throw new EndOfStreamException("VNC 服务端关闭了连接。");
            }

            offset += read;
        }
    }

    private async Task WriteAsync(byte[] payload, CancellationToken cancellationToken)
    {
        try
        {
            NetworkStream stream = RequireStream();
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            throw Disconnected(ex);
        }
    }

    private NetworkStream RequireStream() =>
        _stream ?? throw ProtocolError(
            "VNC 尚未连接。",
            "先建立连接再取帧。");

    private CancellationTokenSource CreateTimeoutToken(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_timeout);
        return linked;
    }

    private async Task TeardownAsync()
    {
        NetworkStream? stream = _stream;
        TcpClient? tcp = _tcp;
        _stream = null;
        _tcp = null;

        try
        {
            stream?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 已释放则无需处理。
        }

        try
        {
            tcp?.Dispose();
        }
        catch (SocketException)
        {
            // 关闭套接字失败不影响后续状态。
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void EnsurePixelCapacity(int required)
    {
        if (_pixelBuffer.Length >= required)
        {
            return;
        }

        _pixelBuffer = new byte[required];
    }

    private static XBearException ProtocolError(
        string message,
        string? remediation = null,
        Exception? inner = null) =>
        new(ErrorCategory.Protocol, message, remediation, inner);

    private static XBearException Disconnected(Exception inner) =>
        ProtocolError(
            "与实例的 VNC 连接已中断。",
            "请确认实例仍在运行，必要时停止后重新启动实例再建立投屏。",
            inner);

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw ProtocolError(
                $"VNC 端口 {port} 不是合法端口。",
                "请确认实例已分配到 VNC 端口后再建立投屏。");
        }
    }

    private static void ValidateFrameGeometry(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw ProtocolError(
                $"VNC 服务端公布的画面尺寸非法：{width}×{height}。",
                "请确认实例的显示分辨率配置正确。");
        }
    }

    private static string DescribeSecurityTypes(byte[] types)
    {
        if (types.Length == 0)
        {
            return "空的安全类型列表";
        }

        var builder = new StringBuilder();
        foreach (byte type in types)
        {
            if (builder.Length > 0)
            {
                builder.Append('、');
            }

            builder.Append(type);
        }

        return builder.ToString();
    }

    private static byte[] BuildFramebufferUpdateRequest(ScreenGeometry geometry, byte incremental)
    {
        var payload = new byte[10];
        payload[0] = Rfb.FramebufferUpdateRequestIncremental; // type = 3
        payload[1] = incremental;
        // x = 0, y = 0
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6, 2), (ushort)Math.Max(geometry.Width, 1));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(8, 2), (ushort)Math.Max(geometry.Height, 1));
        return payload;
    }

    private static byte[] BuildSetPixelFormat(RfbPixelFormat format)
    {
        var payload = new byte[20];
        payload[0] = 0; // message-type: SetPixelFormat
        // payload[1..3] = padding

        payload[4] = (byte)format.BitsPerPixel;
        payload[5] = (byte)format.Depth;
        payload[6] = format.BigEndian ? (byte)1 : (byte)0;
        payload[7] = format.TrueColour ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(8, 2), (ushort)format.RedMax);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(10, 2), (ushort)format.GreenMax);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(12, 2), (ushort)format.BlueMax);
        payload[14] = (byte)format.RedShift;
        payload[15] = (byte)format.GreenShift;
        payload[16] = (byte)format.BlueShift;
        // payload[17..19] = 3 bytes padding
        return payload;
    }

    private static byte[] BuildSetEncodings()
    {
        int[] encodings =
        [
            Rfb.EncodingRaw,
            Rfb.PseudoEncodingDesktopSize,
        ];

        var payload = new byte[4 + (encodings.Length * 4)];
        payload[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), (ushort)encodings.Length);

        int offset = 4;
        foreach (int encoding in encodings)
        {
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(offset, 4), encoding);
            offset += 4;
        }

        return payload;
    }
}

/// <summary>VNC 服务端在初始化阶段公布的信息。</summary>
/// <param name="Geometry">服务端公布的画面尺寸。</param>
/// <param name="DesktopName">服务端公布的桌面名。</param>
public readonly record struct VncServerInfo(ScreenGeometry Geometry, string DesktopName);
