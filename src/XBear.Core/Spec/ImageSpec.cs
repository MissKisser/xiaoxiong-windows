using System.Text.Json.Serialization;

namespace XBear.Core.Spec;

/// <summary>镜像清单，对应 spec/schema/image.schema.json。</summary>
public sealed class ImageSpec
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("androidVersion")]
    public string AndroidVersion { get; set; } = string.Empty;

    [JsonPropertyName("abi")]
    public string Abi { get; set; } = "x86_64";

    [JsonPropertyName("source")]
    public ImageSource Source { get; set; } = new();

    /// <summary>实测验证结论，只填真实测试结果，untested 不得臆测改为 pass。</summary>
    [JsonPropertyName("verified")]
    public FidelityVerification Verified { get; set; } = new();
}

/// <summary>镜像来源信息。</summary>
public sealed class ImageSource
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}

/// <summary>保真度逐项实测结论，P1~P6。</summary>
public sealed class FidelityVerification
{
    [JsonPropertyName("P1")]
    public VerificationState P1 { get; set; } = VerificationState.Untested;

    [JsonPropertyName("P2")]
    public VerificationState P2 { get; set; } = VerificationState.Untested;

    [JsonPropertyName("P3")]
    public VerificationState P3 { get; set; } = VerificationState.Untested;

    [JsonPropertyName("P4")]
    public VerificationState P4 { get; set; } = VerificationState.Untested;

    [JsonPropertyName("P5")]
    public VerificationState P5 { get; set; } = VerificationState.Untested;

    [JsonPropertyName("P6")]
    public VerificationState P6 { get; set; } = VerificationState.Untested;

    /// <summary>每项的实测备注，未实测项为空。</summary>
    [JsonPropertyName("notes")]
    public Dictionary<string, string>? Notes { get; set; }
}

/// <summary>单项保真度的验证状态。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VerificationState
{
    Untested,
    Pass,
    Fail
}