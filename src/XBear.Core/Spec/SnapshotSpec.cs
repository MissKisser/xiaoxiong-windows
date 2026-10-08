using System.Text.Json;
using System.Text.Json.Serialization;
using XBear.Core.Serialization;

namespace XBear.Core.Spec;

/// <summary>
/// 实例快照的元数据，字段与 spec/schema/snapshot.schema.json 严格对应。
/// 快照由宿主持有，描述实例在某一时刻的状态，可用于把实例回退到该时刻。
/// </summary>
public sealed class SnapshotSpec
{
    /// <summary>快照契约自身的版本，与规格层版本同源。</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0.0";

    /// <summary>快照的全局唯一标识，删除后进墓碑记录不得自动复用。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>快照显示名，缺省时由两端从创建时刻渲染。</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>所属实例的标识，快照随实例一同删除。</summary>
    [JsonPropertyName("instanceRef")]
    public string InstanceRef { get; set; } = string.Empty;

    /// <summary>来源镜像的标识，记录创建时刻实例所基于的镜像。</summary>
    [JsonPropertyName("imageRef")]
    public string ImageRef { get; set; } = string.Empty;

    /// <summary>快照创建时刻，带时区偏移的 ISO 8601 文本，按原样保留。</summary>
    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>快照状态。</summary>
    [JsonPropertyName("state")]
    public SnapshotState State { get; set; } = SnapshotState.Creating;

    /// <summary>父快照标识，缺省表示该实例的首个快照。</summary>
    [JsonPropertyName("parentRef")]
    public string? ParentRef { get; set; }

    /// <summary>用户为该快照写的备注，说明创建意图。</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>平台特有字段逃生舱，通用契约不解析其内部结构。</summary>
    [JsonPropertyName("platformConfig")]
    public JsonElement? PlatformConfig { get; set; }

    /// <summary>
    /// 判断该快照是否可用于恢复。只有磁盘链完整的 ready 快照允许恢复。
    /// </summary>
    /// <returns>可用于恢复时为 true。</returns>
    public bool IsRestorable() => State == SnapshotState.Ready;

    /// <summary>
    /// 判断该快照是否为某个实例的首个快照，即不存在父快照。
    /// </summary>
    /// <returns>为首个快照时为 true。</returns>
    public bool IsChainRoot() => string.IsNullOrEmpty(ParentRef);
}

/// <summary>
/// 快照状态。creating 与 failed 的磁盘链不可用于恢复，
/// failed 也不得据此销毁实例数据。
/// </summary>
[JsonConverter(typeof(LowerCaseEnumConverter<SnapshotState>))]
public enum SnapshotState
{
    /// <summary>宿主持有元数据但磁盘链尚未写完。</summary>
    Creating = 0,

    /// <summary>磁盘链完整，可用于恢复。</summary>
    Ready = 1,

    /// <summary>写入中断或校验不通过，不可用于恢复。</summary>
    Failed = 2
}
