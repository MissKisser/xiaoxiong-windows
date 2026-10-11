using System.Text.Json.Serialization;
using XBear.Core.Serialization;

namespace XBear.Core.Spec;

/// <summary>
/// 每实例唯一身份对象，对应 spec/schema/identity.schema.json。
/// 一次生成、多通道一致下发：同一实例在所有可读通道上必须呈现同一个故事，
/// 任一字段在实例生命周期内不得变更。
/// </summary>
public sealed class IdentitySpec
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0.0";

    [JsonPropertyName("serialNo")]
    public string SerialNo { get; set; } = string.Empty;

    [JsonPropertyName("androidId")]
    public string AndroidId { get; set; } = string.Empty;

    /// <summary>IMEI。无 telephony 能力的实例必须为 null，不得下发到无基带的实例。</summary>
    [JsonPropertyName("imei")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Imei { get; set; }

    [JsonPropertyName("mac")]
    public string Mac { get; set; } = string.Empty;

    [JsonPropertyName("deviceProfile")]
    public DeviceProfileSpec DeviceProfile { get; set; } = new();

    [JsonPropertyName("fingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Fingerprint { get; set; }

    [JsonPropertyName("buildIdentity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BuildIdentitySpec? BuildIdentity { get; set; }

    [JsonPropertyName("bootIdentity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BootIdentitySpec? BootIdentity { get; set; }

    [JsonPropertyName("adbGuid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdbGuid { get; set; }

    [JsonPropertyName("hostName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HostName { get; set; }

    /// <summary>标识通道的实测接通记录，只能由实例启动后的回读比对填写。</summary>
    [JsonPropertyName("channels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IdentityChannelSet? Channels { get; set; }

    [JsonPropertyName("evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Evidence { get; set; }
}

/// <summary>
/// 机型族画像。DMI/SMBIOS 字符串、系统属性与构建指纹的唯一来源，
/// 三者不得各写一份，各分区属性必须由本对象统一派生。
/// </summary>
public sealed class DeviceProfileSpec
{
    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = string.Empty;

    [JsonPropertyName("brand")]
    public string Brand { get; set; } = string.Empty;

    [JsonPropertyName("manufacturer")]
    public string Manufacturer { get; set; } = string.Empty;

    [JsonPropertyName("productName")]
    public string ProductName { get; set; } = string.Empty;

    [JsonPropertyName("productVersion")]
    public string ProductVersion { get; set; } = string.Empty;

    [JsonPropertyName("productFamily")]
    public string ProductFamily { get; set; } = string.Empty;

    [JsonPropertyName("productSku")]
    public string ProductSku { get; set; } = string.Empty;

    /// <summary>产品代号，派生 ro.product.device、指纹串的产品段与实例内主机名。</summary>
    [JsonPropertyName("device")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Device { get; set; }

    [JsonPropertyName("cpuManufacturer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CpuManufacturer { get; set; }

    [JsonPropertyName("cpuVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CpuVersion { get; set; }

    /// <summary>CPU 架构。取值受限于本产品的 x86_64 运行形态。</summary>
    [JsonPropertyName("cpuArchitecture")]
    public string CpuArchitecture { get; set; } = CpuArchitectures.X86_64;

    [JsonPropertyName("boardManufacturer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BoardManufacturer { get; set; }

    [JsonPropertyName("boardProduct")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BoardProduct { get; set; }

    [JsonPropertyName("chassisManufacturer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChassisManufacturer { get; set; }

    [JsonPropertyName("chassisAsset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChassisAsset { get; set; }
}

/// <summary>CPU 架构取值。契约只接受 x86_64，真机 ARM 画像的冲突属已知不可缓项。</summary>
public static class CpuArchitectures
{
    /// <summary>本产品唯一支持的架构。</summary>
    public const string X86_64 = "x86_64";
}

/// <summary>
/// Build 属性族。type、tags、flavor 必须与指纹的类型段同源，
/// 指纹内写 userdebug 而 ro.build.type 写 user 是可被一致性校验直接命中的破绽。
/// </summary>
public sealed class BuildIdentitySpec
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public string Tags { get; set; } = string.Empty;

    [JsonPropertyName("flavor")]
    public string Flavor { get; set; } = string.Empty;

    [JsonPropertyName("buildId")]
    public string BuildId { get; set; } = string.Empty;

    [JsonPropertyName("incremental")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Incremental { get; set; }
}

/// <summary>
/// 启动链属性族。校验强度必须与 Root 权限、系统可写能力自洽：
/// 保留 Root 权限与可写系统时不得声明为完整校验通过的零售机画像。
/// </summary>
public sealed class BootIdentitySpec
{
    [JsonPropertyName("flashLocked")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FlashLocked { get; set; }

    [JsonPropertyName("vbmetaDeviceState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VbmetaDeviceState { get; set; }

    [JsonPropertyName("verifiedBootState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VerifiedBootState { get; set; }

    [JsonPropertyName("verityMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VerityMode { get; set; }

    [JsonPropertyName("bootloader")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Bootloader { get; set; }

    /// <summary>对应 ro.baseband。镜像无 telephony 能力时为 null。</summary>
    [JsonPropertyName("baseband")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Baseband { get; set; }

    [JsonPropertyName("bootmode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Bootmode { get; set; }
}

/// <summary>
/// 标识通道的实测接通记录。形态取自 image.schema.json 的 verified：
/// 只登记实测结论，不登记实现意图；通道存在不等于值生效。
/// </summary>
public sealed class IdentityChannelSet
{
    [JsonPropertyName("smbios")]
    public VerificationState Smbios { get; set; } = VerificationState.Untested;

    [JsonPropertyName("kernelCmdline")]
    public VerificationState KernelCmdline { get; set; } = VerificationState.Untested;

    [JsonPropertyName("systemProperties")]
    public VerificationState SystemProperties { get; set; } = VerificationState.Untested;

    [JsonPropertyName("androidId")]
    public VerificationState AndroidId { get; set; } = VerificationState.Untested;

    /// <summary>IMEI 通道。镜像无 telephony 能力时恒为 fail，此时 imei 必须为 null。</summary>
    [JsonPropertyName("telephonyImei")]
    public VerificationState TelephonyImei { get; set; } = VerificationState.Untested;

    [JsonPropertyName("mac")]
    public VerificationState Mac { get; set; } = VerificationState.Untested;
}