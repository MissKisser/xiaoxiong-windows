using XBear.Core.Diagnostics;

namespace XBear.Core.FileTransfers;

/// <summary>
/// 传输路径的边界与安全校验器。
/// 实例侧限定用户可见域 /sdcard/ 前缀并防御路径穿越；
/// 宿主侧校验绝对路径、规范化并排查非法控制字符。
/// </summary>
public static class FileTransferPathValidator
{
    /// <summary>实例侧允许的用户存储域根前缀。</summary>
    public const string GuestAllowedPrefix = "/sdcard/";

    /// <summary>实例侧允许的用户存储根路径（不带斜杠尾缀）。</summary>
    public const string GuestAllowedRoot = "/sdcard";

    /// <summary>
    /// 校验并归一化实例侧路径。路径必须落在 /sdcard/ 用户可见域内，
    /// 并在消除 . 与 .. 段后确认未发生逃逸。
    /// </summary>
    /// <param name="remotePath">待校验的实例侧原始路径。</param>
    /// <returns>归一化后的实例侧绝对路径（正斜杠分隔）。</returns>
    /// <exception cref="XBearException">路径为空、含控制字符或越出 /sdcard/ 域时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public static string NormalizeAndValidateGuestPath(string? remotePath)
    {
        string raw = (remotePath ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "实例侧路径不能为空。",
                "请提供位于 /sdcard/ 域内的实例侧路径。");
        }

        AssertNoControlCharacters(raw, "实例侧");

        string normalizedSeparators = raw.Replace('\\', '/');
        if (!normalizedSeparators.StartsWith('/'))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"实例侧路径 {raw} 不是以正斜杠开头的绝对路径。",
                "实例侧路径必须以 /sdcard/ 开头。");
        }

        string canonical = ResolveCanonicalUnixPath(normalizedSeparators);

        if (!IsWithinGuestAllowedDomain(canonical))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"实例侧路径 {raw} 超出允许的用户存储域 /sdcard/（归一化为 {canonical}）。",
                "实例侧路径必须限定在 /sdcard/ 域内，不得使用上级目录段回退越界。");
        }

        return canonical;
    }

    /// <summary>
    /// 校验并归一化宿主侧路径。宿主侧必须为显式绝对路径，排查非法字符与控制字符。
    /// </summary>
    /// <param name="localPath">待校验的宿主侧原始路径。</param>
    /// <returns>规范化后的宿主侧绝对路径。</returns>
    /// <exception cref="XBearException">路径为空、含非法字符或非绝对路径时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public static string NormalizeAndValidateHostPath(string? localPath)
    {
        string raw = (localPath ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "宿主侧路径不能为空。",
                "请指定宿主侧完整的绝对路径。");
        }

        AssertNoControlCharacters(raw, "宿主侧");

        if (raw.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"宿主侧路径 {raw} 包含文件系统非法字符。",
                "请移除路径中的非法字符后重试。");
        }

        if (!Path.IsPathRooted(raw))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"宿主侧路径 {raw} 不是绝对路径。",
                "宿主侧路径必须为显式给定的绝对路径（例如以盘符开头的完整路径）。");
        }

        try
        {
            return Path.GetFullPath(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"宿主侧路径 {raw} 格式非法：{ex.Message}",
                "请提供有效的宿主绝对路径。",
                ex);
        }
    }

    private static bool IsWithinGuestAllowedDomain(string canonicalPath)
    {
        return canonicalPath == GuestAllowedRoot || canonicalPath.StartsWith(GuestAllowedPrefix, StringComparison.Ordinal);
    }

    private static string ResolveCanonicalUnixPath(string path)
    {
        var segments = new List<string>();
        foreach (string part in path.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(part);
        }

        return "/" + string.Join('/', segments);
    }

    private static void AssertNoControlCharacters(string text, string scope)
    {
        foreach (char c in text)
        {
            if (char.IsControl(c) || c <= 0x1F)
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"{scope}路径包含不允许的控制字符。",
                    "请使用不含不可见控制字符的合法路径。");
            }
        }
    }
}
