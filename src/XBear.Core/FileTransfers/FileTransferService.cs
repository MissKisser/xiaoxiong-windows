using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using XBear.Core.Abstractions;
using XBear.Core.Adb;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.Core.FileTransfers;

/// <summary>
/// 宿主侧文件传输服务：在宿主机与实例之间双向传输文件，
/// 维护强类型任务记录与三终态状态机，遵循规格契约。
/// </summary>
public sealed class FileTransferService
{
    private static readonly Regex TaskIdRegex = new("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$", RegexOptions.Compiled);

    /// <summary>guest 侧执行的数据刷盘命令。</summary>
    public const string GuestFlushCommand = "sync";

    private readonly Func<IAdbClient> _adbClientFactory;
    private readonly TimeSpan _connectTimeout;
    private readonly ConcurrentDictionary<string, FileTransferTask> _tasks = new(StringComparer.Ordinal);

    /// <summary>任务状态发生变更时的事件。</summary>
    public event EventHandler<FileTransferTaskEventArgs>? TaskStateChanged;

    /// <summary>任务传输进度发生更新时的事件。</summary>
    public event EventHandler<FileTransferProgressEventArgs>? ProgressChanged;

    /// <summary>
    /// 初始化文件传输服务。
    /// </summary>
    /// <param name="adbClientFactory">
    /// adb 客户端工厂，为空时使用默认 <see cref="AdbClient"/>。每次操作独立建立并释放连接。
    /// </param>
    /// <param name="connectTimeout">连接与握手的等待上限，为空时使用客户端默认上限。</param>
    public FileTransferService(Func<IAdbClient>? adbClientFactory = null, TimeSpan? connectTimeout = null)
    {
        _adbClientFactory = adbClientFactory ?? (static () => new AdbClient());
        _connectTimeout = connectTimeout ?? AdbClient.DefaultConnectTimeout;
    }

    /// <summary>
    /// 获取指定标识的传输任务。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    /// <returns>找到的任务，不存在时返回 null。</returns>
    public FileTransferTask? GetTask(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return _tasks.TryGetValue(taskId, out FileTransferTask? task) ? task : null;
    }

    /// <summary>
    /// 列出所有已记录的传输任务，可选按实例过滤。
    /// </summary>
    /// <param name="instanceRef">所属实例标识，为空时返回所有任务。</param>
    /// <returns>传输任务列表。</returns>
    public IReadOnlyList<FileTransferTask> ListTasks(string? instanceRef = null)
    {
        IEnumerable<FileTransferTask> values = _tasks.Values;
        if (!string.IsNullOrWhiteSpace(instanceRef))
        {
            values = values.Where(t => string.Equals(t.InstanceRef, instanceRef, StringComparison.Ordinal));
        }

        return values.OrderBy(t => t.CreatedAt, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// 执行一次双向文件传输。
    /// </summary>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="request">传输请求，包含方向、来源、目标与冲突策略。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>进入终态后的契约记录快照。</returns>
    /// <exception cref="XBearException">
    /// 路径非法时分类为 <see cref="ErrorCategory.Spec"/>；
    /// 文件不存在或冲突策略拒绝时分类为 <see cref="ErrorCategory.Storage"/>；
    /// 通信失败分类为 <see cref="ErrorCategory.Protocol"/>；
    /// 状态冲突分类为 <see cref="ErrorCategory.State"/>。
    /// </exception>
    /// <exception cref="OperationCanceledException">用户主动取消时抛出，任务进入 Cancelled 终态。</exception>
    public async Task<FileTransferSpec> TransferAsync(
        FileTransferTarget target,
        FileTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        ArgumentNullException.ThrowIfNull(request);

        string direction = request.Direction;
        if (direction is not (FileTransferDirections.HostToInstance or FileTransferDirections.InstanceToHost))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"不支持的传输方向：{direction}。",
                "传输方向必须为 host-to-instance 或 instance-to-host。");
        }

        string normalizedSource;
        string normalizedTarget;

        if (direction == FileTransferDirections.HostToInstance)
        {
            normalizedSource = FileTransferPathValidator.NormalizeAndValidateHostPath(request.SourcePath);
            normalizedTarget = FileTransferPathValidator.NormalizeAndValidateGuestPath(request.TargetPath);
        }
        else
        {
            normalizedSource = FileTransferPathValidator.NormalizeAndValidateGuestPath(request.SourcePath);
            normalizedTarget = FileTransferPathValidator.NormalizeAndValidateHostPath(request.TargetPath);
        }

        string taskId = ResolveTaskId(request.TaskId, target.InstanceId);
        if (_tasks.ContainsKey(taskId))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"传输任务标识 {taskId} 已存在，契约禁止复用任务标识。",
                "请使用全局唯一的任务标识，或省略任务标识以自动生成。");
        }

        JsonElement platformConfig = JsonSerializer.SerializeToElement(new
        {
            channel = "adb",
            adbPort = target.AdbPort
        });

        var task = new FileTransferTask(
            taskId,
            target.InstanceId,
            direction,
            normalizedSource,
            normalizedTarget,
            request.Overwrite,
            request.Recursive,
            platformConfig);

        if (!_tasks.TryAdd(taskId, task))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"传输任务标识 {taskId} 已存在。",
                "任务标识不得复用。");
        }

        NotifyStateChanged(task, FileTransferState.Queued, FileTransferState.Queued);

        if (cancellationToken.IsCancellationRequested)
        {
            task.Cancel();
            NotifyStateChanged(task, FileTransferState.Queued, FileTransferState.Cancelled);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (direction == FileTransferDirections.HostToInstance)
        {
            return await ExecuteHostToInstanceAsync(target, task, request, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ExecuteInstanceToHostAsync(target, task, request, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 便捷重载：执行一次双向文件传输。
    /// </summary>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="sourcePath">来源路径。</param>
    /// <param name="targetPath">目标路径。</param>
    /// <param name="direction">传输方向。</param>
    /// <param name="overwrite">目标存在时是否覆盖。</param>
    /// <param name="flush">写入后是否触发 guest 刷盘。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>进入终态后的契约记录快照。</returns>
    public Task<FileTransferSpec> TransferAsync(
        FileTransferTarget target,
        string sourcePath,
        string targetPath,
        string direction,
        bool overwrite = false,
        bool flush = true,
        CancellationToken cancellationToken = default)
    {
        var request = new FileTransferRequest(
            direction,
            sourcePath,
            targetPath,
            overwrite,
            recursive: false,
            flush: flush);

        return TransferAsync(target, request, cancellationToken);
    }

    private async Task<FileTransferSpec> ExecuteHostToInstanceAsync(
        FileTransferTarget target,
        FileTransferTask task,
        FileTransferRequest request,
        CancellationToken cancellationToken)
    {
        string hostSource = task.SourcePath;
        string guestTarget = task.TargetPath;

        if (!File.Exists(hostSource))
        {
            string reason = $"宿主侧源文件 {hostSource} 不存在。";
            task.Fail(reason);
            NotifyStateChanged(task, FileTransferState.Queued, FileTransferState.Failed);
            throw new XBearException(ErrorCategory.Storage, reason, "请确认宿主侧源文件存在且路径正确。");
        }

        // 传输前预检实例侧目标文件冲突
        bool targetExists = await RemotePathExistsAsync(target, guestTarget, cancellationToken).ConfigureAwait(false);
        if (targetExists && request.Overwrite != ConflictPolicy.Overwrite)
        {
            string reason = $"实例侧目标文件 {guestTarget} 已存在，且冲突策略为 {request.Overwrite}，已拒绝覆盖。";
            task.Fail(reason);
            NotifyStateChanged(task, FileTransferState.Queued, FileTransferState.Failed);
            throw new XBearException(
                ErrorCategory.Storage,
                reason,
                "若需覆盖目标文件，请在传输请求中将冲突策略设置为 overwrite。");
        }

        var fileInfo = new FileInfo(hostSource);
        long totalBytes = fileInfo.Length;
        string currentEntry = Path.GetFileName(guestTarget);

        task.Start(totalBytes, currentEntry);
        NotifyStateChanged(task, FileTransferState.Queued, FileTransferState.Running);

        var progressHandler = OrderedProgress<long>.Create(transferred =>
        {
            task.ReportProgress(transferred, currentEntry);
            NotifyProgress(task);
        });

        try
        {
            await ExecuteAdbAsync(
                target,
                async client =>
                {
                    if (client is IAdbFileTransferClient ftClient)
                    {
                        await ftClient.PushAsync(hostSource, guestTarget, progressHandler, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await client.PushAsync(hostSource, guestTarget, cancellationToken).ConfigureAwait(false);
                        progressHandler.Report(totalBytes);
                    }

                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            if (request.Flush)
            {
                await ExecuteAdbAsync(
                    target,
                    async client =>
                    {
                        await client.ShellAsync(GuestFlushCommand, cancellationToken).ConfigureAwait(false);
                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            task.Complete();
            NotifyStateChanged(task, FileTransferState.Running, FileTransferState.Completed);
            return task.ToSpec();
        }
        catch (Exception ex) when (ex is OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            task.Cancel();
            NotifyStateChanged(task, FileTransferState.Running, FileTransferState.Cancelled);
            await TryCleanupGuestFileAsync(target, guestTarget).ConfigureAwait(false);
            throw new OperationCanceledException("推送文件到实例已被取消。", ex, cancellationToken);
        }
        catch (Exception ex)
        {
            string reason = $"推送文件到实例失败：{ex.Message}";
            task.Fail(reason);
            NotifyStateChanged(task, FileTransferState.Running, FileTransferState.Failed);
            await TryCleanupGuestFileAsync(target, guestTarget).ConfigureAwait(false);

            if (ex is XBearException)
            {
                throw;
            }

            throw new XBearException(ErrorCategory.Protocol, reason, null, ex);
        }
    }

    private async Task<FileTransferSpec> ExecuteInstanceToHostAsync(
        FileTransferTarget target,
        FileTransferTask task,
        FileTransferRequest request,
        CancellationToken cancellationToken)
    {
        string guestSource = task.SourcePath;
        string hostTarget = task.TargetPath;

        AdbRemoteFileInfo? stat = null;
        try
        {
            stat = await ExecuteAdbAsync(
                target,
                async client => client is IAdbFileTransferClient ftClient
                    ? await ftClient.StatAsync(guestSource, cancellationToken).ConfigureAwait(false)
                    : null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            stat = null;
        }

        bool sourceExists = stat is not null || await RemotePathExistsAsync(target, guestSource, cancellationToken).ConfigureAwait(false);
        if (!sourceExists)
        {
            string reason = $"实例侧源文件 {guestSource} 不存在。";
            task.Fail(reason);
            NotifyStateChanged(task, FileTransferState.Queued, FileTransferState.Failed);
            throw new XBearException(ErrorCategory.Storage, reason, "请确认实例侧源文件存在且路径正确。");
        }

        // 传输前预检宿主侧目标文件冲突
        if (File.Exists(hostTarget) && request.Overwrite != ConflictPolicy.Overwrite)
        {
            string reason = $"宿主侧目标文件 {hostTarget} 已存在，且冲突策略为 {request.Overwrite}，已拒绝覆盖。";
            task.Fail(reason);
            NotifyStateChanged(task, FileTransferState.Queued, FileTransferState.Failed);
            throw new XBearException(
                ErrorCategory.Storage,
                reason,
                "若需覆盖目标文件，请在传输请求中将冲突策略设置为 overwrite。");
        }

        // 实例侧源文件大小：若 STAT 可知则填入，取不到保持 null，不伪造
        long? totalBytes = stat is not null && stat.Size >= 0 ? stat.Size : null;
        string currentEntry = Path.GetFileName(hostTarget);

        task.Start(totalBytes, currentEntry);
        NotifyStateChanged(task, FileTransferState.Queued, FileTransferState.Running);

        string? hostDir = Path.GetDirectoryName(hostTarget);
        if (!string.IsNullOrEmpty(hostDir) && !Directory.Exists(hostDir))
        {
            Directory.CreateDirectory(hostDir);
        }

        var progressHandler = OrderedProgress<long>.Create(transferred =>
        {
            task.ReportProgress(transferred, currentEntry);
            NotifyProgress(task);
        });

        try
        {
            await ExecuteAdbAsync(
                target,
                async client =>
                {
                    if (client is IAdbFileTransferClient ftClient2)
                    {
                        await ftClient2.PullAsync(guestSource, hostTarget, progressHandler, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await client.PullAsync(guestSource, hostTarget, cancellationToken).ConfigureAwait(false);
                        if (totalBytes.HasValue)
                        {
                            progressHandler.Report(totalBytes.Value);
                        }
                    }

                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            task.Complete();
            NotifyStateChanged(task, FileTransferState.Running, FileTransferState.Completed);
            return task.ToSpec();
        }
        catch (Exception ex) when (ex is OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            task.Cancel();
            NotifyStateChanged(task, FileTransferState.Running, FileTransferState.Cancelled);
            TryCleanupHostFile(hostTarget);
            throw new OperationCanceledException("从实例拉取文件已被取消。", ex, cancellationToken);
        }
        catch (Exception ex)
        {
            string reason = $"从实例拉取文件失败：{ex.Message}";
            task.Fail(reason);
            NotifyStateChanged(task, FileTransferState.Running, FileTransferState.Failed);
            TryCleanupHostFile(hostTarget);

            if (ex is XBearException)
            {
                throw;
            }

            throw new XBearException(ErrorCategory.Protocol, reason, null, ex);
        }
    }

    private async Task<bool> RemotePathExistsAsync(
        FileTransferTarget target,
        string remotePath,
        CancellationToken cancellationToken)
    {
        // 优先通过独立的 sync STAT 判定
        try
        {
            AdbRemoteFileInfo? info = await ExecuteAdbAsync(
                target,
                async client => client is IAdbFileTransferClient ftClient
                    ? await ftClient.StatAsync(remotePath, cancellationToken).ConfigureAwait(false)
                    : null,
                cancellationToken).ConfigureAwait(false);

            return info is not null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // STAT 异常或不支持时退回 shell 探测
        }

        // 退回独立连接执行 shell test -e 探测
        try
        {
            return await ExecuteAdbAsync(
                target,
                async client =>
                {
                    await client.ShellAsync($"test -e \"{remotePath}\"", cancellationToken).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<T> ExecuteAdbAsync<T>(
        FileTransferTarget target,
        Func<IAdbClient, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await using IAdbClient client = _adbClientFactory();
        try
        {
            await client.ConnectAsync(target.AdbPort, cancellationToken, _connectTimeout).ConfigureAwait(false);
            return await action(client).ConfigureAwait(false);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException("传输操作已被取消。", ex, cancellationToken);
        }
    }

    private async Task TryCleanupGuestFileAsync(FileTransferTarget target, string remotePath)
    {
        try
        {
            await ExecuteAdbAsync(
                target,
                async client =>
                {
                    await client.ShellAsync($"rm -f \"{remotePath}\"", CancellationToken.None).ConfigureAwait(false);
                    return true;
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 清理半成品属于最佳努力，不掩盖原始异常
        }
    }

    private static void TryCleanupHostFile(string localPath)
    {
        try
        {
            if (File.Exists(localPath))
            {
                File.Delete(localPath);
            }
        }
        catch
        {
            // 清理半成品属于最佳努力，不掩盖原始异常
        }
    }

    /// <summary>
    /// 保序的进度接收端：存在同步上下文时按上下文队列保序派发，
    /// 不存在时直接在报告线程上回调。
    /// 直接使用 <see cref="Progress{T}"/> 会把每一次报告独立投递给线程池，
    /// 各次回调的执行次序不保证，接收端会看到字节数回退。
    /// </summary>
    /// <typeparam name="T">进度值的类型。</typeparam>
    private sealed class OrderedProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        private readonly SynchronizationContext? _context;

        private OrderedProgress(Action<T> handler, SynchronizationContext? context)
        {
            _handler = handler;
            _context = context;
        }

        /// <summary>
        /// 创建保序进度接收端。
        /// </summary>
        /// <param name="handler">进度回调。</param>
        /// <returns>保序进度接收端。</returns>
        public static IProgress<T> Create(Action<T> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            return new OrderedProgress<T>(handler, SynchronizationContext.Current);
        }

        /// <summary>
        /// 保序派发时携带的进度值。
        /// </summary>
        /// <param name="Handler">进度回调。</param>
        /// <param name="Value">进度值。</param>
        private sealed record ProgressPayload(Action<T> Handler, T Value);

        /// <summary>
        /// 报告一次进度。
        /// </summary>
        /// <param name="value">进度值。</param>
        public void Report(T value)
        {
            if (_context is null)
            {
                _handler(value);
                return;
            }

            var payload = new ProgressPayload(_handler, value);
            _context.Post(
                static state =>
                {
                    if (state is ProgressPayload typed)
                    {
                        typed.Handler(typed.Value);
                    }
                },
                payload);
        }
    }

    private void NotifyStateChanged(FileTransferTask task, FileTransferState prev, FileTransferState current)
    {
        TaskStateChanged?.Invoke(this, new FileTransferTaskEventArgs(task, prev, current));
    }

    private void NotifyProgress(FileTransferTask task)
    {
        FileTransferProgress? progress = task.Progress;
        if (progress is not null)
        {
            ProgressChanged?.Invoke(this, new FileTransferProgressEventArgs(
                task.Id,
                progress.BytesTransferred ?? 0,
                progress.TotalBytes,
                progress.CurrentEntry));
        }
    }

    private static void ValidateTarget(FileTransferTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.InstanceId);

        if (target.AdbPort is < 1 or > 65535)
        {
            throw new XBearException(
                ErrorCategory.Port,
                $"实例 {target.InstanceId} 的 adb 端口 {target.AdbPort} 不是合法端口。",
                "请检查实例端口映射配置。");
        }
    }

    private static string ResolveTaskId(string? customId, string instanceId)
    {
        if (!string.IsNullOrWhiteSpace(customId))
        {
            string trimmed = customId.Trim();
            if (!TaskIdRegex.IsMatch(trimmed))
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"传输任务标识 {trimmed} 不符合契约规范（须为小写字母、数字与连字符，长 2-64 位）。",
                    "请提供符合契约规范的任务标识。");
            }

            return trimmed;
        }

        string cleanInstance = instanceId.ToLowerInvariant().Replace('_', '-');
        string random = Guid.NewGuid().ToString("N");
        string candidate = $"ft-{cleanInstance}-{random}";
        return candidate.Length <= 64 ? candidate : candidate[..64];
    }
}
