using System.Collections.Concurrent;

namespace XBear.Core.Spec;

/// <summary>只读镜像清单目录，按标识索引镜像。</summary>
public sealed class ImageCatalog : Abstractions.IImageCatalog
{
    private readonly ConcurrentDictionary<string, ImageSpec> _images;

    /// <summary>
    /// 以已有镜像集合构造目录。
    /// </summary>
    /// <param name="images">镜像清单集合，可为空。</param>
    public ImageCatalog(IEnumerable<ImageSpec>? images)
    {
        _images = new ConcurrentDictionary<string, ImageSpec>(StringComparer.Ordinal);
        if (images is null)
        {
            return;
        }

        foreach (var image in images)
        {
            if (!string.IsNullOrWhiteSpace(image.Id))
            {
                _images[image.Id] = image;
            }
        }
    }

    /// <summary>当前目录中的镜像数量。</summary>
    public int Count => _images.Count;

    /// <inheritdoc />
    public ImageSpec? Find(string imageRef)
    {
        if (string.IsNullOrWhiteSpace(imageRef))
        {
            return null;
        }

        return _images.TryGetValue(imageRef, out ImageSpec? image) ? image : null;
    }
}