using XBear.Core.Diagnostics;

namespace XBear.Core.Spec;

/// <summary>
/// 共享规格根目录的定位。
/// 安装布局把程序放在 app/ 下、契约放在与 app/ 平级的 spec/ 下，
/// 源码树布局把契约放在仓库根的 spec 子模块内；两种布局按候选顺序探测，
/// 首个同时含有版本契约与 schema 目录的候选即为规格根。
/// </summary>
public static class SpecRootResolver
{
    /// <summary>向上回溯的目录层数上限，防止在异常目录结构下无界探测。</summary>
    public const int MaxAncestorDepth = 12;

    /// <summary>
    /// 判断目录是否为可用的规格根：版本契约文件与 schema 目录同时存在才算完整，
    /// 只命中其一说明候选目录是开发中间产物，不足以支撑运行期读取。
    /// </summary>
    /// <param name="directory">待判断的目录绝对路径。</param>
    /// <returns>目录构成完整规格根时为 true。</returns>
    public static bool IsSpecRoot(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        return File.Exists(Path.Combine(directory, SpecLoader.VersionFileName))
               && Directory.Exists(Path.Combine(directory, SpecLoader.SchemaDirectoryName));
    }

    /// <summary>
    /// 按探测顺序构造去重后的候选目录列表。
    /// 顺序为：外部显式指定、安装目录内的同级与上两级、程序目录、再逐层向上回溯。
    /// </summary>
    /// <param name="baseDirectory">程序所在目录，通常为 <see cref="AppContext.BaseDirectory"/>。</param>
    /// <param name="searchDirectories">外部显式指定的候选目录，排在最前，可为空。</param>
    /// <returns>去重后的候选目录绝对路径列表。</returns>
    /// <exception cref="ArgumentException">程序目录为空时抛出。</exception>
    public static IReadOnlyList<string> BuildCandidates(
        string baseDirectory,
        IEnumerable<string>? searchDirectories = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var candidates = new List<string>();

        void Append(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            string full;
            try
            {
                full = Path.GetFullPath(directory.Trim());
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 单个候选路径非法不应中断其余候选的探测。
                return;
            }

            if (!candidates.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(full);
            }
        }

        if (searchDirectories is not null)
        {
            foreach (string directory in searchDirectories)
            {
                Append(directory);
            }
        }

        string root = Path.GetFullPath(baseDirectory);
        Append(Path.Combine(root, SpecLoader.SpecDirectoryName));
        Append(Path.Combine(root, "..", SpecLoader.SpecDirectoryName));
        Append(Path.Combine(root, "..", "..", SpecLoader.SpecDirectoryName));

        DirectoryInfo? current = new DirectoryInfo(root);
        for (int depth = 0; depth <= MaxAncestorDepth && current is not null; depth++, current = current.Parent)
        {
            // 子模块布局为 <仓库根>/spec/spec，其中外层 spec 是子模块根，内层才是契约根。
            Append(Path.Combine(current.FullName, SpecLoader.SpecDirectoryName));
            Append(Path.Combine(
                current.FullName,
                SpecLoader.SpecDirectoryName,
                SpecLoader.SpecDirectoryName));
        }

        return candidates;
    }

    /// <summary>
    /// 定位规格根目录，全部候选均不构成完整契约时返回 null。
    /// </summary>
    /// <param name="baseDirectory">程序所在目录。</param>
    /// <param name="searchDirectories">外部显式指定的候选目录，可为空。</param>
    /// <returns>命中的规格根绝对路径，未命中时为 null。</returns>
    public static string? TryResolve(
        string baseDirectory,
        IEnumerable<string>? searchDirectories = null)
    {
        foreach (string candidate in BuildCandidates(baseDirectory, searchDirectories))
        {
            if (IsSpecRoot(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 定位规格根目录，全部候选均不构成完整契约时抛出规格类异常。
    /// </summary>
    /// <param name="baseDirectory">程序所在目录。</param>
    /// <param name="searchDirectories">外部显式指定的候选目录，可为空。</param>
    /// <returns>命中的规格根绝对路径。</returns>
    /// <exception cref="XBearException">全部候选均不构成完整契约时抛出，分类为 <see cref="ErrorCategory.Spec"/>。</exception>
    public static string Resolve(
        string baseDirectory,
        IEnumerable<string>? searchDirectories = null)
    {
        IReadOnlyList<string> candidates = BuildCandidates(baseDirectory, searchDirectories);
        return TryResolve(baseDirectory, searchDirectories) ?? throw new XBearException(
            ErrorCategory.Spec,
            $"未找到共享规格契约，已探测目录：{string.Join("、", candidates)}",
            "确认安装目录下存在 spec/ 子目录，或拉取 spec 子模块后重新构建；"
            + $"也可设置环境变量 {SpecLoader.SpecRootEnvironmentVariable} 指向契约根目录。");
    }
}