using System.Text.Json;
using System.Text.Json.Serialization;
using XBear.Core.Diagnostics;

namespace XBear.Core.Spec;

/// <summary>
/// 共享规格文件的定位与读取。以程序目录下的 spec/ 为根，
/// 文件缺失或 JSON 非法时抛出 <see cref="ErrorCategory.Spec"/> 类别的 <see cref="XBearException"/>。
/// </summary>
public sealed class SpecLoader
{
    /// <summary>输出目录下规格子目录名。</summary>
    public const string SpecDirectoryName = "spec";

    /// <summary>schema 子目录名。</summary>
    public const string SchemaDirectoryName = "schema";

    /// <summary>样例子目录名。</summary>
    public const string FixturesDirectoryName = "fixtures";

    /// <summary>设计令牌子目录名。</summary>
    public const string TokensDirectoryName = "tokens";

    /// <summary>实例配置 Schema 文件名。</summary>
    public const string InstanceSchemaFileName = "instance.schema.json";

    /// <summary>镜像清单 Schema 文件名。</summary>
    public const string ImageSchemaFileName = "image.schema.json";

    /// <summary>实例快照 Schema 文件名。</summary>
    public const string SnapshotSchemaFileName = "snapshot.schema.json";

    /// <summary>投屏会话 Schema 文件名。</summary>
    public const string ProjectionSchemaFileName = "projection.schema.json";

    /// <summary>传输任务 Schema 文件名。</summary>
    public const string FileTransferSchemaFileName = "filetransfer.schema.json";

    /// <summary>模块清单与安装状态 Schema 文件名。</summary>
    public const string ModuleSchemaFileName = "module.schema.json";

    /// <summary>应用管理 Schema 文件名，文件内含嵌套的操作记录契约。</summary>
    public const string ApplicationSchemaFileName = "application.schema.json";

    /// <summary>术语表 Schema 文件名。</summary>
    public const string TerminologySchemaFileName = "terminology.schema.json";

    /// <summary>Windows 端实例样例文件名。</summary>
    public const string WindowsInstanceFixtureName = "instance.windows.json";

    /// <summary>Android 端实例样例文件名。</summary>
    public const string AndroidInstanceFixtureName = "instance.android.json";

    /// <summary>镜像清单样例文件名。</summary>
    public const string ImageFixtureName = "image.json";

    /// <summary>最小快照样例文件名，只含必填字段。</summary>
    public const string SnapshotMinimalFixtureName = "snapshot-minimal.json";

    /// <summary>完整快照样例文件名，含可选字段与平台特有字段。</summary>
    public const string SnapshotFullFixtureName = "snapshot-full.json";

    /// <summary>最小投屏会话样例文件名，只含必填字段。</summary>
    public const string ProjectionMinimalFixtureName = "projection-minimal.json";

    /// <summary>完整投屏会话样例文件名，含输入语义与平台特有字段。</summary>
    public const string ProjectionFullFixtureName = "projection-full.json";

    /// <summary>最小传输任务样例文件名，只含必填字段。</summary>
    public const string FileTransferMinimalFixtureName = "filetransfer-minimal.json";

    /// <summary>完整传输任务样例文件名，含进度、时刻与平台特有字段。</summary>
    public const string FileTransferFullFixtureName = "filetransfer-full.json";

    /// <summary>最小模块样例文件名，只含必填字段。</summary>
    public const string ModuleMinimalFixtureName = "module-minimal.json";

    /// <summary>完整模块样例文件名，含清单、结构约定与平台特有字段。</summary>
    public const string ModuleFullFixtureName = "module-full.json";

    /// <summary>最小应用样例文件名，只含必填字段。</summary>
    public const string ApplicationMinimalFixtureName = "application-minimal.json";

    /// <summary>完整应用样例文件名，含版本、来源与最近一次操作记录。</summary>
    public const string ApplicationFullFixtureName = "application-full.json";

    /// <summary>术语表文件名。</summary>
    public const string TerminologyFileName = "terminology.json";

    /// <summary>版本契约文件名。</summary>
    public const string VersionFileName = "version.json";

    /// <summary>性能基线文件名。</summary>
    public const string BaselineFileName = "baseline.json";

    /// <summary>设计令牌文件名。</summary>
    public const string DesignTokensFileName = "design-tokens.json";

    /// <summary>品牌目录名。</summary>
    public const string BrandDirectoryName = "brand";

    /// <summary>品牌配色映射文件名。</summary>
    public const string PaletteFileName = "palette.json";

    private readonly JsonSerializerOptions _readOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// 序列化选项。写出时忽略 null，避免把可选字段补成显式 null 而破坏 Schema 的类型约束。
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    /// <summary>以程序目录下的 spec/ 为根的默认读取器。</summary>
    public static SpecLoader Default { get; } =
        new(Path.Combine(AppContext.BaseDirectory, SpecDirectoryName));

    /// <summary>规格根目录的绝对路径。</summary>
    public string SpecRoot { get; }

    /// <summary>
    /// 创建规格读取器。
    /// </summary>
    /// <param name="specRoot">规格根目录路径。</param>
    public SpecLoader(string specRoot)
    {
        SpecRoot = specRoot ?? throw new XBearException(
            ErrorCategory.Spec,
            "规格根目录不能为空。");
    }

    /// <summary>拼接 schema 目录下的文件绝对路径。</summary>
    /// <param name="fileName">schema 文件名。</param>
    /// <returns>文件绝对路径。</returns>
    public string SchemaPath(string fileName) =>
        Path.Combine(SpecRoot, SchemaDirectoryName, fileName);

    /// <summary>拼接样例目录下的文件绝对路径。</summary>
    /// <param name="fileName">样例文件名。</param>
    /// <returns>文件绝对路径。</returns>
    public string FixturePath(string fileName) =>
        Path.Combine(SpecRoot, SchemaDirectoryName, FixturesDirectoryName, fileName);

    /// <summary>
    /// 读取文件全部文本。
    /// </summary>
    /// <param name="absolutePath">文件绝对路径。</param>
    /// <returns>文件文本内容。</returns>
    /// <exception cref="XBearException">文件缺失或不可读时抛出。</exception>
    public string ReadText(string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"规格文件缺失：{absolutePath}",
                "确认共享规格子模块已拉取，且构建时已将 spec/ 复制到输出目录。");
        }

        try
        {
            return File.ReadAllText(absolutePath);
        }
        catch (IOException ex)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"规格文件不可读：{absolutePath}",
                "确认文件未被占用且当前账户具备读取权限。",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"规格文件不可读：{absolutePath}",
                "确认文件未被占用且当前账户具备读取权限。",
                ex);
        }
    }

    /// <summary>读取 schema 文件文本。</summary>
    /// <param name="fileName">schema 文件名。</param>
    /// <returns>schema 文件文本内容。</returns>
    public string ReadSchemaText(string fileName) => ReadText(SchemaPath(fileName));

    /// <summary>读取样例文件文本。</summary>
    /// <param name="fileName">样例文件名。</param>
    /// <returns>样例文件文本内容。</returns>
    public string ReadFixtureText(string fileName) => ReadText(FixturePath(fileName));

    /// <summary>从文件读取并解析实例配置。</summary>
    /// <param name="absolutePath">实例配置绝对路径。</param>
    /// <returns>解析后的实例配置。</returns>
    public InstanceSpec LoadInstanceFile(string absolutePath) => ParseInstance(ReadText(absolutePath));

    /// <summary>从文件读取并解析镜像清单。</summary>
    /// <param name="absolutePath">镜像清单绝对路径。</param>
    /// <returns>解析后的镜像清单。</returns>
    public ImageSpec LoadImageFile(string absolutePath) => ParseImage(ReadText(absolutePath));

    /// <summary>从文件读取并解析投屏会话。</summary>
    /// <param name="absolutePath">投屏会话绝对路径。</param>
    /// <returns>解析后的投屏会话。</returns>
    public ProjectionSpec LoadProjectionFile(string absolutePath) => ParseProjection(ReadText(absolutePath));

    /// <summary>从文件读取并解析传输任务。</summary>
    /// <param name="absolutePath">传输任务绝对路径。</param>
    /// <returns>解析后的传输任务。</returns>
    public FileTransferSpec LoadFileTransferFile(string absolutePath) => ParseFileTransfer(ReadText(absolutePath));

    /// <summary>从文件读取并解析模块清单与安装状态。</summary>
    /// <param name="absolutePath">模块记录绝对路径。</param>
    /// <returns>解析后的模块记录。</returns>
    public ModuleSpec LoadModuleFile(string absolutePath) => ParseModule(ReadText(absolutePath));

    /// <summary>从文件读取并解析应用记录。</summary>
    /// <param name="absolutePath">应用记录绝对路径。</param>
    /// <returns>解析后的应用记录。</returns>
    public ApplicationSpec LoadApplicationFile(string absolutePath) => ParseApplication(ReadText(absolutePath));

    /// <summary>读取并解析指定样例文件中的快照元数据。</summary>
    /// <param name="fixtureFileName">样例文件名。</param>
    /// <returns>解析后的快照元数据。</returns>
    public SnapshotSpec LoadSnapshotFixture(string fixtureFileName) =>
        ParseSnapshot(ReadFixtureText(fixtureFileName));

    /// <summary>读取并解析指定样例文件中的投屏会话。</summary>
    /// <param name="fixtureFileName">样例文件名。</param>
    /// <returns>解析后的投屏会话。</returns>
    public ProjectionSpec LoadProjectionFixture(string fixtureFileName) =>
        ParseProjection(ReadFixtureText(fixtureFileName));

    /// <summary>读取并解析指定样例文件中的传输任务。</summary>
    /// <param name="fixtureFileName">样例文件名。</param>
    /// <returns>解析后的传输任务。</returns>
    public FileTransferSpec LoadFileTransferFixture(string fixtureFileName) =>
        ParseFileTransfer(ReadFixtureText(fixtureFileName));

    /// <summary>读取并解析指定样例文件中的模块清单与安装状态。</summary>
    /// <param name="fixtureFileName">样例文件名。</param>
    /// <returns>解析后的模块记录。</returns>
    public ModuleSpec LoadModuleFixture(string fixtureFileName) =>
        ParseModule(ReadFixtureText(fixtureFileName));

    /// <summary>读取并解析指定样例文件中的应用记录。</summary>
    /// <param name="fixtureFileName">样例文件名。</param>
    /// <returns>解析后的应用记录。</returns>
    public ApplicationSpec LoadApplicationFixture(string fixtureFileName) =>
        ParseApplication(ReadFixtureText(fixtureFileName));

    /// <summary>读取并解析版本契约。</summary>
    /// <returns>解析后的版本契约文档。</returns>
    public VersionDocument LoadVersion() =>
        ParseVersion(ReadText(Path.Combine(SpecRoot, VersionFileName)));

    /// <summary>读取并解析性能基线。</summary>
    /// <returns>解析后的性能基线文档。</returns>
    public PerformanceBaseline LoadBaseline() =>
        ParseBaseline(ReadText(Path.Combine(SpecRoot, BaselineFileName)));

    /// <summary>读取并解析术语表。</summary>
    /// <returns>解析后的术语表文档。</returns>
    public TerminologyDocument LoadTerminology() =>
        ParseTerminology(ReadText(Path.Combine(SpecRoot, TerminologyFileName)));

    /// <summary>读取并解析设计令牌。</summary>
    /// <returns>解析后的设计令牌。</returns>
    public DesignTokens LoadDesignTokens() =>
        DesignTokens.Parse(ReadText(Path.Combine(SpecRoot, TokensDirectoryName, DesignTokensFileName)));

    /// <summary>读取并解析品牌配色映射。</summary>
    /// <returns>解析后的品牌调色板契约文档。</returns>
    public PaletteDocument LoadPalette() =>
        ParsePalette(ReadText(Path.Combine(SpecRoot, BrandDirectoryName, PaletteFileName)));

    /// <summary>
    /// 解析实例配置 JSON。
    /// </summary>
    /// <param name="json">实例配置 JSON 文本。</param>
    /// <returns>解析后的实例配置。</returns>
    /// <exception cref="XBearException">JSON 非法或顶层不是对象时抛出。</exception>
    public InstanceSpec ParseInstance(string json)
    {
        var spec = Deserialize<InstanceSpec>(json, "实例配置");
        return spec;
    }

    /// <summary>
    /// 解析镜像清单 JSON。
    /// </summary>
    /// <param name="json">镜像清单 JSON 文本。</param>
    /// <returns>解析后的镜像清单。</returns>
    /// <exception cref="XBearException">JSON 非法或顶层不是对象时抛出。</exception>
    public ImageSpec ParseImage(string json)
    {
        var spec = Deserialize<ImageSpec>(json, "镜像清单");
        return spec;
    }

    /// <summary>
    /// 解析术语表 JSON。
    /// </summary>
    /// <param name="json">术语表 JSON 文本。</param>
    /// <returns>解析后的术语表文档。</returns>
    /// <exception cref="XBearException">JSON 非法或顶层不是对象时抛出。</exception>
    public TerminologyDocument ParseTerminology(string json)
    {
        var doc = Deserialize<TerminologyDocument>(json, "术语表");
        return doc;
    }

    /// <summary>
    /// 解析快照元数据 JSON。
    /// </summary>
    /// <param name="json">快照元数据 JSON 文本。</param>
    /// <returns>解析后的快照元数据。</returns>
    /// <exception cref="XBearException">JSON 非法或顶层不是对象时抛出。</exception>
    public SnapshotSpec ParseSnapshot(string json) =>
        Deserialize<SnapshotSpec>(json, "快照元数据");

    /// <summary>
    /// 解析投屏会话 JSON。
    /// </summary>
    /// <param name="json">投屏会话 JSON 文本。</param>
    /// <returns>解析后的投屏会话。</returns>
    /// <exception cref="XBearException">JSON 非法或顶层不是对象时抛出。</exception>
    public ProjectionSpec ParseProjection(string json) =>
        Deserialize<ProjectionSpec>(json, "投屏会话");

    /// <summary>
    /// 解析传输任务 JSON。
    /// </summary>
    /// <param name="json">传输任务 JSON 文本。</param>
    /// <returns>解析后的传输任务。</returns>
    /// <exception cref="XBearException">JSON 非法或顶层不是对象时抛出。</exception>
    public FileTransferSpec ParseFileTransfer(string json) =>
        Deserialize<FileTransferSpec>(json, "传输任务");

    /// <summary>
    /// 解析模块清单与安装状态 JSON。
    /// </summary>
    /// <param name="json">模块记录 JSON 文本。</param>
    /// <returns>解析后的模块记录。</returns>
    /// <exception cref="XBearException">JSON 非法或顶层不是对象时抛出。</exception>
    public ModuleSpec ParseModule(string json) =>
        Deserialize<ModuleSpec>(json, "模块记录");

    /// <summary>
    /// 解析应用记录 JSON。
    /// </summary>
    /// <param name="json">应用记录 JSON 文本。</param>
    /// <returns>解析后的应用记录。</returns>
    /// <exception cref="XBearException">JSON 非法或顶层不是对象时抛出。</exception>
    public ApplicationSpec ParseApplication(string json) =>
        Deserialize<ApplicationSpec>(json, "应用记录");

    /// <summary>
    /// 解析版本契约 JSON。
    /// </summary>
    /// <param name="json">版本契约 JSON 文本。</param>
    /// <returns>解析后的版本契约文档。</returns>
    /// <exception cref="XBearException">JSON 非法或顶层不是对象时抛出。</exception>
    public VersionDocument ParseVersion(string json) =>
        Deserialize<VersionDocument>(json, "版本契约");

    /// <summary>
    /// 解析性能基线 JSON。
    /// </summary>
    /// <param name="json">性能基线 JSON 文本。</param>
    /// <returns>解析后的性能基线文档。</returns>
    /// <exception cref="XBearException">JSON 非法或顶层不是对象时抛出。</exception>
    public PerformanceBaseline ParseBaseline(string json) =>
        Deserialize<PerformanceBaseline>(json, "性能基线");

    /// <summary>
    /// 解析品牌配色映射 JSON。
    /// </summary>
    /// <param name="json">品牌配色映射 JSON 文本。</param>
    /// <returns>解析后的品牌调色板契约文档。</returns>
    /// <exception cref="XBearException">JSON 非法或缺少 roles 节点时抛出。</exception>
    public PaletteDocument ParsePalette(string json) =>
        PaletteDocument.Parse(json);

    private T Deserialize<T>(string json, string what)
    {
        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(json, _readOptions);
        }
        catch (JsonException ex)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"{what}不是合法 JSON。",
                "修正 JSON 语法后重试；跨端契约文件不应由单端自行改动。",
                ex);
        }

        return value ?? throw new XBearException(
            ErrorCategory.Spec,
            $"{what}JSON 的顶层为 null，无法解析为对象。");
    }
}

/// <summary>双端统一术语表文档。</summary>
public sealed class TerminologyDocument
{
    /// <summary>术语表版本号。</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>术语条目集合。</summary>
    [JsonPropertyName("terms")]
    public List<Term> Terms { get; set; } = new();

    /// <summary>
    /// 按标识查词，找不到时返回 null。
    /// </summary>
    /// <param name="id">术语标识。</param>
    /// <returns>匹配的术语条目，未找到时为 null。</returns>
    public Term? Find(string id) =>
        Terms.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// 按标识查词，找不到时抛出异常。
    /// </summary>
    /// <param name="id">术语标识。</param>
    /// <returns>匹配的术语条目。</returns>
    /// <exception cref="XBearException">标识不存在时抛出。</exception>
    public Term Require(string id) =>
        Find(id) ?? throw new XBearException(
            ErrorCategory.Spec,
            $"术语表缺少标识为 {id} 的条目。",
            "术语表是跨端契约，新增条目需两端同步确认。");
}

/// <summary>单个术语条目。</summary>
public sealed class Term
{
    /// <summary>术语标识，代码与测试引用该字段。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>界面显示用中文。</summary>
    [JsonPropertyName("zh")]
    public string Zh { get; set; } = string.Empty;

    /// <summary>代码标识与日志用英文。</summary>
    [JsonPropertyName("en")]
    public string En { get; set; } = string.Empty;

    /// <summary>一句话说明，避免误解。</summary>
    [JsonPropertyName("meaning")]
    public string Meaning { get; set; } = string.Empty;

    /// <summary>禁止使用的近义词。</summary>
    [JsonPropertyName("forbidden")]
    public List<string> Forbidden { get; set; } = new();
}