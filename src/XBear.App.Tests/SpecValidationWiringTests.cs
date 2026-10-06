using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XBear.App.Spec;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// App 层 Schema 校验器的接线测试。校验由 App 注入的求值实现完成，
/// Core 保持零 Schema 依赖。断言同时覆盖实例配置的启动前校验与镜像清单的组合期校验。
/// </summary>
public class SpecValidationWiringTests : IDisposable
{
    private const string InstanceId = "win-spec-01";
    private const string ImageRef = "bliss-os-17-x86_64";

    private readonly TempRoot _temp = new();

    public void Dispose() => _temp.Cleanup();

    /// <summary>
    /// 用 App 层求值实现构造校验器。求值实现保持 internal，此处经工厂取得。
    /// </summary>
    /// <param name="loader">规格读取器。</param>
    /// <returns>校验器。</returns>
    private static SpecValidator CreateValidator(SpecLoader loader) =>
        new(SchemaEvaluatorFactory.Create(), loader);

    private static InstanceSpec BuildSpec() => new()
    {
        Id = InstanceId,
        DisplayName = "规格校验实例",
        Platform = "windows",
        ImageRef = ImageRef,
        Resources = new ResourceSpec { MemoryMB = 4096, CpuCores = 4, DiskGB = 64 },
    };

    [Fact]
    public void EvaluatorAcceptsWindowsInstanceFixture()
    {
        SpecLoader loader = XBeeSpec.TestSpec();
        SpecValidator validator = CreateValidator(loader);

        ValidationResult result = validator.ValidateInstance(
            loader.ReadFixtureText(SpecLoader.WindowsInstanceFixtureName));

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void EvaluatorAcceptsAndroidInstanceFixture()
    {
        SpecLoader loader = XBeeSpec.TestSpec();
        SpecValidator validator = CreateValidator(loader);

        ValidationResult result = validator.ValidateInstance(
            loader.ReadFixtureText(SpecLoader.AndroidInstanceFixtureName));

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void EvaluatorAcceptsImageFixture()
    {
        SpecLoader loader = XBeeSpec.TestSpec();
        SpecValidator validator = CreateValidator(loader);

        ValidationResult result = validator.ValidateImage(
            loader.ReadFixtureText(SpecLoader.ImageFixtureName));

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void EvaluatorRejectsEnumValueOutsideContract()
    {
        SpecLoader loader = XBeeSpec.TestSpec();
        SpecValidator validator = CreateValidator(loader);

        // platform 的契约取值只有 windows 与 android。
        ValidationResult result = validator.ValidateInstance(
            """
            {
              "schemaVersion": "1.0.0",
              "id": "win-spec-01",
              "displayName": "枚举越界",
              "platform": "symbian",
              "imageRef": "bliss-os-17-x86_64",
              "resources": { "memoryMB": 4096, "cpuCores": 4 }
            }
            """);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void EvaluatorRejectsMissingRequiredField()
    {
        SpecLoader loader = XBeeSpec.TestSpec();
        SpecValidator validator = CreateValidator(loader);

        // imageRef 是契约要求的必填字段。
        ValidationResult result = validator.ValidateInstance(
            """
            {
              "schemaVersion": "1.0.0",
              "id": "win-spec-01",
              "displayName": "缺字段",
              "platform": "windows",
              "resources": { "memoryMB": 4096, "cpuCores": 4 }
            }
            """);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void EvaluatorRejectsUnknownFieldWhenContractForbidsIt()
    {
        SpecLoader loader = XBeeSpec.TestSpec();
        SpecValidator validator = CreateValidator(loader);

        // 实例配置契约不允许出现未声明的字段。
        ValidationResult result = validator.ValidateInstance(
            """
            {
              "schemaVersion": "1.0.0",
              "id": "win-spec-01",
              "displayName": "多字段",
              "platform": "windows",
              "imageRef": "bliss-os-17-x86_64",
              "resources": { "memoryMB": 4096, "cpuCores": 4 },
              "unexpectedField": true
            }
            """);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task StartIsBlockedWhenInstanceSpecViolatesContract()
    {
        var repository = new StubInstanceRepository();

        // platform 取值不在契约枚举内，属于不合规配置。
        repository.Add(new InstanceSpec
        {
            Id = InstanceId,
            DisplayName = "不合规实例",
            Platform = "symbian",
            ImageRef = ImageRef,
            Resources = new ResourceSpec { MemoryMB = 4096, CpuCores = 4, DiskGB = 64 },
        });

        var launcher = new StubQemuLauncher();
        var ports = new StubPortAllocator();

        var manager = new InstanceManager(
            repository,
            ports,
            new StubArgBuilder(),
            launcher,
            new StubQcow2Manager(),
            _temp.NewImagesRootWithBaseImage(ImageRef),
            _temp.New("instances"),
            CreateValidator(XBeeSpec.TestSpec()));

        XBearException error = await Assert.ThrowsAsync<XBearException>(() => manager.StartAsync(InstanceId));

        // 不合规配置必须在拉起进程前被拦下，并归类为规格问题。
        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.Empty(launcher.Started);
        Assert.Empty(ports.Held);
    }

    [Fact]
    public async Task StartProceedsWhenInstanceSpecSatisfiesContract()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        var launcher = new StubQemuLauncher();
        var ports = new StubPortAllocator();

        var manager = new InstanceManager(
            repository,
            ports,
            new StubArgBuilder(),
            launcher,
            new StubQcow2Manager(),
            _temp.NewImagesRootWithBaseImage(ImageRef),
            _temp.New("instances"),
            CreateValidator(XBeeSpec.TestSpec()));

        await manager.StartAsync(InstanceId);

        Assert.Single(launcher.Started);
        Assert.Equal(InstanceState.Running, manager.GetState(InstanceId));
    }

    [Fact]
    public async Task StartSkipsValidationWhenNoValidatorIsInjected()
    {
        var repository = new StubInstanceRepository();
        repository.Add(new InstanceSpec
        {
            Id = InstanceId,
            DisplayName = "未装配校验器",
            Platform = "symbian",
            ImageRef = ImageRef,
            Resources = new ResourceSpec { MemoryMB = 4096, CpuCores = 4, DiskGB = 64 },
        });

        var launcher = new StubQemuLauncher();

        // 未注入校验器时保持既有行为，不因缺少装配而阻断启动。
        var manager = new InstanceManager(
            repository,
            new StubPortAllocator(),
            new StubArgBuilder(),
            launcher,
            new StubQcow2Manager(),
            _temp.NewImagesRootWithBaseImage(ImageRef),
            _temp.New("instances"));

        await manager.StartAsync(InstanceId);

        Assert.Single(launcher.Started);
    }

    [Fact]
    public void CompositionRejectsManifestThatViolatesContractAndRecordsReason()
    {
        string imagesRoot = _temp.New("images");
        string manifests = Path.Combine(imagesRoot, AppComposition.ManifestDirectoryName);
        Directory.CreateDirectory(manifests);

        // abi 不在契约枚举内，且存在未声明字段，该清单不得进入镜像集合。
        File.WriteAllText(Path.Combine(manifests, "bad.json"),
            """
            {
              "schemaVersion": "1.0.0",
              "id": "bad-image-01",
              "displayName": "不合规镜像",
              "androidVersion": "11",
              "abi": "mips",
              "source": { "type": "builtin" },
              "surpriseField": 1
            }
            """);

        // 另一份清单合法，必须照常进入镜像集合。
        File.WriteAllText(Path.Combine(manifests, "good.json"),
            XBeeSpec.TestSpec().ReadFixtureText(SpecLoader.ImageFixtureName));

        AppServices services = AppComposition.Create(
            _temp.New("data"),
            imagesRoot,
            XBeeSpec.TestSpec());

        // 不合规清单被拦下，原因被记录而不是静默跳过。
        Assert.DoesNotContain("bad-image-01", services.Images.Keys);
        ManifestRejection rejection = Assert.Single(
            services.RejectedManifests.Where(r => r.ManifestName == "bad.json"));

        Assert.NotEmpty(rejection.Reason);

        // 合法清单不受牵连。
        Assert.Contains("bliss-os-14.10.3-x86_64", services.Images.Keys);
        Assert.DoesNotContain(
            services.RejectedManifests,
            r => r.ManifestName == "good.json");
    }

    [Fact]
    public void CompositionAcceptsValidManifestWithoutRecordingRejection()
    {
        string imagesRoot = _temp.New("images");
        string manifests = Path.Combine(imagesRoot, AppComposition.ManifestDirectoryName);
        Directory.CreateDirectory(manifests);

        File.WriteAllText(Path.Combine(manifests, "image.json"),
            XBeeSpec.TestSpec().ReadFixtureText(SpecLoader.ImageFixtureName));

        AppServices services = AppComposition.Create(
            _temp.New("data"),
            imagesRoot,
            XBeeSpec.TestSpec());

        Assert.Single(services.Images);
        Assert.Empty(services.RejectedManifests);
    }

    [Fact]
    public void CompositionWithoutManifestDirectoryStartsWithEmptyImages()
    {
        AppServices services = AppComposition.Create(
            _temp.New("data"),
            _temp.New("images"),
            XBeeSpec.TestSpec());

        // 目录缺失不是契约违规，不应产生任何拒绝记录。
        Assert.Empty(services.Images);
        Assert.Empty(services.RejectedManifests);
    }

    [Fact]
    public void CompositionExposesValidatorForRuntimeReuse()
    {
        AppServices services = AppComposition.Create(
            _temp.New("data"),
            _temp.New("images"),
            XBeeSpec.TestSpec());

        Assert.NotNull(services.Validator);
    }
}
