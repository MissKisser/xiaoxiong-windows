using XBear.Core.Spec;

namespace XBear.Core.FileTransfers;

/// <summary>传输任务状态变化事件参数。</summary>
/// <param name="Task">当前任务对象。</param>
/// <param name="PreviousState">变化前的状态。</param>
/// <param name="CurrentState">变化后的状态。</param>
public sealed record FileTransferTaskEventArgs(
    FileTransferTask Task,
    FileTransferState PreviousState,
    FileTransferState CurrentState);

/// <summary>传输进度变化事件参数。</summary>
/// <param name="TaskId">任务标识。</param>
/// <param name="BytesTransferred">已复制字节数。</param>
/// <param name="TotalBytes">总字节数（总量未知时为 null）。</param>
/// <param name="CurrentEntry">正在处理的条目名。</param>
public sealed record FileTransferProgressEventArgs(
    string TaskId,
    long BytesTransferred,
    long? TotalBytes,
    string? CurrentEntry);
