using XBear.Core.Spec;

namespace XBear.Core.Abstractions;

/// <summary>
/// 镜像清单目录。实例启动时按 imageRef 解析镜像保真度结论，
/// 使引导参数与镜像实测证据保持同源。
/// </summary>
public interface IImageCatalog
{
    /// <summary>按标识查找镜像清单。</summary>
    /// <param name="imageRef">镜像标识，对应实例配置的 imageRef。</param>
    /// <returns>镜像清单，不存在时返回 null。</returns>
    ImageSpec? Find(string imageRef);
}