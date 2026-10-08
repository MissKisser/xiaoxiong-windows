using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XBear.App.Theme;

/// <summary>
/// 品牌矢量资源到窗口图标的渲染。WPF 的 <see cref="Window.Icon"/> 只接受位图，
/// 因此把资源字典中的矢量绘制按目标尺寸渲染成 <see cref="BitmapSource"/>。
/// 图标画刷本身已绑定品牌语义色，渲染结果与界面上的品牌图形天然一致，
/// 不需要在图标路径上再引入一套独立取值。
/// </summary>
public static class WindowIcon
{
    /// <summary>窗口图标资源键。</summary>
    public const string ResourceKey = "Brand.AppIcon";

    /// <summary>窗口图标边长，与规格允许的最小使用尺寸一致。</summary>
    public const int Size = 32;

    /// <summary>
    /// 从应用资源中取出品牌应用图标并渲染为窗口可用的位图。
    /// </summary>
    /// <param name="resources">应用资源字典。</param>
    /// <param name="size">输出边长，单位为设备无关像素。</param>
    /// <returns>渲染完成的图标位图；资源缺失时返回 null。</returns>
    public static BitmapSource? Create(ResourceDictionary resources, int size = Size)
    {
        ArgumentNullException.ThrowIfNull(resources);

        return resources[ResourceKey] is DrawingImage drawing
            ? Render(drawing, size)
            : null;
    }

    /// <summary>
    /// 把矢量绘制渲染为指定边长的位图。品牌资产一律等比缩放，
    /// 按绘制自身的包围盒换算缩放比例，不单独拉伸某一方向。
    /// </summary>
    /// <param name="image">矢量绘制资源。</param>
    /// <param name="size">输出边长，单位为设备无关像素。</param>
    /// <returns>渲染完成的图标位图。</returns>
    public static BitmapSource Render(DrawingImage image, int size = Size)
    {
        ArgumentNullException.ThrowIfNull(image);

        Rect bounds = image.Drawing.Bounds;
        double scale = bounds.Width <= 0 || bounds.Height <= 0
            ? 1d
            : size / Math.Max(bounds.Width, bounds.Height);

        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(scale, scale));
            context.DrawDrawing(image.Drawing);
        }

        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);

        target.Render(visual);
        target.Freeze();
        return target;
    }
}