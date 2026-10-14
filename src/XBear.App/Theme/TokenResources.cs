using System.Windows;
using System.Windows.Media;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.App.Theme;

/// <summary>
/// 设计令牌到 WPF 资源字典的桥接。启动时把 <see cref="DesignTokens"/> 的取值写入
/// 应用级资源，XAML 一律通过 <c>DynamicResource</c> 消费，不出现字面量颜色、字号与间距。
/// </summary>
public static class TokenResources
{
    /// <summary>资源键前缀，统一以 Token. 开头便于与框架内置资源区分。</summary>
    private const string Prefix = "Token.";

    /// <summary>
    /// 把设计令牌写入应用资源字典。
    /// </summary>
    /// <param name="tokens">共享设计令牌访问器。</param>
    /// <param name="resources">目标资源字典。</param>
    /// <param name="roles">品牌语义色到令牌键的映射，取自 brand/palette.json。</param>
    /// <exception cref="XBearException">令牌缺失或取值非法时抛出，界面不会静默降级。</exception>
    public static void Apply(
        DesignTokens tokens,
        ResourceDictionary resources,
        IReadOnlyList<BrandRole>? roles = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(resources);

        Put(resources, "Color.Brand.Primary", Freeze(tokens.BrandPrimary));
        Put(resources, "Color.Brand.Secondary", Freeze(tokens.BrandSecondary));
        Put(resources, "Color.Brand.Accent", Freeze(tokens.BrandAccent));
        Put(resources, "Color.Surface.Base", Freeze(tokens.SurfaceBase));
        Put(resources, "Color.Surface.Raised", Freeze(tokens.SurfaceRaised));
        Put(resources, "Color.Surface.Overlay", Freeze(tokens.SurfaceOverlay));
        Put(resources, "Color.Text.Primary", Freeze(tokens.TextPrimary));
        Put(resources, "Color.Text.Secondary", Freeze(tokens.TextSecondary));
        Put(resources, "Color.Text.Inverse", Freeze(tokens.TextInverse));
        Put(resources, "Color.State.Success", Freeze(tokens.StateSuccess));
        Put(resources, "Color.State.Warning", Freeze(tokens.StateWarning));
        Put(resources, "Color.State.Danger", Freeze(tokens.StateDanger));
        Put(resources, "Color.State.Info", Freeze(tokens.StateInfo));

        // 矢量图标中的 SolidColorBrush.Color 需要纯 Color 结构体，而非 Brush 引用
        Put(resources, "RawColor.Text.Primary", ColorOf(tokens.TextPrimary));
        Put(resources, "RawColor.Brand.Secondary", ColorOf(tokens.BrandSecondary));
        Put(resources, "RawColor.State.Warning", ColorOf(tokens.StateWarning));
        Put(resources, "RawColor.State.Danger", ColorOf(tokens.StateDanger));

        // 间距令牌在 XAML 中主要被 Margin 与 Padding 消费（属性类型均为 Thickness）。
        // 为保证运行时直接消费与样式层解耦，Space.* 与 Thickness.* 均注入 Thickness 对象，
        // 避免 WPF 动态资源求值时因类型不匹配（double -> Thickness）抛出 InvalidOperationException。
        Put(resources, "Space.Xs", ThicknessOf(tokens.Spacing("xs")));
        Put(resources, "Space.Sm", ThicknessOf(tokens.Spacing("sm")));
        Put(resources, "Space.Md", ThicknessOf(tokens.Spacing("md")));
        Put(resources, "Space.Lg", ThicknessOf(tokens.Spacing("lg")));
        Put(resources, "Space.Xl", ThicknessOf(tokens.Spacing("xl")));
        Put(resources, "Space.Xxl", ThicknessOf(tokens.Spacing("xxl")));

        Put(resources, "Thickness.Xs", ThicknessOf(tokens.Spacing("xs")));
        Put(resources, "Thickness.Sm", ThicknessOf(tokens.Spacing("sm")));
        Put(resources, "Thickness.Md", ThicknessOf(tokens.Spacing("md")));
        Put(resources, "Thickness.Lg", ThicknessOf(tokens.Spacing("lg")));
        Put(resources, "Thickness.Xl", ThicknessOf(tokens.Spacing("xl")));
        Put(resources, "Thickness.Xxl", ThicknessOf(tokens.Spacing("xxl")));

        // 同理，Border.CornerRadius 的属性类型是 CornerRadius，
        // Radius.* 与 Corner.* 均注入 CornerRadius 对象供样式安全绑定。
        Put(resources, "Corner.Sm", CornerOf(tokens.Radius("sm")));
        Put(resources, "Corner.Md", CornerOf(tokens.Radius("md")));
        Put(resources, "Corner.Lg", CornerOf(tokens.Radius("lg")));
        Put(resources, "Corner.Full", CornerOf(tokens.Radius("full")));

        Put(resources, "Radius.Sm", CornerOf(tokens.Radius("sm")));
        Put(resources, "Radius.Md", CornerOf(tokens.Radius("md")));
        Put(resources, "Radius.Lg", CornerOf(tokens.Radius("lg")));
        Put(resources, "Radius.Full", CornerOf(tokens.Radius("full")));

        // 界面复合间距，为非均等边距提供预置 Thickness 实例
        resources["Space.Margin.BesideIcon"] = new Thickness(tokens.Spacing("sm"));
        resources["Space.Margin.Section"] = new Thickness(0, tokens.Spacing("xl"), 0, 0);
        resources["Space.Margin.HeaderStack"] = new Thickness(tokens.Spacing("md"));

        Put(resources, "Font.Caption", (double)tokens.FontSize("caption"));
        Put(resources, "Font.Body", (double)tokens.FontSize("body"));
        Put(resources, "Font.Subtitle", (double)tokens.FontSize("subtitle"));
        Put(resources, "Font.Title", (double)tokens.FontSize("title"));
        Put(resources, "Font.Display", (double)tokens.FontSize("display"));

        Put(resources, "Weight.Regular", WeightOf(tokens.FontWeight("regular")));
        Put(resources, "Weight.Medium", WeightOf(tokens.FontWeight("medium")));
        Put(resources, "Weight.Bold", WeightOf(tokens.FontWeight("bold")));

        Put(resources, "Motion.Fast", (double)tokens.MotionDuration("fast"));
        Put(resources, "Motion.Normal", (double)tokens.MotionDuration("normal"));
        Put(resources, "Motion.Slow", (double)tokens.MotionDuration("slow"));

        Put(resources, "Font.Primary", FontFamilyOf(tokens.FontFamilyPrimary));
        Put(resources, "Font.Monospace", FontFamilyOf(tokens.FontFamilyMonospace));

        // 阴影与动效同样属于令牌，登记后才能被样式与过渡动画按资源键消费。
        ElevationTokens.Apply(tokens, resources);
        MotionTokens.Apply(tokens, resources);

        if (roles is not null)
        {
            BrandPalette.Apply(roles, tokens, resources);
        }
    }

    /// <summary>
    /// 按令牌键名构造资源键，供代码与测试引用。
    /// </summary>
    /// <param name="name">不含前缀的令牌名。</param>
    /// <returns>完整资源键。</returns>
    public static string Key(string name) => Prefix + name;

    private static void Put(ResourceDictionary resources, string name, object value) =>
        resources[Key(name)] = value;

    private static Color ColorOf(string color)
    {
        object? converted = ColorConverter.ConvertFromString(color);
        if (converted is not Color parsed)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"设计令牌颜色 {color} 无法解析。",
                "令牌是双端共享契约，非法取值应修正 design-tokens.json 而非在界面侧兜底。");
        }

        return parsed;
    }

    private static SolidColorBrush Freeze(string color)
    {
        var brush = new SolidColorBrush(ColorOf(color));
        brush.Freeze();
        return brush;
    }

    private static FontFamily FontFamilyOf(string value) =>
        new(value.Split(',')[0].Trim());

    /// <summary>
    /// 把单个间距数值转换为四边等距的粗细值。
    /// </summary>
    /// <param name="spacing">令牌中的间距数值。</param>
    /// <returns>四边相同的 <see cref="Thickness"/>。</returns>
    private static Thickness ThicknessOf(double spacing) =>
        new(spacing);

    /// <summary>
    /// 把单个圆角数值转换为四角等值的 <see cref="CornerRadius"/>。
    /// </summary>
    /// <param name="radius">令牌中的圆角数值。</param>
    /// <returns>四角相同的 <see cref="CornerRadius"/>。</returns>
    private static CornerRadius CornerOf(double radius) =>
        new(radius);

    /// <summary>
    /// 按数值取字重。令牌里的 400、500、700 对应 WPF 的标准字重档位。
    /// </summary>
    /// <param name="weight">字重数值。</param>
    /// <returns>对应的字重。</returns>
    private static FontWeight WeightOf(int weight) =>
        weight switch
        {
            >= 600 => FontWeights.Bold,
            >= 500 => FontWeights.SemiBold,
            _ => FontWeights.Normal
        };
}