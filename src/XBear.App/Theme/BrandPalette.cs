using System.IO;
using System.Windows;
using System.Windows.Media;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.App.Theme;

/// <summary>
/// 品牌语义色到设计令牌键的映射与画刷生成。消费 Core 层的 <see cref="PaletteDocument"/>，
/// 并回查 <see cref="DesignTokens"/> 的实际取值，两处不一致时抛出异常——品牌资产与令牌必须同源，
/// 不允许界面侧自行兜底。
/// </summary>
public static class BrandPalette
{
    /// <summary>品牌目录名，位于规格根目录下。</summary>
    public const string BrandDirectoryName = SpecLoader.BrandDirectoryName;

    /// <summary>品牌配色映射文件名。</summary>
    public const string PaletteFileName = SpecLoader.PaletteFileName;

    /// <summary>资源键前缀，语义色一律以 Palette. 开头。</summary>
    private const string Prefix = "Palette.";

    /// <summary>
    /// 载入品牌配色映射。
    /// </summary>
    /// <param name="loader">规格读取器，其 <see cref="SpecLoader.SpecRoot"/> 下必须有 brand/palette.json。</param>
    /// <returns>语义名到令牌键与取值的映射，键序与 palette.json 一致。</returns>
    /// <exception cref="XBearException">文件缺失、JSON 非法或语义名与令牌取值不一致时抛出。</exception>
    public static IReadOnlyList<BrandRole> Load(SpecLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        return loader.LoadPalette().Roles;
    }

    /// <summary>
    /// 解析品牌配色映射文本。
    /// </summary>
    /// <param name="json">palette.json 文本。</param>
    /// <returns>语义名到令牌键与取值的映射。</returns>
    /// <exception cref="XBearException">JSON 非法或 roles 缺失时抛出。</exception>
    public static IReadOnlyList<BrandRole> Parse(string json) =>
        PaletteDocument.Parse(json).Roles;

    /// <summary>
    /// 把品牌语义色写入资源字典，形成 <c>Palette.&lt;语义名&gt;</c> 画刷。
    /// </summary>
    /// <param name="palette">品牌调色板契约文档。</param>
    /// <param name="tokens">设计令牌，用于核对两者取值一致。</param>
    /// <param name="resources">目标资源字典。</param>
    /// <exception cref="XBearException">语义色声明的取值与令牌不一致时抛出。</exception>
    public static void Apply(
        PaletteDocument palette,
        DesignTokens tokens,
        ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(palette);
        Apply(palette.Roles, tokens, resources);
    }

    /// <summary>
    /// 把品牌语义色写入资源字典，形成 <c>Palette.&lt;语义名&gt;</c> 画刷。
    /// </summary>
    /// <param name="roles">语义色映射。</param>
    /// <param name="tokens">设计令牌，用于核对两者取值一致。</param>
    /// <param name="resources">目标资源字典。</param>
    /// <exception cref="XBearException">语义色声明的取值与令牌不一致时抛出。</exception>
    public static void Apply(
        IReadOnlyList<BrandRole> roles,
        DesignTokens tokens,
        ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(resources);

        foreach (BrandRole role in roles)
        {
            string actual = Resolve(tokens, role.Token);

            if (!string.Equals(actual, role.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"品牌语义色 {role.Name} 声明为令牌 {role.Token}，但取值与令牌不一致。",
                    "palette.json 与 design-tokens.json 必须同步更新，不可单边改动。");
            }

            resources[Key(role.Name)] = BrushOf(actual);
        }
    }

    /// <summary>
    /// 按语义名构造资源键，供代码与测试引用。
    /// </summary>
    /// <param name="name">palette.json 中的语义名。</param>
    /// <returns>完整资源键。</returns>
    public static string Key(string name) => Prefix + name;

    /// <summary>
    /// 按令牌键取设计令牌取值。仅暴露品牌映射实际引用到的键，
    /// 未登记的键视为映射写错，直接抛出而不是返回空值。
    /// </summary>
    /// <param name="tokens">设计令牌访问器。</param>
    /// <param name="tokenKey">令牌键，如 color.brand.primary。</param>
    /// <returns>该令牌的取值文本。</returns>
    /// <exception cref="XBearException">令牌键未登记时抛出。</exception>
    private static string Resolve(DesignTokens tokens, string tokenKey) =>
        tokenKey switch
        {
            "color.brand.primary" => tokens.BrandPrimary,
            "color.brand.secondary" => tokens.BrandSecondary,
            "color.brand.accent" => tokens.BrandAccent,
            "color.surface.base" => tokens.SurfaceBase,
            "color.surface.raised" => tokens.SurfaceRaised,
            "color.text.primary" => tokens.TextPrimary,
            "color.text.secondary" => tokens.TextSecondary,
            "color.text.inverse" => tokens.TextInverse,
            _ => throw new XBearException(
                ErrorCategory.Spec,
                $"品牌配色映射引用了未登记的令牌键 {tokenKey}。",
                "令牌键必须来自 design-tokens.json，不可自造。")
        };

    private static SolidColorBrush BrushOf(string color)
    {
        object? converted = ColorConverter.ConvertFromString(color);
        if (converted is not Color parsed)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"品牌语义色取值 {color} 无法解析为颜色。");
        }

        var brush = new SolidColorBrush(parsed);
        brush.Freeze();
        return brush;
    }
}
