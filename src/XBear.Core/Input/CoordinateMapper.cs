using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Input;

/// <summary>坐标映射，把调用方画面上的点换算到 guest 显示分辨率范围。</summary>
public interface ICoordinateMapper
{
    /// <summary>把一个点从源尺寸换算到目标尺寸，并对越界坐标夹取。</summary>
    /// <param name="point">源坐标系下的点，允许越界。</param>
    /// <param name="source">源尺寸，非法时按不缩放处理，只做夹取。</param>
    /// <param name="target">目标尺寸，必须为正。</param>
    /// <returns>目标尺寸范围内的整数像素点。</returns>
    InputPoint Map(InputPoint point, ScreenGeometry source, ScreenGeometry target);
}

/// <summary>
/// 线性坐标映射。横纵轴各自独立换算：源区间的两端分别对应目标区间的两端，
/// 因此源尺寸与目标尺寸相等时为恒等映射，越界的坐标夹取到目标区间的边界像素。
/// 该组件不含任何默认分辨率，尺寸一律由调用方给出或从 QMP 查得。
/// </summary>
public sealed class CoordinateMapper : ICoordinateMapper
{
    /// <summary>
    /// 把一个点从源尺寸换算到目标尺寸，并对越界坐标夹取。
    /// </summary>
    /// <param name="point">源坐标系下的点，允许越界。</param>
    /// <param name="source">源尺寸，非法时按不缩放处理。</param>
    /// <param name="target">目标尺寸，必须为正。</param>
    /// <returns>目标尺寸范围内的整数像素点。</returns>
    /// <exception cref="XBearException">目标尺寸非法时抛出，分类为 <see cref="ErrorCategory.Spec"/>。</exception>
    public InputPoint Map(InputPoint point, ScreenGeometry source, ScreenGeometry target)
    {
        if (!target.IsValid)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "目标画面尺寸无效，无法换算触摸坐标。",
                "请确认实例已完全启动并取得 guest 显示尺寸后重试。");
        }

        int x = ScaleAndClamp(point.X, source.Width, target.Width);
        int y = ScaleAndClamp(point.Y, source.Height, target.Height);
        return new InputPoint(x, y);
    }

    /// <summary>
    /// 单轴换算并夹取。源跨度为零或非法时视为不缩放；换算结果四舍五入后夹到目标像素范围。
    /// </summary>
    /// <param name="value">源坐标。</param>
    /// <param name="sourceExtent">源尺寸的轴向像素数。</param>
    /// <param name="targetExtent">目标尺寸的轴向像素数。</param>
    /// <returns>目标范围内的整数像素坐标。</returns>
    private static int ScaleAndClamp(double value, int sourceExtent, int targetExtent)
    {
        if (sourceExtent == targetExtent || sourceExtent <= 1)
        {
            return Clamp(value, targetExtent);
        }

        // 以像素下标换算：源的首末像素分别落在目标的首末像素上。
        double ratio = (targetExtent - 1) / (double)(sourceExtent - 1);
        return Clamp(Math.Round(value * ratio, MidpointRounding.AwayFromZero), targetExtent);
    }

    private static int Clamp(double value, int targetExtent) =>
        (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, targetExtent - 1);
}
