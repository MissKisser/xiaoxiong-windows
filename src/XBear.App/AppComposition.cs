using System.IO;
using XBear.App.Presentation;
using XBear.App.Services;
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

        string instancesRoot = Path.Combine(dataRoot, "instances");
        string logRoot = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(instancesRoot);
        Directory.CreateDirectory(logRoot);

        var tombstones = new TombstoneStore(Path.Combine(dataRoot, "tombstones.json"));
        IInstanceRepository repository = new FileInstanceRepository(instancesRoot, tombstones);

        IPortAllocator portAllocator = new PortAllocator();
        var argBuilder = new QemuArgBuilder();
        var paths = new QemuPaths();
        IQemuLauncher launcher = new QemuLauncher(paths, argBuilder, logRoot);
        IQcow2Manager qcow2 = new Qcow2Manager(paths);

        var manager = new InstanceManager(
            repository,
            portAllocator,
            argBuilder,
            launcher,
            qcow2,
            imagesRoot,
            instancesRoot);

        var diagnostics = new DiagnosticsExporter(loader);
        var audit = new AuditLog(Path.Combine(logRoot, "audit.log"));
        var terms = new TerminologyCatalog(loader.LoadTerminology());

        IReadOnlyDictionary<string, ImageSpec> images = LoadImages(loader, imagesRoot);

        return new AppServices(
            repository,
            manager,
            images,
            diagnostics,
            audit,
            terms,
            loader.LoadDesignTokens());
    }

    /// <summary>
    /// 扫描镜像清单目录，按标识索引镜像。目录缺失时返回空集合，不阻断界面启动。
    /// </summary>
    private static IReadOnlyDictionary<string, ImageSpec> LoadImages(SpecLoader loader, string imagesRoot)
    {
        var images = new Dictionary<string, ImageSpec>(StringComparer.Ordinal);

        // image.schema.json 描述的是单个镜像，没有目录级清单；
        // 因此界面按 manifests/ 目录扫描 *.json 作为镜像清单来源。
        string manifestDirectory = Path.Combine(imagesRoot, ManifestDirectoryName);
        if (!Directory.Exists(manifestDirectory))
        {
            return images;
        }

        foreach (string manifestPath in Directory.EnumerateFiles(manifestDirectory, "*.json"))
        {
            ImageSpec spec;
            try
            {
                spec = loader.LoadImageFile(manifestPath);
            }
            catch (XBearException)
            {
                // 单个清单损坏不应让整个界面起不来，跳过即可。
                continue;
            }

            if (!string.IsNullOrWhiteSpace(spec.Id))
            {
                images[spec.Id] = spec;
            }
        }

        return images;
    }
}

/// <summary>组合根产出的界面依赖集合。</summary>
/// <param name="Repository">实例配置仓库。</param>
/// <param name="Manager">实例生命周期编排器。</param>
/// <param name="Images">镜像清单，按标识索引。</param>
/// <param name="Diagnostics">诊断包导出器。</param>
/// <param name="Audit">审计日志。</param>
/// <param name="Terms">界面文案术语来源。</param>
/// <param name="Tokens">共享设计令牌。</param>
public sealed record AppServices(
    IInstanceRepository Repository,
    InstanceManager Manager,
    IReadOnlyDictionary<string, ImageSpec> Images,
    DiagnosticsExporter Diagnostics,
    AuditLog Audit,
    TerminologyCatalog Terms,
    DesignTokens Tokens);