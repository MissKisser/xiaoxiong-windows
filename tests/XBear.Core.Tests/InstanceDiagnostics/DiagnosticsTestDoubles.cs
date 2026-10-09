using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Tests.InstanceDiagnostics;

/// <summary>进程句柄替身，命令行与日志路径由测试指定。</summary>
internal sealed class StubQemuProcessHandle : QemuProcessHandle
{
    /// <summary>构造替身。</summary>
    /// <param name="commandLine">启动命令行原文。</param>
    /// <param name="logFilePath">日志文件路径。</param>
    public StubQemuProcessHandle(string commandLine, string logFilePath)
    {
        CommandLine = commandLine;
        LogFilePath = logFilePath;
    }

    /// <summary>宿主进程标识。</summary>
    public int ProcessId { get; init; } = 4242;

    /// <summary>启动命令行原文。</summary>
    public string CommandLine { get; }

    /// <summary>日志文件路径。</summary>
    public string LogFilePath { get; }

    /// <summary>进程是否已退出。</summary>
    public bool HasExited { get; init; }

    /// <summary>异常退出时的退出码。</summary>
    public int ExitCode { get; init; } = -1;

    /// <summary>记录一次停止调用。</summary>
    /// <param name="timeout">等待超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>记录一次释放调用。</summary>
    /// <returns>异步任务。</returns>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>QMP 客户端替身，按命令名脚本化回包，不建立任何真实连接。</summary>
internal sealed class FakeQmpClient : IQmpClient
{
    private readonly Func<string, object?, CancellationToken, Task<JsonElement>> _respond;

    /// <summary>构造替身。</summary>
    /// <param name="respond">
    /// 命令分发器，收到命令名与参数；默认对状态查询回空对象，
    /// 对 screendump 在参数指定的路径上写入一张极小的 PPM。
    /// </param>
    public FakeQmpClient(Func<string, object?, CancellationToken, Task<JsonElement>>? respond = null)
    {
        _respond = respond ?? DefaultRespond;
    }

    /// <summary>已收到的命令名，按序记录。</summary>
    public List<string> Commands { get; } = [];

    /// <summary>已连接过的端口，按序记录。快照与截图各自建连，因此通常会有两条。</summary>
    public List<int> ConnectedPorts { get; } = [];

    /// <summary>释放次数。</summary>
    public int DisposeCount { get; private set; }

    /// <summary>连接时抛出，模拟 QMP 端口不可达。</summary>
    public Exception? ConnectFailure { get; init; }

    /// <summary>连接时永不返回，用于验证单步时限。</summary>
    public bool HangOnConnect { get; init; }

    /// <summary>
    /// 记录连接并返回能力集。
    /// </summary>
    /// <param name="port">QMP 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>能力集。</returns>
    public async Task<IReadOnlySet<string>> ConnectAsync(int port, CancellationToken cancellationToken = default)
    {
        ConnectedPorts.Add(port);
        Commands.Add("connect");

        if (HangOnConnect)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (ConnectFailure is not null)
        {
            throw ConnectFailure;
        }

        return new HashSet<string>(StringComparer.Ordinal) { "oob" };
    }

    /// <summary>
    /// 按命令名分发到脚本化回包。
    /// </summary>
    /// <param name="command">命令名。</param>
    /// <param name="arguments">命令参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>回包中的 return 对象。</returns>
    public Task<JsonElement> ExecuteAsync(
        string command,
        object? arguments = null,
        CancellationToken cancellationToken = default)
    {
        Commands.Add(command);
        return _respond(command, arguments, cancellationToken);
    }

    /// <summary>
    /// 恒为 false，导出流程不使用该便捷查询。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>false。</returns>
    public Task<bool> QueryRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>
    /// 恒为 false，导出流程不使用该便捷查询。
    /// </summary>
    /// <param name="timeout">等待超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>false。</returns>
    public Task<bool> RequestShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>记录一次释放。</summary>
    /// <returns>异步任务。</returns>
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }

    /// <summary>默认回包分发器：状态查询给一份可解析的对象，screendump 在参数指定的路径上写入一张极小的 PPM。</summary>
    public static Func<string, object?, CancellationToken, Task<JsonElement>> DefaultRespond { get; } = DefaultRespondAsync;

    /// <summary>
    /// 默认回包实现。
    /// </summary>
    /// <param name="command">命令名。</param>
    /// <param name="arguments">命令参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>回包中的 return 对象。</returns>
    private static Task<JsonElement> DefaultRespondAsync(
        string command,
        object? arguments,
        CancellationToken cancellationToken)
    {
        if (string.Equals(command, "screendump", StringComparison.Ordinal))
        {
            string? path = ReadStringArgument(arguments, "filename");
            if (path is not null)
            {
                File.WriteAllBytes(path, FakePpm);
            }

            return Task.FromResult(Parse("{}"));
        }

        return Task.FromResult(command switch
        {
            "query-status" => Parse("""{"status":"running","running":true,"singlestep":false}"""),
            "query-name" => Parse("""{"name":"xiaoxiong-01"}"""),
            "query-kvm" => Parse("""{"enabled":true,"present":true}"""),
            "query-memory-size-summary" => Parse("""{"base-memory":6107136}"""),
            _ => Parse("{}"),
        });
    }

    /// <summary>一张 1x1 的 PPM 图片字节。</summary>
    public static byte[] FakePpm { get; } =
        "P6\n1 1\n255\n\0\0\0"u8.ToArray();

    /// <summary>
    /// 从命令参数里取出字符串字段。
    /// </summary>
    /// <param name="arguments">命令参数。</param>
    /// <param name="name">字段名。</param>
    /// <returns>字段值，不存在时返回 null。</returns>
    public static string? ReadStringArgument(object? arguments, string name)
    {
        if (arguments is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(arguments, arguments.GetType()));
        return document.RootElement.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
    }

    /// <summary>
    /// 把 JSON 文本解析为可序列化的 JsonElement。
    /// </summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>解析结果。</returns>
    public static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>adb 客户端替身，可分别模拟连不上、传输层通但 shell 无响应与完全可用。</summary>
internal sealed class FakeAdbClient : IAdbClient
{
    /// <summary>连接失败时抛出，模拟 adbd 端口不可达。</summary>
    public Exception? ConnectFailure { get; init; }

    /// <summary>shell 失败时抛出，模拟传输层通但实例内服务未就绪。</summary>
    public Exception? ShellFailure { get; init; }

    /// <summary>握手返回的协议版本。</summary>
    public int ProtocolVersion { get; init; } = 0x01000000;

    /// <summary>实例是否以 root 身份运行。</summary>
    public bool Root { get; init; } = true;

    /// <summary>释放次数。</summary>
    public int DisposeCount { get; private set; }

    /// <summary>
    /// 返回协议版本或抛出预设的连接失败。
    /// </summary>
    /// <param name="port">adb 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="timeout">超时。</param>
    /// <returns>协议版本。</returns>
    public Task<int> ConnectAsync(int port, CancellationToken cancellationToken = default, TimeSpan? timeout = null) =>
        ConnectFailure is not null ? throw ConnectFailure : Task.FromResult(ProtocolVersion);

    /// <summary>
    /// 返回固定输出或抛出预设的 shell 失败。
    /// </summary>
    /// <param name="command">命令与参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标准输出。</returns>
    public Task<string> ShellAsync(string command, CancellationToken cancellationToken = default) =>
        ShellFailure is not null ? throw ShellFailure : Task.FromResult("0");

    /// <summary>
    /// 返回预设的 root 结论或抛出预设的 shell 失败。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否以 root 运行。</returns>
    public Task<bool> IsRootAsync(CancellationToken cancellationToken = default)
    {
        if (ShellFailure is not null)
        {
            throw ShellFailure;
        }

        return Task.FromResult(Root);
    }

    /// <summary>不做实际推送。</summary>
    /// <param name="localPath">宿主文件路径。</param>
    /// <param name="remotePath">实例内目标路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task PushAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>不做实际拉取。</summary>
    /// <param name="remotePath">实例内源路径。</param>
    /// <param name="localPath">宿主目标路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task PullAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>记录一次释放。</summary>
    /// <returns>异步任务。</returns>
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 构造一个表示连不上的替身。
    /// </summary>
    /// <param name="message">失败原因。</param>
    /// <returns>连接阶段即失败的替身。</returns>
    public static FakeAdbClient Unreachable(string message = "无法连接实例 adbd 端口 15555。") =>
        new() { ConnectFailure = new XBearException(ErrorCategory.Protocol, message) };

    /// <summary>
    /// 构造一个表示传输层通但 shell 无响应的替身。
    /// </summary>
    /// <param name="message">失败原因。</param>
    /// <returns>shell 阶段失败的替身。</returns>
    public static FakeAdbClient Offline(string message = "adbd 提前关闭了连接。") =>
        new() { ShellFailure = new XBearException(ErrorCategory.Protocol, message) };
}