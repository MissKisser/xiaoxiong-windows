using XBear.Core.Diagnostics;
using XBear.Core.Qemu;

namespace XBear.Core.Tests.Qemu;

/// <summary>QEMU 二进制定位行为的单元测试。</summary>
public sealed class QemuPathsTests
{
    [Fact]
    public void 全部候选目录缺失时不抛异常且状态可查询()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), "xbear-missing-" + Guid.NewGuid().ToString("N"));

        var paths = new QemuPaths(missingDirectory, Array.Empty<string>());

        // 装机场景下 QEMU 本就可能缺席，定位失败只记录状态，不得让构造抛出。
        Assert.False(paths.IsResolved);
        Assert.Equal(string.Empty, paths.InstallDirectory);
        Assert.Equal(string.Empty, paths.QemuSystemPath);
        Assert.Equal(string.Empty, paths.QemuImgPath);
        Assert.Contains(missingDirectory, paths.SearchedDirectories);
    }

    [Fact]
    public void 全部候选目录缺失时取路径给出带处置建议的依赖错误()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), "xbear-missing-" + Guid.NewGuid().ToString("N"));
        var paths = new QemuPaths(missingDirectory, Array.Empty<string>());

        var exception = Assert.Throws<XBearException>(() => paths.RequireSystemExecutablePath());

        Assert.Equal(ErrorCategory.Dependency, exception.Category);
        Assert.False(string.IsNullOrWhiteSpace(exception.Remediation));
        Assert.Contains("qemu", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            Path.GetFileName(missingDirectory),
            exception.Message,
            StringComparison.OrdinalIgnoreCase);

        // 磁盘镜像工具走同一条定位结论，诊断信息同样可执行。
        var imageException = Assert.Throws<XBearException>(() => paths.RequireImageExecutablePath());
        Assert.Equal(ErrorCategory.Dependency, imageException.Category);
        Assert.False(string.IsNullOrWhiteSpace(imageException.Remediation));
    }

    [Fact]
    public void 命中候选目录时返回两个可执行文件路径()
    {
        var installDirectory = CreateFakeInstallDirectory("xbear-qemu-");

        try
        {
            var paths = new QemuPaths(installDirectory, Array.Empty<string>());

            Assert.True(paths.IsResolved);
            Assert.Equal(Path.GetFullPath(installDirectory), Path.GetFullPath(paths.InstallDirectory));
            Assert.Equal(
                Path.Combine(installDirectory, QemuPaths.SystemExecutableName),
                paths.QemuSystemPath);
            Assert.Equal(
                Path.Combine(installDirectory, QemuPaths.ImageExecutableName),
                paths.QemuImgPath);

            // 已解析时取路径不再报错，调用方无需先判断状态。
            Assert.Equal(paths.QemuSystemPath, paths.RequireSystemExecutablePath());
            Assert.Equal(paths.QemuImgPath, paths.RequireImageExecutablePath());
        }
        finally
        {
            Directory.Delete(installDirectory, recursive: true);
        }
    }

    [Fact]
    public void 候选目录只含其一可执行文件时不算命中()
    {
        var installDirectory = Path.Combine(Path.GetTempPath(), "xbear-qemu-partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDirectory);

        try
        {
            File.WriteAllBytes(Path.Combine(installDirectory, QemuPaths.SystemExecutableName), Array.Empty<byte>());

            var paths = new QemuPaths(installDirectory, Array.Empty<string>());

            Assert.False(paths.IsResolved);
            Assert.Throws<XBearException>(() => paths.RequireImageExecutablePath());
        }
        finally
        {
            Directory.Delete(installDirectory, recursive: true);
        }
    }

    [Fact]
    public void 显式目录优先于其后的候选目录()
    {
        var first = CreateFakeInstallDirectory("xbear-qemu-first-");
        var second = CreateFakeInstallDirectory("xbear-qemu-second-");

        try
        {
            var paths = new QemuPaths(first, new[] { second });

            Assert.True(paths.IsResolved);
            Assert.Equal(Path.GetFullPath(first), Path.GetFullPath(paths.InstallDirectory));
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void 首个候选目录缺可执行文件时向后继续探测()
    {
        var empty = Path.Combine(Path.GetTempPath(), "xbear-qemu-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        var populated = CreateFakeInstallDirectory("xbear-qemu-populated-");

        try
        {
            var paths = new QemuPaths(null, new[] { empty, populated });

            Assert.True(paths.IsResolved);
            Assert.Equal(Path.GetFullPath(populated), Path.GetFullPath(paths.InstallDirectory));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
            Directory.Delete(populated, recursive: true);
        }
    }

    /// <summary>造一个内含两个占位可执行文件的 QEMU 安装目录。</summary>
    /// <param name="prefix">目录名前缀。</param>
    /// <returns>目录绝对路径。</returns>
    private static string CreateFakeInstallDirectory(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, QemuPaths.SystemExecutableName), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(directory, QemuPaths.ImageExecutableName), Array.Empty<byte>());
        return directory;
    }
}
