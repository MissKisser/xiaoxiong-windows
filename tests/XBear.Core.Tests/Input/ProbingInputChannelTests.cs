using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Input;

namespace XBear.Core.Tests.Input;

/// <summary>QMP 客户端替身，可按需让原生注入失败。</summary>
internal sealed class StubQmpClient : IQmpClient
{
    /// <summary>原生注入时抛出的异常，为 null 表示注入成功。</summary>
    public Exception? SendEventFailure { get; set; }

    /// <summary>连接时抛出的异常。</summary>
    public Exception? ConnectFailure { get; set; }

    /// <summary>执行过的 QMP 命令序列。</summary>
    public List<string> ExecutedCommands { get; } = new();

    /// <summary>input-send-event 的调用次数。</summary>
    public int SendEventCount { get; private set; }

    /// <summary>
    /// 完成握手。
    /// </summary>
    /// <param name="port">QMP 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>空能力集。</returns>
    public Task<IReadOnlySet<string>> ConnectAsync(int port, CancellationToken cancellationToken = default)
    {
        if (ConnectFailure is not null)
        {
            throw ConnectFailure;
        }

        return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    /// <summary>
    /// 执行 QMP 命令。
    /// </summary>
    /// <param name="command">命令名。</param>
    /// <param name="arguments">命令参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>空返回对象或预设失败。</returns>
    public Task<JsonElement> ExecuteAsync(
        string command,
        object? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ExecutedCommands.Add(command);
        SendEventCount++;

        if (SendEventFailure is not null)
        {
            throw SendEventFailure;
        }

        return Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());
    }

    /// <summary>
    /// 返回运行中。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true。</returns>
    public Task<bool> QueryRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    /// <summary>
    /// 返回关机成功。
    /// </summary>
    /// <param name="timeout">等待超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true。</returns>
    public Task<bool> RequestShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <summary>
    /// 释放替身。
    /// </summary>
    /// <returns>异步任务。</returns>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>adb 客户端替身，可按需让投屏注入失败。</summary>
internal sealed class StubAdbClient : IAdbClient
{
    /// <summary>shell 执行时抛出的异常。</summary>
    public Exception? ShellFailure { get; set; }

    /// <summary>连接时抛出的异常。</summary>
    public Exception? ConnectFailure { get; set; }

    /// <summary>实例是否以 root 运行。</summary>
    public bool IsRoot { get; set; } = true;

    /// <summary>执行过的 shell 命令。</summary>
    public List<string> ShellCommands { get; } = new();

    /// <summary>
    /// 完成握手。
    /// </summary>
    /// <param name="port">adb 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>协议版本 41。</returns>
    public Task<int> ConnectAsync(int port, CancellationToken cancellationToken = default)
    {
        if (ConnectFailure is not null)
        {
            throw ConnectFailure;
        }

        return Task.FromResult(41);
    }

    /// <summary>
    /// 执行 shell 命令。
    /// </summary>
    /// <param name="command">命令。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>空输出。</returns>
    public Task<string> ShellAsync(string command, CancellationToken cancellationToken = default)
    {
        ShellCommands.Add(command);
        return ShellFailure is not null ? throw ShellFailure : Task.FromResult(string.Empty);
    }

    /// <summary>
    /// 返回 root 状态。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>root 状态。</returns>
    public Task<bool> IsRootAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsRoot);

    /// <summary>
    /// 记录推送。
    /// </summary>
    /// <param name="localPath">宿主路径。</param>
    /// <param name="remotePath">实例内路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task PushAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// 记录拉取。
    /// </summary>
    /// <param name="remotePath">实例内路径。</param>
    /// <param name="localPath">宿主路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task PullAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// 释放替身。
    /// </summary>
    /// <returns>异步任务。</returns>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>输入通道探测与降级策略测试。</summary>
public sealed class ProbingInputChannelTests
{
    [Fact]
    public async Task 原生通路成功_选中原生且无失败原因()
    {
        var qmp = new StubQmpClient();
        var adb = new StubAdbClient();
        await using var channel = new ProbingInputChannel(qmp, adb);

        InputProbeResult result = await channel.ProbeAsync();

        Assert.Equal(InputChannelKind.Native, result.Channel);
        Assert.Equal(InputChannelKind.Native, channel.ActiveChannel);
        Assert.Null(result.NativeFailure);
        Assert.Null(result.ProjectionFailure);
        Assert.Equal(1, qmp.SendEventCount);
    }

    [Fact]
    public async Task 原生连续失败三次后投屏成功_降级到投屏并保留原生失败原因()
    {
        var qmp = new StubQmpClient
        {
            SendEventFailure = new XBearException(ErrorCategory.Protocol, "guest 无触摸输入后端"),
        };
        var adb = new StubAdbClient();
        await using var channel = new ProbingInputChannel(
            qmp,
            adb,
            new InputChannelOptions { NativeFailureThreshold = 3 });

        InputProbeResult result = await channel.ProbeAsync();

        Assert.Equal(InputChannelKind.Projection, result.Channel);
        Assert.Equal(InputChannelKind.Projection, channel.ActiveChannel);
        Assert.False(string.IsNullOrWhiteSpace(result.NativeFailure));
        Assert.Null(result.ProjectionFailure);

        // 原生通路应被完整重试到阈值次数后才降级。
        Assert.Equal(3, qmp.SendEventCount);
        Assert.NotEmpty(adb.ShellCommands);
    }

    [Fact]
    public async Task 原生失败一次成功一次_不降级且计数清零()
    {
        var qmp = new ThrowingOnceQmpClient();
        var adb = new StubAdbClient();
        await using var channel = new ProbingInputChannel(
            qmp,
            adb,
            new InputChannelOptions { NativeFailureThreshold = 3 });

        InputProbeResult result = await channel.ProbeAsync();

        Assert.Equal(InputChannelKind.Native, result.Channel);
        Assert.Null(result.NativeFailure);
        Assert.Equal(0, channel.ConsecutiveNativeFailures);
    }

    [Fact]
    public async Task 两条通路都失败_返回不可用且两条失败原因均非空()
    {
        var qmp = new StubQmpClient { SendEventFailure = new InvalidOperationException("原生注入异常") };
        var adb = new StubAdbClient { ShellFailure = new XBearException(ErrorCategory.Protocol, "adb 未连接") };
        await using var channel = new ProbingInputChannel(
            qmp,
            adb,
            new InputChannelOptions { NativeFailureThreshold = 2 });

        InputProbeResult result = await channel.ProbeAsync();

        Assert.Equal(InputChannelKind.Unavailable, result.Channel);
        Assert.Equal(InputChannelKind.Unavailable, channel.ActiveChannel);
        Assert.False(string.IsNullOrWhiteSpace(result.NativeFailure));
        Assert.False(string.IsNullOrWhiteSpace(result.ProjectionFailure));

        // 绝不返回 Unknown 卡住。
        Assert.NotEqual(InputChannelKind.Unknown, result.Channel);
    }

    [Fact]
    public async Task 投屏通路_实例非root时如实报告失败原因()
    {
        var qmp = new StubQmpClient { SendEventFailure = new InvalidOperationException("原生注入异常") };
        var adb = new StubAdbClient { IsRoot = false };
        await using var channel = new ProbingInputChannel(qmp, adb, new InputChannelOptions { NativeFailureThreshold = 1 });

        InputProbeResult result = await channel.ProbeAsync();

        Assert.Equal(InputChannelKind.Unavailable, result.Channel);
        Assert.Contains("权限", result.ProjectionFailure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 阈值可配置_按配置次数重试原生通路()
    {
        var qmp = new StubQmpClient { SendEventFailure = new InvalidOperationException("原生注入异常") };
        var adb = new StubAdbClient();
        await using var channel = new ProbingInputChannel(
            qmp,
            adb,
            new InputChannelOptions { NativeFailureThreshold = 5 });

        await channel.ProbeAsync();

        Assert.Equal(5, qmp.SendEventCount);
    }

    [Fact]
    public async Task 原生通路返回QMP错误对象_识别为失败并降级()
    {
        var qmp = new QmpErrorClient();
        var adb = new StubAdbClient();
        await using var channel = new ProbingInputChannel(qmp, adb, new InputChannelOptions { NativeFailureThreshold = 1 });

        InputProbeResult result = await channel.ProbeAsync();

        Assert.Equal(InputChannelKind.Projection, result.Channel);
        Assert.Contains("no such device", result.NativeFailure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 探测后重复探测_原生成功后计数归零()
    {
        var qmp = new StubQmpClient();
        var adb = new StubAdbClient();
        await using var channel = new ProbingInputChannel(qmp, adb);

        await channel.ProbeAsync();
        InputProbeResult second = await channel.ProbeAsync();

        Assert.Equal(InputChannelKind.Native, second.Channel);
        Assert.Equal(0, channel.ConsecutiveNativeFailures);
        Assert.Equal(2, qmp.SendEventCount);
    }

    [Fact]
    public async Task 原生连续失败达阈值后_后续探测不再重试原生通路()
    {
        var qmp = new StubQmpClient { SendEventFailure = new InvalidOperationException("原生注入异常") };
        var adb = new StubAdbClient();
        await using var channel = new ProbingInputChannel(
            qmp,
            adb,
            new InputChannelOptions { NativeFailureThreshold = 2 });

        InputProbeResult first = await channel.ProbeAsync();
        Assert.Equal(2, qmp.SendEventCount);
        Assert.Equal(2, channel.ConsecutiveNativeFailures);

        qmp.SendEventFailure = null;
        InputProbeResult second = await channel.ProbeAsync();

        // 门控跨调用生效：即使原生通路此刻已恢复，也不应在本通道内重新试探。
        Assert.Equal(2, qmp.SendEventCount);
        Assert.Equal(InputChannelKind.Projection, second.Channel);
        Assert.Equal(InputChannelKind.Projection, first.Channel);
        Assert.False(string.IsNullOrWhiteSpace(second.NativeFailure));
    }

    [Fact]
    public async Task 原生成功后计数清零_再次探测重新按阈值重试()
    {
        var qmp = new ThrowingTwiceQmpClient();
        var adb = new StubAdbClient();
        await using var channel = new ProbingInputChannel(
            qmp,
            adb,
            new InputChannelOptions { NativeFailureThreshold = 3 });

        InputProbeResult first = await channel.ProbeAsync();

        // 两次失败后第三次成功，计数必须归零而不是停在阈值。
        Assert.Equal(InputChannelKind.Native, first.Channel);
        Assert.Equal(3, qmp.SendEventCount);
        Assert.Equal(0, channel.ConsecutiveNativeFailures);

        InputProbeResult second = await channel.ProbeAsync();

        Assert.Equal(InputChannelKind.Native, second.Channel);
        Assert.Equal(4, qmp.SendEventCount);
        Assert.Equal(0, channel.ConsecutiveNativeFailures);
    }

    [Fact]
    public void 构造触摸事件载荷_包含按下与抬起()
    {
        object payload = ProbingInputChannel.BuildTouchEventPayload(10, 20);

        string json = JsonSerializer.Serialize(payload);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement events = doc.RootElement.GetProperty("events");

        Assert.Equal(4, events.GetArrayLength());
        Assert.Equal("abs", events[0].GetProperty("type").GetString());
        Assert.Equal(10, events[0].GetProperty("data").GetProperty("value").GetInt32());
        Assert.Equal(20, events[1].GetProperty("data").GetProperty("value").GetInt32());
        Assert.Equal("btn", events[2].GetProperty("type").GetString());
        Assert.True(events[2].GetProperty("data").GetProperty("down").GetBoolean());
        Assert.False(events[3].GetProperty("data").GetProperty("down").GetBoolean());
    }

    [Fact]
    public async Task 释放后再次探测_抛出对象已释放异常()
    {
        var channel = new ProbingInputChannel(new StubQmpClient(), new StubAdbClient());
        await channel.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => channel.ProbeAsync());
    }

    /// <summary>首次注入失败、其后成功的 QMP 替身。</summary>
    private sealed class ThrowingOnceQmpClient : IQmpClient
    {
        private bool _failed;

        /// <summary>
        /// 首次抛出异常，其后返回成功。
        /// </summary>
        /// <param name="command">命令名。</param>
        /// <param name="arguments">命令参数。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>空返回对象。</returns>
        public Task<JsonElement> ExecuteAsync(
            string command,
            object? arguments = null,
            CancellationToken cancellationToken = default)
        {
            if (!_failed)
            {
                _failed = true;
                throw new XBearException(ErrorCategory.Protocol, "偶发失败");
            }

            return Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());
        }

        /// <summary>完成握手。</summary>
        /// <param name="port">QMP 端口。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>空能力集。</returns>
        public Task<IReadOnlySet<string>> ConnectAsync(int port, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        /// <summary>返回运行中。</summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>true。</returns>
        public Task<bool> QueryRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        /// <summary>返回关机成功。</summary>
        /// <param name="timeout">等待超时。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>true。</returns>
        public Task<bool> RequestShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        /// <summary>释放替身。</summary>
        /// <returns>异步任务。</returns>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>前两次注入失败、其后成功的 QMP 替身。</summary>
    private sealed class ThrowingTwiceQmpClient : IQmpClient
    {
        private int _failures;

        /// <summary>input-send-event 的调用次数。</summary>
        public int SendEventCount { get; private set; }

        /// <summary>
        /// 前两次抛出异常，其后返回成功。
        /// </summary>
        /// <param name="command">命令名。</param>
        /// <param name="arguments">命令参数。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>空返回对象。</returns>
        public Task<JsonElement> ExecuteAsync(
            string command,
            object? arguments = null,
            CancellationToken cancellationToken = default)
        {
            SendEventCount++;

            if (_failures < 2)
            {
                _failures++;
                throw new XBearException(ErrorCategory.Protocol, "连续两次失败");
            }

            return Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());
        }

        /// <summary>完成握手。</summary>
        /// <param name="port">QMP 端口。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>空能力集。</returns>
        public Task<IReadOnlySet<string>> ConnectAsync(int port, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        /// <summary>返回运行中。</summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>true。</returns>
        public Task<bool> QueryRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        /// <summary>返回关机成功。</summary>
        /// <param name="timeout">等待超时。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>true。</returns>
        public Task<bool> RequestShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        /// <summary>释放替身。</summary>
        /// <returns>异步任务。</returns>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>始终返回 QMP 错误对象的客户端替身。</summary>
    private sealed class QmpErrorClient : IQmpClient
    {
        /// <summary>完成握手。</summary>
        /// <param name="port">QMP 端口。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>空能力集。</returns>
        public Task<IReadOnlySet<string>> ConnectAsync(int port, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        /// <summary>返回 QMP 错误对象。</summary>
        /// <param name="command">命令名。</param>
        /// <param name="arguments">命令参数。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>含 error 字段的返回对象。</returns>
        public Task<JsonElement> ExecuteAsync(
            string command,
            object? arguments = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonDocument.Parse("{\"error\":{\"desc\":\"no such device\"}}").RootElement.Clone());

        /// <summary>返回运行中。</summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>true。</returns>
        public Task<bool> QueryRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        /// <summary>返回关机成功。</summary>
        /// <param name="timeout">等待超时。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>true。</returns>
        public Task<bool> RequestShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        /// <summary>释放替身。</summary>
        /// <returns>异步任务。</returns>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}