using XBear.Core.Diagnostics;

namespace XBear.Core.Tests.Diagnostics;

/// <summary>
/// 应用程序路径解析单元测试。
/// </summary>
public sealed class AppPathsTests
{
    [Fact]
    public void ResolveDataRootDefaultsToLocalAppData()
    {
        string original = Environment.GetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable) ?? string.Empty;
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, null);

            string dataRoot = AppPaths.ResolveDataRoot();
            Assert.False(string.IsNullOrWhiteSpace(dataRoot));
            Assert.Contains(AppPaths.AppDataLeafName, dataRoot);
            Assert.EndsWith(AppPaths.AppDataLeafName, dataRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                AppPaths.DataRootEnvironmentVariable,
                string.IsNullOrEmpty(original) ? null : original);
        }
    }

    [Fact]
    public void ResolveDataRootHonorsEnvironmentVariable()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "xbear-test-data-" + Guid.NewGuid().ToString("N"));
        string original = Environment.GetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable) ?? string.Empty;
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, tempDir);

            string dataRoot = AppPaths.ResolveDataRoot();
            Assert.Equal(Path.GetFullPath(tempDir), dataRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                AppPaths.DataRootEnvironmentVariable,
                string.IsNullOrEmpty(original) ? null : original);
        }
    }

    [Fact]
    public void ResolveInstancesAndLogRootAreUnderDataRoot()
    {
        string dataRoot = AppPaths.ResolveDataRoot();
        string instancesRoot = AppPaths.ResolveInstancesRoot();
        string logRoot = AppPaths.ResolveLogRoot();
        string crashLog = AppPaths.ResolveCrashLogPath();

        Assert.Equal(Path.Combine(dataRoot, AppPaths.InstancesDirectoryName), instancesRoot);
        Assert.Equal(Path.Combine(dataRoot, AppPaths.LogsDirectoryName), logRoot);
        Assert.Equal(Path.Combine(logRoot, AppPaths.CrashLogFileName), crashLog);
    }

    [Fact]
    public void BuildPreferredQemuDirectoriesIncludesBundledDirectory()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "xbear-app-test-" + Guid.NewGuid().ToString("N"));
        IReadOnlyList<string> dirs = AppPaths.BuildPreferredQemuDirectories(baseDir);

        Assert.NotEmpty(dirs);
        Assert.Contains(Path.Combine(baseDir, AppPaths.BundledQemuDirectoryName), dirs);
    }

    [Fact]
    public void BuildPreferredAdbDirectoriesIncludesBundledDirectory()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "xbear-app-test-" + Guid.NewGuid().ToString("N"));
        IReadOnlyList<string> dirs = AppPaths.BuildPreferredAdbDirectories(baseDir);

        Assert.NotEmpty(dirs);
        Assert.Contains(Path.Combine(baseDir, AppPaths.BundledAdbDirectoryName), dirs);
    }
}