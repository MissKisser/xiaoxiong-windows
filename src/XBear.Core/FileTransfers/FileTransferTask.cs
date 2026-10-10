using System.Globalization;
using System.Text.Json;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.Core.FileTransfers;

/// <summary>
/// 传输任务状态机包装器。维护契约字段与三终态状态流转，
/// 阻止非法状态迁移，并保证终态时刻与失败原因的语义自洽。
/// </summary>
public sealed class FileTransferTask
{
    private const int MaxFailureReasonLength = 512;
    private readonly object _sync = new();
    private readonly FileTransferSpec _spec;

    /// <summary>
    /// 初始化处于已受理状态的传输任务。
    /// </summary>
    /// <param name="id">任务标识，同一实例内唯一。</param>
    /// <param name="instanceRef">所属实例标识。</param>
    /// <param name="direction">传输方向。</param>
    /// <param name="sourcePath">规范化后的来源路径。</param>
    /// <param name="targetPath">规范化后的目标路径。</param>
    /// <param name="overwrite">目标已存在同名项时的处置策略。</param>
    /// <param name="recursive">是否为目录递归复制。</param>
    /// <param name="platformConfig">平台特有字段。</param>
    public FileTransferTask(
        string id,
        string instanceRef,
        string direction,
        string sourcePath,
        string targetPath,
        ConflictPolicy overwrite,
        bool recursive = false,
        JsonElement? platformConfig = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        _spec = new FileTransferSpec
        {
            SchemaVersion = "1.0.0",
            Id = id,
            InstanceRef = instanceRef,
            Direction = direction,
            Paths = new FileTransferPaths
            {
                Source = sourcePath,
                Target = targetPath
            },
            Recursive = recursive,
            Overwrite = overwrite,
            State = FileTransferState.Queued,
            CreatedAt = FormatTimestamp(DateTimeOffset.Now),
            PlatformConfig = platformConfig
        };
    }

    /// <summary>任务标识。</summary>
    public string Id => _spec.Id;

    /// <summary>所属实例标识。</summary>
    public string InstanceRef => _spec.InstanceRef;

    /// <summary>传输方向。</summary>
    public string Direction => _spec.Direction;

    /// <summary>来源路径。</summary>
    public string SourcePath => _spec.Paths.Source;

    /// <summary>目标路径。</summary>
    public string TargetPath => _spec.Paths.Target;

    /// <summary>目标已存在同名项时的冲突处置策略。</summary>
    public ConflictPolicy Overwrite => _spec.Overwrite;

    /// <summary>当前状态。</summary>
    public FileTransferState State
    {
        get
        {
            lock (_sync)
            {
                return _spec.State;
            }
        }
    }

    /// <summary>失败原因。失败时非空，取消或进行中时为空。</summary>
    public string? FailureReason
    {
        get
        {
            lock (_sync)
            {
                return _spec.FailureReason;
            }
        }
    }

    /// <summary>当前进度快照。</summary>
    public FileTransferProgress? Progress
    {
        get
        {
            lock (_sync)
            {
                if (_spec.Progress is null)
                {
                    return null;
                }

                return new FileTransferProgress
                {
                    BytesTransferred = _spec.Progress.BytesTransferred,
                    TotalBytes = _spec.Progress.TotalBytes,
                    CurrentEntry = _spec.Progress.CurrentEntry
                };
            }
        }
    }

    /// <summary>受理时刻。</summary>
    public string CreatedAt => _spec.CreatedAt;

    /// <summary>进入传输中的时刻。</summary>
    public string? StartedAt
    {
        get
        {
            lock (_sync)
            {
                return _spec.StartedAt;
            }
        }
    }

    /// <summary>进入终态的时刻。</summary>
    public string? FinishedAt
    {
        get
        {
            lock (_sync)
            {
                return _spec.FinishedAt;
            }
        }
    }

    /// <summary>判断任务是否已进入终态。</summary>
    /// <returns>处于完成、失败或取消终态时返回 true。</returns>
    public bool IsTerminal()
    {
        lock (_sync)
        {
            return _spec.IsTerminal();
        }
    }

    /// <summary>
    /// 将任务推进到传输中状态。
    /// </summary>
    /// <param name="totalBytes">待复制总字节数，未知时置为 null。</param>
    /// <param name="currentEntry">正在处理的条目名。</param>
    /// <exception cref="XBearException">当前状态非已受理时抛出 <see cref="ErrorCategory.State"/>。</exception>
    public void Start(long? totalBytes, string? currentEntry)
    {
        lock (_sync)
        {
            if (_spec.State != FileTransferState.Queued)
            {
                throw new XBearException(
                    ErrorCategory.State,
                    $"传输任务 {Id} 当前状态为 {_spec.State}，无法进入传输中状态。",
                    "仅处于已受理状态的任务允许启动传输。");
            }

            _spec.State = FileTransferState.Running;
            _spec.StartedAt = FormatTimestamp(DateTimeOffset.Now);
            _spec.Progress = new FileTransferProgress
            {
                BytesTransferred = 0,
                TotalBytes = totalBytes,
                CurrentEntry = currentEntry
            };
        }
    }

    /// <summary>
    /// 更新已复制字节数与当前条目，已复制字节数保证单调不减。
    /// </summary>
    /// <param name="transferredBytes">累计已复制字节数。</param>
    /// <param name="currentEntry">当前条目名，为 null 时保持原样。</param>
    /// <exception cref="XBearException">当前状态非传输中时抛出 <see cref="ErrorCategory.State"/>。</exception>
    public void ReportProgress(long transferredBytes, string? currentEntry = null)
    {
        if (transferredBytes < 0)
        {
            return;
        }

        lock (_sync)
        {
            if (IsTerminal())
            {
                return;
            }

            if (_spec.State != FileTransferState.Running)
            {
                throw new XBearException(
                    ErrorCategory.State,
                    $"传输任务 {Id} 当前状态为 {_spec.State}，无法上报传输进度。",
                    "仅在传输进行中时允许更新进度。");
            }

            if (_spec.Progress is null)
            {
                _spec.Progress = new FileTransferProgress
                {
                    BytesTransferred = transferredBytes,
                    TotalBytes = null,
                    CurrentEntry = currentEntry
                };
            }
            else
            {
                long current = _spec.Progress.BytesTransferred ?? 0;
                if (transferredBytes > current)
                {
                    _spec.Progress.BytesTransferred = transferredBytes;
                }

                if (currentEntry is not null)
                {
                    _spec.Progress.CurrentEntry = currentEntry;
                }
            }
        }
    }

    /// <summary>
    /// 将任务推进到已完成终态。
    /// </summary>
    /// <exception cref="XBearException">当前状态非传输中时抛出 <see cref="ErrorCategory.State"/>。</exception>
    public void Complete()
    {
        lock (_sync)
        {
            if (_spec.State != FileTransferState.Running)
            {
                throw new XBearException(
                    ErrorCategory.State,
                    $"传输任务 {Id} 当前状态为 {_spec.State}，无法标记为已完成。",
                    "仅处于传输中状态的任务允许完成。");
            }

            _spec.State = FileTransferState.Completed;
            _spec.FinishedAt = FormatTimestamp(DateTimeOffset.Now);
            _spec.FailureReason = null;

            if (_spec.Progress is not null && _spec.Progress.TotalBytes.HasValue)
            {
                long total = _spec.Progress.TotalBytes.Value;
                if ((_spec.Progress.BytesTransferred ?? 0) < total)
                {
                    _spec.Progress.BytesTransferred = total;
                }
            }
        }
    }

    /// <summary>
    /// 将任务推进到失败终态。
    /// </summary>
    /// <param name="reason">失败原因，必须给出。</param>
    /// <exception cref="XBearException">当前任务已处于终态时抛出 <see cref="ErrorCategory.State"/>。</exception>
    public void Fail(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_sync)
        {
            if (_spec.IsTerminal())
            {
                throw new XBearException(
                    ErrorCategory.State,
                    $"传输任务 {Id} 已处于终态 {_spec.State}，无法再次迁移到失败。",
                    "已处于终态的任务不允许重复流转。");
            }

            _spec.State = FileTransferState.Failed;
            _spec.FinishedAt = FormatTimestamp(DateTimeOffset.Now);
            _spec.FailureReason = ClampReason(reason);
        }
    }

    /// <summary>
    /// 将任务推进到已取消终态。
    /// </summary>
    /// <exception cref="XBearException">当前任务已处于终态时抛出 <see cref="ErrorCategory.State"/>。</exception>
    public void Cancel()
    {
        lock (_sync)
        {
            if (_spec.IsTerminal())
            {
                throw new XBearException(
                    ErrorCategory.State,
                    $"传输任务 {Id} 已处于终态 {_spec.State}，无法再次迁移到取消。",
                    "已处于终态的任务不允许重复流转。");
            }

            _spec.State = FileTransferState.Cancelled;
            _spec.FinishedAt = FormatTimestamp(DateTimeOffset.Now);
            _spec.FailureReason = null;
        }
    }

    /// <summary>
    /// 导出当前任务记录的深拷贝契约模型。
    /// </summary>
    /// <returns>符合契约的 <see cref="FileTransferSpec"/> 实例。</returns>
    public FileTransferSpec ToSpec()
    {
        lock (_sync)
        {
            return new FileTransferSpec
            {
                SchemaVersion = _spec.SchemaVersion,
                Id = _spec.Id,
                InstanceRef = _spec.InstanceRef,
                Direction = _spec.Direction,
                Paths = new FileTransferPaths
                {
                    Source = _spec.Paths.Source,
                    Target = _spec.Paths.Target
                },
                Recursive = _spec.Recursive,
                Overwrite = _spec.Overwrite,
                Progress = _spec.Progress is null
                    ? null
                    : new FileTransferProgress
                    {
                        BytesTransferred = _spec.Progress.BytesTransferred,
                        TotalBytes = _spec.Progress.TotalBytes,
                        CurrentEntry = _spec.Progress.CurrentEntry
                    },
                State = _spec.State,
                FailureReason = _spec.FailureReason,
                CreatedAt = _spec.CreatedAt,
                StartedAt = _spec.StartedAt,
                FinishedAt = _spec.FinishedAt,
                PlatformConfig = _spec.PlatformConfig
            };
        }
    }

    private static string ClampReason(string reason)
    {
        string trimmed = reason.Trim();
        return trimmed.Length <= MaxFailureReasonLength ? trimmed : trimmed[..MaxFailureReasonLength];
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
}
