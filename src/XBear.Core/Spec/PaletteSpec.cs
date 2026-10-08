using System.Text.Json;
using System.Text.Json.Nodes;
using XBear.Core.Diagnostics;

namespace XBear.Core.Spec;

/// <summary>
/// 一条品牌语义色记录：语义名、对应令牌键、取值与用途说明。
/// </summary>
/// <param name="Name">语义名，如 bear-brown。</param>
/// <param name="Token">对应的设计令牌键，如 color.brand.primary。</param>
/// <param name="Value">取值，与设计令牌保持一致。</param>
/// <param name="Description">用途说明。</param>
public sealed record BrandRole(string Name, string Token, string Value, string Description);

/// <summary>
/// 品牌调色板契约文档，对应 spec/brand/palette.json。
/// 将品牌语义角色映射到 design-tokens.json 的设计令牌键。
/// </summary>
public sealed class PaletteDocument
{
    /// <summary>品牌调色板名称。</summary>
    public string Name { get; }

    /// <summary>契约版本号。</summary>
    public string Version { get; }

    /// <summary>契约说明。</summary>
    public string Description { get; }

    /// <summary>设计令牌来源路径。</summary>
    public string Source { get; }

    /// <summary>语义色角色定义集合，顺序与 palette.json 严格一致。</summary>
    public IReadOnlyList<BrandRole> Roles { get; }

    /// <summary>语义色角色名到设计令牌键的映射契约字典。</summary>
    public IReadOnlyDictionary<string, string> RoleToTokenMap { get; }

    /// <summary>语义色角色名到完整角色定义的字典。</summary>
    public IReadOnlyDictionary<string, BrandRole> RolesByName { get; }

    /// <summary>
    /// 构造品牌调色板契约文档。
    /// </summary>
    /// <param name="name">契约名称。</param>
    /// <param name="version">契约版本。</param>
    /// <param name="description">契约说明。</param>
    /// <param name="source">令牌来源。</param>
    /// <param name="roles">语义色角色集合。</param>
    public PaletteDocument(
        string name,
        string version,
        string description,
        string source,
        IReadOnlyList<BrandRole> roles)
    {
        Name = name ?? string.Empty;
        Version = version ?? string.Empty;
        Description = description ?? string.Empty;
        Source = source ?? string.Empty;
        Roles = roles ?? Array.Empty<BrandRole>();
        RoleToTokenMap = Roles.ToDictionary(r => r.Name, r => r.Token, StringComparer.Ordinal);
        RolesByName = Roles.ToDictionary(r => r.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// 按角色名查找语义色角色。
    /// </summary>
    /// <param name="roleName">角色名，如 bear-brown。</param>
    /// <returns>匹配的角色定义，未找到时返回 null。</returns>
    public BrandRole? FindRole(string roleName) =>
        RolesByName.TryGetValue(roleName, out var role) ? role : null;

    /// <summary>
    /// 按角色名查找语义色角色，未找到时抛出异常。
    /// </summary>
    /// <param name="roleName">角色名。</param>
    /// <returns>匹配的角色定义。</returns>
    /// <exception cref="XBearException">角色未登记时抛出。</exception>
    public BrandRole RequireRole(string roleName) =>
        FindRole(roleName) ?? throw new XBearException(
            ErrorCategory.Spec,
            $"品牌配色映射缺少语义色角色 {roleName}。",
            "语义色角色由 spec/brand/palette.json 契约固定，新增角色需两端同步确认。");

    /// <summary>
    /// 获取语义色角色对应的设计令牌键。
    /// </summary>
    /// <param name="roleName">角色名。</param>
    /// <returns>设计令牌键。</returns>
    public string GetToken(string roleName) =>
        RequireRole(roleName).Token;

    /// <summary>
    /// 解析品牌配色映射 JSON 文本。
    /// </summary>
    /// <param name="json">palette.json 文本。</param>
    /// <returns>解析后的品牌调色板契约文档。</returns>
    /// <exception cref="XBearException">JSON 非法、缺少 roles 节点或语义色定义非法时抛出。</exception>
    public static PaletteDocument Parse(string json)
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
                "品牌配色映射不是合法 JSON。",
                "修正 brand/palette.json 语法后重试；该文件是双端共享契约。",
                ex);
        }

        if (node is not JsonObject root ||
            root["roles"] is not JsonObject roles)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "品牌配色映射缺少 roles 节点。");
        }

        var result = new List<BrandRole>(roles.Count);

        foreach (var entry in roles)
        {
            if (entry.Value is not JsonObject role)
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"品牌语义色 {entry.Key} 的定义不是对象。");
            }

            string token = RequireText(role, "token", entry.Key);
            string value = RequireText(role, "value", entry.Key);

            result.Add(new BrandRole(
                entry.Key,
                token,
                value,
                role["description"]?.GetValue<string>() ?? string.Empty));
        }

        if (result.Count == 0)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "品牌配色映射没有登记任何语义色。");
        }

        string name = root["name"]?.GetValue<string>() ?? string.Empty;
        string version = root["version"]?.GetValue<string>() ?? string.Empty;
        string description = root["description"]?.GetValue<string>() ?? string.Empty;
        string source = root["source"]?.GetValue<string>() ?? string.Empty;

        return new PaletteDocument(name, version, description, source, result);
    }

    private static string RequireText(JsonObject role, string property, string name)
    {
        if (role[property] is not JsonValue value ||
            !value.TryGetValue<string>(out string? text) ||
            string.IsNullOrWhiteSpace(text))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"品牌语义色 {name} 缺少字符串字段 {property}。");
        }

        return text;
    }
}
