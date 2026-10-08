using System.Text.Json.Serialization;
using XBear.Core.Diagnostics;

namespace XBear.Core.Spec;

/// <summary>
/// 性能基线文档，字段与 spec/baseline.json 严格对应。
/// 每个指标的 target 门限与 measured 实测值分列存储，
/// measured 语义上可以为 null，表示该指标尚未取得可复核的实测值。
/// </summary>
public sealed class PerformanceBaseline
{
    /// <summary>基线文档自身的版本。</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>基线状态，取值见 <see cref="BaselineStatus"/>。</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>当前状态的说明，交代实测列能否回填的前提。</summary>
    [JsonPropertyName("statusNote")]
    public string StatusNote { get; set; } = string.Empty;

    /// <summary>各类指标的单位符号。</summary>
    [JsonPropertyName("units")]
    public BaselineUnits Units { get; set; } = new();

    /// <summary>指标定义集合。</summary>
    [JsonPropertyName("metrics")]
    public List<BaselineMetric> Metrics { get; set; } = new();

    /// <summary>多实例密度档位与并行数基线。</summary>
    [JsonPropertyName("density")]
    public BaselineDensity Density { get; set; } = new();

    /// <summary>实测值回填规则。</summary>
    [JsonPropertyName("fillPolicy")]
    public List<string> FillPolicy { get; set; } = new();

    /// <summary>
    /// 解析基线文档自身的版本。
    /// </summary>
    /// <returns>基线文档的语义版本值。</returns>
    /// <exception cref="XBearException">版本号不是三段式语义版本时抛出。</exception>
    public SemanticVersion ParseVersion() => SemanticVersion.Parse(Version);

    /// <summary>
    /// 判断基线是否只含目标值、尚无实测值。
    /// </summary>
    /// <returns>状态为仅目标值时为 true。</returns>
    public bool IsTargetsOnly() =>
        string.Equals(Status, BaselineStatus.TargetsOnly, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 按指标标识查找指标，忽略大小写。
    /// </summary>
    /// <param name="id">指标标识。</param>
    /// <returns>匹配的指标，未找到时为 null。</returns>
    public BaselineMetric? Find(string id) =>
        Metrics.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 按指标标识查找指标，找不到时抛出异常。
    /// </summary>
    /// <param name="id">指标标识。</param>
    /// <returns>匹配的指标。</returns>
    /// <exception cref="XBearException">指标标识不存在时抛出。</exception>
    public BaselineMetric Require(string id) =>
        Find(id) ?? throw new XBearException(
            ErrorCategory.Spec,
            $"性能基线缺少标识为 {id} 的指标。",
            "指标集合是跨端验收依据，新增指标需两端同步确认。");

    /// <summary>
    /// 列出尚未回填实测值的指标。
    /// </summary>
    /// <returns>measured 为 null 的指标集合。</returns>
    public IReadOnlyList<BaselineMetric> PendingMetrics() =>
        Metrics.Where(m => !m.IsMeasured()).ToArray();

    /// <summary>
    /// 列出已声明受阻原因的指标。
    /// </summary>
    /// <returns>填有 blockedBy 的指标集合。</returns>
    public IReadOnlyList<BaselineMetric> BlockedMetrics() =>
        Metrics.Where(m => m.IsBlocked()).ToArray();
}

/// <summary>基线文档的状态取值。</summary>
public static class BaselineStatus
{
    /// <summary>只含一期目标值，实测列全部为 null。</summary>
    public const string TargetsOnly = "targets-only";
}

/// <summary>各类指标的单位符号，供指标声明的单位引用与核对。</summary>
public sealed class BaselineUnits
{
    /// <summary>时长单位类别名。</summary>
    public const string DurationKind = "duration";

    /// <summary>延迟单位类别名。</summary>
    public const string LatencyKind = "latency";

    /// <summary>帧率单位类别名。</summary>
    public const string FramerateKind = "framerate";

    /// <summary>内存单位类别名。</summary>
    public const string MemoryKind = "memory";

    /// <summary>时长单位符号。</summary>
    [JsonPropertyName(DurationKind)]
    public string Duration { get; set; } = string.Empty;

    /// <summary>延迟单位符号。</summary>
    [JsonPropertyName(LatencyKind)]
    public string Latency { get; set; } = string.Empty;

    /// <summary>帧率单位符号。</summary>
    [JsonPropertyName(FramerateKind)]
    public string Framerate { get; set; } = string.Empty;

    /// <summary>内存单位符号。</summary>
    [JsonPropertyName(MemoryKind)]
    public string Memory { get; set; } = string.Empty;

    /// <summary>
    /// 按单位符号反查单位类别名。
    /// </summary>
    /// <param name="symbol">单位符号。</param>
    /// <returns>匹配的单位类别名，未声明时为 null。</returns>
    public string? FindBySymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        foreach (var candidate in Kinds())
        {
            if (string.Equals(candidate.Symbol, symbol, StringComparison.Ordinal))
            {
                return candidate.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// 判断单位符号是否已在单位表中声明。
    /// </summary>
    /// <param name="symbol">单位符号。</param>
    /// <returns>已声明时为 true。</returns>
    public bool ContainsSymbol(string symbol) => FindBySymbol(symbol) is not null;

    /// <summary>
    /// 枚举全部已声明的单位类别。
    /// </summary>
    /// <returns>单位类别名与符号的集合。</returns>
    public IEnumerable<BaselineUnitKind> Kinds()
    {
        yield return new BaselineUnitKind(DurationKind, Duration);
        yield return new BaselineUnitKind(LatencyKind, Latency);
        yield return new BaselineUnitKind(FramerateKind, Framerate);
        yield return new BaselineUnitKind(MemoryKind, Memory);
    }
}

/// <summary>单位表中的一个类别，类别名对应单位表的键，符号为指标声明时引用的取值。</summary>
/// <param name="Name">单位类别名。</param>
/// <param name="Symbol">单位符号。</param>
public sealed record BaselineUnitKind(string Name, string Symbol);

/// <summary>单项性能指标的定义、目标门限与实测值。</summary>
public sealed class BaselineMetric
{
    /// <summary>指标标识，代码与验收表引用该字段。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>指标中文名。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>测量口径，交代从哪里开始算到哪里结束。</summary>
    [JsonPropertyName("measure")]
    public string Measure { get; set; } = string.Empty;

    /// <summary>目标门限。</summary>
    [JsonPropertyName("target")]
    public BaselineThreshold Target { get; set; } = new();

    /// <summary>实测值，未取得可复核的实测值时为 null。</summary>
    [JsonPropertyName("measured")]
    public double? Measured { get; set; }

    /// <summary>负责回填实测值的端。</summary>
    [JsonPropertyName("owner")]
    public string Owner { get; set; } = string.Empty;

    /// <summary>实测受阻原因，指标当前无法测量时填写。</summary>
    [JsonPropertyName("blockedBy")]
    public string? BlockedBy { get; set; }

    /// <summary>
    /// 判断该指标是否已回填实测值。
    /// </summary>
    /// <returns>measured 非 null 时为 true。</returns>
    public bool IsMeasured() => Measured.HasValue;

    /// <summary>
    /// 判断该指标是否声明了受阻原因。
    /// </summary>
    /// <returns>填有 blockedBy 时为 true。</returns>
    public bool IsBlocked() => !string.IsNullOrWhiteSpace(BlockedBy);

    /// <summary>
    /// 用实测值比对目标门限。
    /// </summary>
    /// <returns>达标为 true，未达标为 false，实测值缺失时为 null。</returns>
    public bool? MeasuredMeetsTarget() =>
        Measured.HasValue ? Target.Matches(Measured.Value) : null;
}

/// <summary>带比较运算符的阈值，供指标目标值与密度并行数共用。</summary>
public sealed class BaselineThreshold
{
    /// <summary>比较运算符，取值见 <see cref="ComparisonOperators"/>。</summary>
    [JsonPropertyName("op")]
    public string Op { get; set; } = string.Empty;

    /// <summary>阈值数值。</summary>
    [JsonPropertyName("value")]
    public double Value { get; set; }

    /// <summary>单位符号，无量纲阈值（如并行数）时为 null。</summary>
    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    /// <summary>
    /// 判断该阈值使用的比较运算符是否为已定义取值。
    /// </summary>
    /// <returns>运算符已定义时为 true。</returns>
    public bool HasKnownOperator() => ComparisonOperators.IsKnown(Op);

    /// <summary>
    /// 按比较运算符判断给定取值是否满足阈值。
    /// </summary>
    /// <param name="candidate">待比对的取值。</param>
    /// <returns>满足阈值时为 true。</returns>
    /// <exception cref="XBearException">比较运算符未定义时抛出。</exception>
    public bool Matches(double candidate) => ComparisonOperators.Matches(Op, Value, candidate);
}

/// <summary>多实例密度档位与并行数基线。</summary>
public sealed class BaselineDensity
{
    /// <summary>密度必须绑定宿主内存配置的说明。</summary>
    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    /// <summary>按宿主内存分档的并行数目标。</summary>
    [JsonPropertyName("profiles")]
    public List<BaselineDensityProfile> Profiles { get; set; } = new();

    /// <summary>并行数达标的附加条件。</summary>
    [JsonPropertyName("regression")]
    public DensityRegression Regression { get; set; } = new();

    /// <summary>
    /// 按档位名查找密度档位，找不到时抛出异常。
    /// </summary>
    /// <param name="tier">档位名。</param>
    /// <returns>匹配的密度档位。</returns>
    /// <exception cref="XBearException">档位不存在时抛出。</exception>
    public BaselineDensityProfile RequireTier(string tier) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Tier, tier, StringComparison.Ordinal)) ??
        throw new XBearException(
            ErrorCategory.Spec,
            $"性能基线缺少档位为 {tier} 的密度配置。",
            "密度档位属于跨端验收依据，档位集合由共享规格定义。");
}

/// <summary>单个宿主内存档位下的并行实例数目标。</summary>
public sealed class BaselineDensityProfile
{
    /// <summary>档位名。</summary>
    [JsonPropertyName("tier")]
    public string Tier { get; set; } = string.Empty;

    /// <summary>该档位的宿主内存容量，单位为 GB。</summary>
    [JsonPropertyName("hostMemoryGB")]
    public double HostMemoryGB { get; set; }

    /// <summary>该档位下的并行实例数目标。</summary>
    [JsonPropertyName("targetInstances")]
    public BaselineThreshold TargetInstances { get; set; } = new();

    /// <summary>实测并行数，未取得可复核的实测值时为 null。</summary>
    [JsonPropertyName("measured")]
    public double? Measured { get; set; }

    /// <summary>
    /// 判断该档位是否已回填实测并行数。
    /// </summary>
    /// <returns>measured 非 null 时为 true。</returns>
    public bool IsMeasured() => Measured.HasValue;
}

/// <summary>并行实例数达标的附加条件。</summary>
public sealed class DensityRegression
{
    /// <summary>附加条件的说明。</summary>
    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    /// <summary>允许的最大延迟劣化百分比。</summary>
    [JsonPropertyName("maxDegradationPercent")]
    public double MaxDegradationPercent { get; set; }

    /// <summary>
    /// 判断给定的延迟劣化百分比是否仍在容许范围内。
    /// </summary>
    /// <param name="degradationPercent">实测的延迟劣化百分比。</param>
    /// <returns>未超过容许上限时为 true。</returns>
    public bool AcceptsDegradation(double degradationPercent) =>
        degradationPercent <= MaxDegradationPercent;
}

/// <summary>阈值支持的比较运算符。</summary>
public static class ComparisonOperators
{
    /// <summary>小于等于。</summary>
    public const string LessOrEqual = "<=";

    /// <summary>大于等于。</summary>
    public const string GreaterOrEqual = ">=";

    /// <summary>小于。</summary>
    public const string LessThan = "<";

    /// <summary>大于。</summary>
    public const string GreaterThan = ">";

    /// <summary>等于。</summary>
    public const string Equal = "==";

    /// <summary>
    /// 判断运算符是否为已定义取值。
    /// </summary>
    /// <param name="op">运算符文本。</param>
    /// <returns>已定义时为 true。</returns>
    public static bool IsKnown(string op) =>
        op is LessOrEqual or GreaterOrEqual or LessThan or GreaterThan or Equal;

    /// <summary>
    /// 按运算符比较取值与阈值。
    /// </summary>
    /// <param name="op">运算符文本。</param>
    /// <param name="threshold">阈值。</param>
    /// <param name="candidate">待比对的取值。</param>
    /// <returns>满足阈值时为 true。</returns>
    /// <exception cref="XBearException">运算符未定义时抛出。</exception>
    public static bool Matches(string op, double threshold, double candidate)
    {
        switch (op)
        {
            case LessOrEqual:
                return candidate <= threshold;

            case GreaterOrEqual:
                return candidate >= threshold;

            case LessThan:
                return candidate < threshold;

            case GreaterThan:
                return candidate > threshold;

            case Equal:
                return candidate == threshold;

            default:
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"阈值使用了未定义的比较运算符 {op}。",
                    "运算符取值由基线契约约定，单端不得自行扩充。");
        }
    }
}
