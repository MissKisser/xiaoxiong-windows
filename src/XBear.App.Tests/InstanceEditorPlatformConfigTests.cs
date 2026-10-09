using System.IO;
using XBear.App;
using XBear.App.Presentation;
using XBear.App.Services;
using XBear.App.ViewModels;
using XBear.Core.Abstractions;
using XBear.Core.Identity;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 实例创建视图模型装配测试：验证 platformConfig 自动填充逻辑。
/// 若所选镜像携带引导推荐配置，创建实例时应自动填充 platformConfig 的内核直启参数；
/// 若镜像未携带引导推荐，platformConfig 应保持为 null。
/// </summary>
public class InstanceEditorPlatformConfigTests : IDisposable
{
    private readonly TempRoot _temp = new();

    public void Dispose() => _temp.Cleanup();

    private static SpecLoader SpecLoader => XBeeSpec.TestSpec();

    private (FileInstanceRepository Repository, string DataRoot) CreateRepository()
    {
        string dataRoot = _temp.New("data");
        var repository = new FileInstanceRepository(
            Path.Combine(dataRoot, AppComposition.InstancesDirectoryName),
            new TombstoneStore(Path.Combine(dataRoot, AppComposition.TombstoneFileName)));
        return (repository, dataRoot);
    }

    private static IReadOnlyDictionary<string, ImageSpec> CreateImageCatalog(
        ImageSpec? imageWithBoot,
        ImageSpec? imageWithoutBoot)
    {
        var catalog = new Dictionary<string, ImageSpec>(StringComparer.Ordinal);
        if (imageWithBoot is not null)
            catalog[imageWithBoot.DisplayName] = imageWithBoot;
        if (imageWithoutBoot is not null)
            catalog[imageWithoutBoot.DisplayName] = imageWithoutBoot;
        return catalog;
    }

    private static ImageSpec CreateBootImage(string displayName)
    {
        return new ImageSpec
        {
            DisplayName = displayName,
            Boot = new BootSpec
            {
                Kernel = "kernel",
                Initrd = "initrd.img",
                KernelAppend = "root=/dev/ram0 quiet nomodeset"
            }
        };
    }

    private static ImageSpec CreatePlainImage(string displayName)
    {
        return new ImageSpec { DisplayName = displayName };
    }

    private InstanceEditorViewModel CreateEditor(
        IInstanceRepository repository,
        IReadOnlyDictionary<string, ImageSpec> images)
    {
        var editor = new InstanceEditorViewModel(
            repository,
            images,
            new AuditLog(Path.Combine(_temp.New("audit"), "audit.log")),
            new TerminologyCatalog(SpecLoader.LoadTerminology()))
        {
            DisplayName = "平台装配测试实例",
        };
        return editor;
    }

    [Fact]
    public async Task BuildSpecWithBootImagePopulatesPlatformConfig()
    {
        const string bootImageName = "bliss-os-with-boot";
        (FileInstanceRepository repository, _) = CreateRepository();
        IReadOnlyDictionary<string, ImageSpec> catalog = CreateImageCatalog(
            CreateBootImage(bootImageName), null);

        InstanceEditorViewModel editor = CreateEditor(repository, catalog);
        editor.ImageRef = bootImageName;

        Assert.True(await editor.CreateAsync());

        InstanceSpec saved = Assert.Single(await repository.ListAsync());
        Assert.NotNull(saved.PlatformConfig);

        string expectedImagesRoot = Path.Combine(BaseImageImportService.DefaultImagesRoot, bootImageName);
        Assert.Equal(Path.Combine(expectedImagesRoot, "kernel"), saved.PlatformConfig!.KernelImage);
        Assert.Equal(Path.Combine(expectedImagesRoot, "initrd.img"), saved.PlatformConfig.InitrdImage);
        Assert.Equal("root=/dev/ram0 quiet nomodeset", saved.PlatformConfig.KernelAppend);
    }

    [Fact]
    public async Task BuildSpecWithPlainImageLeavesPlatformConfigNull()
    {
        const string plainImageName = "generic-os-plain";
        (FileInstanceRepository repository, _) = CreateRepository();
        IReadOnlyDictionary<string, ImageSpec> catalog = CreateImageCatalog(
            null, CreatePlainImage(plainImageName));

        InstanceEditorViewModel editor = CreateEditor(repository, catalog);
        editor.ImageRef = plainImageName;

        Assert.True(await editor.CreateAsync());

        InstanceSpec saved = Assert.Single(await repository.ListAsync());
        Assert.Null(saved.PlatformConfig);
    }

    [Fact]
    public async Task BuildSpecWithMixedImagesUsesSelectedImageBoot()
    {
        const string bootImageName = "w-m0-boot-image";
        const string plainImageName = "generic-plain";
        (FileInstanceRepository repository, _) = CreateRepository();
        IReadOnlyDictionary<string, ImageSpec> catalog = CreateImageCatalog(
            CreateBootImage(bootImageName), CreatePlainImage(plainImageName));

        InstanceEditorViewModel editor = CreateEditor(repository, catalog);
        editor.ImageRef = bootImageName;

        Assert.True(await editor.CreateAsync());

        InstanceSpec saved = Assert.Single(await repository.ListAsync());
        Assert.NotNull(saved.PlatformConfig);

        string expectedImagesRoot = Path.Combine(BaseImageImportService.DefaultImagesRoot, bootImageName);
        Assert.Equal(Path.Combine(expectedImagesRoot, "kernel"), saved.PlatformConfig!.KernelImage);
        Assert.Equal(Path.Combine(expectedImagesRoot, "initrd.img"), saved.PlatformConfig.InitrdImage);
        Assert.Equal("root=/dev/ram0 quiet nomodeset", saved.PlatformConfig.KernelAppend);
    }

    [Fact]
    public async Task BuildSpecWithEmptyImageRefDoesNotPopulatePlatformConfig()
    {
        (FileInstanceRepository repository, _) = CreateRepository();
        IReadOnlyDictionary<string, ImageSpec> catalog = CreateImageCatalog(
            CreateBootImage("any-boot-image"), null);

        InstanceEditorViewModel editor = CreateEditor(repository, catalog);
        editor.ImageRef = string.Empty;

        // 空 ImageRef 为非法输入，CreateAsync 必须拒绝。
        Assert.False(await editor.CreateAsync());

        // 未创建任何实例，自然不存在 PlatformConfig。
        Assert.Empty(await repository.ListAsync());
    }
}
