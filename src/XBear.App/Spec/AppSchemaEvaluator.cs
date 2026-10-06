using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.App.Spec;

/// <summary>
/// Schema 求值器的公开入口。求值实现保持 internal 不外泄，
/// 需要求值能力的装配层经本工厂取得 <see cref="ISchemaEvaluator"/>。
/// </summary>
public static class SchemaEvaluatorFactory
{
    /// <summary>
    /// 创建一个基于 JsonSchema.Net 的 Schema 求值器。
    /// </summary>
    /// <returns>Schema 求值器实例。</returns>
    public static ISchemaEvaluator Create() => new AppSchemaEvaluator();
}

/// <summary>
/// 基于 JsonSchema.Net 的 Schema 求值器。Core 工程不引用任何 Schema 库，
/// 求值实现在引用了 JsonSchema.Net 的装配层注入，组合根据此构造校验器。
/// 求值逻辑直接委托给 JsonSchema.Net，不自行实现关键字语义。
/// </summary>
internal sealed class AppSchemaEvaluator : ISchemaEvaluator
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
    /// <exception cref="XBearException">Schema 本身不合法，或待校验内容不是合法 JSON 时抛出。</exception>
    public IReadOnlyList<ValidationError> Evaluate(
        string schemaJson,
        string instanceJson,
        string schemaFileName)
    {
        JsonSchema schema = ParseSchema(schemaJson, schemaFileName);
        JsonNode instance = ParseInstance(instanceJson, schemaFileName);

        EvaluationResults results = schema.Evaluate(instance, new EvaluationOptions
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
            string path = node.InstanceLocation?.ToString() ?? string.Empty;
            string keyword = node.SchemaLocation?.ToString() ?? string.Empty;

            foreach (KeyValuePair<string, string> entry in node.Errors ?? EmptyErrors)
            {
                // entry.Key 为触发的关键字名，entry.Value 为失败原因。
                string message = string.IsNullOrEmpty(entry.Key)
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

        foreach (EvaluationResults child in node.Details)
        {
            Walk(child, errors, seen);
        }
    }
}
