using System.Text.Json.Serialization;
using XBear.Core.Serialization;

namespace XBear.Core.Spec;

/// <summary>镜像清单，字段与 spec/schema/image.schema.json 严格对应。</summary>
public sealed class ImageSpec
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0.0";

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("androidVersion")]
    public string AndroidVersion { get; set; } = string.Empty;

    [JsonPropertyName("abi")]
    public string Abi { get; set; } = "x86_64";

    [JsonPropertyName("source")]
    public ImageSource Source { get; set; } = new();

    [JsonPropertyName("verified")]
    public ImageVerification? Verified { get; set; }
}

/// <summary>镜像来源信息。</summary>
public sealed class ImageSource
{
    /// <summary>来源类型，取值 url / local / builtin。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "url";

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>镜像校验值，64 位小写十六进制。下载后必须回填真实值。</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}

/// <summary>镜像实测结论与证据链。</summary>
public sealed class ImageVerification
{
    /// <summary>实际生效的输入通道，取值 none / native / scrcpy。</summary>
    [JsonPropertyName("inputChannel")]
    public string InputChannel { get; set; } = "none";

    /// <summary>实测时间，ISO 8601 格式。</summary>
    [JsonPropertyName("verifiedAt")]
    public string? VerifiedAt { get; set; }

    [JsonPropertyName("qemuVersion")]
    public string? QemuVersion { get; set; }

    /// <summary>可复现的验证命令与关键输出。</summary>
    [JsonPropertyName("evidence")]
    public string? Evidence { get; set; }

    /// <summary>保真度逐项实测结论。</summary>
    [JsonPropertyName("fidelity")]
    public FidelitySet Fidelity { get; set; } = new();
}

/// <summary>保真度 P1~P6 的逐项实测结论。</summary>
public sealed class FidelitySet
{
    /// <summary>P1 可获取 root。</summary>
    [JsonPropertyName("P1_root")]
    public VerificationState P1Root { get; set; } = VerificationState.Untested;

    /// <summary>P2 系统分区可写。</summary>
    [JsonPropertyName("P2_systemWrite")]
    public VerificationState P2SystemWrite { get; set; } = VerificationState.Untested;

    /// <summary>P3 可刷模块。</summary>
    [JsonPropertyName("P3_moduleFlash")]
    public VerificationState P3ModuleFlash { get; set; } = VerificationState.Untested;

    /// <summary>P4 可刷镜像。</summary>
    [JsonPropertyName("P4_imageSwap")]
    public VerificationState P4ImageSwap { get; set; } = VerificationState.Untested;

    /// <summary>P5 root 持久。</summary>
    [JsonPropertyName("P5_rootPersist")]
    public VerificationState P5RootPersist { get; set; } = VerificationState.Untested;

    /// <summary>P6 可运行 ARM 应用。</summary>
    [JsonPropertyName("P6_armApp")]
    public VerificationState P6ArmApp { get; set; } = VerificationState.Untested;
}

/// <summary>单项保真度的验证状态。untested 不得臆测改为 pass。</summary>
[JsonConverter(typeof(LowerCaseEnumConverter<VerificationState>))]
public enum VerificationState
{
    Untested = 0,
    Pass = 1,
    Fail = 2
}