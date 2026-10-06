using System.Text.Json;
using System.Text.Json.Nodes;
using XBear.Core.Diagnostics;

namespace XBear.Core.Spec;

/// <summary>
/// 双端共享设计令牌的强类型访问器。
/// 令牌键缺失或类型不符时一律抛出 <see cref="XBearException"/>，
/// 不返回 null 或默认值——静默兜底会掩盖两端令牌不一致。
/// </summary>
public sealed class DesignTokens
{
    private readonly JsonNode _root;

    private DesignTokens(JsonNode root)
    {
        _root = root;
    }

    /// <summary>令牌集名称。</summary>
    public string Name => RequireString("name");

    /// <summary>令牌集版本号。</summary>
    public string Version => RequireString("version");

    /// <summary>品牌主色，小熊棕。</summary>
    public string BrandPrimary => RequireString("color.brand.primary");

    /// <summary>辅助色，森林绿。</summary>
    public string BrandSecondary => RequireString("color.brand.secondary");

    /// <summary>强调色，蜂蜜黄。</summary>
    public string BrandAccent => RequireString("color.brand.accent");

    /// <summary>基础底色。</summary>
    public string SurfaceBase => RequireString("color.surface.base");

    /// <summary>抬升面底色。</summary>
    public string SurfaceRaised => RequireString("color.surface.raised");

    /// <summary>遮罩底色。</summary>
    public string SurfaceOverlay => RequireString("color.surface.overlay");

    /// <summary>主要文字色。</summary>
    public string TextPrimary => RequireString("color.text.primary");

    /// <summary>次要文字色。</summary>
    public string TextSecondary => RequireString("color.text.secondary");

    /// <summary>反色文字色。</summary>
    public string TextInverse => RequireString("color.text.inverse");

    /// <summary>成功态颜色。</summary>
    public string StateSuccess => RequireString("color.state.success");

    /// <summary>警告态颜色。</summary>
    public string StateWarning => RequireString("color.state.warning");

    /// <summary>危险态颜色。</summary>
    public string StateDanger => RequireString("color.state.danger");

    /// <summary>信息态颜色。</summary>
    public string StateInfo => RequireString("color.state.info");

    /// <summary>间距基准值。</summary>
    public double SpacingBase => RequireNumber("spacing.base");

    /// <summary>间距单位。</summary>
    public string SpacingUnit => RequireString("spacing.base.unit");

    /// <summary>
    /// 按名称取间距刻度值。
    /// </summary>
    /// <param name="key">刻度名，取值范围为 xs、sm、md、lg、xl、xxl。</param>
    /// <returns>间距刻度值。</returns>
    public double Spacing(string key) => RequireNumber($"spacing.scale.{RequireKey(key)}");

    /// <summary>正文字体族。</summary>
    public string FontFamilyPrimary => RequireString("typography.fontFamily.primary");

    /// <summary>等宽字体族。</summary>
    public string FontFamilyMonospace => RequireString("typography.fontFamily.monospace");

    /// <summary>
    /// 按名称取字号。
    /// </summary>
    /// <param name="key">字号名，取值范围为 caption、body、subtitle、title、display。</param>
    /// <returns>字号数值。</returns>
    public int FontSize(string key) => RequireInt($"typography.size.{RequireKey(key)}");

    /// <summary>
    /// 按名称取字重。
    /// </summary>
    /// <param name="key">字重名，取值范围为 regular、medium、bold。</param>
    /// <returns>字重数值。</returns>
    public int FontWeight(string key) => RequireInt($"typography.weight.{RequireKey(key)}");

    /// <summary>
    /// 按名称取圆角半径。
    /// </summary>
    /// <param name="key">圆角名，取值范围为 sm、md、lg、full。</param>
    /// <returns>圆角半径。</returns>
    public int Radius(string key) => RequireInt($"radius.{RequireKey(key)}");

    /// <summary>
    /// 按名称取动效时长。
    /// </summary>
    /// <param name="key">时长名，取值范围为 fast、normal、slow。</param>
    /// <returns>动效时长，单位为毫秒。</returns>
    public int MotionDuration(string key) => RequireInt($"motion.duration.{RequireKey(key)}");

    /// <summary>
    /// 按名称取投影描述。
    /// </summary>
    /// <param name="key">投影名，取值范围为 card、dialog。</param>
    /// <returns>投影描述文本。</returns>
    public string Elevation(string key) => RequireString($"elevation.{RequireKey(key)}");

    /// <summary>
    /// 解析设计令牌 JSON 文本。
    /// </summary>
    /// <param name="json">设计令牌 JSON 文本。</param>
    /// <returns>解析后的设计令牌访问器。</returns>
    /// <exception cref="XBearException">JSON 非法或顶层不是对象时抛出。</exception>
    public static DesignTokens Parse(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(
                json,
                nodeOptions: null,
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
        }
        catch (JsonException ex)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "设计令牌不是合法 JSON。",
                "修正 JSON 语法后重试；跨端契约文件不应由单端自行改动。",
                ex);
        }

        if (node is not JsonObject root)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "设计令牌的顶层必须是对象。");
        }

        return new DesignTokens(root);
    }

    private static string RequireKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "设计令牌键名不能为空。");
        }

        return key;
    }

    private JsonNode RequireNode(string path)
    {
        var segments = path.Split('.');
        JsonNode? current = _root;

        foreach (var segment in segments)
        {
            if (current is not JsonObject obj ||
                !obj.TryGetPropertyValue(segment, out var next) ||
                next is null)
            {
                throw MissingToken(path);
            }

            current = next;
        }

        return current;
    }

    /// <summary>
    /// 取节点值。节点形如 { "value": ... } 时向下展开一层，
    /// 以兼容令牌文件中裸值与带元数据对象两种写法。
    /// </summary>
    private JsonNode Unwrap(JsonNode node) =>
        node is JsonObject obj && obj.TryGetPropertyValue("value", out var inner) && inner is not null
            ? inner
            : node;

    private string RequireString(string path)
    {
        var node = Unwrap(RequireNode(path));
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        throw new XBearException(
            ErrorCategory.Spec,
            $"设计令牌 {path} 的值不是字符串。");
    }

    private double RequireNumber(string path)
    {
        var node = Unwrap(RequireNode(path));
        if (node is JsonValue value && value.TryGetValue<double>(out var number))
        {
            return number;
        }

        throw new XBearException(
            ErrorCategory.Spec,
            $"设计令牌 {path} 的值不是数值。");
    }

    private int RequireInt(string path)
    {
        var number = RequireNumber(path);
        return checked((int)Math.Round(number, MidpointRounding.AwayFromZero));
    }

    private static XBearException MissingToken(string path) =>
        new(
            ErrorCategory.Spec,
            $"设计令牌缺少键 {path}。",
            "设计令牌是双端一致性的根基，不允许静默兜底；新增令牌需两端同步确认。");
}