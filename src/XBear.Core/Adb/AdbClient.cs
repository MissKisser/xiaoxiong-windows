using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Adb;

/// <summary>adb 轻客户端：直连 adbd 的 TCP 端口，不依赖 adb 可执行文件。</summary>
/// <remarks>
/// 单条连接上串行执行操作；传输文件时按 64KB 分块流式读写，不会把整个文件读进内存。
/// </remarks>
public sealed class AdbClient : IAdbFileTransferClient
{
    private const int MaxChunkSize = 64 * 1024;
    private const string ExitMarker = "__XB_EXIT__";
    private const int MaxShellOutputBytes = 8 * 1024 * 1024;
    private const uint DefaultFileMode = 0x1A4;

    /// <summary>
    /// 连接与握手的默认等待上限。
    /// hostfwd 在宿主侧立即接受连接，adbd 未就绪时不会主动断开，
    /// 不设上限会让调用方永久阻塞，因此连接与握手必须在有限时间内给出结论。
    /// </summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeSpan _connectTimeout;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _disposed;

    /// <summary>创建 adb 客户端。</summary>
    public AdbClient()
        : this(null)
    {
    }

    /// <summary>创建 adb 客户端并指定连接与握手的默认等待上限。</summary>
    /// <param name="connectTimeout">
    /// 连接与握手的默认等待上限，为 null 时使用 <see cref="DefaultConnectTimeout"/>。
    /// </param>
    public AdbClient(TimeSpan? connectTimeout)
    {
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
    }

    /// <summary>连接实例的 adbd 端口，执行 host:version 握手并返回协议版本。</summary>
    /// <param name="port">实例在宿主上映射的 adb 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="timeout">
    /// 本次连接与握手的等待上限，为 null 时使用实例级默认上限。
    /// </param>
    /// <returns>adbd 通告的协议版本号。</returns>
    /// <exception cref="XBearException">
    /// 连接失败或握手不符合协议时抛出 <see cref="ErrorCategory.Protocol"/>；
    /// 超过等待上限时抛出 <see cref="ErrorCategory.Timeout"/>。
    /// </exception>
    public async Task<int> ConnectAsync(
        int port,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (port is < 1 or > 65535)
        {
            throw new XBearException(ErrorCategory.Protocol, $"adb 端口 {port} 不是合法端口。");
        }

        TimeSpan effectiveTimeout = timeout ?? _connectTimeout;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                throw new XBearException(ErrorCategory.Protocol, "adb 客户端已处于连接状态。");
            }

            // 超时与调用方取消必须可区分：前者按可识别异常上报，后者保持取消语义。
            using var timeoutSource = new CancellationTokenSource();
            if (effectiveTimeout > TimeSpan.Zero)
            {
                timeoutSource.CancelAfter(effectiveTimeout);
            }

            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdown.Token,
                timeoutSource.Token);

            // 取消或超时时关闭套接字，保证阻塞中的读取一定被唤醒，不存在无法取消的死等。
            using var registration = operation.Token.Register(AbortSocket);

            bool connected = false;
            try
            {
                var client = new TcpClient { NoDelay = true };
                NetworkStream stream;
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, port, operation.Token).ConfigureAwait(false);
                    stream = client.GetStream();
                }
                catch
                {
                    client.Dispose();
                    throw;
                }

                _client = client;
                _stream = stream;
                connected = true;

                try
                {
                    int version = await ReadProtocolVersionAsync(stream, operation.Token).ConfigureAwait(false);
                    await SelectTransportAsync(stream, operation.Token).ConfigureAwait(false);
                    return version;
                }
                catch
                {
                    Teardown();
                    throw;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException)
            {
                Teardown();

                // 调用方主动取消保持取消语义，其余按可识别的超时或协议错误上报。
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                ThrowIfConnectTimeout(ex, port, effectiveTimeout, timeoutSource, cancellationToken);
                throw new XBearException(
                    ErrorCategory.Protocol,
                    connected ? $"与实例 adbd 端口 {port} 的握手失败。" : $"无法连接实例 adbd 端口 {port}。",
                    null,
                    ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 把「等待上限到期」转换成可识别的超时异常；其余异常按原样上抛。
    /// 调用方主动取消或客户端释放导致的取消不属于超时，必须保持取消语义。
    /// </summary>
    /// <param name="exception">实际发生的异常。</param>
    /// <param name="port">目标端口。</param>
    /// <param name="effectiveTimeout">本次生效的等待上限。</param>
    /// <param name="timeoutSource">等待上限令牌源。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <exception cref="XBearException">确认是等待上限到期时抛出 <see cref="ErrorCategory.Timeout"/>。</exception>
    private static void ThrowIfConnectTimeout(
        Exception exception,
        int port,
        TimeSpan effectiveTimeout,
        CancellationTokenSource timeoutSource,
        CancellationToken cancellationToken)
    {
        if (!timeoutSource.IsCancellationRequested ||
            cancellationToken.IsCancellationRequested ||
            exception is not (OperationCanceledException or SocketException or IOException))
        {
            return;
        }

        throw new XBearException(
            ErrorCategory.Timeout,
            $"连接实例 adbd 端口 {port} 超时：{effectiveTimeout.TotalSeconds:F1} 秒内未完成握手。",
            "实例可能尚未启动到 adbd 就绪；请等待启动完成后再连接，或调大该次连接的等待上限。",
            exception);
    }

    /// <summary>执行一条 shell 命令并收集标准输出。</summary>
    /// <param name="command">命令与参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标准输出原文。</returns>
    /// <exception cref="XBearException">命令以非零退出码结束时抛出，消息中包含错误输出，分类为 <see cref="ErrorCategory.Protocol"/>。</exception>
    public async Task<string> ShellAsync(string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NetworkStream stream = RequireStream();
            using CancellationTokenSource linked = CreateLinkedToken(cancellationToken);
            CancellationToken token = linked.Token;

            await WriteRequestAsync(stream, $"shell:{command}; echo {ExitMarker}$?", token).ConfigureAwait(false);
            await ReadStatusAsync(stream, token).ConfigureAwait(false);
            string output = await ReadToEndAsync(stream, MaxShellOutputBytes, token).ConfigureAwait(false);
            return ParseShellResult(output, command);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>通过 id -u 判断实例是否以 root 身份运行。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>uid 为 0 时返回 true。</returns>
    public async Task<bool> IsRootAsync(CancellationToken cancellationToken = default)
    {
        string output = await ShellAsync("id -u", cancellationToken).ConfigureAwait(false);
        return int.TryParse(output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int uid) && uid == 0;
    }

    /// <summary>以 sync 子协议把宿主文件推送到实例。</summary>
    /// <param name="localPath">宿主文件路径。</param>
    /// <param name="remotePath">实例内目标路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="XBearException">本地文件不可读时分类为 <see cref="ErrorCategory.Storage"/>；传输被 adbd 拒绝时分类为 <see cref="ErrorCategory.Protocol"/>。</exception>
    public Task PushAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
        PushAsync(localPath, remotePath, null, cancellationToken);

    /// <summary>以 sync 子协议把宿主文件推送到实例，并在复制过程中上报累计字节数。</summary>
    /// <param name="localPath">宿主文件路径。</param>
    /// <param name="remotePath">实例内目标路径。</param>
    /// <param name="progress">进度接收方，为 null 时不上报。回调给出的是累计已复制字节数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="XBearException">本地文件不可读时分类为 <see cref="ErrorCategory.Storage"/>；传输被 adbd 拒绝时分类为 <see cref="ErrorCategory.Protocol"/>。</exception>
    public async Task PushAsync(
        string localPath,
        string remotePath,
        IProgress<long>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NetworkStream stream = RequireStream();
            using CancellationTokenSource linked = CreateLinkedToken(cancellationToken);
            CancellationToken token = linked.Token;

            await OpenSyncAsync(stream, token).ConfigureAwait(false);
            using FileStream file = OpenLocalFile(localPath);
            await SendFileAsync(stream, file, localPath, remotePath, progress, token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>以 sync 子协议把实例内文件拉取到宿主。</summary>
    /// <param name="remotePath">实例内源路径。</param>
    /// <param name="localPath">宿主目标路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="XBearException">远端文件不存在时分类为 <see cref="ErrorCategory.Protocol"/>；本地写入失败时分类为 <see cref="ErrorCategory.Storage"/>。</exception>
    public Task PullAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
        PullAsync(remotePath, localPath, null, cancellationToken);

    /// <summary>以 sync 子协议把实例内文件拉取到宿主，并在复制过程中上报累计字节数。</summary>
    /// <param name="remotePath">实例内源路径。</param>
    /// <param name="localPath">宿主目标路径。</param>
    /// <param name="progress">进度接收方，为 null 时不上报。回调给出的是累计已复制字节数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="XBearException">远端文件不存在时分类为 <see cref="ErrorCategory.Protocol"/>；本地写入失败时分类为 <see cref="ErrorCategory.Storage"/>。</exception>
    public async Task PullAsync(
        string remotePath,
        string localPath,
        IProgress<long>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NetworkStream stream = RequireStream();
            using CancellationTokenSource linked = CreateLinkedToken(cancellationToken);
            CancellationToken token = linked.Token;

            await OpenSyncAsync(stream, token).ConfigureAwait(false);
            using FileStream file = CreateLocalFile(localPath);
            await ReceiveFileAsync(stream, file, remotePath, progress, token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>以 sync 子协议的 STAT 查询实例内某个路径的元信息。</summary>
    /// <param name="remotePath">实例内路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 路径存在时返回其模式位、字节数与最后修改时间；
    /// adbd 回答不存在时返回 null，让调用方按「取不到」而非失败处理。
    /// </returns>
    /// <exception cref="XBearException">回包类型未知或连接中断时分类为 <see cref="ErrorCategory.Protocol"/>。</exception>
    public async Task<AdbRemoteFileInfo?> StatAsync(
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NetworkStream stream = RequireStream();
            using CancellationTokenSource linked = CreateLinkedToken(cancellationToken);
            CancellationToken token = linked.Token;

            await OpenSyncAsync(stream, token).ConfigureAwait(false);
            byte[] payload = Encoding.UTF8.GetBytes(remotePath);
            await WriteSyncHeaderAsync(stream, "STAT", payload, token).ConfigureAwait(false);

            byte[] idBytes = await ReadExactAsync(stream, 4, token).ConfigureAwait(false);
            string kind = Encoding.ASCII.GetString(idBytes);

            if (kind == "FAIL")
            {
                byte[] lenBytes = await ReadExactAsync(stream, 4, token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32LittleEndian(lenBytes);
                string message = await ReadSyncMessageAsync(stream, length, token).ConfigureAwait(false);
                throw new XBearException(ErrorCategory.Protocol, $"adbd 拒绝 STAT：{message}");
            }

            if (kind != "STAT")
            {
                throw new XBearException(ErrorCategory.Protocol, $"adbd 返回了未知状态 {kind}。");
            }

            // STAT 回包固定为 12 字节：模式位(4) + 字节数(4) + 最后修改时间(4)。
            byte[] body = await ReadExactAsync(stream, 12, token).ConfigureAwait(false);
            uint mode = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4, 4));
            uint mtime = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8, 4));

            if (mode == 0 && size == 0 && mtime == 0)
            {
                return null;
            }

            return new AdbRemoteFileInfo(mode, size, mtime);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>释放套接字与内部资源。</summary>
    /// <returns>表示释放完成的异步结果。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Teardown();
            _shutdown.Cancel();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _shutdown.Dispose();
        }
    }

    private static byte[] EncodeRequest(string command)
    {
        byte[] body = Encoding.UTF8.GetBytes(command);

        // 请求帧为「4 字节小端长度 + 十六进制 ASCII 长度 + 命令原文」，
        // 长度字段同时描述整个负载，十六进制头与 4 字节长度必须一致。
        int payloadLength = 4 + body.Length;
        byte[] header = Encoding.ASCII.GetBytes(payloadLength.ToString("x4", CultureInfo.InvariantCulture));
        var frame = new byte[4 + payloadLength];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payloadLength);
        header.CopyTo(frame, 4);
        body.CopyTo(frame, 4 + header.Length);
        return frame;
    }

    private static async Task WriteRequestAsync(Stream stream, string command, CancellationToken cancellationToken)
    {
        byte[] frame = EncodeRequest(command);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadStatusAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] status = await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false);
        string text = Encoding.ASCII.GetString(status);
        if (text == "OKAY")
        {
            return text;
        }

        if (text == "FAIL")
        {
            byte[] lengthBytes = await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false);
            int length = ParseHexLength(lengthBytes);
            string reason = length > 0
                ? Encoding.UTF8.GetString(await ReadExactAsync(stream, length, cancellationToken).ConfigureAwait(false))
                : string.Empty;
            throw new XBearException(ErrorCategory.Protocol, $"adbd 返回失败：{reason}");
        }

        throw new XBearException(ErrorCategory.Protocol, $"adbd 返回了未知状态 {text}。");
    }

    private static async Task<byte[]> ReadLengthPrefixedAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] lengthBytes = await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false);
        int length = ParseHexLength(lengthBytes);
        if (length < 0)
        {
            throw new XBearException(ErrorCategory.Protocol, "adbd 返回了非法长度。");
        }

        return length > 0
            ? await ReadExactAsync(stream, length, cancellationToken).ConfigureAwait(false)
            : Array.Empty<byte>();
    }

    private static int ParseHexLength(byte[] lengthBytes)
    {
        string text = Encoding.ASCII.GetString(lengthBytes);
        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int length))
        {
            throw new XBearException(ErrorCategory.Protocol, $"adbd 返回的长度 {text} 不是十六进制。");
        }

        return length;
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read;
            try
            {
                read = await stream
                    .ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("读取 adbd 响应已被取消。", ex, cancellationToken);
                }

                throw new XBearException(ErrorCategory.Protocol, "读取 adbd 响应时连接中断。", null, ex);
            }

            if (read == 0)
            {
                throw new XBearException(ErrorCategory.Protocol, "adbd 提前关闭了连接。");
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task WriteSyncHeaderAsync(
        Stream stream,
        string id,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        // sync 子协议报文固定为 8 字节头：4 字节 ASCII 命令字在前，4 字节小端长度在后，
        // 随后紧跟 length 个字节的负载。所有二进制整数均为小端。
        if (payload.Length > MaxChunkSize)
        {
            throw new XBearException(ErrorCategory.Protocol, "sync 负载超过单帧上限。");
        }

        var frame = new byte[8 + payload.Length];
        Encoding.ASCII.GetBytes(id).CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(frame, 8);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 构造 SEND 请求头：命令字 "SEND" + 小端长度 + 「路径,十进制权限位」拼接串。
    /// 权限位与路径共用同一个长度字段，没有独立的 mode 长度。
    /// 长度字段是二进制小端整数而非 ASCII 十进制，这是 sync 模式「所有二进制整数均为小端」
    /// 的直接要求，也与协议文档中 u32 size = len(filename) 的写法一致。
    /// </summary>
    /// <param name="remotePath">实例内目标路径。</param>
    /// <param name="mode">十进制权限位。</param>
    /// <returns>SEND 请求的完整字节序列。</returns>
    public static byte[] BuildSendHeader(string remotePath, uint mode)
    {
        string combined = string.Concat(remotePath, ",", mode.ToString(CultureInfo.InvariantCulture));
        byte[] payload = Encoding.UTF8.GetBytes(combined);
        var frame = new byte[8 + payload.Length];
        Encoding.ASCII.GetBytes("SEND").CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    /// <summary>
    /// 构造 DATA 分块头：命令字 "DATA" + 小端块长度，分块长度不得大于 64KB。
    /// </summary>
    /// <param name="chunkSize">本块数据的字节数。</param>
    /// <returns>DATA 分块头的完整字节序列。</returns>
    public static byte[] BuildDataHeader(int chunkSize)
    {
        if (chunkSize < 0 || chunkSize > MaxChunkSize)
        {
            throw new XBearException(ErrorCategory.Protocol, $"sync 分块长度 {chunkSize} 越界。");
        }

        var frame = new byte[8];
        Encoding.ASCII.GetBytes("DATA").CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), (uint)chunkSize);
        return frame;
    }

    /// <summary>构造 DONE 结束帧：命令字 "DONE" + 小端最后修改时间。</summary>
    /// <param name="modifiedTimeSeconds">文件的最后修改时间（秒）。</param>
    /// <returns>DONE 帧的完整字节序列。</returns>
    public static byte[] BuildDoneHeader(uint modifiedTimeSeconds)
    {
        var frame = new byte[8];
        Encoding.ASCII.GetBytes("DONE").CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), modifiedTimeSeconds);
        return frame;
    }

    private static async Task<string> ReadToEndAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await ReadChunkAsync(stream, chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                throw new XBearException(ErrorCategory.Protocol, "shell 输出超过允许的上限。");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<int> ReadChunkAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("读取 adbd 数据已被取消。", ex, cancellationToken);
            }

            throw new XBearException(ErrorCategory.Protocol, "读取 adbd 数据时连接中断。", null, ex);
        }
    }

    private static string ParseShellResult(string output, string command)
    {
        int marker = output.LastIndexOf(ExitMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            throw new XBearException(
                ErrorCategory.Protocol,
                $"shell 命令 {command} 未返回退出码，输出：{output.Trim()}");
        }

        int digits = marker + ExitMarker.Length;
        int end = digits;
        while (end < output.Length && char.IsAsciiDigit(output[end]))
        {
            end++;
        }

        if (end == digits)
        {
            throw new XBearException(ErrorCategory.Protocol, $"shell 命令 {command} 的退出码格式非法。");
        }

        string stdout = output[..marker];
        int exitCode = int.Parse(output.AsSpan(digits, end - digits), CultureInfo.InvariantCulture);
        if (exitCode != 0)
        {
            throw new XBearException(
                ErrorCategory.Protocol,
                $"shell 命令 {command} 以退出码 {exitCode} 失败，错误输出：{stdout.Trim()}");
        }

        return stdout;
    }

    private static FileStream OpenLocalFile(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new XBearException(ErrorCategory.Storage, $"无法读取本地文件 {path}。", null, ex);
        }
    }

    private static FileStream CreateLocalFile(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new XBearException(ErrorCategory.Storage, $"无法写入本地文件 {path}。", null, ex);
        }
    }

    private static async Task<int> ReadProtocolVersionAsync(Stream stream, CancellationToken cancellationToken)
    {
        await WriteRequestAsync(stream, "host:version", cancellationToken).ConfigureAwait(false);
        await ReadStatusAsync(stream, cancellationToken).ConfigureAwait(false);

        byte[] payload = await ReadLengthPrefixedAsync(stream, cancellationToken).ConfigureAwait(false);
        if (payload.Length < 8)
        {
            throw new XBearException(ErrorCategory.Protocol, "host:version 回包长度不足。");
        }

        int declared = ParseHexLength(payload.AsSpan(0, 4).ToArray());
        if (declared > payload.Length)
        {
            throw new XBearException(ErrorCategory.Protocol, "host:version 回包长度不自洽。");
        }

        // 回包形如「4 字节十六进制长度 + 4 字节十六进制长度 + 4 字节版本号」，
        // 部分实现会在其后追加填充，故版本号固定取内层长度之后的 4 字节。
        int offset = payload.Length >= 12 ? 8 : 4;
        ParseHexLength(payload.AsSpan(4, 4).ToArray());
        return BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4));
    }

    private static async Task SelectTransportAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            await WriteRequestAsync(stream, "host:transport-any", cancellationToken).ConfigureAwait(false);
            await ReadStatusAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (XBearException)
        {
            // adbd 不支持该选择时退回默认传输，后续命令仍可用。
        }
    }

    private async Task OpenSyncAsync(Stream stream, CancellationToken cancellationToken)
    {
        await WriteRequestAsync(stream, "sync:", cancellationToken).ConfigureAwait(false);
        await ReadStatusAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendFileAsync(
        Stream stream,
        FileStream file,
        string localPath,
        string remotePath,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        // 打开时就固定最后修改时间，避免读到与本次传输不一致的值。
        uint modified = ToUnixSeconds(File.GetLastWriteTimeUtc(localPath));

        // SEND 请求：命令字 + 小端长度 + 「路径,十进制权限位」。
        await stream.WriteAsync(BuildSendHeader(remotePath, DefaultFileMode), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // adbd 受理 SEND 后回一个 OKAY，此时还没有文件内容。
        await ReadSyncStatusAsync(stream, cancellationToken).ConfigureAwait(false);

        // 按 64KB 分块流式发送。DATA 块服务端不回任何应答，因此这里不读响应。
        // 读到 0 字节表示传输结束，任一块都不会把整个文件读进内存。
        byte[] buffer = new byte[MaxChunkSize];
        long transferred = 0;
        while (true)
        {
            int read = await file.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await stream.WriteAsync(BuildDataHeader(read), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            // 进度按累计字节数给出，调用方据此呈现「已复制多少」而不必自行换算分块。
            transferred += read;
            progress?.Report(transferred);
        }

        // DONE 携带最后修改时间，服务端在此之后才回 OKAY，其长度可忽略。
        await stream.WriteAsync(BuildDoneHeader(modified), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await ReadSyncStatusAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static uint ToUnixSeconds(DateTime utc)
    {
        long seconds = new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();
        return seconds < 0 || seconds > uint.MaxValue ? 0u : (uint)seconds;
    }

    private async Task ReceiveFileAsync(
        Stream stream,
        FileStream file,
        string remotePath,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes(remotePath);
        await WriteSyncHeaderAsync(stream, "RECV", payload, cancellationToken).ConfigureAwait(false);

        // RECV 的首个回包固定是 OKAY 头（长度为文件总大小）或 FAIL 头。
        await ReadSyncStatusAsync(stream, cancellationToken).ConfigureAwait(false);

        long transferred = 0;
        while (true)
        {
            // 服务端回包同样是 8 字节头：4 字节命令字 + 4 字节小端长度。
            byte[] header = await ReadExactAsync(stream, 8, cancellationToken).ConfigureAwait(false);
            string kind = Encoding.ASCII.GetString(header, 0, 4);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));

            if (kind == "DATA")
            {
                if (length < 0 || length > MaxChunkSize)
                {
                    throw new XBearException(ErrorCategory.Protocol, $"adbd 返回的分块长度 {length} 越界。");
                }

                await CopyExactlyAsync(stream, file, length, cancellationToken).ConfigureAwait(false);
                transferred += length;
                progress?.Report(transferred);
                continue;
            }

            if (kind == "DONE")
            {
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (kind == "FAIL")
            {
                string reason = await ReadSyncMessageAsync(stream, length, cancellationToken).ConfigureAwait(false);
                throw new XBearException(ErrorCategory.Protocol, $"adbd 拒绝拉取文件：{reason}");
            }

            throw new XBearException(ErrorCategory.Protocol, $"sync 回包出现未知类型 {kind}。");
        }
    }

    /// <summary>
    /// 读取 sync 层的状态回包：OKAY 表示成功，FAIL 携带失败原因。
    /// </summary>
    /// <param name="stream">连接流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="XBearException">回包为 FAIL 或类型未知时抛出，分类为 <see cref="ErrorCategory.Protocol"/>。</exception>
    private static async Task ReadSyncStatusAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = await ReadExactAsync(stream, 8, cancellationToken).ConfigureAwait(false);
        string kind = Encoding.ASCII.GetString(header, 0, 4);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));

        if (kind == "OKAY")
        {
            return;
        }

        if (kind == "FAIL")
        {
            string reason = await ReadSyncMessageAsync(stream, length, cancellationToken).ConfigureAwait(false);
            throw new XBearException(ErrorCategory.Protocol, $"adbd 拒绝该文件操作：{reason}");
        }

        throw new XBearException(ErrorCategory.Protocol, $"adbd 返回了未知状态 {kind}。");
    }

    private static async Task<string> ReadSyncMessageAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        if (length < 0 || length > MaxChunkSize)
        {
            throw new XBearException(ErrorCategory.Protocol, $"adbd 返回的失败信息长度 {length} 越界。");
        }

        return length > 0
            ? Encoding.UTF8.GetString(await ReadExactAsync(stream, length, cancellationToken).ConfigureAwait(false))
            : string.Empty;
    }

    private static async Task CopyExactlyAsync(
        Stream source,
        Stream destination,
        int count,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[Math.Min(count, 64 * 1024)];
        int remaining = count;
        while (remaining > 0)
        {
            int read;
            try
            {
                read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("读取 adbd 数据已被取消。", ex, cancellationToken);
                }

                throw new XBearException(ErrorCategory.Protocol, "读取 adbd 数据时连接中断。", null, ex);
            }

            if (read == 0)
            {
                throw new XBearException(ErrorCategory.Protocol, "adbd 在分块传输中途关闭了连接。");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private CancellationTokenSource CreateLinkedToken(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        if (cancellationToken.CanBeCanceled)
        {
            // 取消时关闭套接字，保证阻塞中的读取一定被唤醒，不存在无法取消的死等。
            linked.Token.Register(AbortSocket);
        }

        return linked;
    }

    private void AbortSocket()
    {
        try
        {
            _stream?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 已释放时无需处理。
        }

        try
        {
            _client?.Dispose();
        }
        catch (SocketException)
        {
            // 关闭失败不影响取消流程。
        }
    }

    private NetworkStream RequireStream()
    {
        return _stream
            ?? throw new XBearException(ErrorCategory.Protocol, "尚未连接实例的 adbd。", "先调用 ConnectAsync。");
    }

    private void Teardown()
    {
        NetworkStream? stream = _stream;
        TcpClient? client = _client;
        _stream = null;
        _client = null;

        try
        {
            stream?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 已释放时无需处理。
        }

        try
        {
            client?.Dispose();
        }
        catch (SocketException)
        {
            // 关闭套接字失败不影响后续状态。
        }
    }
}