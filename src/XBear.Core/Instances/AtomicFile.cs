namespace XBear.Core.Instances;

/// <summary>
/// 原子写入工具。先把内容写入同目录下的临时文件，再以替换方式落到目标路径，
/// 保证进程崩溃时目标文件要么是旧内容要么是完整新内容，不会留下半个文件。
/// </summary>
internal static class AtomicFile
{
    /// <summary>
    /// 以原子方式把文本写入目标文件。
    /// </summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="content">待写入的文本内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 临时文件与目标同目录，确保替换是同一卷上的原子操作。
        string tempPath = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清理临时文件失败不应掩盖原始异常。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}