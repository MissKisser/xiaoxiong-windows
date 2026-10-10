using System.Text.Json;
using System.Text.Json.Serialization;
using XBear.Core.Serialization;

namespace XBear.Core.Spec;

/// <summary>
/// 模块清单与安装状态，字段与 spec/schema/module.schema.json 严格对应。
/// 一份文档描述单个实例内的单个已安装模块。模块目录恒位于实例内的共享常量目录，
/// 引导期加载器扫描该目录，把各模块的 system 子树叠加进系统并按固定顺序执行阶段脚本。
/// </summary>
public sealed class ModuleSpec
{
    /// <summary>模块契约自身的版本，与规格层版本同源。</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0.0";

    /// <summary>模块标识，恒等于模块目录名。标识直接进入实例内路径并拼进宿主下发的命令。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>所属实例的标识，引用实例契约的 id。模块随实例一同销毁。</summary>
    [JsonPropertyName("instanceRef")]
    public string InstanceRef { get; set; } = string.Empty;

    /// <summary>模块在实例内的目录路径，末段与模块标识适用同一套约束。</summary>
    [JsonPropertyName("remotePath")]
    public string? RemotePath { get; set; }

    /// <summary>
    /// 模块清单，即模块属性的最小字段集合。
    /// 仅凭 system 子树安装、未携带清单的模块不带本对象。
    /// </summary>
    [JsonPropertyName("manifest")]
    public ModuleManifest? Manifest { get; set; }

    /// <summary>模块内结构约定。目录名、system 子树与阶段脚本三样都缺的模块属于无效输入。</summary>
    [JsonPropertyName("layout")]
    public ModuleLayout? Layout { get; set; }

    /// <summary>安装记录，记录模块这份落盘状态的来源与生效判定。</summary>
    [JsonPropertyName("install")]
    public ModuleInstall Install { get; set; } = new();

    /// <summary>平台特有字段逃生舱，通用契约不解析其内部结构。</summary>
    [JsonPropertyName("platformConfig")]
    public JsonElement? PlatformConfig { get; set; }

    /// <summary>
    /// 判断该模块是否会在下一次引导被加载器应用。
    /// </summary>
    /// <returns>安装状态为已启用时为 true。</returns>
    public bool IsEnabledOnNextBoot() => Install.State == ModuleInstallState.Enabled;

    /// <summary>
    /// 判断该模块是否已被显式禁用。禁用时模块目录保留不删，下次引导必须跳过。
    /// </summary>
    /// <returns>安装状态为已禁用时为 true。</returns>
    public bool IsDisabled() => Install.State == ModuleInstallState.Disabled;
}

/// <summary>
/// 模块清单。清单属于外部生态格式，允许出现契约未列出的额外键，
/// 两端只约束本类型显式声明的五个字段，未映射的键原样保留。
/// </summary>
public sealed class ModuleManifest
{
    /// <summary>清单声明的模块标识，必须与模块记录的顶层标识相同。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>模块名，供用户在模块列表中辨认。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>模块版本字符串，契约不解析其结构，只要求非空以便与后续版本区分。</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>模块作者署名，可为空串，不少模块包不填作者。</summary>
    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    /// <summary>模块说明文本，可为空串，只作展示用，不参与任何判定。</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>未映射到强类型属性的清单字段，反序列化时原样保留，序列化时原样写出。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>模块内结构约定。加载器只按目录名、system 子树与阶段脚本这三样生效。</summary>
public sealed class ModuleLayout
{
    /// <summary>模块是否携带 system 子树。为 false 时加载器不叠加任何文件。</summary>
    [JsonPropertyName("systemOverlay")]
    public bool? SystemOverlay { get; set; }

    /// <summary>
    /// 模块内实际存在的阶段脚本名。契约取值域为 post-fs-data.sh、service.sh 与 boot-completed.sh，
    /// 字面量含点号与连字符故以字符串承载。加载器按固定顺序逐个执行，与数组本身的顺序无关。
    /// </summary>
    [JsonPropertyName("stageScripts")]
    public List<string>? StageScripts { get; set; }

    /// <summary>
    /// 判断模块是否携带指定名称的阶段脚本。
    /// </summary>
    /// <param name="scriptName">阶段脚本名。</param>
    /// <returns>该阶段脚本存在时为 true。</returns>
    public bool HasStageScript(string scriptName) =>
        StageScripts is not null && StageScripts.Contains(scriptName, StringComparer.Ordinal);
}

/// <summary>安装记录。记录模块这份落盘状态的来源与生效判定，不描述安装过程本身。</summary>
public sealed class ModuleInstall
{
    /// <summary>安装状态。三个状态都是终态，不经引导不互相流转。</summary>
    [JsonPropertyName("state")]
    public ModuleInstallState State { get; set; } = ModuleInstallState.Installed;

    /// <summary>模块文件全部落入实例内并完成刷盘的时刻，带时区偏移。</summary>
    [JsonPropertyName("installedAt")]
    public string InstalledAt { get; set; } = string.Empty;

    /// <summary>安装所用来源包的文件名，不含宿主目录路径且不含目录分隔符。</summary>
    [JsonPropertyName("sourceZip")]
    public string SourceZip { get; set; } = string.Empty;
}

/// <summary>
/// 安装记录的状态。installed 模块目录已落盘但尚未在引导期的加载器中确认生效；
/// enabled 未被禁用且会在下一次引导被加载器应用；disabled 已被显式禁用。
/// </summary>
[JsonConverter(typeof(LowerCaseEnumConverter<ModuleInstallState>))]
public enum ModuleInstallState
{
    /// <summary>模块目录已落盘，但尚未在引导期的加载器中确认生效。</summary>
    Installed = 0,

    /// <summary>未被禁用，且下一次引导会被加载器应用。</summary>
    Enabled = 1,

    /// <summary>已被显式禁用，下一次引导必须跳过，模块目录保留不删。</summary>
    Disabled = 2
}
