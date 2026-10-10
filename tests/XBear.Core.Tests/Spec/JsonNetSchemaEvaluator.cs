using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.Core.Tests.Spec;

/// <summary>
/// 基于 JsonSchema.Net 的 Schema 求值器。
/// Core 工程不引用 Schema 库，求值实现在引用了 Schema 库的测试装配中注入。
/// </summary>
internal sealed class JsonNetSchemaEvaluator : ISchemaEvaluator
{
    private static readonly IReadOnlyDictionary<string, string> EmptyErrors =
        new Dictionary<string, string>();

    /// <summary>
    /// 只作为判定条件、不直接对实例下结论的关键字。
    /// 契约用它们表达「满足某条件时才要求某字段」，例如未安装的包不得携带版本号。
    /// 这些子求值本就可以合法地失败：条件不成立时其内部报错属于探测噪声，
    /// 契约本身仍为通过。逐层遍历时必须跳过它们，否则会把条件不成立误报成校验失败。
    /// </summary>
    private static readonly HashSet<string> ProbeKeywords =
        new(StringComparer.Ordinal)
        {
            "if",
            "else",
            "not"
        };

    /// <summary>
    /// 对照给定 Schema 求值一份 JSON 文本，并展开为结构化错误列表。
    /// </summary>
    /// <param name="schemaJson">Schema 文件文本。</param>
    /// <param name="instanceJson">待校验的 JSON 文本。</param>
    /// <param name="schemaFileName">Schema 文件名，用于错误定位。</param>
    /// <returns>校验错误集合，通过校验时为空集合。</returns>
    public IReadOnlyList<ValidationError> Evaluate(
        string schemaJson,
        string instanceJson,
        string schemaFileName)
    {
        var schema = ParseSchema(schemaJson, schemaFileName);
        var instance = ParseInstance(instanceJson, schemaFileName);

        var results = schema.Evaluate(instance, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        return Collect(results);
    }

    private static JsonSchema ParseSchema(string schemaJson, string schemaFileName)
    {
        try
        {
            return JsonSchema.FromText(schemaJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"{schemaFileName} 不是合法的 JSON Schema。",
                "Schema 属于跨端契约，修复需两端同步确认。",
                ex);
        }
    }

    private static JsonNode ParseInstance(string instanceJson, string schemaFileName)
    {
        try
        {
            return JsonNode.Parse(instanceJson) ?? throw new XBearException(
                ErrorCategory.Spec,
                $"待校验内容为空，无法对照 {schemaFileName} 求值。");
        }
        catch (JsonException ex)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"待校验内容不是合法 JSON，无法对照 {schemaFileName} 求值。",
                null,
                ex);
        }
    }

    private static List<ValidationError> Collect(EvaluationResults results)
    {
        // 整体求值通过即没有违规：条件求值子树本就可以合法失败，
        // 逐层遍历必须以整体结论为准，否则条件不成立会被误报成校验失败。
        if (results.IsValid)
        {
            return new List<ValidationError>();
        }

        var errors = new List<ValidationError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Walk(results, errors, seen);

        return errors;
    }

    private static void Walk(EvaluationResults node, List<ValidationError> errors, HashSet<string> seen)
    {
        // 条件求值节点自身通过时，其子树内的失败只是条件不成立的探测噪声，契约仍为通过。
        // 条件求值节点自身不通过才是真正的违规，此时照常报告并下探。
        if (node.IsValid && IsProbeNode(node))
        {
            return;
        }

        if (!node.IsValid && node.HasErrors)
        {
            var path = node.InstanceLocation?.ToString() ?? string.Empty;
            var keyword = node.SchemaLocation?.ToString() ?? string.Empty;

            foreach (var entry in node.Errors ?? EmptyErrors)
            {
                // entry.Key 为触发的关键字名，entry.Value 为失败原因。
                var message = string.IsNullOrEmpty(entry.Key)
                    ? entry.Value
                    : $"{entry.Key}: {entry.Value}";

                var error = new ValidationError(path, message, keyword);
                if (seen.Add($"{path}|{message}"))
                {
                    errors.Add(error);
                }
            }
        }

        if (!node.HasDetails)
        {
            return;
        }

        foreach (var child in node.Details)
        {
            Walk(child, errors, seen);
        }
    }

    /// <summary>
    /// 判断一个求值节点是否位于条件求值子树内。契约用条件表达
    /// 「满足某前提时才要求某字段」，条件不成立时其子树的失败只是探测噪声，
    /// 报告出来会把合规内容误判为违规。
    /// </summary>
    /// <param name="node">待判断的求值节点。</param>
    /// <returns>该节点属于条件探测子树时为 true。</returns>
    private static bool IsProbeNode(EvaluationResults node)
    {
        var location = node.SchemaLocation?.ToString();
        if (string.IsNullOrEmpty(location))
        {
            return false;
        }

        var segments = location.Split('/');

        for (var i = 1; i < segments.Length; i++)
        {
            // properties 之后的同名段是字段名而非条件关键字，
            // 契约允许字段就叫这个名字，不能据此判定为探测节点。
            if (segments[i - 1] == "properties")
            {
                continue;
            }

            if (ProbeKeywords.Contains(segments[i]))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>测试用规格入口，集中提供读取器与校验器实例。</summary>
internal static class SpecTestHost
{
    /// <summary>规格读取器，定位输出目录下的 spec/。</summary>
    public static SpecLoader Loader { get; } = SpecLoader.Default;

    /// <summary>Schema 校验器。</summary>
    public static SpecValidator Validator { get; } =
        new(new JsonNetSchemaEvaluator(), Loader);

    /// <summary>
    /// 读取 Windows 端实例样例文本。
    /// </summary>
    /// <returns>样例文件文本。</returns>
    public static string WindowsInstanceJson() =>
        Loader.ReadFixtureText(SpecLoader.WindowsInstanceFixtureName);

    /// <summary>
    /// 读取 Android 端实例样例文本。
    /// </summary>
    /// <returns>样例文件文本。</returns>
    public static string AndroidInstanceJson() =>
        Loader.ReadFixtureText(SpecLoader.AndroidInstanceFixtureName);

    /// <summary>
    /// 读取镜像清单样例文本。
    /// </summary>
    /// <returns>样例文件文本。</returns>
    public static string ImageJson() =>
        Loader.ReadFixtureText(SpecLoader.ImageFixtureName);

    /// <summary>
    /// 读取只含必填字段的最小快照样例文本。
    /// </summary>
    /// <returns>样例文件文本。</returns>
    public static string SnapshotMinimalJson() =>
        Loader.ReadFixtureText(SpecLoader.SnapshotMinimalFixtureName);

    /// <summary>
    /// 读取含全部可选字段的完整快照样例文本。
    /// </summary>
    /// <returns>样例文件文本。</returns>
    public static string SnapshotFullJson() =>
        Loader.ReadFixtureText(SpecLoader.SnapshotFullFixtureName);

    /// <summary>
    /// 读取只含必填字段的最小投屏会话样例文本。
    /// </summary>
    /// <returns>样例文件文本。</returns>
    public static string ProjectionMinimalJson() =>
        Loader.ReadFixtureText(SpecLoader.ProjectionMinimalFixtureName);

    /// <summary>
    /// 读取含输入语义与平台特有字段的完整投屏会话样例文本。
    /// </summary>
    /// <returns>样例文件文本。</returns>
    public static string ProjectionFullJson() =>
        Loader.ReadFixtureText(SpecLoader.ProjectionFullFixtureName);

    /// <summary>
    /// 读取只含必填字段的最小传输任务样例文本。
    /// </summary>
    /// <returns>样例文件文本。</returns>
    public static string FileTransferMinimalJson() =>
        Loader.ReadFixtureText(SpecLoader.FileTransferMinimalFixtureName);

    /// <summary>
    /// 读取含进度与时刻字段的完整传输任务样例文本。
    /// </summary>
    /// <returns>样例文件文本。</returns>
    public static string FileTransferFullJson() =>
        Loader.ReadFixtureText(SpecLoader.FileTransferFullFixtureName);

    /// <summary>
    /// 读取只含必填字段的最小模块样例文本。
    /// </summary>
    /// <returns>样例文件文本。</returns>
    public static string ModuleMinimalJson() =>
        Loader.ReadFixtureText(SpecLoader.ModuleMinimalFixtureName);

    /// <summary>
    /// 读取含清单与结构约定的完整模块样例文本。
    /// </summary>
    /// <returns>样例文件文本。</returns>
    public static string ModuleFullJson() =>
        Loader.ReadFixtureText(SpecLoader.ModuleFullFixtureName);

    /// <summary>
    /// 读取只含必填字段的最小应用样例文本。
    /// </summary>
    /// <returns>样例文件文本。</returns>
    public static string ApplicationMinimalJson() =>
        Loader.ReadFixtureText(SpecLoader.ApplicationMinimalFixtureName);

    /// <summary>
    /// 读取含版本、来源与操作记录的完整应用样例文本。
    /// </summary>
    /// <returns>样例文件文本。</returns>
    public static string ApplicationFullJson() =>
        Loader.ReadFixtureText(SpecLoader.ApplicationFullFixtureName);

    /// <summary>
    /// 读取版本契约文本。
    /// </summary>
    /// <returns>版本契约文件文本。</returns>
    public static string VersionJson() =>
        Loader.ReadText(Path.Combine(Loader.SpecRoot, SpecLoader.VersionFileName));

    /// <summary>
    /// 读取性能基线文本。
    /// </summary>
    /// <returns>性能基线文件文本。</returns>
    public static string BaselineJson() =>
        Loader.ReadText(Path.Combine(Loader.SpecRoot, SpecLoader.BaselineFileName));

    /// <summary>
    /// 读取术语表文档。
    /// </summary>
    /// <returns>解析后的术语表。</returns>
    public static TerminologyDocument Terminology() => Loader.LoadTerminology();

    /// <summary>
    /// 读取设计令牌。
    /// </summary>
    /// <returns>解析后的设计令牌。</returns>
    public static DesignTokens Tokens() => Loader.LoadDesignTokens();
}