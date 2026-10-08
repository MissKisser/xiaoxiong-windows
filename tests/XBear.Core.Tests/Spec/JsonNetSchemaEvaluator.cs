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
        var errors = new List<ValidationError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Walk(results, errors, seen);

        return errors;
    }

    private static void Walk(EvaluationResults node, List<ValidationError> errors, HashSet<string> seen)
    {
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