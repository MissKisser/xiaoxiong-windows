using XBear.Core.Spec;

namespace XBear.Core.FileTransfers;

/// <summary>
/// 发起文件传输请求的参数承载。
/// </summary>
public sealed class FileTransferRequest
{
    /// <summary>传输方向，取值须为 host-to-instance 或 instance-to-host。</summary>
    public string Direction { get; }

    /// <summary>来源文件路径。</summary>
    public string SourcePath { get; }

    /// <summary>目标文件路径。</summary>
    public string TargetPath { get; }

    /// <summary>目标已存在同名项时的处置策略。</summary>
    public ConflictPolicy Overwrite { get; }

    /// <summary>是否递归传输目录。</summary>
    public bool Recursive { get; }

    /// <summary>写入实例后是否触发 guest 刷盘。</summary>
    public bool Flush { get; }

    /// <summary>显式指定的任务标识，为空时自动生成。</summary>
    public string? TaskId { get; }

    /// <summary>
    /// 使用布尔值覆盖选项初始化传输请求。
    /// </summary>
    /// <param name="direction">传输方向。</param>
    /// <param name="sourcePath">来源路径。</param>
    /// <param name="targetPath">目标路径。</param>
    /// <param name="overwrite">目标存在时是否覆盖（true 表示覆盖，false 表示保留并跳过/拒绝）。</param>
    /// <param name="recursive">是否递归复制目录。</param>
    /// <param name="flush">写入后是否触发 guest 刷盘。</param>
    /// <param name="taskId">自定义任务标识。</param>
    public FileTransferRequest(
        string direction,
        string sourcePath,
        string targetPath,
        bool overwrite,
        bool recursive = false,
        bool flush = true,
        string? taskId = null)
        : this(
            direction,
            sourcePath,
            targetPath,
            overwrite ? ConflictPolicy.Overwrite : ConflictPolicy.Skip,
            recursive,
            flush,
            taskId)
    {
    }

    /// <summary>
    /// 使用强类型冲突策略初始化传输请求。
    /// </summary>
    /// <param name="direction">传输方向。</param>
    /// <param name="sourcePath">来源路径。</param>
    /// <param name="targetPath">目标路径。</param>
    /// <param name="overwrite">目标已存在同名项时的处置策略。</param>
    /// <param name="recursive">是否递归复制目录。</param>
    /// <param name="flush">写入后是否触发 guest 刷盘。</param>
    /// <param name="taskId">自定义任务标识。</param>
    public FileTransferRequest(
        string direction,
        string sourcePath,
        string targetPath,
        ConflictPolicy overwrite = ConflictPolicy.Skip,
        bool recursive = false,
        bool flush = true,
        string? taskId = null)
    {
        Direction = direction;
        SourcePath = sourcePath;
        TargetPath = targetPath;
        Overwrite = overwrite;
        Recursive = recursive;
        Flush = flush;
        TaskId = taskId;
    }
}
