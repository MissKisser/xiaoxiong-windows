using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Adb;

/// <summary>
/// adb 客户端：通过宿主 adb 服务的 Smart Socket 协议管理实例连接、执行命令与传输文件。
/// </summary>
/// <remarks>
/// 客户端通过 127.0.0.1 上的 adb server 转发与 Android guest adbd 交互，
/// 单条连接上串行执行操作；传输文件时按 64KB 分块流式读写，不将整个文件载入内存。
/// </remarks>
public sealed class AdbClient : IAdbFileTransferClient
{
    private const int MaxChunkSize = 64 * 1024;
    private const string ExitMarker = "__XB_EXIT__";
    private const int MaxShellOutputBytes = 8 * 1024 * 1024;
    private const uint DefaultFileMode = 0x1A4;

    /// <summary>宿主 adb server 的默认监听端口。</summary>
    public const int DefaultServerPort = 5037;

    /// <summary>
    /// 连接与握手的默认等待上限。
    /// </summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeSpan _connectTimeout;
    private readonly int _serverPort;

    private string? _targetSerial;
    private int _targetPort;
    private int _protocolVersion;
    private int _connected;
    private int _disposed;

    /// <summary>创建使用默认端口与等待上限的 adb 客户端。</summary>
    public AdbClient()
        : this(null, DefaultServerPort)
    {
    }

    /// <summary>创建指定等待上限的 adb 客户端。</summary>
    /// <param name="connectTimeout">连接与握手的等待上限，为 null 时使用默认值。</param>
    public AdbClient(TimeSpan? connectTimeout)
        : this(connectTimeout, DefaultServerPort)
    {
    }

    /// <summary>创建指定宿主 adb 服务端口的客户端。</summary>
    /// <param name="serverPort">宿主 adb 服务监听端口。</param>
    public AdbClient(int serverPort)
        : this(null, serverPort)
    {
    }

    /// <summary>创建指定等待上限与宿主 adb 服务端口的客户端。</summary>
    /// <param name="connectTimeout">连接与握手的等待上限，为 null 时使用默认值。</param>
    /// <param name="serverPort">宿主 adb 服务监听端口。</param>
    public AdbClient(TimeSpan? connectTimeout, int serverPort)
    {
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
        _serverPort = serverPort;
    }

    /// <summary>创建指定宿主 adb 服务端口与等待上限的客户端。</summary>
    /// <param name="serverPort">宿主 adb 服务监听端口。</param>
    /// <param name="connectTimeout">连接与握手的等待上限，为 null 时使用默认值。</param>
    public AdbClient(int serverPort, TimeSpan? connectTimeout)
        : this(connectTimeout, serverPort)
    {
    }

    /// <summary>连接宿主 adb 服务并确认实例就绪，返回协议版本。</summary>
    /// <param name="port">实例在宿主上映射的 adb 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="timeout">本次连接与握手的等待上限，为 null 时使用客户端默认上限。</param>
    /// <returns>协议版本号。</returns>
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
            if (Volatile.Read(ref _connected) != 0)
            {
                throw new XBearException(ErrorCategory.Protocol, "adb 客户端已处于连接状态。");
            }

            using var timeoutSource = new CancellationTokenSource();
            if (effectiveTimeout > TimeSpan.Zero)
            {
                timeoutSource.CancelAfter(effectiveTimeout);
            }

            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdown.Token,
                timeoutSource.Token);

            bool versionNegotiated = false;
            try
            {
                int version = await QueryServerVersionAsync(operation.Token).ConfigureAwait(false);
                versionNegotiated = true;

                string serial = await EnsureDeviceConnectedAsync(port, operation.Token).ConfigureAwait(false);

                _targetPort = port;
                _targetSerial = serial;
                _protocolVersion = version;
                Interlocked.Exchange(ref _connected, 1);
                return version;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException)
            {
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                ThrowIfConnectTimeout(ex, port, effectiveTimeout, timeoutSource, cancellationToken);
                throw new XBearException(
                    ErrorCategory.Protocol,
                    versionNegotiated ? $"与实例 adbd 端口 {port} 的握手失败。" : $"无法连接实例 adbd 端口 {port}。",
                    null,
                    ex);
            }
        }
        finally
        {
            _gate.Release();
        }
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
        EnsureConnected();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using CancellationTokenSource linked = CreateLinkedToken(cancellationToken);
            CancellationToken token = linked.Token;

            var (tcp, stream) = await OpenTransportConnectionAsync(token).ConfigureAwait(false);
            using (tcp)
            using (stream)
            {
                using var registration = token.CanBeCanceled ? token.Register(() => AbortSocket(tcp, stream)) : default;
                await WriteRequestAsync(stream, $"shell:{command}; echo {ExitMarker}$?", token).ConfigureAwait(false);
                await ReadStatusAsync(stream, token).ConfigureAwait(false);
                string output = await ReadToEndAsync(stream, MaxShellOutputBytes, token).ConfigureAwait(false);
                return ParseShellResult(output, command);
            }
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
    public Task PushAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
        PushAsync(localPath, remotePath, null, cancellationToken);

    /// <summary>以 sync 子协议把宿主文件推送到实例，并在复制过程中上报累计字节数。</summary>
    /// <param name="localPath">宿主文件路径。</param>
    /// <param name="remotePath">实例内目标路径。</param>
    /// <param name="progress">进度接收方，为 null 时不上报。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task PushAsync(
        string localPath,
        string remotePath,
        IProgress<long>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        EnsureConnected();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using CancellationTokenSource linked = CreateLinkedToken(cancellationToken);
            CancellationToken token = linked.Token;

            var (tcp, stream) = await OpenSyncConnectionAsync(token).ConfigureAwait(false);
            using (tcp)
            using (stream)
            {
                using var registration = token.CanBeCanceled ? token.Register(() => AbortSocket(tcp, stream)) : default;
                using FileStream file = OpenLocalFile(localPath);
                await SendFileAsync(stream, file, localPath, remotePath, progress, token).ConfigureAwait(false);
            }
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
    public Task PullAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
        PullAsync(remotePath, localPath, null, cancellationToken);

    /// <summary>以 sync 子协议把实例内文件拉取到宿主，并在复制过程中上报累计字节数。</summary>
    /// <param name="remotePath">实例内源路径。</param>
    /// <param name="localPath">宿主目标路径。</param>
    /// <param name="progress">进度接收方，为 null 时不上报。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task PullAsync(
        string remotePath,
        string localPath,
        IProgress<long>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        EnsureConnected();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using CancellationTokenSource linked = CreateLinkedToken(cancellationToken);
            CancellationToken token = linked.Token;

            var (tcp, stream) = await OpenSyncConnectionAsync(token).ConfigureAwait(false);
            using (tcp)
            using (stream)
            {
                using var registration = token.CanBeCanceled ? token.Register(() => AbortSocket(tcp, stream)) : default;
                using FileStream file = CreateLocalFile(localPath);
                await ReceiveFileAsync(stream, file, remotePath, progress, token).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>以 sync 子协议的 STAT 查询实例内某个路径的元信息。</summary>
    /// <param name="remotePath">实例内路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>路径存在时返回元信息，不存在时返回 null。</returns>
    public async Task<AdbRemoteFileInfo?> StatAsync(
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        EnsureConnected();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using CancellationTokenSource linked = CreateLinkedToken(cancellationToken);
            CancellationToken token = linked.Token;

            var (tcp, stream) = await OpenSyncConnectionAsync(token).ConfigureAwait(false);
            using (tcp)
            using (stream)
            {
                using var registration = token.CanBeCanceled ? token.Register(() => AbortSocket(tcp, stream)) : default;

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
            Interlocked.Exchange(ref _connected, 0);
            _shutdown.Cancel();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _shutdown.Dispose();
        }
    }

    /// <summary>构造 SEND 请求头。</summary>
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

    /// <summary>构造 DATA 分块头。</summary>
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

    /// <summary>构造 DONE 结束帧。</summary>
    /// <param name="modifiedTimeSeconds">文件的最后修改时间（秒）。</param>
    /// <returns>DONE 帧的完整字节序列。</returns>
    public static byte[] BuildDoneHeader(uint modifiedTimeSeconds)
    {
        var frame = new byte[8];
        Encoding.ASCII.GetBytes("DONE").CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), modifiedTimeSeconds);
        return frame;
    }

    private void EnsureConnected()
    {
        if (Volatile.Read(ref _connected) == 0)
        {
            throw new XBearException(ErrorCategory.Protocol, "尚未连接实例的 adbd。", "先调用 ConnectAsync。");
        }
    }

    private async Task<int> QueryServerVersionAsync(CancellationToken cancellationToken)
    {
        using var client = new TcpClient { NoDelay = true };
        using var registration = cancellationToken.CanBeCanceled ? cancellationToken.Register(client.Dispose) : default;

        try
        {
            await client.ConnectAsync(IPAddress.Loopback, _serverPort, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException) when (_serverPort == DefaultServerPort)
        {
            if (TryStartAdbServer())
            {
                await client.ConnectAsync(IPAddress.Loopback, _serverPort, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw;
            }
        }

        NetworkStream stream = client.GetStream();
        await WriteRequestAsync(stream, "host:version", cancellationToken).ConfigureAwait(false);
        await ReadStatusAsync(stream, cancellationToken).ConfigureAwait(false);

        byte[] payload = await ReadLengthPrefixedAsync(stream, cancellationToken).ConfigureAwait(false);
        string versionText = Encoding.ASCII.GetString(payload);
        if (!int.TryParse(versionText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int version)
            && !int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
        {
            throw new XBearException(ErrorCategory.Protocol, $"host:version 回包 {versionText} 不是合法版本号。");
        }

        return version;
    }

    private async Task<string> EnsureDeviceConnectedAsync(int port, CancellationToken cancellationToken)
    {
        string primarySerial = $"127.0.0.1:{port}";
        string fallbackSerial = port == 5555 ? "emulator-5554" : $"emulator-{port - 1}";

        while (!cancellationToken.IsCancellationRequested)
        {
            if (await TryCheckTransportAsync(primarySerial, cancellationToken).ConfigureAwait(false))
            {
                return primarySerial;
            }

            if (await TryCheckTransportAsync(fallbackSerial, cancellationToken).ConfigureAwait(false))
            {
                return fallbackSerial;
            }

            if (await SendConnectCommandAsync(primarySerial, cancellationToken).ConfigureAwait(false))
            {
                if (await TryCheckTransportAsync(primarySerial, cancellationToken).ConfigureAwait(false))
                {
                    return primarySerial;
                }
            }

            try
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return primarySerial;
    }

    private async Task<bool> TryCheckTransportAsync(string serial, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient { NoDelay = true };
            using var registration = cancellationToken.CanBeCanceled ? cancellationToken.Register(client.Dispose) : default;
            await client.ConnectAsync(IPAddress.Loopback, _serverPort, cancellationToken).ConfigureAwait(false);

            NetworkStream stream = client.GetStream();
            await WriteRequestAsync(stream, $"host:transport:{serial}", cancellationToken).ConfigureAwait(false);

            byte[] status = await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false);
            return Encoding.ASCII.GetString(status) == "OKAY";
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> SendConnectCommandAsync(string serial, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient { NoDelay = true };
            using var registration = cancellationToken.CanBeCanceled ? cancellationToken.Register(client.Dispose) : default;
            await client.ConnectAsync(IPAddress.Loopback, _serverPort, cancellationToken).ConfigureAwait(false);

            NetworkStream stream = client.GetStream();
            await WriteRequestAsync(stream, $"host:connect:{serial}", cancellationToken).ConfigureAwait(false);

            byte[] status = await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false);
            if (Encoding.ASCII.GetString(status) != "OKAY")
            {
                return false;
            }

            byte[] payload = await ReadLengthPrefixedAsync(stream, cancellationToken).ConfigureAwait(false);
            string response = Encoding.UTF8.GetString(payload);
            return !response.StartsWith("cannot connect", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryStartAdbServer()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "adb",
                Arguments = "start-server",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (proc is null)
            {
                return false;
            }

            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<(TcpClient Client, NetworkStream Stream)> OpenTransportConnectionAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, _serverPort, cancellationToken).ConfigureAwait(false);
            NetworkStream stream = client.GetStream();

            string transportCommand = string.IsNullOrEmpty(_targetSerial)
                ? "host:transport-any"
                : $"host:transport:{_targetSerial}";

            await WriteRequestAsync(stream, transportCommand, cancellationToken).ConfigureAwait(false);
            await ReadStatusAsync(stream, cancellationToken).ConfigureAwait(false);
            return (client, stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<(TcpClient Client, NetworkStream Stream)> OpenSyncConnectionAsync(CancellationToken cancellationToken)
    {
        var (client, stream) = await OpenTransportConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteRequestAsync(stream, "sync:", cancellationToken).ConfigureAwait(false);
            await ReadStatusAsync(stream, cancellationToken).ConfigureAwait(false);
            return (client, stream);
        }
        catch
        {
            stream.Dispose();
            client.Dispose();
            throw;
        }
    }

    private static byte[] EncodeRequest(string command)
    {
        byte[] body = Encoding.UTF8.GetBytes(command);
        int length = body.Length;
        if (length > 0xFFFF)
        {
            throw new XBearException(ErrorCategory.Protocol, $"adb 请求长度 {length} 超过十六进制上限。");
        }

        byte[] header = Encoding.ASCII.GetBytes(length.ToString("x4", CultureInfo.InvariantCulture));
        var frame = new byte[4 + length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame, 4);
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

    private async Task SendFileAsync(
        Stream stream,
        FileStream file,
        string localPath,
        string remotePath,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        uint modified = ToUnixSeconds(File.GetLastWriteTimeUtc(localPath));

        await stream.WriteAsync(BuildSendHeader(remotePath, DefaultFileMode), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

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

            transferred += read;
            progress?.Report(transferred);
        }

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

        long transferred = 0;
        while (true)
        {
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

    private CancellationTokenSource CreateLinkedToken(CancellationToken cancellationToken)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
    }

    private static void AbortSocket(TcpClient client, NetworkStream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch
        {
        }

        try
        {
            client.Dispose();
        }
        catch
        {
        }
    }
}
