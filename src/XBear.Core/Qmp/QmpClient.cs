using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Qmp;

/// <summary>QMP 轻客户端：JSON over TCP，完成握手、按请求-响应配对执行命令并查询实例状态。</summary>
/// <remarks>
/// 读取由单一后台读循环完成，读到的异步事件会被跳过，直到拿到属于当前命令的 return 或 error。
/// </remarks>
public sealed class QmpClient : IQmpClient
{
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShutdownPollInterval = TimeSpan.FromMilliseconds(200);
    private const int ReadBufferSize = 8192;

    private readonly TimeSpan _commandTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<JsonDocument> _messages =
        Channel.CreateUnbounded<JsonDocument>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    private TcpClient? _client;
    private NetworkStream? _stream;
    private QmpFrameReader? _frames;
    private Task? _pump;
    private int _disposed;

    /// <summary>创建 QMP 客户端。</summary>
    /// <param name="commandTimeout">单条命令等待响应的超时，为 null 时使用默认超时。</param>
    public QmpClient(TimeSpan? commandTimeout = null)
    {
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
    }

    /// <summary>连接 QMP 端口，读取 greeting 并完成 qmp_capabilities 握手。</summary>
    /// <param name="port">QMP 宿主端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>对端通告的能力集。</returns>
    /// <exception cref="XBearException">连接失败或握手不符合协议时抛出，分类为 <see cref="ErrorCategory.Protocol"/>。</exception>
    public async Task<IReadOnlySet<string>> ConnectAsync(int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ValidatePort(port, "QMP");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                throw new XBearException(ErrorCategory.Protocol, "QMP 客户端已处于连接状态。");
            }

            var client = new TcpClient { NoDelay = true };
            NetworkStream stream;
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
                stream = client.GetStream();
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                throw;
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
            {
                client.Dispose();
                throw new XBearException(ErrorCategory.Protocol, $"无法连接 QMP 端口 {port}。", null, ex);
            }

            _client = client;
            _stream = stream;
            _frames = new QmpFrameReader();
            _pump = Task.Run(PumpAsync, CancellationToken.None);

            try
            {
                IReadOnlySet<string> capabilities = await ReadGreetingAsync(cancellationToken).ConfigureAwait(false);
                await ExchangeAsync("qmp_capabilities", null, cancellationToken).ConfigureAwait(false);
                return capabilities;
            }
            catch
            {
                await TeardownAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>执行一条 QMP 命令并返回 return 对象。</summary>
    /// <param name="command">命令名。</param>
    /// <param name="arguments">命令参数，可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命令回包中的 return 对象。</returns>
    /// <exception cref="XBearException">对端返回 error、回包异常或连接中断时抛出，分类为 <see cref="ErrorCategory.Protocol"/>；等待响应超时为 <see cref="ErrorCategory.Timeout"/>。</exception>
    public async Task<JsonElement> ExecuteAsync(
        string command,
        object? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExchangeAsync(command, arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>查询实例是否处于运行状态。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>query-status 的 status 字段为 running 时返回 true。</returns>
    public async Task<bool> QueryRunningAsync(CancellationToken cancellationToken = default)
    {
        JsonElement result = await ExecuteAsync("query-status", null, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("status", out JsonElement status)
            || status.ValueKind != JsonValueKind.String)
        {
            throw new XBearException(ErrorCategory.Protocol, "query-status 响应缺少字符串字段 status。");
        }

        return string.Equals(status.GetString(), "running", StringComparison.Ordinal);
    }

    /// <summary>查询实例名称。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>query-name 回包中的 name 字段。</returns>
    public async Task<string> QueryNameAsync(CancellationToken cancellationToken = default)
    {
        JsonElement result = await ExecuteAsync("query-name", null, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("name", out JsonElement name)
            || name.ValueKind != JsonValueKind.String)
        {
            throw new XBearException(ErrorCategory.Protocol, "query-name 响应缺少字符串字段 name。");
        }

        return name.GetString()!;
    }

    /// <summary>请求 guest 正常关机并轮询至实例停止。</summary>
    /// <param name="timeout">等待关机完成的超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>在超时内确认实例已停止时返回 true，超时仍未停止返回 false。</returns>
    public async Task<bool> RequestShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync("system_powerdown", null, cancellationToken).ConfigureAwait(false);

        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (!await QueryRunningAsync(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is XBearException { Category: ErrorCategory.Protocol }
                or IOException
                or SocketException
                or ObjectDisposedException)
            {
                // 关机过程中 QEMU 退出并关闭套接字，连接不可达即视为已停止。
                return true;
            }

            if (elapsed.Elapsed >= timeout)
            {
                return false;
            }

            await Task.Delay(ShutdownPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>释放套接字并等待读循环结束，不留后台任务。</summary>
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
            await TeardownAsync().ConfigureAwait(false);
            _shutdown.Cancel();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _shutdown.Dispose();
        }
    }

    private static void ValidatePort(int port, string label)
    {
        if (port is < 1 or > 65535)
        {
            throw new XBearException(ErrorCategory.Protocol, $"{label} 端口 {port} 不是合法端口。");
        }
    }

    private async Task<IReadOnlySet<string>> ReadGreetingAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CreateTimeoutToken(cancellationToken);
        while (true)
        {
            using JsonDocument document = await NextMessageAsync(timeout.Token).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new XBearException(ErrorCategory.Protocol, "QMP greeting 不是 JSON 对象。");
            }

            if (root.TryGetProperty("event", out _))
            {
                continue;
            }

            if (!root.TryGetProperty("QMP", out JsonElement greeting) || greeting.ValueKind != JsonValueKind.Object)
            {
                throw new XBearException(ErrorCategory.Protocol, "QMP greeting 结构不符合协议。");
            }

            var capabilities = new HashSet<string>(StringComparer.Ordinal);
            if (greeting.TryGetProperty("capabilities", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name)
                    {
                        capabilities.Add(name);
                    }
                }
            }

            return capabilities;
        }
    }

    private async Task<JsonElement> ExchangeAsync(string command, object? arguments, CancellationToken cancellationToken)
    {
        NetworkStream stream = RequireStream();
        await WriteCommandAsync(stream, command, arguments, cancellationToken).ConfigureAwait(false);

        using CancellationTokenSource timeout = CreateTimeoutToken(cancellationToken);
        while (true)
        {
            JsonDocument document;
            try
            {
                document = await NextMessageAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                throw new XBearException(ErrorCategory.Protocol, "QMP 客户端已释放。");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new XBearException(ErrorCategory.Timeout, $"QMP 命令 {command} 等待响应超时。");
            }

            using (document)
            {
                if (document.RootElement.TryGetProperty("event", out _))
                {
                    continue;
                }

                return ExtractResponse(document.RootElement, command);
            }
        }
    }

    private static JsonElement ExtractResponse(JsonElement root, string command)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new XBearException(ErrorCategory.Protocol, $"QMP 命令 {command} 的回包不是 JSON 对象。");
        }

        if (root.TryGetProperty("error", out JsonElement error))
        {
            string className = ReadStringOrEmpty(error, "class");
            string description = ReadStringOrEmpty(error, "desc");
            throw new XBearException(
                ErrorCategory.Protocol,
                $"QMP 命令 {command} 失败：{className} {description}".TrimEnd());
        }

        if (root.TryGetProperty("return", out JsonElement result))
        {
            return result.Clone();
        }

        throw new XBearException(ErrorCategory.Protocol, $"QMP 命令 {command} 的回包既无 return 也无 error。");
    }

    private static string ReadStringOrEmpty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static async Task WriteCommandAsync(
        NetworkStream stream,
        string command,
        object? arguments,
        CancellationToken cancellationToken)
    {
        byte[] payload;
        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("execute", command);
                if (arguments is not null)
                {
                    writer.WritePropertyName("arguments");
                    JsonSerializer.Serialize(writer, arguments, arguments.GetType());
                }

                writer.WriteEndObject();
            }

            buffer.Write("\r\n"u8);
            payload = buffer.ToArray();
        }

        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> NextMessageAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _messages.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            if (ex.InnerException is XBearException known)
            {
                throw known;
            }

            throw new XBearException(ErrorCategory.Protocol, "QMP 连接已关闭。", null, ex);
        }
    }

    private CancellationTokenSource CreateTimeoutToken(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        linked.CancelAfter(_commandTimeout);
        return linked;
    }

    private NetworkStream RequireStream()
    {
        return _stream
            ?? throw new XBearException(ErrorCategory.Protocol, "QMP 尚未连接。", "先调用 ConnectAsync 再执行命令。");
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                JsonDocument document = await ReadDocumentAsync(_shutdown.Token).ConfigureAwait(false);
                await _messages.Writer.WriteAsync(document, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 释放或超时导致读循环结束，属正常路径。
        }
        catch (XBearException ex)
        {
            _messages.Writer.TryComplete(ex);
            return;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or JsonException)
        {
            _messages.Writer.TryComplete(new XBearException(ErrorCategory.Protocol, "QMP 连接中断。", null, ex));
            return;
        }
        finally
        {
            _messages.Writer.TryComplete();
        }
    }

    private async Task<JsonDocument> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        QmpFrameReader frames = _frames!;
        NetworkStream stream = _stream!;
        byte[] buffer = new byte[ReadBufferSize];
        while (true)
        {
            if (frames.TryTakeMessage(out ReadOnlyMemory<byte> message))
            {
                return JsonDocument.Parse(message);
            }

            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new XBearException(ErrorCategory.Protocol, "QMP 对端关闭了连接。");
            }

            frames.Append(buffer.AsSpan(0, read));
        }
    }

    private async Task TeardownAsync()
    {
        NetworkStream? stream = _stream;
        TcpClient? client = _client;
        Task? pump = _pump;
        _stream = null;
        _client = null;
        _pump = null;
        _frames = null;

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
            client?.Dispose();
        }
        catch (SocketException)
        {
            // 关闭套接字失败不影响后续状态。
        }

        _messages.Writer.TryComplete();

        if (pump is null)
        {
            return;
        }

        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 读循环随释放结束。
        }
        catch (XBearException)
        {
            // 断开原因已通过通道或调用点表达。
        }
    }
}