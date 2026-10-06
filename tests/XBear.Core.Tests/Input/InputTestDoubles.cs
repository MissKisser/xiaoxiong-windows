using System.Text.Json;
using XBear.Core.Abstractions;

namespace XBear.Core.Tests.Input;

/// <summary>记录全部 QMP 调用的客户端替身，可按需让指定命令失败。</summary>
internal sealed class RecordingQmpClient : IQmpClient
{
    /// <summary>命令名与其参数对象。</summary>
    public List<(string Command, object? Arguments)> Calls { get; } = new();

    /// <summary>连接次数，重复连接视为错误。</summary>
    public int ConnectCount { get; private set; }

    /// <summary>执行命令时抛出的异常，为 null 表示成功。</summary>
    public Func<string, Exception?>? FailureSelector { get; set; }

    /// <summary>连接时抛出的异常。</summary>
    public Exception? ConnectFailure { get; set; }

    /// <summary>只统计指定命令名的调用次数。</summary>
    /// <param name="command">命令名。</param>
    /// <returns>该命令被调用的次数。</returns>
    public int CountOf(string command) => Calls.Count(call => call.Command == command);

    /// <summary>截图命令的落盘回调，为空表示不产生文件。</summary>
    public Action<string>? ScreendumpWriter { get; set; }

    /// <summary>取指定命令的参数对象。</summary>
    /// <param name="index">同一命令的第几次调用。</param>
    /// <returns>参数对象。</returns>
    public object? ArgumentsOf(int index) => Calls[index].Arguments;

    /// <summary>
    /// 完成握手。
    /// </summary>
    /// <param name="port">QMP 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>空能力集。</returns>
    public Task<IReadOnlySet<string>> ConnectAsync(int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ConnectFailure is not null)
        {
            throw ConnectFailure;
        }

        ConnectCount++;
        return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    /// <summary>
    /// 记录命令调用。
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
        cancellationToken.ThrowIfCancellationRequested();

        Calls.Add((command, arguments));

        if (command == "screendump" && arguments is Dictionary<string, object> payload &&
            payload.TryGetValue("filename", out object? filename) && filename is string path)
        {
            ScreendumpWriter?.Invoke(path);
        }

        Exception? failure = FailureSelector?.Invoke(command);
        return failure is null
            ? Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone())
            : throw failure;
    }

    /// <summary>
    /// 返回运行中。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true。</returns>
    public Task<bool> QueryRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    /// <summary>
    /// 返回停止请求成功。
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

/// <summary>记录全部 shell 调用的 adb 客户端替身，可按需让注入失败。</summary>
internal sealed class RecordingAdbClient : IAdbClient
{
    /// <summary>执行过的 shell 命令串。</summary>
    public List<string> ShellCommands { get; } = new();

    /// <summary>实例是否具备注入所需权限。</summary>
    public bool IsRoot { get; set; } = true;

    /// <summary>shell 执行时抛出的异常。</summary>
    public Exception? ShellFailure { get; set; }

    /// <summary>连接时抛出的异常。</summary>
    public Exception? ConnectFailure { get; set; }

    /// <summary>连接次数，重复连接视为错误。</summary>
    public int ConnectCount { get; private set; }

    /// <summary>
    /// 完成握手。
    /// </summary>
    /// <param name="port">adb 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>协议版本 41。</returns>
    public Task<int> ConnectAsync(int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ConnectFailure is not null)
        {
            throw ConnectFailure;
        }

        ConnectCount++;
        return Task.FromResult(41);
    }

    /// <summary>
    /// 记录 shell 命令。
    /// </summary>
    /// <param name="command">命令串。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>空输出。</returns>
    public Task<string> ShellAsync(string command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ShellCommands.Add(command);
        return ShellFailure is null
            ? Task.FromResult(string.Empty)
            : throw ShellFailure;
    }

    /// <summary>
    /// 返回权限状态。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>权限状态。</returns>
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
