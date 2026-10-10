using System.Text.Json;
using System.Windows;
using XBear.App.Views;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Snapshots;

namespace XBear.App.Tests;

/// <summary>快照管理窗口宿主替身，记录打开、置前与关闭行为。</summary>
internal sealed class StubSnapshotWindowHost : ISnapshotWindowHost
{
    private readonly Dictionary<string, object> _open = new(StringComparer.Ordinal);

    /// <summary>按创建顺序记录的已打开实例标识序列。</summary>
    public List<string> Created { get; } = new();

    /// <summary>被置前的实例标识序列。</summary>
    public List<string> Activated { get; } = new();

    /// <summary>被关闭的实例标识序列。</summary>
    public List<string> Closed { get; } = new();

    /// <summary>当前已打开的实例标识集合。</summary>
    public IReadOnlyCollection<string> OpenInstanceIds => _open.Keys;

    /// <inheritdoc />
    public void Open(string instanceId, string instanceName, Window? owner)
    {
        if (_open.ContainsKey(instanceId))
        {
            Activated.Add(instanceId);
            return;
        }

        _open[instanceId] = new object();
        Created.Add(instanceId);
    }

    /// <inheritdoc />
    public void Close(string instanceId)
    {
        if (_open.Remove(instanceId))
        {
            Closed.Add(instanceId);
        }
    }
}

/// <summary>
/// QMP 客户端替身。按 human monitor 语义应答 savevm、loadvm、delvm 与 info snapshots，
/// 使快照链路的界面测试不依赖真实 QEMU。
/// </summary>
internal sealed class StubSnapshotQmpClient : IQmpClient
{
    private readonly List<string> _tags = new();

    /// <summary>QEMU 侧当前持有的快照标签。</summary>
    public IReadOnlyList<string> Tags => _tags;

    /// <summary>收到过的 human monitor 命令行。</summary>
    public List<string> HumanMonitorCommands { get; } = new();

    /// <summary>收到过的 QMP 命令名。</summary>
    public List<string> QmpCommands { get; } = new();

    /// <summary>实例当前是否处于运行态。</summary>
    public bool Running { get; set; } = true;

    /// <summary>以指定前缀开头的 human monitor 命令失败时抛出的异常。</summary>
    public Exception? HumanMonitorFailure { get; set; }

    /// <summary>触发失败的命令前缀。</summary>
    public string HumanMonitorFailurePrefix { get; set; } = string.Empty;

    /// <summary>
    /// 连接并完成握手。
    /// </summary>
    /// <param name="port">QMP 宿主端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>空能力集。</returns>
    public Task<IReadOnlySet<string>> ConnectAsync(int port, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));

    /// <summary>
    /// 按命令语义应答。
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
                _tags.Add(name);
            }

            return Task.FromResult(Empty());
        }

        if (command == "blockdev-snapshot-delete-internal-sync")
        {
            string? name = GetArgumentProperty(arguments, "name");
            if (!string.IsNullOrEmpty(name))
            {
                _tags.RemoveAll(tag => string.Equals(tag, name, StringComparison.Ordinal));
            }

            return Task.FromResult(Empty());
        }

        return Task.FromResult(command switch
        {
            SnapshotService.PauseCommandName => Toggle(false),
            SnapshotService.ResumeCommandName => Toggle(true),
            "query-status" => Text(Running ? "running" : "paused"),
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
    public Task<bool> QueryRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(Running);

    /// <summary>
    /// 快照链路不依赖 guest 关机，替身直接返回已完成。
    /// </summary>
    /// <param name="timeout">等待关机完成的超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>恒为 true。</returns>
    public Task<bool> RequestShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <summary>
    /// 释放替身。
    /// </summary>
    /// <returns>表示释放完成的异步结果。</returns>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
                _tags.Add(argument);
                return Text(string.Empty);

            case "loadvm":
                if (!_tags.Contains(argument, StringComparer.Ordinal))
                {
                    throw new XBearException(
                        ErrorCategory.Protocol,
                        $"loadvm: snapshot '{argument}' does not exist",
                        "请先创建该快照后再执行该命令。");
                }

                return Text(string.Empty);

            case "delvm":
                _tags.RemoveAll(tag => string.Equals(tag, argument, StringComparison.Ordinal));
                return Text(string.Empty);

            default:
                throw new XBearException(
                    ErrorCategory.Protocol,
                    $"替身不支持的 human monitor 命令：{line}",
                    "请确认快照服务只下发受支持的命令。");
        }
    }

    private string RenderTable()
    {
        if (_tags.Count == 0)
        {
            return SnapshotRuntimeTable.EmptyTableText;
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("ID       TAG                        VM SIZE   DATE        VM CLOCK");
        for (int i = 0; i < _tags.Count; i++)
        {
            builder.AppendLine(
                $"{i,-8} {_tags[i],-26} 0 B      2026-10-10  09:30:00   00:00:00.000");
        }

        return builder.ToString();
    }

    private JsonElement Toggle(bool running)
    {
        Running = running;
        return Empty();
    }

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