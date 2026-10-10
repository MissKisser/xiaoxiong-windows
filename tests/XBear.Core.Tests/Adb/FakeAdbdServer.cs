using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using XBear.Core.Abstractions;
using XBear.Core.Adb;

namespace XBear.Core.Tests.Adb;

/// <summary>假 adbd 的一条 shell 应答。</summary>
/// <param name="Output">输出内容，含标准输出与标准错误。</param>
/// <param name="ExitCode">退出码。</param>
internal sealed record ShellResponse(string Output, int ExitCode);

/// <summary>
/// 内存内的假 adb server：按真实 adb 服务的 Smart Socket 协议规则处理 host 服务、shell 与 sync 子协议。
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
    private readonly List<string> _shellCommands = [];
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
    /// </summary>
    public bool StallHandshake { get; set; }

    /// <summary>收到 STAT 请求后是否故意返回 FAIL，用于验证取不到源文件大小时的降级逻辑。</summary>
    public bool DisableStat { get; set; }

    /// <summary>客户端发出的请求负载原文。</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>服务端收到的 shell 命令行，已剥除退出码标记，按接收顺序排列。</summary>
    public IReadOnlyList<string> ShellCommands
    {
        get
        {
            lock (_shellCommands)
            {
                return _shellCommands.ToArray();
            }
        }
    }

    /// <summary>客户端发出的请求原始帧，含 4 字符十六进制长度前缀。</summary>
    public IReadOnlyList<byte[]> RawRequests
    {
        get
        {
            lock (_rawRequests)
            {
                return _rawRequests.ToArray();
            }
        }
    }

    /// <summary>客户端在 sync 模式中发出的原始帧，含 8 字节头与负载。</summary>
    public IReadOnlyList<byte[]> RawSyncFrames
    {
        get
        {
            lock (_rawSyncFrames)
            {
                return _rawSyncFrames.ToArray();
            }
        }
    }

    /// <summary>客户端在 sync 模式中发出的命令字，顺序与线上字节一致。</summary>
    public IReadOnlyList<string> SyncCommands
    {
        get
        {
            lock (_syncCommands)
            {
                return _syncCommands.ToArray();
            }
        }
    }

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

    /// <summary>移除实例内的文件，用于模拟 rm 命令。</summary>
    /// <param name="path">实例内路径。</param>
    /// <returns>确实存在并已删除时返回 true。</returns>
    public bool RemoveFile(string path)
    {
        lock (_files)
        {
            _mtimes.Remove(path);
            _modes.Remove(path);
            return _files.Remove(path);
        }
    }

    /// <summary>创建直连当前假服务端的 adb 客户端。</summary>
    /// <param name="timeout">可选超时。</param>
    /// <returns>adb 客户端。</returns>
    public IAdbFileTransferClient CreateClient(TimeSpan? timeout = null) =>
        new AdbClient(timeout, Port);

    /// <summary>关闭监听与全部已建立的连接，释放后台任务。</summary>
    /// <returns>表示释放完成的异步结果。</returns>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();

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

                    string payload = Encoding.UTF8.GetString(frame, 4, frame.Length - 4);
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
            return false;
        }

        if (payload.StartsWith("host:connect:", StringComparison.Ordinal))
        {
            string targetAddr = payload["host:connect:".Length..];
            await WriteStatusAsync(stream, "OKAY").ConfigureAwait(false);
            await WriteLengthPrefixedAsync(stream, Encoding.UTF8.GetBytes($"connected to {targetAddr}")).ConfigureAwait(false);
            return false;
        }

        if (payload.StartsWith("host:transport", StringComparison.Ordinal))
        {
            if (StallHandshake)
            {
                await Task.Delay(Timeout.Infinite, _cts.Token).ConfigureAwait(false);
                return false;
            }

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
        return false;
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

        lock (_shellCommands)
        {
            _shellCommands.Add(command);
        }

        if (command.StartsWith("rm -f ", StringComparison.Ordinal))
        {
            string path = command["rm -f ".Length..].Trim();
            RemoveFile(path);
        }

        ShellResponse? response = ShellHandler?.Invoke(command);
        if (response is null)
        {
            if (command == "id -u")
            {
                response = new ShellResponse("0\n", 0);
            }
            else if (command.StartsWith("test -e ", StringComparison.Ordinal) || command.StartsWith("test -f ", StringComparison.Ordinal))
            {
                string targetPath = command[8..].Trim().Trim('"');
                lock (_files)
                {
                    int code = _files.ContainsKey(targetPath) ? 0 : 1;
                    response = new ShellResponse(string.Empty, code);
                }
            }
            else
            {
                response = new ShellResponse(string.Empty, 0);
            }
        }

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
            byte[] idBytes = await ReadExactAsync(stream, 4).ConfigureAwait(false);
            byte[] lenBytes = await ReadExactAsync(stream, 4).ConfigureAwait(false);
            uint rawLength = BinaryPrimitives.ReadUInt32LittleEndian(lenBytes);
            string command = Encoding.ASCII.GetString(idBytes);

            if (command == "QUIT")
            {
                lock (_rawSyncFrames)
                {
                    var quitFrame = new byte[8];
                    idBytes.CopyTo(quitFrame, 0);
                    lenBytes.CopyTo(quitFrame, 4);
                    _rawSyncFrames.Add(quitFrame);
                    _syncCommands.Add(command);
                }

                return false;
            }

            if (command == "DONE")
            {
                lock (_rawSyncFrames)
                {
                    var doneFrame = new byte[8];
                    idBytes.CopyTo(doneFrame, 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(doneFrame.AsSpan(4, 4), rawLength);
                    _rawSyncFrames.Add(doneFrame);
                    _syncCommands.Add(command);
                }

                await CompleteSendAsync(stream, rawLength, pendingPath, pendingMode, staging).ConfigureAwait(false);
                continue;
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
                    break;
                }

                case "DATA":
                    if (rawLength > ChunkSize)
                    {
                        throw new IOException($"DATA 分块 {rawLength} 超过 64KB 上限。");
                    }

                    staging.Write(payload, 0, payload.Length);
                    break;

                case "STAT":
                {
                    string statPath = Encoding.UTF8.GetString(payload);
                    if (DisableStat)
                    {
                        await SendSyncFailAsync(stream, "STAT disabled").ConfigureAwait(false);
                        break;
                    }

                    byte[]? content = null;
                    uint mode = 0;
                    uint mtime = 0;
                    lock (_files)
                    {
                        if (_files.TryGetValue(statPath, out content))
                        {
                            _modes.TryGetValue(statPath, out mode);
                            _mtimes.TryGetValue(statPath, out mtime);
                            if (mode == 0)
                            {
                                mode = 0x81A4;
                            }
                        }
                    }

                    var response = new byte[16];
                    Encoding.ASCII.GetBytes("STAT").CopyTo(response, 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4, 4), mode);
                    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(8, 4), (uint)(content?.Length ?? 0));
                    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(12, 4), mtime);
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
                    break;
                }

                default:
                    await SendSyncFailAsync(stream, $"unknown sync command {command}").ConfigureAwait(false);
                    return false;
            }
        }

        return false;
    }

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
        string verStr = ProtocolVersion.ToString("x4");
        byte[] payload = Encoding.ASCII.GetBytes(verStr);
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
        string lenHex = Encoding.ASCII.GetString(header);
        if (!int.TryParse(lenHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int length) || length < 0 || length > 0xFFFF)
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
                throw new IOException("连接提前关闭。");
            }

            offset += read;
        }

        return buffer;
    }
}
