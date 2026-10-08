using System.IO;
using XBear.App.Presentation;
using XBear.App.Services;
using XBear.App.Spec;
using XBear.App.ViewModels;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Identity;
using XBear.Core.Instances;
using XBear.Core.Qemu;
using XBear.Core.Spec;

namespace XBear.App;

/// <summary>
/// 组合根。把 Core 的具体实现装配成界面需要的对象，界面层不自行创建 QEMU 相关依赖。
/// </summary>
public static class AppComposition
{
    /// <summary>镜像清单目录名，位于镜像根目录之下。</summary>
    public const string ManifestDirectoryName = "manifests";

    /// <summary>实例配置目录名，位于数据根目录之下。</summary>
    public const string InstancesDirectoryName = "instances";

    /// <summary>标识墓碑文件名，位于数据根目录之下。</summary>
    public const string TombstoneFileName = "tombstones.json";

    /// <summary>
    /// 构造界面所需的全部服务。
    /// </summary>
    /// <param name="dataRoot">可写数据根目录，用于实例配置、overlay 与日志。</param>
    /// <param name="imagesRoot">只读 base 镜像根目录。</param>
    /// <param name="loader">规格读取器。</param>
    /// <returns>装配结果。</returns>
    public static AppServices Create(string dataRoot, string imagesRoot, SpecLoader loader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagesRoot);
        ArgumentNullException.ThrowIfNull(loader);

        Directory.CreateDirectory(dataRoot);

        string instancesRoot = Path.Combine(dataRoot, InstancesDirectoryName);
        string logRoot = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(instancesRoot);
        Directory.CreateDirectory(logRoot);

        var tombstones = new TombstoneStore(Path.Combine(dataRoot, TombstoneFileName));
        IInstanceRepository repository = new FileInstanceRepository(instancesRoot, tombstones);

        // 标识工厂与实例仓库共用同一份墓碑：删除过的实例标识必须被后续实例避开，
        // 两者若各持一份墓碑，防复用就会被绕过。
        var identityFactory = new DeviceIdentityFactory(repository, tombstones);

        IPortAllocator portAllocator = new PortAllocator();
        var argBuilder = new QemuArgBuilder();
        var paths = new QemuPaths();
        IQemuLauncher launcher = new QemuLauncher(paths, argBuilder, logRoot);
        IQcow2Manager qcow2 = new Qcow2Manager(paths);

        // 校验器在此装配：Core 不引用 Schema 库，求值实现由引用了 JsonSchema.Net 的本层注入。
        var validator = new SpecValidator(SchemaEvaluatorFactory.Create(), loader);

        var manager = new InstanceManager(
            repository,
            portAllocator,
            argBuilder,
            launcher,
            qcow2,
            imagesRoot,
            instancesRoot,
            validator);

        var diagnostics = new DiagnosticsExporter(loader);
        var audit = new AuditLog(Path.Combine(logRoot, "audit.log"));
        var terms = new TerminologyCatalog(loader.LoadTerminology());

        ImageLoadOutcome outcome = LoadImages(loader, validator, imagesRoot);

        return new AppServices(
            repository,
            manager,
            outcome.Images,
            diagnostics,
            audit,
            terms,
            loader.LoadDesignTokens(),
            validator,
            identityFactory,
            outcome.RejectedManifests);
    }

    /// <summary>
    /// 扫描镜像清单目录，按标识索引镜像。目录缺失时返回空集合，不阻断界面启动。
    /// 每份清单先过共享契约校验，未通过的清单不进入镜像集合，其原因被记录下来供界面如实告知。
    /// </summary>
    /// <param name="loader">规格读取器。</param>
    /// <param name="validator">实例配置与镜像清单的 Schema 校验器。</param>
    /// <param name="imagesRoot">只读 base 镜像根目录。</param>
    /// <returns>通过校验的镜像集合与被拒绝清单的原因。</returns>
    private static ImageLoadOutcome LoadImages(
        SpecLoader loader,
        SpecValidator validator,
        string imagesRoot)
    {
        var images = new Dictionary<string, ImageSpec>(StringComparer.Ordinal);
        var rejected = new List<ManifestRejection>();

        // image.schema.json 描述的是单个镜像，没有目录级清单；
        // 因此界面按 manifests/ 目录扫描 *.json 作为镜像清单来源。
        string manifestDirectory = Path.Combine(imagesRoot, ManifestDirectoryName);
        if (!Directory.Exists(manifestDirectory))
        {
            return new ImageLoadOutcome(images, rejected);
        }

        foreach (string manifestPath in Directory.EnumerateFiles(manifestDirectory, "*.json"))
        {
            string manifestName = Path.GetFileName(manifestPath);

            string json;
            try
            {
                json = loader.ReadText(manifestPath);
            }
            catch (XBearException ex)
            {
                // 单个清单损坏不应让整个界面起不来，跳过并记录原因。
                rejected.Add(new ManifestRejection(manifestName, ex.Message));
                continue;
            }

            // 契约校验先行：不合规的镜像清单不得进入界面，避免下游按缺省值静默放行。
            ValidationResult validation = validator.ValidateImage(json);
            if (!validation.IsValid)
            {
                rejected.Add(new ManifestRejection(manifestName, validation.DescribeErrors()));
                continue;
            }

            ImageSpec spec;
            try
            {
                spec = loader.ParseImage(json);
            }
            catch (XBearException ex)
            {
                // 通过 Schema 但仍解析失败的清单同样不得进入镜像集合。
                rejected.Add(new ManifestRejection(manifestName, ex.Message));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(spec.Id))
            {
                images[spec.Id] = spec;
            }
            else
            {
                rejected.Add(new ManifestRejection(manifestName, "镜像清单缺少标识，无法在界面中索引。"));
            }
        }

        return new ImageLoadOutcome(images, rejected);
    }
}

/// <summary>镜像清单的加载结果，含通过校验的镜像与被拒绝清单的原因。</summary>
/// <param name="Images">通过校验的镜像清单，按标识索引。</param>
/// <param name="RejectedManifests">被拒绝的镜像清单及原因，无拒绝项时为空集合。</param>
internal sealed record ImageLoadOutcome(
    IReadOnlyDictionary<string, ImageSpec> Images,
    IReadOnlyList<ManifestRejection> RejectedManifests);

/// <summary>单份镜像清单被拒绝的原因。</summary>
/// <param name="ManifestName">镜像清单文件名。</param>
/// <param name="Reason">被拒绝的原因描述。</param>
public sealed record ManifestRejection(string ManifestName, string Reason);

/// <summary>组合根产出的界面依赖集合。</summary>
/// <param name="Repository">实例配置仓库。</param>
/// <param name="Manager">实例生命周期编排器。</param>
/// <param name="Images">镜像清单，按标识索引。</param>
/// <param name="Diagnostics">诊断包导出器。</param>
/// <param name="Audit">审计日志。</param>
/// <param name="Terms">界面文案术语来源。</param>
/// <param name="Tokens">共享设计令牌。</param>
/// <param name="Validator">实例配置与镜像清单的 Schema 校验器。</param>
/// <param name="IdentityFactory">实例设备标识工厂，为每个实例发放独立且不复用的标识。</param>
/// <param name="RejectedManifests">被拒绝的镜像清单及原因，无拒绝项时为空集合。</param>
public sealed record AppServices(
    IInstanceRepository Repository,
    InstanceManager Manager,
    IReadOnlyDictionary<string, ImageSpec> Images,
    DiagnosticsExporter Diagnostics,
    AuditLog Audit,
    TerminologyCatalog Terms,
    DesignTokens Tokens,
    SpecValidator Validator,
    IDeviceIdentityFactory IdentityFactory,
    IReadOnlyList<ManifestRejection> RejectedManifests);