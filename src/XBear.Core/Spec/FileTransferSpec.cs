using System.Text.Json;
using System.Text.Json.Serialization;
using XBear.Core.Serialization;

namespace XBear.Core.Spec;

/// <summary>
/// 传输任务，字段与 spec/schema/filetransfer.schema.json 严格对应。
/// 描述一次宿主与实例之间的文件复制从发起到终态的对外可观测字段，不绑定传输通道实现。
/// 任务随所属实例一同销毁，宿主侧持有。
/// </summary>
public sealed class FileTransferSpec
{
    /// <summary>传输契约自身的版本，与规格层版本同源。</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0.0";

    /// <summary>传输任务的标识，同一实例内唯一，进入终态后不得复用。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>所属实例的标识，引用实例契约的 id。</summary>
    [JsonPropertyName("instanceRef")]
    public string InstanceRef { get; set; } = string.Empty;

    /// <summary>
    /// 传输方向。契约取值域为 host-to-instance 与 instance-to-host，
    /// 字面量含连字符故以字符串承载。方向决定来源与目标各自落在哪个文件系统域，
    /// 不允许用同一条任务反向复用。
    /// </summary>
    [JsonPropertyName("direction")]
    public string Direction { get; set; } = string.Empty;

    /// <summary>本次传输的来源与目标路径，路径语义由方向决定，不自带域前缀。</summary>
    [JsonPropertyName("paths")]
    public FileTransferPaths Paths { get; set; } = new();

    /// <summary>来源路径是否被视为目录，缺省表示按单个文件处理。</summary>
    [JsonPropertyName("recursive")]
    public bool? Recursive { get; set; }

    /// <summary>目标已存在同名项时的处置策略，契约要求任务记录里显式写出实际生效值。</summary>
    [JsonPropertyName("overwrite")]
    public ConflictPolicy Overwrite { get; set; }

    /// <summary>传输进度的可观测快照，任务尚未进入传输中时缺省。</summary>
    [JsonPropertyName("progress")]
    public FileTransferProgress? Progress { get; set; }

    /// <summary>任务状态。</summary>
    [JsonPropertyName("state")]
    public FileTransferState State { get; set; } = FileTransferState.Queued;

    /// <summary>失败原因。失败时必须给出，取消时为 null。</summary>
    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; set; }

    /// <summary>任务受理时刻，带时区偏移。</summary>
    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>进入传输中的时刻，仍处于已受理未开始时为 null。</summary>
    [JsonPropertyName("startedAt")]
    public string? StartedAt { get; set; }

    /// <summary>进入终态的时刻，仅在终态下非 null。</summary>
    [JsonPropertyName("finishedAt")]
    public string? FinishedAt { get; set; }

    /// <summary>平台特有字段逃生舱，通用契约不解析其内部结构。</summary>
    [JsonPropertyName("platformConfig")]
    public JsonElement? PlatformConfig { get; set; }

    /// <summary>
    /// 判断该任务是否已进入终态。三个终态都不可再回到已受理或传输中。
    /// </summary>
    /// <returns>处于已完成、失败或已取消时为 true。</returns>
    public bool IsTerminal() =>
        State is FileTransferState.Completed or FileTransferState.Failed or FileTransferState.Cancelled;
}

/// <summary>本次传输的来源与目标路径，均以字符串承载。</summary>
public sealed class FileTransferPaths
{
    /// <summary>来源路径，落在方向所指的宿主侧或实例侧文件系统域。</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>目标路径，末段可以是待创建的目录名。</summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;
}

/// <summary>
/// 传输进度的可观测快照。总量未知时仍可累计已复制字节数，
/// 但两端不得自行折算百分比对外呈现。
/// </summary>
public sealed class FileTransferProgress
{
    /// <summary>已复制字节数，总量未知时仍然可累计，不得为空。</summary>
    [JsonPropertyName("bytesTransferred")]
    public long? BytesTransferred { get; set; }

    /// <summary>待复制总字节数，总量未知时置为 null。</summary>
    [JsonPropertyName("totalBytes")]
    public long? TotalBytes { get; set; }

    /// <summary>当前正在处理的条目名，尚未开始处理任何条目时为 null。</summary>
    [JsonPropertyName("currentEntry")]
    public string? CurrentEntry { get; set; }

    /// <summary>
    /// 判断待复制总字节数是否已知。总量未知时不得折算百分比对外呈现。
    /// </summary>
    /// <returns>总字节数为正时为 true。</returns>
    public bool HasKnownTotal() => TotalBytes is > 0;
}

/// <summary>
/// 传输任务状态。queued 已受理并落成任务记录，尚未开始复制；
/// running 正在复制；completed 全部来源项按冲突策略处置完毕。
/// </summary>
[JsonConverter(typeof(LowerCaseEnumConverter<FileTransferState>))]
public enum FileTransferState
{
    /// <summary>已受理并落成任务记录，尚未开始复制。</summary>
    Queued = 0,

    /// <summary>正在复制。</summary>
    Running = 1,

    /// <summary>全部来源项按冲突策略处置完毕。</summary>
    Completed = 2,

    /// <summary>复制中断且任务不再继续。</summary>
    Failed = 3,

    /// <summary>用户主动取消，取消不算失败。</summary>
    Cancelled = 4
}

/// <summary>目标已存在同名项时的处置策略。缺省值由两端各自的界面默认值给出。</summary>
[JsonConverter(typeof(LowerCaseEnumConverter<ConflictPolicy>))]
public enum ConflictPolicy
{
    /// <summary>用来源覆盖目标。</summary>
    Overwrite = 0,

    /// <summary>保留目标并跳过该来源项，只复制目标侧尚不存在的项。</summary>
    Skip = 1,

    /// <summary>两侧内容都保留，给来源项改名后另存到目标侧，不得覆盖目标。</summary>
    Rename = 2
}
