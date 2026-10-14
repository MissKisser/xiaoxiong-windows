using System.IO;
using XBear.Core.Diagnostics;
using XBear.Core.Qemu;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 组合根在 QEMU 缺席时的接线测试。
///
/// QEMU 是宿主外部依赖，装机场景下本就可能未安装：
/// 组合根不得因定位失败而崩溃，否则用户只看到闪退而不是可执行的处置建议。
/// 解析结果必须可查询，真正取路径时才抛出带指引的依赖异常。
/// </summary>
public class AppCompositionQemuWiringTests : IDisposable
{
    private readonly TempRoot _temp = new();

    public void Dispose() => _temp.Cleanup();

    /// <summary>
    /// 组合根在 QEMU 未解析时仍能完成装配，并暴露可查询的定位状态。
    /// </summary>
    [Fact]
    public void CompositionSucceedsAndExposesQueryableStateWhenQemuIsAbsent()
    {
        // 显式注入未命中的定位结果，等价于构建机上探测不到 QEMU 的情形。
        QemuPaths absent = new QemuPaths(_temp.New("no-qemu-here"), Array.Empty<string>());
        AppServices services = AppComposition.Create(
            _temp.New("data"),
            _temp.New("images"),
            XBeeSpec.TestSpec(),
            absent);

        Assert.NotNull(services.Manager);
        Assert.NotNull(services.Validator);
        Assert.Same(absent, services.QemuPaths);

        // 定位状态可查询：缺席是合法状态，取路径时才给出带指引的依赖异常。
        Assert.False(services.QemuPaths.IsResolved);
        Assert.NotEmpty(services.QemuPaths.SearchedDirectories);
        Assert.Equal(string.Empty, services.QemuPaths.QemuSystemPath);
        Assert.Equal(string.Empty, services.QemuPaths.QemuImgPath);

        var exception = Assert.Throws<XBearException>(() => services.QemuPaths.RequireSystemExecutablePath());
        Assert.Equal(ErrorCategory.Dependency, exception.Category);
        Assert.False(string.IsNullOrWhiteSpace(exception.Remediation));
    }

    /// <summary>
    /// 组合根在 QEMU 已解析时把定位结果原样交给启动器与镜像管理器。
    /// </summary>
    [Fact]
    public void CompositionPassesTheResolvedPlacementThroughToTheServices()
    {
        QemuPaths present = new QemuPaths(CreatePlacementWithFakeExecutables("qemu"), Array.Empty<string>());

        AppServices services = AppComposition.Create(
            _temp.New("data"),
            _temp.New("images"),
            XBeeSpec.TestSpec(),
            present);

        Assert.Same(present, services.QemuPaths);
        Assert.True(services.QemuPaths.IsResolved);
        Assert.Equal(
            Path.Combine(present.InstallDirectory, QemuPaths.ImageExecutableName),
            services.QemuPaths.RequireImageExecutablePath());
    }

    /// <summary>
    /// 不注入定位结果时按宿主环境探测，命中与否都是合法结果。
    /// </summary>
    [Fact]
    public void CompositionProbesTheHostEnvironmentByDefault()
    {
        AppServices services = AppComposition.Create(
            _temp.New("data"),
            _temp.New("images"),
            XBeeSpec.TestSpec());

        Assert.NotNull(services.QemuPaths);
        Assert.NotEmpty(services.QemuPaths.SearchedDirectories);

        if (services.QemuPaths.IsResolved)
        {
            Assert.NotEqual(string.Empty, services.QemuPaths.QemuSystemPath);
            Assert.NotEqual(string.Empty, services.QemuPaths.QemuImgPath);
        }
        else
        {
            var exception = Assert.Throws<XBearException>(() => services.QemuPaths.RequireSystemExecutablePath());
            Assert.Equal(ErrorCategory.Dependency, exception.Category);
        }
    }

    /// <summary>
    /// 目录内存在两个占位可执行文件时，定位结果为已解析且指向该目录。
    /// </summary>
    [Fact]
    public void PlacementDirectoryWithBothExecutablesResolvesToThatDirectory()
    {
        string installDirectory = CreatePlacementWithFakeExecutables("qemu");

        var paths = new QemuPaths(installDirectory, Array.Empty<string>());

        Assert.True(paths.IsResolved);
        Assert.Equal(Path.GetFullPath(installDirectory), Path.GetFullPath(paths.InstallDirectory));
        Assert.Equal(
            Path.Combine(installDirectory, QemuPaths.ImageExecutableName),
            paths.RequireImageExecutablePath());
    }

    /// <summary>
    /// 目录只含一个可执行文件时不算命中，取路径时给出依赖错误。
    /// </summary>
    [Fact]
    public void PlacementDirectoryWithOnlyOneExecutableIsNotResolved()
    {
        string installDirectory = _temp.New("qemu-partial");
        File.WriteAllBytes(
            Path.Combine(installDirectory, QemuPaths.SystemExecutableName),
            Array.Empty<byte>());

        var paths = new QemuPaths(installDirectory, Array.Empty<string>());

        Assert.False(paths.IsResolved);
        Assert.Equal(ErrorCategory.Dependency, Assert.Throws<XBearException>(
            () => paths.RequireSystemExecutablePath()).Category);
    }

    /// <summary>造一个内含两个占位可执行文件的安装目录。</summary>
    /// <param name="name">目录名。</param>
    /// <returns>目录绝对路径。</returns>
    private string CreatePlacementWithFakeExecutables(string name)
    {
        string installDirectory = _temp.New(name);
        File.WriteAllBytes(
            Path.Combine(installDirectory, QemuPaths.SystemExecutableName),
            Array.Empty<byte>());
        File.WriteAllBytes(
            Path.Combine(installDirectory, QemuPaths.ImageExecutableName),
            Array.Empty<byte>());
        return installDirectory;
    }
}
