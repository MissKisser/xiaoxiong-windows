using System.Globalization;
using System.Text.Json.Serialization;
using XBear.Core.Diagnostics;

namespace XBear.Core.Spec;

/// <summary>
/// 版本契约文档，字段与 spec/version.json 严格对应。
/// 产品版本与规格版本是两个各自独立递增的序列，
/// 任何一端都不得硬编码版本字符串，也不得用其中一个推导另一个。
/// </summary>
public sealed class VersionDocument
{
    /// <summary>产品标识与产品版本序列。</summary>
    [JsonPropertyName("product")]
    public ProductIdentity Product { get; set; } = new();

    /// <summary>共享规格层版本序列。</summary>
    [JsonPropertyName("spec")]
    public SpecRelease Spec { get; set; } = new();

    /// <summary>术语表版本与规格层版本的关系约定。</summary>
    [JsonPropertyName("terminology")]
    public TerminologyVersionPolicy Terminology { get; set; } = new();

    /// <summary>构建标识的拼装约定。</summary>
    [JsonPropertyName("build")]
    public BuildIdPolicy Build { get; set; } = new();

    /// <summary>两个序列各自的递增规则。</summary>
    [JsonPropertyName("rules")]
    public List<VersionRule> Rules { get; set; } = new();

    /// <summary>两个序列之间的防漂移约定。</summary>
    [JsonPropertyName("consistency")]
    public VersionConsistency Consistency { get; set; } = new();

    /// <summary>
    /// 解析产品版本序列的当前取值。
    /// </summary>
    /// <returns>产品版本的语义版本值。</returns>
    /// <exception cref="XBearException">版本号不是三段式语义版本时抛出。</exception>
    public SemanticVersion ParseProductVersion() => SemanticVersion.Parse(Product.Version);

    /// <summary>
    /// 解析规格层版本序列的当前取值。
    /// </summary>
    /// <returns>规格层版本的语义版本值。</returns>
    /// <exception cref="XBearException">版本号不是三段式语义版本时抛出。</exception>
    public SemanticVersion ParseSpecVersion() => SemanticVersion.Parse(Spec.Version);

    /// <summary>
    /// 按规则名查找递增规则，忽略大小写。
    /// </summary>
    /// <param name="level">规则名，取值范围见 <see cref="VersionBumpRules"/>。</param>
    /// <returns>匹配的规则，未找到时为 null。</returns>
    public VersionRule? FindRule(string level) =>
        Rules.FirstOrDefault(r => string.Equals(r.Level, level, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 按规则名查找递增规则，找不到时抛出异常。
    /// </summary>
    /// <param name="level">规则名。</param>
    /// <returns>匹配的递增规则。</returns>
    /// <exception cref="XBearException">规则名不存在时抛出。</exception>
    public VersionRule RequireRule(string level) =>
        FindRule(level) ?? throw new XBearException(
            ErrorCategory.Spec,
            $"版本契约缺少级别为 {level} 的递增规则。",
            "递增规则由共享规格定义，新增级别需两端同步确认。");

    /// <summary>
    /// 按构建标识模板拼装构建标识，模板与字段取值均来自契约文件，不自行拼接。
    /// </summary>
    /// <param name="commitShort">构建时所在提交的短哈希，无版本控制信息时传入 unknown。</param>
    /// <returns>拼装好的构建标识。</returns>
    /// <exception cref="XBearException">模板为空、声明了未知字段或存在未替换的占位符时抛出。</exception>
    public string FormatBuildId(string commitShort)
    {
        if (string.IsNullOrWhiteSpace(Build.IdFormat))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "版本契约未声明构建标识模板。",
                "关于页与诊断包统一引用该模板，不允许各处自行拼接构建标识。");
        }

        var id = Build.IdFormat;

        foreach (var field in Build.Fields.Keys)
        {
            id = id.Replace(
                "{" + field + "}",
                ResolveBuildField(field, commitShort),
                StringComparison.Ordinal);
        }

        if (id.Contains('{', StringComparison.Ordinal) || id.Contains('}', StringComparison.Ordinal))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"构建标识模板 {Build.IdFormat} 存在未声明的占位符。",
                "模板中的每个占位符都必须在 build.fields 中声明。");
        }

        return id;
    }

    private string ResolveBuildField(string field, string commitShort)
    {
        if (string.Equals(field, BuildIdPolicy.ProductVersionField, StringComparison.Ordinal))
        {
            return Product.Version;
        }

        if (string.Equals(field, BuildIdPolicy.CommitShortField, StringComparison.Ordinal))
        {
            return commitShort;
        }

        throw new XBearException(
            ErrorCategory.Spec,
            $"构建标识声明了未知字段 {field}。",
            "可解析的字段由契约固定，新增字段需两端同步确认。");
    }
}

/// <summary>产品标识与产品版本序列。</summary>
public sealed class ProductIdentity
{
    /// <summary>产品标识，跨端统一引用。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>产品中文名。</summary>
    [JsonPropertyName("nameZh")]
    public string NameZh { get; set; } = string.Empty;

    /// <summary>产品英文名。</summary>
    [JsonPropertyName("nameEn")]
    public string NameEn { get; set; } = string.Empty;

    /// <summary>产品版本，取值来自契约文件，不得硬编码。</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;
}

/// <summary>共享规格层版本序列。</summary>
public sealed class SpecRelease
{
    /// <summary>规格层版本，覆盖 spec/ 下全部机器可读契约。</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>规格层版本覆盖范围的说明。</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

/// <summary>术语表版本与规格层版本的关系约定。</summary>
public sealed class TerminologyVersionPolicy
{
    /// <summary>术语表版本所在的字段位置。</summary>
    [JsonPropertyName("versionField")]
    public string VersionField { get; set; } = string.Empty;

    /// <summary>术语表版本的递增条件。</summary>
    [JsonPropertyName("rule")]
    public string Rule { get; set; } = string.Empty;

    /// <summary>术语表版本与规格层版本的关系说明。</summary>
    [JsonPropertyName("relationship")]
    public string Relationship { get; set; } = string.Empty;
}

/// <summary>构建标识的拼装约定。</summary>
public sealed class BuildIdPolicy
{
    /// <summary>产品版本字段名。</summary>
    public const string ProductVersionField = "productVersion";

    /// <summary>提交短哈希字段名。</summary>
    public const string CommitShortField = "commitShort";

    /// <summary>构建标识模板，字段名以花括号包裹。</summary>
    [JsonPropertyName("idFormat")]
    public string IdFormat { get; set; } = string.Empty;

    /// <summary>构建标识的用途说明。</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>模板中每个字段的取值来源说明。</summary>
    [JsonPropertyName("fields")]
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>单个级别的递增规则。</summary>
public sealed class VersionRule
{
    /// <summary>规则名，取值范围见 <see cref="VersionBumpRules"/>。</summary>
    [JsonPropertyName("level")]
    public string Level { get; set; } = string.Empty;

    /// <summary>触发该级别的变更情形。</summary>
    [JsonPropertyName("when")]
    public string When { get; set; } = string.Empty;

    /// <summary>该级别要求两端执行的动作。</summary>
    [JsonPropertyName("effects")]
    public List<string> Effects { get; set; } = new();

    /// <summary>
    /// 解析规则名对应的递增级别。
    /// </summary>
    /// <param name="level">解析出的递增级别。</param>
    /// <returns>规则名属于已定义级别时为 true。</returns>
    public bool TryResolveLevel(out VersionBumpLevel level) =>
        VersionBumpRules.TryParseLevel(Level, out level);

    /// <summary>
    /// 该规则作用在哪个版本序列上。
    /// </summary>
    /// <returns>作用的产品或规格序列，规则名未定义时为 null。</returns>
    public VersionSequence? AffectedSequence()
    {
        TryResolveLevel(out var level);
        return VersionBumpRules.TryGetSequence(level, out var sequence) ? sequence : null;
    }
}

/// <summary>两个版本序列之间的防漂移约定。</summary>
public sealed class VersionConsistency
{
    /// <summary>两个序列相互独立的说明。</summary>
    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    /// <summary>读取方不得硬编码版本字符串的约束。</summary>
    [JsonPropertyName("driftPolicy")]
    public string DriftPolicy { get; set; } = string.Empty;

    /// <summary>规格层版本递增对两端解析能力的要求。</summary>
    [JsonPropertyName("schemaCompatibility")]
    public string SchemaCompatibility { get; set; } = string.Empty;
}

/// <summary>可独立递增的版本序列。</summary>
public enum VersionSequence
{
    /// <summary>产品版本序列。</summary>
    Product = 0,

    /// <summary>规格层版本序列。</summary>
    Spec = 1
}

/// <summary>版本递增级别，两个序列各自拥有一套同名的三档级别。</summary>
public enum VersionBumpLevel
{
    /// <summary>规格层主版本递增。</summary>
    Major = 0,

    /// <summary>规格层次版本递增。</summary>
    Minor = 1,

    /// <summary>规格层修订号递增。</summary>
    Patch = 2,

    /// <summary>产品主版本递增。</summary>
    ProductMajor = 3,

    /// <summary>产品次版本递增。</summary>
    ProductMinor = 4,

    /// <summary>产品修订号递增。</summary>
    ProductPatch = 5
}

/// <summary>
/// 递增级别与版本序列的对应关系。级别名取自版本契约的 rules 数组，
/// 此处只做名称到枚举的映射，不内嵌任何版本取值。
/// </summary>
public static class VersionBumpRules
{
    private static readonly (string Level, VersionBumpLevel Bump, VersionSequence Sequence)[] Table =
    {
        ("major", VersionBumpLevel.Major, VersionSequence.Spec),
        ("minor", VersionBumpLevel.Minor, VersionSequence.Spec),
        ("patch", VersionBumpLevel.Patch, VersionSequence.Spec),
        ("product-major", VersionBumpLevel.ProductMajor, VersionSequence.Product),
        ("product-minor", VersionBumpLevel.ProductMinor, VersionSequence.Product),
        ("product-patch", VersionBumpLevel.ProductPatch, VersionSequence.Product)
    };

    /// <summary>规格层版本的三个级别名。</summary>
    public static IReadOnlyList<string> SpecLevels { get; } =
        Table.Where(entry => entry.Sequence == VersionSequence.Spec)
            .Select(entry => entry.Level)
            .ToArray();

    /// <summary>产品版本的三个级别名。</summary>
    public static IReadOnlyList<string> ProductLevels { get; } =
        Table.Where(entry => entry.Sequence == VersionSequence.Product)
            .Select(entry => entry.Level)
            .ToArray();

    /// <summary>全部级别名。</summary>
    public static IReadOnlyList<string> AllLevels { get; } =
        Table.Select(entry => entry.Level).ToArray();

    /// <summary>
    /// 判断级别名是否为已定义级别，忽略大小写。
    /// </summary>
    /// <param name="level">级别名。</param>
    /// <returns>已定义时为 true。</returns>
    public static bool IsKnownLevel(string level) => TryParseLevel(level, out _);

    /// <summary>
    /// 把级别名解析为递增级别，忽略大小写。
    /// </summary>
    /// <param name="level">级别名。</param>
    /// <param name="parsed">解析出的递增级别。</param>
    /// <returns>级别名已定义时为 true。</returns>
    public static bool TryParseLevel(string level, out VersionBumpLevel parsed)
    {
        parsed = default;

        if (string.IsNullOrWhiteSpace(level))
        {
            return false;
        }

        foreach (var entry in Table)
        {
            if (string.Equals(entry.Level, level, StringComparison.OrdinalIgnoreCase))
            {
                parsed = entry.Bump;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 查询递增级别作用的版本序列。
    /// </summary>
    /// <param name="level">递增级别。</param>
    /// <param name="sequence">作用的产品或规格序列。</param>
    /// <returns>该级别已定义时为 true。</returns>
    public static bool TryGetSequence(VersionBumpLevel level, out VersionSequence sequence)
    {
        foreach (var entry in Table)
        {
            if (entry.Bump == level)
            {
                sequence = entry.Sequence;
                return true;
            }
        }

        sequence = default;
        return false;
    }
}

/// <summary>三段式语义版本值，用于表示两个独立版本序列的当前取值。</summary>
public readonly struct SemanticVersion : IEquatable<SemanticVersion>
{
    /// <summary>主版本号。</summary>
    public int Major { get; }

    /// <summary>次版本号。</summary>
    public int Minor { get; }

    /// <summary>修订号。</summary>
    public int Patch { get; }

    private SemanticVersion(int major, int minor, int patch)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    /// <summary>
    /// 解析三段式语义版本号。
    /// </summary>
    /// <param name="text">版本号文本。</param>
    /// <param name="version">解析出的版本值。</param>
    /// <returns>文本为三段非负整数时为 true。</returns>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!TryParseSegment(parts[0], out var major) ||
            !TryParseSegment(parts[1], out var minor) ||
            !TryParseSegment(parts[2], out var patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch);
        return true;
    }

    /// <summary>
    /// 解析三段式语义版本号，失败时抛出规格类异常。
    /// </summary>
    /// <param name="text">版本号文本。</param>
    /// <returns>解析出的版本值。</returns>
    /// <exception cref="XBearException">文本不是三段式语义版本时抛出。</exception>
    public static SemanticVersion Parse(string? text)
    {
        if (TryParse(text, out var version))
        {
            return version;
        }

        throw new XBearException(
            ErrorCategory.Spec,
            $"版本号 {text ?? "（空）"} 不是三段式语义版本。",
            "版本取值来自共享规格的版本契约，单端不得自行拼接版本字符串。");
    }

    /// <summary>
    /// 按级别推导出递增后的版本值。
    /// </summary>
    /// <param name="level">递增级别，主版本递增时次版本与修订号归零。</param>
    /// <returns>递增后的版本值。</returns>
    /// <exception cref="XBearException">级别未定义时抛出。</exception>
    public SemanticVersion Next(VersionBumpLevel level)
    {
        switch (level)
        {
            case VersionBumpLevel.Major:
            case VersionBumpLevel.ProductMajor:
                return new SemanticVersion(Major + 1, 0, 0);

            case VersionBumpLevel.Minor:
            case VersionBumpLevel.ProductMinor:
                return new SemanticVersion(Major, Minor + 1, 0);

            case VersionBumpLevel.Patch:
            case VersionBumpLevel.ProductPatch:
                return new SemanticVersion(Major, Minor, Patch + 1);

            default:
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"递增级别 {level} 未定义。",
                    "级别名由版本契约的 rules 数组给出，不允许单端自行扩充。");
        }
    }

    /// <summary>返回三段式文本表示。</summary>
    /// <returns>形如 <c>1.2.0</c> 的文本。</returns>
    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    /// <summary>判断两个版本值是否相等。</summary>
    /// <param name="other">待比较的版本值。</param>
    /// <returns>三段取值全部相同时为 true。</returns>
    public bool Equals(SemanticVersion other) =>
        Major == other.Major && Minor == other.Minor && Patch == other.Patch;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch);

    /// <summary>判断两个版本值是否相等。</summary>
    /// <param name="left">左侧版本值。</param>
    /// <param name="right">右侧版本值。</param>
    /// <returns>相等时为 true。</returns>
    public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);

    /// <summary>判断两个版本值是否不等。</summary>
    /// <param name="left">左侧版本值。</param>
    /// <param name="right">右侧版本值。</param>
    /// <returns>不等时为 true。</returns>
    public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);

    private static bool TryParseSegment(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
