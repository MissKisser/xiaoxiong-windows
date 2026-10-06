using System.Text;

namespace XBear.Core.Qemu;

/// <summary>按 Windows 命令行解析规则把参数序列拼为字符串。</summary>
internal static class CommandLineFormatter
{
    /// <summary>把参数序列拼接为完整命令行，不含可执行文件路径。</summary>
    /// <param name="arguments">待拼接的参数序列。</param>
    /// <returns>可直接赋给 ProcessStartInfo.Arguments 的字符串。</returns>
    internal static string Join(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var builder = new StringBuilder();
        for (var index = 0; index < arguments.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(Quote(arguments[index]));
        }

        return builder.ToString();
    }

    /// <summary>按需为单个参数补齐引号与转义。</summary>
    /// <param name="argument">原始参数。</param>
    /// <returns>可安全嵌入命令行的参数文本。</returns>
    internal static string Quote(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        if (!argument.Any(static c => c is ' ' or '\t' or '\n' or '"'))
        {
            return argument;
        }

        var builder = new StringBuilder(argument.Length + 2);
        builder.Append('"');

        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1);
                backslashes = 0;
                builder.Append(character);
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(character);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }
}