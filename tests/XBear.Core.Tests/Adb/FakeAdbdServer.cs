using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace XBear.Core.Tests.Adb;

/// <summary>假 adbd 的一条 shell 应答。</summary>
/// <param name="Output">输出内容，含标准输出与标准错误。</param>
/// <param name="ExitCode">退出码。</param>
internal sealed record ShellResponse(string Output, int ExitCode);

/// <summary>
/// 内存内的假 adbd：按真实 adbd 的字节流规则处理 host 服务、shell 与 sync 子协议，
/// 用于在不依赖真实 Android 实例的前提下验证客户端。
/// </summary>
internal sealed class FakeAdbdServer : IAsyncDisposable
{
    private const string ExitMarker = "__XB_EXIT__";
    private const int ChunkSize = 64 * 1024;
    private const int MaxSyncPayload = 4 * 1024 * 1024;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<byte[]> _rawRequests = [];
    private readonly List<string> _requests = [];
    private readonly List<byte[]> _rawSyncFrames = [];
    private readonly List<string> _syncCommands = [];
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _mtimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _modes = new(StringComparer.Ordinal);
    private readonly List<TcpClient> _connections = [];
    private readonly Task _acceptLoop;

    public FakeAdbdServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>监听端口。</summary>
    public int Port { get; }

    /// <summary>对端通告的协议版本。</summary>
    public int ProtocolVersion { get; set; } = 0x29;

    /// <summary>是否在 host:version 回包中附带填充字节。</summary>
    public bool PadVersionResponse { get; set; } = true;

    /// <summary>shell 命令处理器，返回 null 表示命令未知。</summary>
    public Func<string, ShellResponse?>? ShellHandler { get; set; }

    /// <summary>收到 shell 命令后是否故意不回包，用于验证取消。</summary>
    public bool StallShell { get; set; }

    /// <summary>
    /// 是否接受连接后故意不回应握手。
    /// 宿主转发会在宿主侧立即接受连接，adbd 未就绪时正是这种表现，
    /// 用于验证客户端不会在握手上永久阻塞。
    /// </summary>
    public bool StallHandshake { get; set; }

    /// <summary>客户端发出的请求负载原文。</summary>
    public IReadOnlyList<string> Requests => _requests;

    /// <summary>客户端发出的请求原始帧，含 4 字节小端长度前缀。</summary>
    public IReadOnlyList<byte[]> RawRequests => _rawRequests;

    /// <summary>客户端在 sync 模式中发出的原始帧，含 8 字节头与负载。</summary>
    public IReadOnlyList<byte[]> RawSyncFrames => _rawSyncFrames;

    /// <summary>客户端在 sync 模式中发出的命令字，顺序与线上字节一致。</summary>
    public IReadOnlyList<string> SyncCommands => _syncCommands;

    /// <summary>按命令字筛出 sync 模式中发出的原始帧。</summary>
    /// <param name="command">命令字，例如 SEND、DATA、DONE。</param>
    /// <returns>匹配该命令字的全部帧，按发送顺序排列。</returns>
    public IReadOnlyList<byte[]> SyncFramesOf(string command)
    {
        var result = new List<byte[]>();
        lock (_rawSyncFrames)
        {
            for (int i = 0; i < _syncCommands.Count && i < _rawSyncFrames.Count; i++)
            {
                if (string.Equals(_syncCommands[i], command, StringComparison.Ordinal))
                {
                    result.Add(_rawSyncFrames[i]);
                }
            }
        }

        return result;
    }

    /// <summary>读取实例内某个文件被 adbd 记录的最后修改时间。</summary>
    /// <param name="path">实例内路径。</param>
    /// <returns>DONE 帧中携带的小端时间值，未知路径返回 null。</returns>
    public uint? GetMtime(string path)
    {
        lock (_files)
        {
            return _mtimes.TryGetValue(path, out uint value) ? value : null;
        }
    }

    /// <summary>读取实例内某个文件被 adbd 解析出的权限位。</summary>
    /// <param name="path">实例内路径。</param>
    /// <returns>SEND 负载中十进制权限位对应的数值，未知路径返回 null。</returns>
    public uint? GetMode(string path)
    {
        lock (_files)
        {
            return _modes.TryGetValue(path, out uint value) ? value : null;
        }
    }

    /// <summary>实例内的文件内容。</summary>
    public IReadOnlyDictionary<string, byte[]> Files => _files;

    /// <summary>读取实例内某个文件的当前内容。</summary>
    /// <param name="path">实例内路径。</param>
    /// <returns>文件内容，不存在时返回 null。</returns>
    public byte[]? GetFile(string path)
    {
        lock (_files)
        {
            return _files.TryGetValue(path, out byte[]? content) ? content : null;
        }
    }

    /// <summary>预置实例内的文件内容。</summary>
    /// <param name="path">实例内路径。</param>
    /// <param name="content">文件内容。</param>
    public void SetFile(string path, byte[] content)
    {
        lock (_files)
        {
            _files[path] = content;
        }
    }

    /// <summary>关闭监听与全部已建立的连接，释放后台任务。</summary>
    /// <returns>表示释放完成的异步结果。</returns>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();

        // 先关闭所有已接受的连接，解除服务端线程在读阻塞上的等待。
        TcpClient[] clients;
        lock (_connections)
        {
            clients = _connections.ToArray();
            _connections.Clear();
        }

        foreach (TcpClient client in clients)
        {
            client.Dispose();
        }

        try
        {
            await _acceptLoop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // 监听结束属正常路径。
        }
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

            TcpClient accepted = client;
            Task worker = Task.Run(() => ServeAsync(accepted));
            lock (_connections)
            {
                _connections.Add(accepted);
            }

            _ = worker.ContinueWith(
                _ =>
                {
                    lock (_connections)
                    {
                        _connections.Remove(accepted);
                    }
                },
                TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            NetworkStream stream = client.GetStream();
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    byte[]? frame = await ReadFrameAsync(stream).ConfigureAwait(false);
                    if (frame is null)
                    {
                        return;
                    }

                    string payload = Encoding.UTF8.GetString(frame, 8, frame.Length - 8);
                    lock (_rawRequests)
                    {
                        _rawRequests.Add(frame);
                        _requests.Add(payload);
                    }

                    bool keepOpen = await DispatchAsync(stream, payload).ConfigureAwait(false);
                    if (!keepOpen)
                    {
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
            {
                // 连接结束属正常路径。
            }
        }
    }

    private async Task<bool> DispatchAsync(NetworkStream stream, string payload)
    {
        if (payload == "host:version")
        {
            if (StallHandshake)
            {
                await Task.Delay(Timeout.Infinite, _cts.Token).ConfigureAwait(false);
                return false;
            }

            await WriteStatusAsync(stream, "OKAY").ConfigureAwait(false);
            await WriteVersionAsync(stream).ConfigureAwait(false);
            return true;
        }

        if (payload.StartsWith("host:transport", StringComparison.Ordinal))
        {
            await WriteStatusAsync(stream, "OKAY").ConfigureAwait(false);
            return true;
        }

        if (payload.StartsWith("shell:", StringComparison.Ordinal))
        {
            return await HandleShellAsync(stream, payload["shell:".Length..]).ConfigureAwait(false);
        }

        if (payload == "sync:")
        {
            await WriteStatusAsync(stream, "OKAY").ConfigureAwait(false);
            return await HandleSyncAsync(stream).ConfigureAwait(false);
        }

        await WriteStatusAsync(stream, "FAIL").ConfigureAwait(false);
        await WriteLengthPrefixedAsync(stream, Encoding.UTF8.GetBytes("unknown host service")).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HandleShellAsync(NetworkStream stream, string commandLine)
    {
        const string suffix = "; echo " + ExitMarker + "$?";
        string command = commandLine.EndsWith(suffix, StringComparison.Ordinal)
            ? commandLine[..^suffix.Length]
            : commandLine;

        await WriteStatusAsync(stream, "OKAY").ConfigureAwait(false);
        if (StallShell)
        {
            await Task.Delay(Timeout.Infinite, _cts.Token).ConfigureAwait(false);
            return false;
        }

        ShellResponse? response = ShellHandler?.Invoke(command) ?? new ShellResponse(string.Empty, 0);
        byte[] output = Encoding.UTF8.GetBytes(response.Output + ExitMarker + response.ExitCode + "\n");
        await stream.WriteAsync(output, _cts.Token).ConfigureAwait(false);
        await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
        return false;
    }

    private async Task<bool> HandleSyncAsync(NetworkStream stream)
    {
        using var staging = new MemoryStream();
        string? pendingPath = null;
        uint pendingMode = 0;

        while (!_cts.IsCancellationRequested)
        {
            // sync 请求固定为 8 字节头：4 字节 ASCII 命令字在前，4 字节小端长度在后。
            byte[] idBytes = await ReadExactAsync(stream, 4).ConfigureAwait(false);
            byte[] lenBytes = await ReadExactAsync(stream, 4).ConfigureAwait(false);
            uint rawLength = BinaryPrimitives.ReadUInt32LittleEndian(lenBytes);
            string command = Encoding.ASCII.GetString(idBytes);

            if (command == "DONE")
            {
                // DONE 的长度字段是文件的最后修改时间，不是负载长度，其后没有负载字节。
                lock (_rawSyncFrames)
                {
                    var doneFrame = new byte[8];
                    idBytes.CopyTo(doneFrame, 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(doneFrame.AsSpan(4, 4), rawLength);
                    _rawSyncFrames.Add(doneFrame);
                    _syncCommands.Add(command);
                }

                await CompleteSendAsync(stream, rawLength, pendingPath, pendingMode, staging).ConfigureAwait(false);
                return true;
            }

            if (rawLength > MaxSyncPayload)
            {
                throw new IOException($"sync 请求负载长度 {rawLength} 非法。");
            }

            byte[] payload = await ReadExactAsync(stream, (int)rawLength).ConfigureAwait(false);
            lock (_rawSyncFrames)
            {
                var frame = new byte[8 + payload.Length];
                idBytes.CopyTo(frame, 0);
                BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), (int)rawLength);
                payload.CopyTo(frame, 8);
                _rawSyncFrames.Add(frame);
                _syncCommands.Add(command);
            }

            switch (command)
            {
                case "SEND":
                {
                    // 路径与权限位拼在同一个负载里，用最后一个逗号分隔，权限位为十进制 ASCII。
                    string combined = Encoding.UTF8.GetString(payload);
                    int comma = combined.LastIndexOf(',');
                    if (comma < 0)
                    {
                        await SendSyncFailAsync(stream, "SEND 负载缺少逗号分隔的权限位").ConfigureAwait(false);
                        return false;
                    }

                    pendingPath = combined[..comma];
                    pendingMode = uint.TryParse(
                        combined[(comma + 1)..],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out uint mode)
                        ? mode
                        : 0;
                    staging.SetLength(0);

                    // 受理 SEND 后回一个 OKAY，其长度可忽略。
                    await SendSyncOkayAsync(stream).ConfigureAwait(false);
                    break;
                }

                case "DATA":
                    if (rawLength > ChunkSize)
                    {
                        throw new IOException($"DATA 分块 {rawLength} 超过 64KB 上限。");
                    }

                    // DATA 块服务端不返回任何应答。
                    staging.Write(payload, 0, payload.Length);
                    break;

                case "STAT":
                {
                    string statPath = Encoding.UTF8.GetString(payload);
                    byte[]? content = null;
                    uint mode = 0;
                    lock (_files)
                    {
                        _files.TryGetValue(statPath, out content);
                        _modes.TryGetValue(statPath, out mode);
                    }

                    if (content is null)
                    {
                        await SendSyncFailAsync(stream, $"No such file or directory: {statPath}").ConfigureAwait(false);
                        return false;
                    }

                    // STAT 回包为「STAT + 长度 1 + 模式/大小/时间」。
                    var response = new byte[8 + 16];
                    Encoding.ASCII.GetBytes("STAT").CopyTo(response, 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4, 4), 1);
                    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(8, 4), mode);
                    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(12, 4), (uint)content.Length);
                    uint mtime = 0;
                    _mtimes.TryGetValue(statPath, out mtime);
                    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(16, 4), mtime);
                    await WriteAndFlushAsync(stream, response).ConfigureAwait(false);
                    break;
                }

                case "RECV":
                {
                    string recvPath = Encoding.UTF8.GetString(payload);
                    byte[]? content = null;
                    lock (_files)
                    {
                        _files.TryGetValue(recvPath, out content);
                    }

                    if (content is null)
                    {
                        await SendSyncFailAsync(stream, $"No such file or directory: {recvPath}").ConfigureAwait(false);
                        return false;
                    }

                    // 受理回包为「OKAY + 总长度」，随后是若干 DATA 分块，最后以 DONE 收尾。
                    var preamble = new byte[8];
                    Encoding.ASCII.GetBytes("OKAY").CopyTo(preamble, 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(preamble.AsSpan(4, 4), (uint)content.Length);
                    await stream.WriteAsync(preamble, _cts.Token).ConfigureAwait(false);

                    for (int offset = 0; offset < content.Length; offset += ChunkSize)
                    {
                        int count = Math.Min(ChunkSize, content.Length - offset);
                        var chunk = new byte[8 + count];
                        Encoding.ASCII.GetBytes("DATA").CopyTo(chunk, 0);
                        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4, 4), (uint)count);
                        content.AsSpan(offset, count).CopyTo(chunk.AsSpan(8));
                        await stream.WriteAsync(chunk, _cts.Token).ConfigureAwait(false);
                    }

                    await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                    var done = new byte[8];
                    Encoding.ASCII.GetBytes("DONE").CopyTo(done, 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(done.AsSpan(4, 4), 0);
                    await WriteAndFlushAsync(stream, done).ConfigureAwait(false);
                    return true;
                }

                default:
                    await SendSyncFailAsync(stream, $"unknown sync command {command}").ConfigureAwait(false);
                    return false;
            }
        }

        return false;
    }

    /// <summary>收尾一次 SEND 传输，落盘暂存内容并回复 OKAY。</summary>
    /// <param name="stream">连接流。</param>
    /// <param name="mtime">DONE 帧长度字段携带的最后修改时间。</param>
    /// <param name="pendingPath">SEND 帧声明的目标路径，未发送 SEND 时为 null。</param>
    /// <param name="mode">SEND 帧中解析出的权限位。</param>
    /// <param name="staging">累积的文件内容。</param>
    /// <returns>表示回复已发出的异步结果。</returns>
    private async Task CompleteSendAsync(
        NetworkStream stream,
        uint mtime,
        string? pendingPath,
        uint mode,
        MemoryStream staging)
    {
        if (pendingPath is not null)
        {
            lock (_files)
            {
                _files[pendingPath] = staging.ToArray();
                _mtimes[pendingPath] = mtime;
                _modes[pendingPath] = mode;
            }
        }

        await SendSyncOkayAsync(stream).ConfigureAwait(false);
    }

    private static async Task SendSyncOkayAsync(NetworkStream stream)
    {
        var frame = new byte[8];
        Encoding.ASCII.GetBytes("OKAY").CopyTo(frame, 0);
        await WriteAndFlushAsync(stream, frame).ConfigureAwait(false);
    }

    private static async Task SendSyncFailAsync(NetworkStream stream, string reason)
    {
        byte[] message = Encoding.UTF8.GetBytes(reason);
        var frame = new byte[8 + message.Length];
        Encoding.ASCII.GetBytes("FAIL").CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), (uint)message.Length);
        message.CopyTo(frame, 8);
        await WriteAndFlushAsync(stream, frame).ConfigureAwait(false);
    }

    private static async Task WriteAndFlushAsync(NetworkStream stream, byte[] payload)
    {
        await stream.WriteAsync(payload).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteVersionAsync(NetworkStream stream)
    {
        var payload = new byte[PadVersionResponse ? 0x29 : 12];

        // 真实 adbd 形如「十六进制长度 + 内层十六进制长度 + 4 字节版本号」。
        string inner = PadVersionResponse ? "0029" : payload.Length.ToString("x4");
        Encoding.ASCII.GetBytes(inner).CopyTo(payload, 0);
        Encoding.ASCII.GetBytes("0029").CopyTo(payload, 4);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8, 4), ProtocolVersion);
        await WriteLengthPrefixedAsync(stream, payload).ConfigureAwait(false);
    }

    private async Task WriteStatusAsync(Stream stream, string status)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(status), _cts.Token).ConfigureAwait(false);
        await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
    }

    private async Task WriteLengthPrefixedAsync(Stream stream, byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        Encoding.ASCII.GetBytes(payload.Length.ToString("x4")).CopyTo(frame, 0);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
        await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
    }

    private async Task<byte[]?> ReadFrameAsync(Stream stream)
    {
        byte[] header = await ReadExactAsync(stream, 4).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 4 || length > 0xFFFF)
        {
            return null;
        }

        byte[] payload = await ReadExactAsync(stream, length).ConfigureAwait(false);
        var frame = new byte[4 + length];
        header.CopyTo(frame, 0);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private async Task<byte[]> ReadExactAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), _cts.Token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("对端关闭了连接。");
            }

            offset += read;
        }

        return buffer;
    }
}
