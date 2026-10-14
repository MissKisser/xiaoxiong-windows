using System.IO;
using System.Text;
using System.Windows;
using XBear.Core.Diagnostics;

namespace XBear.App.Services;

/// <summary>
/// 崩溃日志记录器与用户提示。
/// 集中接管 DispatcherUnhandledException、TaskScheduler.UnobservedTaskException 与
/// AppDomain.UnhandledException 的全局兜底，写入带有构建标识与时间戳的日志，
/// 并执行简单轮转防止日志无界增长；记录后向用户呈现友好且可见的错误提示。
/// </summary>
public sealed class CrashLogger
{
    /// <summary>默认日志单文件字节上限（2 MB），超过时触发文件轮转。</summary>
    public const long DefaultMaxFileSizeBytes = 2 * 1024 * 1024;

    /// <summary>保留的历史轮转文件数上限。</summary>
    public const int DefaultMaxBackupFiles = 3;

    /// <summary>崩溃日志默认文件名。</summary>
    public const string LogFileName = AppPaths.CrashLogFileName;

    private readonly string _logDirectory;
    private readonly long _maxFileSizeBytes;
    private readonly int _maxBackupFiles;
    private readonly Func<string> _buildIdProvider;
    private readonly object _gate = new();

    /// <summary>
    /// 使用默认配置构造崩溃日志器。
    /// 默认日志目录为 <see cref="AppPaths.ResolveLogRoot()"/>。
    /// </summary>
    public CrashLogger()
        : this(AppPaths.ResolveLogRoot(), DefaultMaxFileSizeBytes, DefaultMaxBackupFiles, () => BuildInfo.BuildId)
    {
    }

    /// <summary>
    /// 使用指定参数构造崩溃日志器，便于单测在隔离目录下验证轮转与写入逻辑。
    /// </summary>
    /// <param name="logDirectory">日志输出目录绝对路径。</param>
    /// <param name="maxFileSizeBytes">单个日志文件的字节上限，超过时触发轮转。</param>
    /// <param name="maxBackupFiles">历史备份文件的最大保留个数。</param>
    /// <param name="buildIdProvider">构建标识提供函数，为空时使用 <see cref="BuildInfo.BuildId"/>。</param>
    public CrashLogger(
        string logDirectory,
        long maxFileSizeBytes = DefaultMaxFileSizeBytes,
        int maxBackupFiles = DefaultMaxBackupFiles,
        Func<string>? buildIdProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        if (maxFileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileSizeBytes), "单文件字节上限必须大于零。");
        }

        if (maxBackupFiles < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBackupFiles), "备份保留个数不能为负。");
        }

        _logDirectory = logDirectory;
        _maxFileSizeBytes = maxFileSizeBytes;
        _maxBackupFiles = maxBackupFiles;
        _buildIdProvider = buildIdProvider ?? (() => BuildInfo.BuildId);
    }

    /// <summary>日志输出目录。</summary>
    public string LogDirectory => _logDirectory;

    /// <summary>主崩溃日志文件绝对路径。</summary>
    public string LogFilePath => Path.Combine(_logDirectory, LogFileName);

    /// <summary>单文件字节上限。</summary>
    public long MaxFileSizeBytes => _maxFileSizeBytes;

    /// <summary>最大备份保留数量。</summary>
    public int MaxBackupFiles => _maxBackupFiles;

    /// <summary>
    /// 格式化崩溃日志正文，便于离线核对或单测断言。
    /// </summary>
    /// <param name="exception">异常对象，可为空。</param>
    /// <param name="source">异常来源，例如来源事件名。</param>
    /// <param name="timestamp">发生时间戳，为空时使用当前时间。</param>
    /// <returns>格式化后的日志正文。</returns>
    public string FormatCrashReport(
        Exception? exception,
        string source = "UnhandledException",
        DateTimeOffset? timestamp = null)
    {
        DateTimeOffset now = timestamp ?? DateTimeOffset.Now;
        string buildId = _buildIdProvider();

        var builder = new StringBuilder();
        builder.AppendLine("================================================================================");
        builder.AppendLine($"[CRASH] {now:yyyy-MM-dd HH:mm:ss.fff zzz}");
        builder.AppendLine($"Source:     {source}");
        builder.AppendLine($"BuildId:    {buildId}");
        builder.AppendLine($"OS:         {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
        builder.AppendLine($"Runtime:    .NET {Environment.Version} (Process: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")})");
        builder.AppendLine($"Processors: {Environment.ProcessorCount}");
        builder.AppendLine("--------------------------------------------------------------------------------");

        if (exception is null)
        {
            builder.AppendLine("（未提供异常对象）");
        }
        else
        {
            builder.AppendLine(exception.ToString());
        }

        builder.AppendLine("================================================================================");
        builder.AppendLine();
        return builder.ToString();
    }

    /// <summary>
    /// 将未处理异常写入日志文件并按需执行轮转。写入失败不抛异常，避免阻断退栈或引发二次崩溃。
    /// </summary>
    /// <param name="exception">待记录的异常。</param>
    /// <param name="source">异常来源名。</param>
    /// <returns>写入成功时返回日志文件绝对路径，失败返回 null。</returns>
    public string? LogException(Exception? exception, string source = "UnhandledException")
    {
        if (exception is null)
        {
            return null;
        }

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_logDirectory);
                RotateIfNeededLocked();

                string content = FormatCrashReport(exception, source);
                File.AppendAllText(LogFilePath, content, Encoding.UTF8);
                return LogFilePath;
            }
        }
        catch
        {
            // 日志落盘为兜底路径，任何写入错误均不得二次抛出。
            return null;
        }
    }

    /// <summary>
    /// 检查并执行日志文件轮转。当主日志文件大小超出设定阈值时，
    /// 依次将 crash.log.{N-1} 移到 crash.log.{N}，并将 crash.log 移为 crash.log.1。
    /// </summary>
    private void RotateIfNeededLocked()
    {
        if (!File.Exists(LogFilePath))
        {
            return;
        }

        try
        {
            var fileInfo = new FileInfo(LogFilePath);
            if (fileInfo.Length < _maxFileSizeBytes)
            {
                return;
            }

            if (_maxBackupFiles <= 0)
            {
                File.Delete(LogFilePath);
                return;
            }

            string oldestBackup = GetBackupFilePath(_maxBackupFiles);
            if (File.Exists(oldestBackup))
            {
                File.Delete(oldestBackup);
            }

            for (int i = _maxBackupFiles - 1; i >= 1; i--)
            {
                string source = GetBackupFilePath(i);
                string destination = GetBackupFilePath(i + 1);
                if (File.Exists(source))
                {
                    File.Move(source, destination, overwrite: true);
                }
            }

            string firstBackup = GetBackupFilePath(1);
            File.Move(LogFilePath, firstBackup, overwrite: true);
        }
        catch
        {
            // 轮转失败时允许继续追加到现有文件，不阻断本次崩溃信息的落盘。
        }
    }

    /// <summary>
    /// 获取指定序号的备份日志文件路径。
    /// </summary>
    /// <param name="index">备份序号，从 1 开始。</param>
    /// <returns>备份日志文件绝对路径。</returns>
    public string GetBackupFilePath(int index) =>
        Path.Combine(_logDirectory, $"{LogFileName}.{index}");

    /// <summary>
    /// 向用户展示可见的崩溃错误提示对话框。
    /// </summary>
    /// <param name="exception">崩溃异常。</param>
    /// <param name="hint">附加提示，可为空。</param>
    public void ShowCrashDialog(Exception? exception, string? hint = null)
    {
        string message = BuildUserFacingCrashMessage(exception, hint);
        string title = "小熊模拟器 · 异常退出";

        try
        {
            MessageBox.Show(
                message,
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // 在 WPF 子系统已损坏的极端情况下忽略弹窗失败。
        }
    }

    /// <summary>
    /// 组装面向用户的崩溃文案。
    /// </summary>
    /// <param name="exception">异常对象。</param>
    /// <param name="hint">附加说明。</param>
    /// <returns>面向用户的提示字符串。</returns>
    public string BuildUserFacingCrashMessage(Exception? exception, string? hint = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("小熊模拟器遇到了未处理的严重错误，需要退出。");
        builder.AppendLine();

        if (exception is not null)
        {
            builder.AppendLine($"错误类型：{exception.GetType().Name}");
            builder.AppendLine($"错误说明：{exception.Message}");
        }

        if (!string.IsNullOrWhiteSpace(hint))
        {
            builder.AppendLine();
            builder.AppendLine($"处理建议：{hint}");
        }

        builder.AppendLine();
        builder.AppendLine($"构建标识：{_buildIdProvider()}");
        builder.AppendLine($"崩溃日志：{LogFilePath}");
        return builder.ToString();
    }
}