using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.App.Theme;

/// <summary>
/// 投影令牌到 WPF 阴影效果的桥接。令牌取值形如
/// <c>0 1px 3px rgba(0,0,0,0.12)</c>，解析为 X/Y 偏移、模糊半径与半透明色。
/// </summary>
public static partial class ElevationTokens
{
    /// <summary>资源键前缀。</summary>
    private const string Prefix = "Elevation.";

    [GeneratedRegex(
        @"^\s*(?<x>-?[\d.]+)(?:px)?\s+(?<y>-?[\d.]+)(?:px)?\s+(?<blur>[\d.]+)(?:px)?\s+rgba\(\s*(?<r>[\d.]+)\s*,\s*(?<g>[\d.]+)\s*,\s*(?<b>[\d.]+)\s*,\s*(?<a>[\d.]+)\s*\)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProjectionPattern();

    /// <summary>
    /// 把投影令牌写入资源字典，形成 <c>Elevation.&lt;名称&gt;</c> 阴影效果。
    /// </summary>
    /// <param name="tokens">设计令牌访问器。</param>
    /// <param name="resources">目标资源字典。</param>
    /// <exception cref="XBearException">令牌缺失或取值格式非法时抛出。</exception>
    public static void Apply(DesignTokens tokens, ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(resources);

        resources[Key("Card")] = EffectOf(tokens.Elevation("card"));
        resources[Key("Dialog")] = EffectOf(tokens.Elevation("dialog"));
    }

    /// <summary>
    /// 按名称构造资源键，供代码与测试引用。
    /// </summary>
    /// <param name="name">投影名，取值范围为 card、dialog。</param>
    /// <returns>完整资源键。</returns>
    public static string Key(string name) => Prefix + name;

    /// <summary>
    /// 把一条投影令牌取值解析为 WPF 阴影效果。X/Y 偏移决定阴影方向，
    /// 模糊半径与 rgba 透明度分别对应 BlurRadius 与 Color 的 Alpha 通道。
    /// </summary>
    /// <param name="description">投影令牌取值文本。</param>
    /// <returns>可直接赋给 <see cref="UIElement.Effect"/> 的阴影效果。</returns>
    /// <exception cref="XBearException">取值格式非法时抛出。</exception>
    public static DropShadowEffect EffectOf(string description)
    {
        ArgumentNullException.ThrowIfNull(description);

        Match match = ProjectionPattern().Match(description);
        if (!match.Success)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"投影令牌取值 {description} 无法解析。",
                "投影是双端共享契约，格式应修正 design-tokens.json 而非在界面侧兜底。");
        }

        double x = Number(match, "x");
        double y = Number(match, "y");
        double blur = Number(match, "blur");
        double alpha = Number(match, "a");

        var color = Color.FromRgb(
            ToChannel(match, "r"),
            ToChannel(match, "g"),
            ToChannel(match, "b"));

        var effect = new DropShadowEffect
        {
            BlurRadius = blur,
            ShadowDepth = y,
            Direction = x >= 0 ? 270 : 90,
            Color = Color.FromArgb(ToAlpha(alpha), color.R, color.G, color.B),
            RenderingBias = RenderingBias.Quality
        };

        effect.Freeze();
        return effect;
    }

    private static double Number(Match match, string group) =>
        double.Parse(
            match.Groups[group].Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture);

    private static byte ToChannel(Match match, string group) =>
        (byte)Math.Clamp(Math.Round(Number(match, group)), 0d, 255d);

    private static byte ToAlpha(double alpha) =>
        (byte)Math.Clamp(Math.Round(alpha * 255d), 0d, 255d);
}