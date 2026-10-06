using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace XBear.App.Presentation;

/// <summary>把布尔值转换为可见性，用于条件展示风险标识与确认对话框。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// 转换布尔值为可见性。
    /// </summary>
    /// <param name="value">待转换的布尔值。</param>
    /// <param name="targetType">目标类型，本转换器固定返回可见性类型。</param>
    /// <param name="parameter">可选参数，传 invert 时取反。</param>
    /// <param name="culture">区域信息，本转换器不使用。</param>
    /// <returns>true 返回可见，否则返回折叠。</returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        bool invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        return invert ^ flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 把可见性转换回布尔值。
    /// </summary>
    /// <param name="value">待转换的可见性。</param>
    /// <param name="targetType">目标类型。</param>
    /// <param name="parameter">可选参数。</param>
    /// <param name="culture">区域信息，本转换器不使用。</param>
    /// <returns>可见返回 true。</returns>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility visibility && visibility == Visibility.Visible;
}