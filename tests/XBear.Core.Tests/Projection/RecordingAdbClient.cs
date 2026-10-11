using System.Text;
using XBear.Core.Abstractions;

namespace XBear.Core.Tests.Projection;

/// <summary>
/// 记录保活通道下发到实例的命令与推送脚本的 adb 替身。
/// 只实现保活通道用到的那部分能力：连接、shell 与文件推送。
/// </summary>
internal sealed class RecordingAdbClient : IAdbClient
{
    /// <summary>替身在进程号文件查询中回报的进程号。</summary>
    public const int Pid = 7777;

    private readonly List<string> _shellCommands = [];

    /// <summary>按发生顺序记录的全部 shell 命令。</summary>
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

    /// <summary>按实例内路径记录被推送的脚本文本。</summary>
    public Dictionary<string, string> PushedScripts { get; } = new(StringComparer.Ordinal);

    /// <summary>连接到的实例 adb 端口，未连接时为 null。</summary>
    public int? ConnectedPort { get; private set; }

    /// <summary>是否已被释放。</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public Task<int> ConnectAsync(
        int port,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        ConnectedPort = port;
        return Task.FromResult(0x29);
    }

    /// <inheritdoc />
    public Task<string> ShellAsync(string command, CancellationToken cancellationToken = default)
    {
        lock (_shellCommands)
        {
            _shellCommands.Add(command);
        }

        bool isPidProbe = command.Contains("keepalive.pid", StringComparison.Ordinal);
        return Task.FromResult(isPidProbe ? $"{Pid}\n" : string.Empty);
    }

    /// <inheritdoc />
    public Task<bool> IsRootAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <inheritdoc />
    public Task PushAsync(string localPath, string remotePath, CancellationToken cancellationToken = default)
    {
        PushedScripts[remotePath] = File.ReadAllText(localPath, Encoding.UTF8);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PullAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}