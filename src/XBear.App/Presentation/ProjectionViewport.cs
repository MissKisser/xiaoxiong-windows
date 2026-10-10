using XBear.Core.Abstractions;

namespace XBear.App.Presentation;

/// <summary>
/// 投屏画面在宿主呈现面上的落位矩形。等比缩放后居中，四周可能留边。
/// </summary>
/// <param name="OffsetX">呈现面内画面左边距，单位与呈现面一致。</param>
/// <param name="OffsetY">呈现面内画面上边距，单位与呈现面一致。</param>
/// <param name="Width">画面呈现宽度，单位与呈现面一致。</param>
/// <param name="Height">画面呈现高度，单位与呈现面一致。</param>
public readonly record struct ProjectionSurfaceRect(
    double OffsetX,
    double OffsetY,
    double Width,
    double Height);

/// <summary>
/// 投屏画面与宿主呈现面之间的换算。呈现面按画面宽高比等比缩放并居中，
/// 指针事件必须先扣除留边再换算回画面像素坐标，否则点按会整体偏移。
/// </summary>
public static class ProjectionViewport
{
    /// <summary>
    /// 计算画面在给定呈现面内的落位矩形。画面或呈现面尺寸非法时返回空矩形。
    /// </summary>
    /// <param name="frame">画面尺寸。</param>
    /// <param name="surfaceWidth">呈现面宽度。</param>
    /// <param name="surfaceHeight">呈现面高度。</param>
    /// <returns>等比缩放并居中后的落位矩形。</returns>
    public static ProjectionSurfaceRect Fit(
        ScreenGeometry frame,
        double surfaceWidth,
        double surfaceHeight)
    {
        if (!frame.IsValid || surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            return default;
        }

        double scale = Math.Min(surfaceWidth / frame.Width, surfaceHeight / frame.Height);
        double drawWidth = frame.Width * scale;
        double drawHeight = frame.Height * scale;

        return new ProjectionSurfaceRect(
            (surfaceWidth - drawWidth) / 2,
            (surfaceHeight - drawHeight) / 2,
            drawWidth,
            drawHeight);
    }

    /// <summary>
    /// 把呈现面上的位置换算为画面像素坐标。落在留边区域内时返回 null，
    /// 因为该处不对应画面上的任何位置，投递过去没有意义。
    /// </summary>
    /// <param name="frame">画面尺寸。</param>
    /// <param name="rect">画面在呈现面内的落位矩形。</param>
    /// <param name="x">呈现面横坐标。</param>
    /// <param name="y">呈现面纵坐标。</param>
    /// <returns>画面像素坐标，落在留边区域时为 null。</returns>
    public static InputPoint? ToFramePoint(
        ScreenGeometry frame,
        ProjectionSurfaceRect rect,
        double x,
        double y)
    {
        if (!frame.IsValid || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        if (x < rect.OffsetX || x > rect.OffsetX + rect.Width ||
            y < rect.OffsetY || y > rect.OffsetY + rect.Height)
        {
            return null;
        }

        double scale = rect.Width / frame.Width;
        double frameX = (x - rect.OffsetX) / scale;
        double frameY = (y - rect.OffsetY) / scale;

        return new InputPoint(
            Math.Clamp(frameX, 0, frame.Width - 1),
            Math.Clamp(frameY, 0, frame.Height - 1));
    }
}

/// <summary>
/// 投屏窗口的外框适配。窗口始终按画面宽高比呈现，分辨率变化时按新宽高比重算外框，
/// 并限制在屏幕可用范围内，避免画面比屏幕大时窗口无法完整摆放。
/// </summary>
public static class ProjectionWindowSizing
{
    /// <summary>
    /// 按画面宽高比计算窗口外框尺寸。画面尺寸非法或可用范围不足时返回 null，由调用方保留原尺寸。
    /// </summary>
    /// <param name="frame">画面尺寸。</param>
    /// <param name="availableWidth">可用宽度。</param>
    /// <param name="availableHeight">可用高度。</param>
    /// <returns>按宽高比适配后的外框尺寸，不可适配时为 null。</returns>
    public static (double Width, double Height)? Fit(
        ScreenGeometry frame,
        double availableWidth,
        double availableHeight)
    {
        if (!frame.IsValid || availableWidth <= 0 || availableHeight <= 0)
        {
            return null;
        }

        double scale = Math.Min(availableWidth / frame.Width, availableHeight / frame.Height);
        return (frame.Width * scale, frame.Height * scale);
    }
}