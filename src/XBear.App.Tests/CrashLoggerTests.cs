using System.IO;
using System.Text;
using XBear.App.Services;

namespace XBear.App.Tests;

/// <summary>
/// 崩溃日志器与轮转逻辑单元测试。
/// </summary>
public sealed class CrashLoggerTests
{
    [Fact]
    public void LogExceptionNullReturnsNullAndDoesNotCreateDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xbear-crash-null-" + Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new CrashLogger(dir, buildIdProvider: () => "0.1.0+test");
            string? result = logger.LogException(null);

            Assert.Null(result);
            Assert.False(Directory.Exists(dir));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void LogExceptionWritesFormattedCrashReportWithBuildId()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xbear-crash-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string expectedBuildId = "0.1.0+build999";
            var logger = new CrashLogger(dir, buildIdProvider: () => expectedBuildId);

            var ex = new InvalidOperationException("模拟致命启动错误");
            string? writtenPath = logger.LogException(ex, "UnitTestSource");

            Assert.NotNull(writtenPath);
            Assert.True(File.Exists(writtenPath));
            Assert.Equal(logger.LogFilePath, writtenPath);

            string content = File.ReadAllText(writtenPath, Encoding.UTF8);
            Assert.Contains("[CRASH]", content);
            Assert.Contains($"BuildId:    {expectedBuildId}", content);
            Assert.Contains("Source:     UnitTestSource", content);
            Assert.Contains("InvalidOperationException", content);
            Assert.Contains("模拟致命启动错误", content);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void LogExceptionRotatesWhenFileExceedsThreshold()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xbear-crash-rotate-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 单文件上限 200 字节，保留 2 份备份
            var logger = new CrashLogger(dir, maxFileSizeBytes: 200, maxBackupFiles: 2, () => "0.1.0+test");

            var ex1 = new Exception("首次崩溃错误信息较长用于撑大日志文件体积以触发阈值");
            logger.LogException(ex1, "First");
            Assert.True(File.Exists(logger.LogFilePath));
            Assert.False(File.Exists(logger.GetBackupFilePath(1)));

            var ex2 = new Exception("第二次崩溃触发轮转");
            logger.LogException(ex2, "Second");
            Assert.True(File.Exists(logger.LogFilePath));
            Assert.True(File.Exists(logger.GetBackupFilePath(1)));

            var ex3 = new Exception("第三次崩溃触发二次轮转");
            logger.LogException(ex3, "Third");
            Assert.True(File.Exists(logger.LogFilePath));
            Assert.True(File.Exists(logger.GetBackupFilePath(1)));
            Assert.True(File.Exists(logger.GetBackupFilePath(2)));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void BuildUserFacingCrashMessageContainsEssentialInfo()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xbear-crash-msg-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string buildId = "0.1.0+userhint";
            var logger = new CrashLogger(dir, buildIdProvider: () => buildId);

            var ex = new ArgumentException("参数非法测试");
            string message = logger.BuildUserFacingCrashMessage(ex, "请重启系统后重试");

            Assert.Contains("参数非法测试", message);
            Assert.Contains("ArgumentException", message);
            Assert.Contains(buildId, message);
            Assert.Contains("请重启系统后重试", message);
            Assert.Contains(logger.LogFilePath, message);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}