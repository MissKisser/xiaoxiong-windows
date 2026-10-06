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

    /// <summary>术语表 Schema 文件名。</summary>
    public const string TerminologySchemaFileName = "terminology.schema.json";

    /// <summary>Windows 端实例样例文件名。</summary>
    public const string WindowsInstanceFixtureName = "instance.windows.json";

    /// <summary>Android 端实例样例文件名。</summary>
    public const string AndroidInstanceFixtureName = "instance.android.json";

    /// <summary>镜像清单样例文件名。</summary>
    public const string ImageFixtureName = "image.json";

    /// <summary>术语表文件名。</summary>
    public const string TerminologyFileName = "terminology.json";

    /// <summary>设计令牌文件名。</summary>
    public const string DesignTokensFileName = "design-tokens.json";

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

    /// <summary>读取并解析术语表。</summary>
    /// <returns>解析后的术语表文档。</returns>
    public TerminologyDocument LoadTerminology() =>
        ParseTerminology(ReadText(Path.Combine(SpecRoot, TerminologyFileName)));

    /// <summary>读取并解析设计令牌。</summary>
    /// <returns>解析后的设计令牌。</returns>
    public DesignTokens LoadDesignTokens() =>
        DesignTokens.Parse(ReadText(Path.Combine(SpecRoot, TokensDirectoryName, DesignTokensFileName)));

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