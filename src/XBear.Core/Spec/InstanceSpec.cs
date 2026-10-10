using System.Text.Json;
using System.Text.Json.Serialization;

namespace XBear.Core.Spec;

/// <summary>实例配置，对应 spec/schema/instance.schema.json。</summary>
public sealed class InstanceSpec
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0.0";

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "windows";

    [JsonPropertyName("imageRef")]
    public string ImageRef { get; set; } = string.Empty;

    [JsonPropertyName("resources")]
    public ResourceSpec Resources { get; set; } = new();

    [JsonPropertyName("network")]
    public NetworkSpec? Network { get; set; }

    [JsonPropertyName("deviceIdentity")]
    public DeviceIdentity? DeviceIdentity { get; set; }

    /// <summary>
    /// 显示设置，缺省时由平台按镜像的显示能力选型。
    /// 分辨率、像素密度与方向在实例运行期间不可改，改动后需下次启动生效。
    /// </summary>
    [JsonPropertyName("display")]
    public DisplaySpec? Display { get; set; }

    /// <summary>平台特有字段逃生舱，通用契约不解析其内部结构，未映射的键原样保留。</summary>
    [JsonPropertyName("platformConfig")]
    public PlatformConfig? PlatformConfig { get; set; }
}

/// <summary>
/// 实例显示设置，宽高必填、像素密度与屏幕方向可选。
/// 契约未把本对象封闭，各端可继续追加专有显示字段，未显式声明的键原样保留；
/// 未设置的可选字段写出时不被补成显式 null，避免把缺省状态固化成契约内容。
/// </summary>
public sealed class DisplaySpec
{
    /// <summary>画面宽度，单位像素。</summary>
    [JsonPropertyName("width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; set; }

    /// <summary>画面高度，单位像素。</summary>
    [JsonPropertyName("height")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Height { get; set; }

    /// <summary>像素密度，单位 dpi。为 null 时由平台按镜像的显示能力选型。</summary>
    [JsonPropertyName("dpi")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Dpi { get; set; }

    /// <summary>屏幕方向。为 null 时跟随宿主系统方向。</summary>
    [JsonPropertyName("orientation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Orientation { get; set; }

    /// <summary>未映射到强类型属性的显示字段，反序列化时原样保留，序列化时原样写出。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// 判断是否已按横屏呈现。为 null 表示跟随宿主系统方向，两端不得各自推断。
    /// </summary>
    /// <returns>屏幕方向显式为横屏时为 true。</returns>
    public bool IsLandscape() => string.Equals(Orientation, "landscape", StringComparison.Ordinal);
}

/// <summary>
/// 实例平台特有配置。
/// 契约作为逃生舱不限制内部结构，两端平台特有字段均置于此处；未显式声明的键原样保留。
/// </summary>
public sealed class PlatformConfig
{
    /// <summary>加速器名称，如 whpx、tcg。</summary>
    [JsonPropertyName("accelerator")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Accelerator { get; set; }

    /// <summary>加速器附加选项，如 kernel-irqchip=off。</summary>
    [JsonPropertyName("acceleratorOptions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AcceleratorOptions { get; set; }

    /// <summary>主板架构机型，如 q35。</summary>
    [JsonPropertyName("machine")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Machine { get; set; }

    /// <summary>显卡设备型号，如 virtio-vga-gl。</summary>
    [JsonPropertyName("graphics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Graphics { get; set; }

    /// <summary>内核镜像文件路径或引用。</summary>
    [JsonPropertyName("kernelImage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KernelImage { get; set; }

    /// <summary>初始 ramdisk 文件路径或引用。</summary>
    [JsonPropertyName("initrdImage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InitrdImage { get; set; }

    /// <summary>内核命令行骨架与自定义参数。</summary>
    [JsonPropertyName("kernelAppend")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KernelAppend { get; set; }

    /// <summary>未映射到强类型属性的平台特有字段，反序列化时原样保留，序列化时原样写出。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>实例资源配额。</summary>
public sealed class ResourceSpec
{
    [JsonPropertyName("memoryMB")]
    public int MemoryMB { get; set; } = 4096;

    [JsonPropertyName("cpuCores")]
    public int CpuCores { get; set; } = 4;

    [JsonPropertyName("diskGB")]
    public int DiskGB { get; set; } = 32;

    /// <summary>
    /// QEMU -cpu 型号，为 null 时由平台按镜像的指令集要求选型。
    /// Android-x86 系镜像要求 SSE4.2，默认 qemu64 缺失会导致系统主动拒绝引导。
    /// </summary>
    [JsonPropertyName("cpuModel")]
    public string? CpuModel { get; set; }
}

/// <summary>实例网络配置。</summary>
public sealed class NetworkSpec
{
    [JsonPropertyName("portForwards")]
    public List<PortForward> PortForwards { get; set; } = new();

    /// <summary>固定地址，格式 10.0.2.x，为 null 时由 QEMU 动态分配。</summary>
    [JsonPropertyName("fixedAddress")]
    public string? FixedAddress { get; set; }

    [JsonPropertyName("proxy")]
    public ProxySpec? Proxy { get; set; }

    /// <summary>端口对外可达级别，缺省为 loopback。</summary>
    [JsonPropertyName("exposure")]
    public string Exposure { get; set; } = "loopback";
}

/// <summary>端口映射条目。</summary>
public sealed class PortForward
{
    [JsonPropertyName("hostPort")]
    public int HostPort { get; set; }

    [JsonPropertyName("guestPort")]
    public int GuestPort { get; set; }

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "tcp";

    [JsonPropertyName("bind")]
    public string? Bind { get; set; }
}

/// <summary>实例内代理配置。</summary>
public sealed class ProxySpec
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "http";

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; }

    /// <summary>pac 类型时使用的脚本地址。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>实例设备身份标识，每实例强制独立且不得自动复用。</summary>
public sealed class DeviceIdentity
{
    [JsonPropertyName("serialNo")]
    public string? SerialNo { get; set; }

    [JsonPropertyName("androidId")]
    public string? AndroidId { get; set; }

    [JsonPropertyName("imei")]
    public string? Imei { get; set; }
}