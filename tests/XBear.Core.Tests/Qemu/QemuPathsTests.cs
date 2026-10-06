using XBear.Core.Diagnostics;
using XBear.Core.Qemu;

namespace XBear.Core.Tests.Qemu;

/// <summary>QEMU 二进制定位行为的单元测试。</summary>
public sealed class QemuPathsTests
{
    [Fact]
    public void 全部候选目录缺失时抛出依赖错误()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), "xbear-missing-" + Guid.NewGuid().ToString("N"));

        var exception = Assert.Throws<XBearException>(() =>
            new QemuPaths(missingDirectory, Array.Empty<string>()));

        Assert.Equal(ErrorCategory.Dependency, exception.Category);
        Assert.False(string.IsNullOrWhiteSpace(exception.Remediation));
        Assert.Contains("qemu", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            Path.GetFileName(missingDirectory),
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 命中候选目录时返回两个可执行文件路径()
    {
        var installDirectory = Path.Combine(Path.GetTempPath(), "xbear-qemu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDirectory);

        try
        {
            File.WriteAllBytes(Path.Combine(installDirectory, QemuPaths.SystemExecutableName), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(installDirectory, QemuPaths.ImageExecutableName), Array.Empty<byte>());

            var paths = new QemuPaths(installDirectory, Array.Empty<string>());

            Assert.Equal(Path.GetFullPath(installDirectory), Path.GetFullPath(paths.InstallDirectory));
            Assert.Equal(
                Path.Combine(installDirectory, QemuPaths.SystemExecutableName),
                paths.QemuSystemPath);
            Assert.Equal(
                Path.Combine(installDirectory, QemuPaths.ImageExecutableName),
                paths.QemuImgPath);
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

            var exception = Assert.Throws<XBearException>(() =>
                new QemuPaths(installDirectory, Array.Empty<string>()));

            Assert.Equal(ErrorCategory.Dependency, exception.Category);
        }
        finally
        {
            Directory.Delete(installDirectory, recursive: true);
        }
    }

    [Fact]
    public void 显式目录优先于其后的候选目录()
    {
        var first = Path.Combine(Path.GetTempPath(), "xbear-qemu-first-" + Guid.NewGuid().ToString("N"));
        var second = Path.Combine(Path.GetTempPath(), "xbear-qemu-second-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        try
        {
            foreach (var directory in new[] { first, second })
            {
                File.WriteAllBytes(Path.Combine(directory, QemuPaths.SystemExecutableName), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(directory, QemuPaths.ImageExecutableName), Array.Empty<byte>());
            }

            var paths = new QemuPaths(first, new[] { second });

            Assert.Equal(Path.GetFullPath(first), Path.GetFullPath(paths.InstallDirectory));
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }
}