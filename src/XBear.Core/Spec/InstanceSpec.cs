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

    /// <summary>
/// 实例的固定地址，形式为 IPv4 地址或 IPv4 地址加前缀长度，为 null 时由宿主网络仿真动态分配。
/// 契约不预设网段，取宿主网络仿真的默认网段会把它写进实例，属可被交叉校验命中的暴露面，
/// 因此只要求取值合法；地址不得随启动顺序漂移。前缀缺省时由产品按选定的网段补齐。
/// </summary>
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

/// <summary>
/// 实例设备身份，每实例强制独立且不得自动复用。
/// 省略或为 null 表示用户未手工指定，由产品在创建实例时生成并写入，不表示该实例无身份；
/// 一旦存在则 serialNo 与 androidId 必须齐全，半截身份一律拒绝——
/// 标识只生成一半时相关下发通道会静默失效，实例对外呈现为空序列号。
/// </summary>
public sealed class DeviceIdentity
{
    /// <summary>设备序列号，恒为 XBSN 加 12 位大写十六。</summary>
    [JsonPropertyName("serialNo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SerialNo { get; set; }

    /// <summary>Android ID，恒为 16 位小写十六。</summary>
    [JsonPropertyName("androidId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AndroidId { get; set; }

    /// <summary>
    /// IMEI，恒为 15 位、国家码 86、末位为 Luhn 校验位。
    /// 仅当所引用镜像实测具备 telephony 能力时填写，否则必须为 null；
    /// 为 null 时不得向内核命令行下发该键，否则制造「声明存在却无人消费」的矛盾。
    /// </summary>
    [JsonPropertyName("imei")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Imei { get; set; }

    /// <summary>
    /// 网卡 MAC，6 字节小写十六冒号分隔，末字节取偶数位（组播位为字节最低位，为 0 才表示单播），
    /// 全零亦被拒。省略或为 null 时由序列号确定性派生，实例首次创建后固定不变：
    /// 同实例跨重启恒定，跨实例唯一；MAC 逐次跳变本身就是异常特征。
    /// </summary>
    [JsonPropertyName("mac")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mac { get; set; }

    /// <summary>
    /// 机型族标识，引用 identity.schema.json 的机型族画像。
    /// 省略或为 null 时按实例序号确定性选取；同机型族实例之间仍需靠序列号、Android ID 与 MAC 区分。
    /// </summary>
    [JsonPropertyName("deviceProfileId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceProfileId { get; set; }

    /// <summary>
    /// 所引用的 identity.schema 版本。读入时若高于自身支持的版本，
    /// 按未识别处理并保留原始字段，不得静默丢弃。
    /// </summary>
    [JsonPropertyName("identityVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdentityVersion { get; set; }

    /// <summary>
    /// 判断设备身份是否已按契约填齐必填字段。必填化后不允许半截身份存在：
    /// 序列号或 Android ID 缺失时相关下发通道会静默失效。
    /// </summary>
    /// <returns>序列号与 Android ID 均非空时返回 true。</returns>
    public bool HasRequiredFields() =>
        !string.IsNullOrWhiteSpace(SerialNo) && !string.IsNullOrWhiteSpace(AndroidId);
}