using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Input;

/// <summary>可移植像素图的尺寸解析，取头部中的宽高。</summary>
public static class PpmHeader
{
    /// <summary>读取尺寸所需的最大字符数，头部之后的像素数据一律不看。</summary>
    private const int MaxHeaderLength = 128;

    /// <summary>
    /// 从二进制图像文件的起始片段解析尺寸。
    /// </summary>
    /// <param name="header">文件起始片段。</param>
    /// <param name="geometry">解析出的尺寸，失败时为默认值。</param>
    /// <returns>头部合法且宽高为正时返回 true。</returns>
    public static bool TryParse(ReadOnlySpan<char> header, out ScreenGeometry geometry)
    {
        geometry = default;

        if (header.Length == 0)
        {
            return false;
        }

        int index = 0;

        // 头部以 P6 开头，其后是宽、高、最大值三段，以空白分隔，行首可有注释。
        SkipWhitespaceAndComments(header, ref index);
        if (!Consume(header, ref index, "P6"))
        {
            return false;
        }

        if (!TryReadInt(header, ref index, out int width) ||
            !TryReadInt(header, ref index, out int height))
        {
            return false;
        }

        if (!TryReadInt(header, ref index, out int maxValue) || maxValue <= 0)
        {
            return false;
        }

        geometry = new ScreenGeometry(width, height);
        return geometry.IsValid;
    }

    /// <summary>从二进制图像文件路径解析尺寸。</summary>
    /// <param name="path">图像文件路径。</param>
    /// <returns>尺寸，文件不可读或头部不合法时为 null。</returns>
    public static ScreenGeometry? TryReadFrom(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII);

            char[] buffer = new char[MaxHeaderLength];
            int read = reader.Read(buffer, 0, buffer.Length);
            return TryParse(buffer.AsSpan(0, read), out ScreenGeometry geometry) ? geometry : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"读取 guest 截图以确定显示尺寸失败：{ex.Message}",
                "请确认实例显示后端已就绪后重试。",
                ex);
        }
    }

    private static void SkipWhitespaceAndComments(ReadOnlySpan<char> text, ref int index)
    {
        while (index < text.Length)
        {
            char current = text[index];

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '#')
            {
                while (index < text.Length && text[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            return;
        }
    }

    private static bool Consume(ReadOnlySpan<char> text, ref int index, string literal)
    {
        if (index + literal.Length > text.Length ||
            !text.Slice(index, literal.Length).SequenceEqual(literal))
        {
            return false;
        }

        index += literal.Length;
        return true;
    }

    private static bool TryReadInt(ReadOnlySpan<char> text, ref int index, out int value)
    {
        value = 0;
        SkipWhitespaceAndComments(text, ref index);

        int start = index;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            index++;
        }

        if (index == start || !int.TryParse(text[start..index].ToString(), out value))
        {
            return false;
        }

        return true;
    }
}
