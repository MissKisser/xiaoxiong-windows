using System.Text.Json.Serialization;
using XBear.Core.Serialization;

namespace XBear.Core.Spec;

/// <summary>
/// 应用记录，字段与 spec/schema/application.schema.json 根对象严格对应。
/// 描述某个应用包在某个实例中的当前可观测事实，包名是跨实例稳定的自然主键：
/// 同一应用在多个实例中的包名相同，其余字段各不相同。
/// 未安装的包只有身份与状态可言，版本、ABI、安装时刻、入口组件与安装来源一律不得出现。
/// </summary>
public sealed class ApplicationSpec
{
    /// <summary>应用记录契约自身的版本，与规格层版本同源。</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0.0";

    /// <summary>应用在实例内的唯一标识，即 Android 包名。</summary>
    [JsonPropertyName("packageName")]
    public string PackageName { get; set; } = string.Empty;

    /// <summary>所属实例的标识，引用实例契约的 id。应用记录随实例一同失效。</summary>
    [JsonPropertyName("instanceRef")]
    public string InstanceRef { get; set; } = string.Empty;

    /// <summary>安装状态。卸载完成后回到未安装，不保留中间态。</summary>
    [JsonPropertyName("installState")]
    public ApplicationInstallState InstallState { get; set; }

    /// <summary>应用自报的版本名，是给人看的字符串，不保证可比较大小。</summary>
    [JsonPropertyName("versionName")]
    public string? VersionName { get; set; }

    /// <summary>应用自报的版本号，是给系统看的单调递增整数，同名包名下它才是升级成败的判据。</summary>
    [JsonPropertyName("versionCode")]
    public int? VersionCode { get; set; }

    /// <summary>
    /// 实例实际承载该应用的 ABI，由包管理器在应用声明的 ABI 与实例的 ABI 列表求交后选出，
    /// 不是应用自述值也不是宿主 CPU 架构。
    /// 契约取值域为 x86_64、x86、arm64-v8a、armeabi-v7a、armeabi 与 none，
    /// 字面量含连字符故以字符串承载。
    /// </summary>
    [JsonPropertyName("primaryCpuAbi")]
    public string? PrimaryCpuAbi { get; set; }

    /// <summary>本次安装在本实例内完成的时刻，带时区偏移。取自实例内系统时钟。</summary>
    [JsonPropertyName("installedAt")]
    public string? InstalledAt { get; set; }

    /// <summary>可拉起的入口组件名，解析不到时缺省，两端不得自行猜测类名。</summary>
    [JsonPropertyName("launchActivity")]
    public string? LaunchActivity { get; set; }

    /// <summary>安装来源，只记录应用包的内容身份，不记录宿主绝对路径。</summary>
    [JsonPropertyName("source")]
    public ApplicationSource? Source { get; set; }

    /// <summary>最近一次作用于该包的操作记录，用于解释安装状态与安装时刻的由来。</summary>
    [JsonPropertyName("lastOperation")]
    public ApplicationOperation? LastOperation { get; set; }

    /// <summary>
    /// 判断该包是否已在本实例中注册，可解析到入口组件。
    /// </summary>
    /// <returns>安装状态为已安装时为 true。</returns>
    public bool IsInstalled() => InstallState == ApplicationInstallState.Installed;
}

/// <summary>安装来源。同一包名可以有多个历史文件名的记录，以摘要值判别。</summary>
public sealed class ApplicationSource
{
    /// <summary>安装所用的应用包文件名，只取不含宿主目录路径的文件名部分。</summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>应用包文件的 SHA-256，与镜像契约的来源摘要同口径。</summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    /// <summary>应用包文件字节数，与镜像契约的来源字节数同口径。</summary>
    [JsonPropertyName("sizeBytes")]
    public long? SizeBytes { get; set; }
}

/// <summary>
/// 一次应用操作的可观测结果记录，对应 application.schema.json 的嵌套操作契约。
/// 安装、卸载、拉起三类操作共用同一形状，差异只在操作类型与失败分类。
/// 与应用记录同文件同契约版本：操作记录以包名为目标，脱离应用记录无法独立成立。
/// </summary>
public sealed class ApplicationOperation
{
    /// <summary>操作记录契约自身的版本，与应用记录独立递增。</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0.0";

    /// <summary>操作的唯一标识，宿主侧发号，用于失败重试时关联同一次意图的多次尝试。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>操作发生的实例，引用实例契约的 id。</summary>
    [JsonPropertyName("instanceRef")]
    public string InstanceRef { get; set; } = string.Empty;

    /// <summary>
    /// 操作类型。刻意不使用「启动」一词：术语表中启动已固定指实例从停止态转为运行态。
    /// </summary>
    [JsonPropertyName("operation")]
    public ApplicationOperationKind Operation { get; set; }

    /// <summary>操作目标的包名，约束与应用记录的包名完全一致。</summary>
    [JsonPropertyName("packageName")]
    public string PackageName { get; set; } = string.Empty;

    /// <summary>操作结果。只有成功与失败两种可观测结论，不设中间态。</summary>
    [JsonPropertyName("result")]
    public ApplicationOperationResult Result { get; set; }

    /// <summary>操作完成的时刻，带时区偏移。取实例侧完成回报的时刻而非发起时刻。</summary>
    [JsonPropertyName("occurredAt")]
    public string OccurredAt { get; set; } = string.Empty;

    /// <summary>端到端耗时毫秒数。安装与卸载为墙钟耗时，拉起为首帧完成耗时。</summary>
    [JsonPropertyName("durationMs")]
    public int? DurationMs { get; set; }

    /// <summary>失败归因，仅在结果为失败时出现，成功时不得出现。</summary>
    [JsonPropertyName("failureReason")]
    public ApplicationOperationFailure? FailureReason { get; set; }

    /// <summary>
    /// 判断该操作是否成功。工具层面未确认成功即判失败，由调用方按需重试。
    /// </summary>
    /// <returns>结果为成功时为 true。</returns>
    public bool IsSuccess() => Result == ApplicationOperationResult.Success;
}

/// <summary>
/// 失败归因。归类沿用术语表的四类错误标识，决定两端如何处置，不能各自另定一套。
/// </summary>
public sealed class ApplicationOperationFailure
{
    /// <summary>
    /// 失败归类。user 对应配置错误，guest 对应实例内错误，
    /// environment 对应宿主环境错误，provider 对应虚拟化实现错误。
    /// </summary>
    [JsonPropertyName("category")]
    public ApplicationErrorCategory Category { get; set; }

    /// <summary>
    /// 工具层面的原始失败码，取自实例侧回报，不翻译、不归并、不截断。
    /// 契约刻意不做闭合枚举：本产品未穷举的码必须原样留存，丢失即等于掩盖了失败真相。
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>工具回报的关键输出片段，原样保留，供界面展示与排查。</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>应用在实例内的安装状态。只有已安装与未安装两种可观测结果。</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ApplicationInstallState>))]
public enum ApplicationInstallState
{
    /// <summary>该包不存在于本实例，仅作为待安装清单条目存在。</summary>
    NotInstalled = 0,

    /// <summary>该包已在本实例中注册，可解析到入口组件。</summary>
    Installed = 1
}

/// <summary>操作类型。安装、卸载、拉起共用同一形状，差异只在失败分类。</summary>
[JsonConverter(typeof(LowerCaseEnumConverter<ApplicationOperationKind>))]
public enum ApplicationOperationKind
{
    /// <summary>把应用包装入实例。</summary>
    Install = 0,

    /// <summary>从实例移除该包。</summary>
    Uninstall = 1,

    /// <summary>把已安装的应用切换到前台运行。</summary>
    Launch = 2
}

/// <summary>操作结果，不设中间态。</summary>
[JsonConverter(typeof(LowerCaseEnumConverter<ApplicationOperationResult>))]
public enum ApplicationOperationResult
{
    /// <summary>实例侧回报成功。</summary>
    Success = 0,

    /// <summary>实例侧回报失败，调用方按需重试或归因。</summary>
    Failure = 1
}

/// <summary>失败归类，取值沿用术语表四类错误的标识。</summary>
[JsonConverter(typeof(LowerCaseEnumConverter<ApplicationErrorCategory>))]
public enum ApplicationErrorCategory
{
    /// <summary>配置错误：配置本身非法，立即返回，不进入任何中间态。</summary>
    User = 0,

    /// <summary>实例内错误：实例内出现问题，可重试。</summary>
    Guest = 1,

    /// <summary>宿主环境错误：宿主不满足前提，需给出修复指引。</summary>
    Environment = 2,

    /// <summary>虚拟化实现错误：实现内部失败，记录日志，允许重试。</summary>
    Provider = 3
}
