using System.Text.Json.Serialization;
using XBear.Core.Serialization;

namespace XBear.Core.Spec;

/// <summary>
/// 检测面指标台账，对应 spec/detection-surface.json。
/// 每条指标是一次可复现的实例内检查：一条检测命令、一句期望结果、一个实测状态。
/// </summary>
/// <remarks>
/// 台账测的是「像不像真机」而不是「能不能用」，与 image.schema.json 的保真度门槛
/// 是两个方向相反的度量，不可互相替代、不可互相推导。台账不是镜像准入门槛，
/// 未通过的指标只登记为已知暴露。
/// </remarks>
public sealed class DetectionSurfaceSpec
{
    /// <summary>台账契约自身的版本，与 spec/version.json 的 spec.version 同源。</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("semantics")]
    public DetectionSurfaceSemantics Semantics { get; set; } = new();

    [JsonPropertyName("execution")]
    public DetectionSurfaceExecution Execution { get; set; } = new();

    /// <summary>按检测面分层组织的指标组，组的顺序即从固件到跨层交叉校验的顺序。</summary>
    [JsonPropertyName("groups")]
    public List<DetectionSurfaceGroup> Groups { get; set; } = new();

    /// <summary>状态回填纪律。</summary>
    [JsonPropertyName("fillPolicy")]
    public List<string> FillPolicy { get; set; } = new();

    /// <summary>枚举全部指标，保持台账内部的声明顺序。</summary>
    /// <returns>跨全部指标组的指标序列。</returns>
    public IEnumerable<DetectionMetric> AllMetrics() =>
        Groups.SelectMany(group => group.Metrics);

    /// <summary>按标识查指标，查不到时返回 null。</summary>
    /// <param name="id">指标标识。</param>
    /// <returns>匹配的指标，未找到时为 null。</returns>
    public DetectionMetric? FindMetric(string id) =>
        AllMetrics().FirstOrDefault(metric => string.Equals(metric.Id, id, StringComparison.Ordinal));

    /// <summary>按标识查指标，查不到时抛出异常。</summary>
    /// <param name="id">指标标识。</param>
    /// <returns>匹配的指标。</returns>
    /// <exception cref="Diagnostics.XBearException">标识不存在时抛出。</exception>
    public DetectionMetric RequireMetric(string id) =>
        FindMetric(id) ?? throw new Diagnostics.XBearException(
            Diagnostics.ErrorCategory.Spec,
            $"检测面指标台账缺少标识为 {id} 的条目。",
            "检测面台账是跨端契约，新增指标需两端同步确认。");
}

/// <summary>检测面指标的权威语义声明，方向、载体与处置三条不可省略。</summary>
public sealed class DetectionSurfaceSemantics
{
    /// <summary>测什么：实例在检测方视角下是否像一台真机。</summary>
    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    /// <summary>通过的方向：pass 表示未暴露，fail 表示已暴露。</summary>
    [JsonPropertyName("stateDirection")]
    public string StateDirection { get; set; } = string.Empty;

    /// <summary>台账的载体位置，与单实例的通道接通记录不是同一份记录。</summary>
    [JsonPropertyName("carrier")]
    public string Carrier { get; set; } = string.Empty;

    /// <summary>未通过时的处置：不淘汰镜像，只登记为已知暴露。</summary>
    [JsonPropertyName("admission")]
    public string Admission { get; set; } = string.Empty;
}

/// <summary>检测命令的执行前提：命令在实例内执行，宿主侧只负责采集与比对。</summary>
public sealed class DetectionSurfaceExecution
{
    /// <summary>执行域。</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    /// <summary>占位符写法：命令中的尖括号片段由宿主侧代入实例实际值。</summary>
    [JsonPropertyName("placeholder")]
    public string Placeholder { get; set; } = string.Empty;
}

/// <summary>一个检测面分层，其指标构成该层的暴露面。</summary>
public sealed class DetectionSurfaceGroup
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("metrics")]
    public List<DetectionMetric> Metrics { get; set; } = new();
}

/// <summary>单条检测面指标。</summary>
public sealed class DetectionMetric
{
    /// <summary>指标标识。台账内的标识空间独立于审计发现的编号空间，两者不可混用。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>检查项，一句话说明这条指标在查什么。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>该指标锚定的审计发现编号，每条指标都必须能回溯到审计中的既有发现。</summary>
    [JsonPropertyName("auditRefs")]
    public List<string> AuditRefs { get; set; } = new();

    /// <summary>检测命令，在实例内执行。</summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    /// <summary>期望结果，写可机械判定的结论。</summary>
    [JsonPropertyName("expected")]
    public string Expected { get; set; } = string.Empty;

    /// <summary>实测状态。只能填真实实测结论，未实测一律保持 untested。</summary>
    [JsonPropertyName("status")]
    public DetectionMetricStatus Status { get; set; } = DetectionMetricStatus.Untested;

    /// <summary>是否为反向功能指标。判定为 true 时 pass 表示功能正常，与其余指标的通过方向相反。</summary>
    [JsonPropertyName("reverseFunctional")]
    public bool ReverseFunctional { get; set; }

    /// <summary>最近一次实测的时刻，必须带时区偏移。状态为 untested 时保持 null。</summary>
    [JsonPropertyName("measuredAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MeasuredAt { get; set; }

    /// <summary>可复现的验证命令与关键输出。状态脱离 untested 时必须补齐。</summary>
    [JsonPropertyName("evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Evidence { get; set; }
}

/// <summary>
/// 检测面指标状态。与保真度的 <see cref="VerificationState"/> 取值同形但方向相反：
/// 这里的 pass 表示未暴露，不表示功能可用，因此单独建模以免两处互相套用。
/// </summary>
[JsonConverter(typeof(LowerCaseEnumConverter<DetectionMetricStatus>))]
public enum DetectionMetricStatus
{
    /// <summary>尚未实测。</summary>
    Untested = 0,

    /// <summary>实测未暴露。</summary>
    Pass = 1,

    /// <summary>实测已暴露。</summary>
    Fail = 2
}