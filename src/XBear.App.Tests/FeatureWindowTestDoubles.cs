using System.Windows;
using XBear.App.Views;
using XBear.Core.Abstractions;

namespace XBear.App.Tests;

/// <summary>
/// 文件传输窗口宿主测试替身。
/// </summary>
internal sealed class StubFileTransferWindowHost : IFileTransferWindowHost
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
/// 应用管理窗口宿主测试替身。
/// </summary>
internal sealed class StubApplicationWindowHost : IApplicationWindowHost
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
/// 模块管理窗口宿主测试替身。
/// </summary>
internal sealed class StubModuleWindowHost : IModuleWindowHost
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
/// 可配置行为的 adb 客户端替身，支持自定义 shell 与传输回调。
/// </summary>
internal sealed class StubFeatureAdbClient : IAdbFileTransferClient
{
    /// <summary>自定义 shell 处理委托。</summary>
    public Func<string, string>? ShellHandler { get; set; }

    /// <summary>自定义 Stat 处理委托。</summary>
    public Func<string, AdbRemoteFileInfo?>? StatHandler { get; set; }

    /// <summary>自定义 Push 处理委托。</summary>
    public Action<string, string, IProgress<long>?, CancellationToken>? PushHandler { get; set; }

    /// <summary>自定义 Pull 处理委托。</summary>
    public Action<string, string, IProgress<long>?, CancellationToken>? PullHandler { get; set; }

    /// <summary>执行过的 shell 命令序列。</summary>
    public List<string> ExecutedShellCommands { get; } = new();

    /// <inheritdoc />
    public Task<int> ConnectAsync(int port, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(41);
    }

    /// <inheritdoc />
    public Task<string> ShellAsync(string command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExecutedShellCommands.Add(command);
        string result = ShellHandler is not null ? ShellHandler(command) : string.Empty;
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<bool> IsRootAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    /// <inheritdoc />
    public async Task PushAsync(string localPath, string remotePath, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        PushHandler?.Invoke(localPath, remotePath, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public async Task PullAsync(string remotePath, string localPath, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        PullHandler?.Invoke(remotePath, localPath, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public Task<AdbRemoteFileInfo?> StatAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AdbRemoteFileInfo? info = StatHandler is not null ? StatHandler(remotePath) : null;
        return Task.FromResult(info);
    }

    /// <inheritdoc />
    public async Task PushAsync(string localPath, string remotePath, IProgress<long>? progress, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        PushHandler?.Invoke(localPath, remotePath, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public async Task PullAsync(string remotePath, string localPath, IProgress<long>? progress, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        PullHandler?.Invoke(remotePath, localPath, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
