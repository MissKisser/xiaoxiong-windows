using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Snapshots;

namespace XBear.Core.Tests.Snapshots;

/// <summary>
/// QMP 客户端替身。按本产品实际下发的命令语义应答：
/// human-monitor-command 走 human monitor 的 savevm、loadvm、delvm 与 info snapshots，
/// QMP 原生的 stop 与 cont 改变运行态，query-status 报告当前状态。全程不启动 QEMU。
/// </summary>
internal sealed class FakeSnapshotQmpClient : IQmpClient
{
    private readonly List<SnapshotRuntimeEntry> _table = new();
    private int _nextId = 1;

    /// <summary>QEMU 侧当前持有的快照标签，按插入顺序排列。</summary>
    public IReadOnlyList<string> Tags => _table.Select(entry => entry.Tag).ToList();

    /// <summary>收到过的 human monitor 命令行。</summary>
    public List<string> HumanMonitorCommands { get; } = new();

    /// <summary>收到过的 QMP 命令名。</summary>
    public List<string> QmpCommands { get; } = new();

    /// <summary>实例当前是否处于运行态。</summary>
    public bool Running { get; set; } = true;

    /// <summary>连接失败时抛出的异常。</summary>
    public Exception? ConnectFailure { get; set; }

    /// <summary>以指定前缀开头的 human monitor 命令失败时抛出的异常。</summary>
    public Exception? HumanMonitorFailure { get; set; }

    /// <summary>触发 <see cref="HumanMonitorFailure"/> 的命令前缀。</summary>
    public string HumanMonitorFailurePrefix { get; set; } = string.Empty;

    /// <summary>连接次数。</summary>
    public int ConnectCount { get; private set; }

    /// <summary>释放次数。</summary>
    public int DisposeCount { get; private set; }

    /// <summary>最近一次连接的端口。</summary>
    public int LastConnectPort { get; private set; }

    /// <summary>
    /// 预置一条 QEMU 侧快照记录。
    /// </summary>
    /// <param name="tag">快照标签。</param>
    /// <param name="vmSize">虚拟机内存体积文本。</param>
    /// <param name="clock">虚拟机时钟文本。</param>
    public void Seed(string tag, string vmSize = "0 B", string clock = "00:00:00.000")
    {
        _table.Add(new SnapshotRuntimeEntry(
            (_nextId++).ToString(),
            tag,
            vmSize,
            new DateTimeOffset(2026, 10, 7, 11, 20, 0, TimeSpan.FromHours(8)),
            clock));
    }

    /// <summary>
    /// 记录一次连接。
    /// </summary>
    /// <param name="port">QMP 宿主端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>空能力集。</returns>
    public Task<IReadOnlySet<string>> ConnectAsync(int port, CancellationToken cancellationToken = default)
    {
        LastConnectPort = port;
        ConnectCount++;

        return ConnectFailure is not null
            ? throw ConnectFailure
            : Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// 按命令语义应答，并把命令记录下来供断言使用。
    /// </summary>
    /// <param name="command">命令名。</param>
    /// <param name="arguments">命令参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命令回包中的 return 对象。</returns>
    public Task<JsonElement> ExecuteAsync(
        string command,
        object? arguments = null,
        CancellationToken cancellationToken = default)
    {
        QmpCommands.Add(command);

        if (command == SnapshotService.HumanMonitorCommandName)
        {
            string line = arguments is HumanMonitorArguments monitor
                ? monitor.CommandLine
                : throw new InvalidOperationException("human-monitor-command 缺少 command-line 参数。");

            HumanMonitorCommands.Add(line);
            return Task.FromResult(HandleHumanMonitor(line));
        }

        if (command == "blockdev-snapshot-internal-sync")
        {
            string? name = GetArgumentProperty(arguments, "name");
            if (!string.IsNullOrEmpty(name))
            {
                Seed(name);
            }

            return Task.FromResult(Empty());
        }

        if (command == "blockdev-snapshot-delete-internal-sync")
        {
            string? name = GetArgumentProperty(arguments, "name");
            if (!string.IsNullOrEmpty(name))
            {
                _table.RemoveAll(entry => string.Equals(entry.Tag, name, StringComparison.Ordinal));
            }

            return Task.FromResult(Empty());
        }

        return Task.FromResult(command switch
        {
            SnapshotService.PauseCommandName => Pause(),
            SnapshotService.ResumeCommandName => Resume(),
            "query-status" => Status(),
            _ => Empty(),
        });
    }

    private static string? GetArgumentProperty(object? arguments, string propertyName)
    {
        if (arguments is null)
        {
            return null;
        }

        var property = arguments.GetType().GetProperty(propertyName);
        if (property is not null)
        {
            return property.GetValue(arguments)?.ToString();
        }

        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
            if (doc.RootElement.TryGetProperty(propertyName, out JsonElement element))
            {
                return element.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>
    /// 报告实例当前运行态。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实例处于运行态时返回 true。</returns>
    public Task<bool> QueryRunningAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Running);

    /// <summary>
    /// 本替身不实现 guest 关机，快照链路只依赖 QMP 与编排器的停止语义。
    /// </summary>
    /// <param name="timeout">等待关机完成的超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>恒为 true。</returns>
    public Task<bool> RequestShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <summary>
    /// 记录一次释放。
    /// </summary>
    /// <returns>表示释放完成的异步结果。</returns>
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }

    private JsonElement HandleHumanMonitor(string line)
    {
        if (HumanMonitorFailure is not null &&
            line.StartsWith(HumanMonitorFailurePrefix, StringComparison.Ordinal))
        {
            throw HumanMonitorFailure;
        }

        string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string verb = parts[0];
        string argument = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (verb)
        {
            case "info":
                return Text(RenderTable());

            case "savevm":
                Seed(argument);
                return Text(string.Empty);

            case "loadvm":
                RequireExists(argument, "loadvm");
                return Text(string.Empty);

            case "delvm":
                _table.RemoveAll(entry => string.Equals(entry.Tag, argument, StringComparison.Ordinal));
                return Text(string.Empty);

            default:
                throw new XBearException(
                    ErrorCategory.Protocol,
                    $"替身不支持的 human monitor 命令：{line}",
                    "请确认快照服务只下发受支持的命令。");
        }
    }

    private void RequireExists(string tag, string verb)
    {
        if (_table.Any(entry => string.Equals(entry.Tag, tag, StringComparison.Ordinal)))
        {
            return;
        }

        throw new XBearException(
            ErrorCategory.Protocol,
            $"{verb}: snapshot '{tag}' does not exist",
            "请先创建该快照后再执行该命令。");
    }

    private string RenderTable()
    {
        if (_table.Count == 0)
        {
            return SnapshotRuntimeTable.EmptyTableText;
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("ID       TAG                 VM SIZE                DATE        VM CLOCK");
        foreach (SnapshotRuntimeEntry entry in _table)
        {
            builder.AppendLine(
                $"{entry.Id,-8} {entry.Tag,-20} {entry.VmSize,-20} "
                + $"{entry.CreatedAt:yyyy-MM-dd} {entry.CreatedAt:HH\\:mm\\:ss}   {entry.VmClock}");
        }

        return builder.ToString();
    }

    private JsonElement Pause()
    {
        Running = false;
        return Empty();
    }

    private JsonElement Resume()
    {
        Running = true;
        return Empty();
    }

    private JsonElement Status() =>
        Text(Running ? "running" : "paused");

    private static JsonElement Empty()
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonElement Text(string value)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }
}