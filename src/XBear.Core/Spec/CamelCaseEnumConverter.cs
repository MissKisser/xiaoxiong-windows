using System.Text.Json;
using System.Text.Json.Serialization;

namespace XBear.Core.Spec;

/// <summary>
/// 把枚举按小驼峰名读写，用于对齐规格中规定的小驼峰枚举取值（如 installed / notInstalled）。
/// C# 成员名遵循大驼峰，与契约的字面量大小写不一致时需要此转换器在两侧对齐。
/// </summary>
/// <typeparam name="TEnum">目标枚举类型。</typeparam>
public sealed class CamelCaseEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    /// <summary>
    /// 从 JSON 读取枚举，忽略大小写匹配成员名的小驼峰形式。
    /// </summary>
    /// <param name="reader">JSON 读取器。</param>
    /// <param name="typeToConvert">目标类型。</param>
    /// <param name="options">序列化选项。</param>
    /// <returns>匹配到的枚举成员。</returns>
    /// <exception cref="JsonException">取值不属于该枚举时抛出。</exception>
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        if (raw is null)
        {
            return default;
        }

        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(
                    char.ToLowerInvariant(name[0]) + name[1..],
                    raw,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<TEnum>(name);
            }
        }

        throw new JsonException($"取值 {raw} 不属于 {typeof(TEnum).Name} 的合法取值");
    }

    /// <summary>
    /// 向 JSON 写出小驼峰枚举名。
    /// </summary>
    /// <param name="writer">JSON 写出器。</param>
    /// <param name="value">待写出的枚举值。</param>
    /// <param name="options">序列化选项。</param>
    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        var name = value.ToString();
        writer.WriteStringValue(char.ToLowerInvariant(name[0]) + name[1..]);
    }
}
