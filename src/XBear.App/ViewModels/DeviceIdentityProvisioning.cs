using System.IO;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Identity;
using XBear.Core.Instances;

namespace XBear.App.ViewModels;

/// <summary>
/// 实例标识工厂的解析入口。组合根注入的工厂优先使用；
/// 未注入时按实例仓库所在的配置目录反查同一数据根下的墓碑文件重建，
/// 使视图模型与实例仓库共用一份墓碑，删除过的实例标识不会被后续实例复用。
/// </summary>
public static class DeviceIdentityProvisioning
{
    /// <summary>
    /// 解析标识工厂。
    /// </summary>
    /// <param name="repository">实例配置仓库，用于查重在册实例的标识。</param>
    /// <param name="injected">组合根装配的标识工厂，为 null 时按数据根重建。</param>
    /// <returns>可用于发放实例标识的工厂。</returns>
    /// <exception cref="XBearException">仓库未暴露配置目录，无法定位墓碑文件时抛出。</exception>
    public static IDeviceIdentityFactory Resolve(
        IInstanceRepository repository,
        IDeviceIdentityFactory? injected = null)
    {
        ArgumentNullException.ThrowIfNull(repository);

        return injected ?? new DeviceIdentityFactory(repository, new TombstoneStore(LocateTombstoneFile(repository)));
    }

    /// <summary>
    /// 定位数据根下的墓碑文件。墓碑与实例配置同处一个数据根，目录布局由组合根定义。
    /// </summary>
    /// <param name="repository">实例配置仓库。</param>
    /// <returns>墓碑文件绝对路径。</returns>
    private static string LocateTombstoneFile(IInstanceRepository repository)
    {
        if (repository is not FileInstanceRepository files)
        {
            throw new XBearException(
                ErrorCategory.Internal,
                "实例仓库未暴露配置文件目录，无法定位标识墓碑文件。",
                "为实例创建流程注入组合根装配的标识工厂。");
        }

        string instancesRoot = Path.GetFullPath(files.Directory);
        string dataRoot = Directory.GetParent(instancesRoot)?.FullName
            ?? throw new XBearException(
                ErrorCategory.Internal,
                $"实例配置目录 {instancesRoot} 没有上级数据根，无法定位标识墓碑文件。",
                "确认数据根目录存在且实例配置目录是其子目录。");

        return Path.Combine(dataRoot, AppComposition.TombstoneFileName);
    }
}