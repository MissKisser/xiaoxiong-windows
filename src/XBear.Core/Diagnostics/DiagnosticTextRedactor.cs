using System.Text;

namespace XBear.Core.Diagnostics;

/// <summary>
/// 把诊断文本里指向宿主用户目录的绝对路径前缀替换为占位符。
/// </summary>
/// <remarks>
/// 诊断包会被用户直接发给他人，其中出现的 <c>C:\Users\某人</c> 形态路径会暴露上报者身份。
/// 路径的形状（是否落在预期根目录下、是否指向真实存在的镜像位置）对故障定位有意义，
/// 而其中的用户名没有，故只保留形状。
/// </remarks>
public static class DiagnosticTextRedactor
{
    private const string UserProfilePlaceholder = "<USERPROFILE>";
    private const string AppDataPlaceholder = "<APPDATA>";

    private static readonly (string Prefix, string Placeholder)[] Prefixes = BuildPrefixes();

    /// <summary>
    /// 脱敏一段文本，返回替换后的新字符串。
    /// </summary>
    /// <param name="text">待脱敏文本，可为 null 或空。</param>
    /// <returns>用户目录前缀已被替换的文本；输入为空时原样返回。</returns>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        string result = text;
        foreach ((string prefix, string placeholder) in Prefixes)
        {
            result = result.Replace(prefix, placeholder, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// 以 UTF-8 解码字节并做脱敏，非法字节序列按替换字符处理。
    /// </summary>
    /// <param name="bytes">待解码字节。</param>
    /// <returns>脱敏后的文本。</returns>
    public static string DecodeAndRedact(ReadOnlySpan<byte> bytes) =>
        Redact(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes));

    /// <summary>
    /// 枚举需要脱敏的宿主目录前缀，同一目录的正斜杠写法一并覆盖。
    /// </summary>
    /// <returns>按前缀长度降序排列的前缀与占位符对照表。</returns>
    private static (string Prefix, string Placeholder)[] BuildPrefixes()
    {
        var collected = new List<(string Prefix, string Placeholder)>();

        Add(collected, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), UserProfilePlaceholder);
        Add(collected, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDataPlaceholder);
        Add(collected, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppDataPlaceholder);

        // 长前缀优先：否则较短的前缀先命中，占位符会把更长的路径前缀从中间截断。
        collected.Sort(static (left, right) => right.Prefix.Length.CompareTo(left.Prefix.Length));
        return collected.ToArray();
    }

    private static void Add(List<(string Prefix, string Placeholder)> collected, string path, string placeholder)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 0)
        {
            return;
        }

        collected.Add((trimmed, placeholder));
        if (trimmed.Contains('\\'))
        {
            collected.Add((trimmed.Replace('\\', '/'), placeholder));
        }
    }
}