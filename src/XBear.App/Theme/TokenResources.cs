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
    /// <exception cref="XBearException">令牌缺失或取值非法时抛出，界面不会静默降级。</exception>
    public static void Apply(DesignTokens tokens, ResourceDictionary resources)
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

        Put(resources, "Space.Xs", tokens.Spacing("xs"));
        Put(resources, "Space.Sm", tokens.Spacing("sm"));
        Put(resources, "Space.Md", tokens.Spacing("md"));
        Put(resources, "Space.Lg", tokens.Spacing("lg"));
        Put(resources, "Space.Xl", tokens.Spacing("xl"));
        Put(resources, "Space.Xxl", tokens.Spacing("xxl"));

        Put(resources, "Font.Caption", (double)tokens.FontSize("caption"));
        Put(resources, "Font.Body", (double)tokens.FontSize("body"));
        Put(resources, "Font.Subtitle", (double)tokens.FontSize("subtitle"));
        Put(resources, "Font.Title", (double)tokens.FontSize("title"));
        Put(resources, "Font.Display", (double)tokens.FontSize("display"));

        Put(resources, "Weight.Regular", WeightOf(tokens.FontWeight("regular")));
        Put(resources, "Weight.Medium", WeightOf(tokens.FontWeight("medium")));
        Put(resources, "Weight.Bold", WeightOf(tokens.FontWeight("bold")));

        Put(resources, "Radius.Sm", (double)tokens.Radius("sm"));
        Put(resources, "Radius.Md", (double)tokens.Radius("md"));
        Put(resources, "Radius.Lg", (double)tokens.Radius("lg"));
        Put(resources, "Radius.Full", (double)tokens.Radius("full"));

        Put(resources, "Motion.Fast", (double)tokens.MotionDuration("fast"));
        Put(resources, "Motion.Normal", (double)tokens.MotionDuration("normal"));
        Put(resources, "Motion.Slow", (double)tokens.MotionDuration("slow"));

        Put(resources, "Font.Primary", FontFamilyOf(tokens.FontFamilyPrimary));
        Put(resources, "Font.Monospace", FontFamilyOf(tokens.FontFamilyMonospace));
    }

    /// <summary>
    /// 按令牌键名构造资源键，供代码与测试引用。
    /// </summary>
    /// <param name="name">不含前缀的令牌名。</param>
    /// <returns>完整资源键。</returns>
    public static string Key(string name) => Prefix + name;

    private static void Put(ResourceDictionary resources, string name, object value) =>
        resources[Key(name)] = value;

    private static SolidColorBrush Freeze(string color)
    {
        object? converted = ColorConverter.ConvertFromString(color);
        if (converted is not Color parsed)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"设计令牌颜色 {color} 无法解析。",
                "令牌是双端共享契约，非法取值应修正 design-tokens.json 而非在界面侧兜底。");
        }

        var brush = new SolidColorBrush(parsed);
        brush.Freeze();
        return brush;
    }

    private static FontFamily FontFamilyOf(string value) =>
        new(value.Split(',')[0].Trim());

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