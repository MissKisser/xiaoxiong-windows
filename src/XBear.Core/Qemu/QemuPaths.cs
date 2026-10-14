using XBear.Core.Diagnostics;

namespace XBear.Core.Qemu;

/// <summary>
/// 定位宿主上已安装的 QEMU 可执行文件。
///
/// 定位失败不在构造期抛出：QEMU 是宿主外部依赖，装机场景下本就可能缺席，
/// 组合根若因此崩溃，用户只会看到闪退而不是可执行的处置建议。
/// 因此构造只记录探测结果，解析状态可查询，真正要用到路径时才抛出带指引的依赖异常。
/// </summary>
public sealed class QemuPaths
{
    /// <summary>系统模拟器可执行文件名。</summary>
    public const string SystemExecutableName = "qemu-system-x86_64.exe";

    /// <summary>磁盘镜像工具可执行文件名。</summary>
    public const string ImageExecutableName = "qemu-img.exe";

    private const string InstallDirectoryLeafName = "qemu";

    /// <summary>是否已在候选目录中命中同时具备两个可执行文件的安装目录。</summary>
    public bool IsResolved { get; }

    /// <summary>本次探测覆盖的候选目录，未命中时用于诊断与处置建议。</summary>
    public IReadOnlyList<string> SearchedDirectories { get; }

    /// <summary>命中目录的绝对路径；未命中时为空字符串。</summary>
    public string InstallDirectory { get; } = string.Empty;

    /// <summary>命中目录中系统模拟器可执行文件的绝对路径；未命中时为空字符串。</summary>
    public string QemuSystemPath { get; } = string.Empty;

    /// <summary>命中目录中磁盘镜像工具可执行文件的绝对路径；未命中时为空字符串。</summary>
    public string QemuImgPath { get; } = string.Empty;

    /// <summary>按候选目录顺序探测 QEMU 安装位置。
    /// 两个可执行文件必须同处一个目录且同时存在，否则继续向后探测；全部候选未命中时不抛异常，
    /// 由 <see cref="IsResolved"/> 与 <see cref="RequireSystemExecutablePath"/> 表达结果。</summary>
    /// <param name="installDirectory">用户显式配置的安装目录，可为空。</param>
    /// <param name="searchDirectories">
    /// 候选目录集合。为 null 时按 PATH 与常见安装目录探测；显式传入时仅在给定目录中查找，便于受控环境复用该探测逻辑。
    /// </param>
    public QemuPaths(string? installDirectory = null, IEnumerable<string>? searchDirectories = null)
    {
        IReadOnlyList<string> candidates = BuildCandidates(installDirectory, searchDirectories);
        SearchedDirectories = candidates;

        foreach (string candidate in candidates)
        {
            string systemPath = Path.Combine(candidate, SystemExecutableName);
            string imagePath = Path.Combine(candidate, ImageExecutableName);
            if (File.Exists(systemPath) && File.Exists(imagePath))
            {
                InstallDirectory = candidate;
                QemuSystemPath = systemPath;
                QemuImgPath = imagePath;
                IsResolved = true;
                return;
            }
        }
    }

    /// <summary>
    /// 取系统模拟器可执行文件的绝对路径。
    /// </summary>
    /// <returns>可执行文件绝对路径。</returns>
    /// <exception cref="XBearException">未解析到 QEMU 安装时抛出 <see cref="ErrorCategory.Dependency"/>。</exception>
    public string RequireSystemExecutablePath() => Require(QemuSystemPath);

    /// <summary>
    /// 取磁盘镜像工具可执行文件的绝对路径。
    /// </summary>
    /// <returns>可执行文件绝对路径。</returns>
    /// <exception cref="XBearException">未解析到 QEMU 安装时抛出 <see cref="ErrorCategory.Dependency"/>。</exception>
    public string RequireImageExecutablePath() => Require(QemuImgPath);

    /// <summary>
    /// 未解析时构造带处置建议的依赖异常。
    /// </summary>
    /// <param name="path">待返回的路径，未解析时不会使用。</param>
    /// <returns>已解析时的路径。</returns>
    private string Require(string path)
    {
        if (IsResolved)
        {
            return path;
        }

        throw new XBearException(
            ErrorCategory.Dependency,
            $"未找到 QEMU 可执行文件，已探测目录：{string.Join("、", SearchedDirectories)}",
            "请安装 QEMU for Windows，确认安装目录内同时存在 qemu-system-x86_64.exe 与 qemu-img.exe；"
            + "也可把该目录显式传入 XBear.Core.Qemu.QemuPaths 的 installDirectory 参数，或把目录加入系统 PATH。");
    }

    /// <summary>组装候选目录：显式配置在前，其后为 PATH 与常见安装目录。</summary>
    /// <param name="installDirectory">用户显式配置的安装目录。</param>
    /// <param name="searchDirectories">外部指定的候选目录集合，为 null 时使用默认探测范围。</param>
    /// <returns>去重后的候选目录列表。</returns>
    private static IReadOnlyList<string> BuildCandidates(
        string? installDirectory,
        IEnumerable<string>? searchDirectories)
    {
        var candidates = new List<string>();

        void Append(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            var trimmed = directory.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length > 0 && !candidates.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(trimmed);
            }
        }

        Append(installDirectory);

        if (searchDirectories is not null)
        {
            foreach (string directory in searchDirectories)
            {
                Append(directory);
            }

            return candidates;
        }

        foreach (string preferred in AppPaths.BuildPreferredQemuDirectories())
        {
            Append(preferred);
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string entry in pathVariable.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Append(entry);
        }

        foreach (Environment.SpecialFolder specialFolder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                 })
        {
            Append(CombineUnderSpecialFolder(specialFolder, InstallDirectoryLeafName));
        }

        Append(CombineUnderSpecialFolder(Environment.SpecialFolder.LocalApplicationData, "Programs", InstallDirectoryLeafName));
        Append(CombineUnderSpecialFolder(Environment.SpecialFolder.CommonApplicationData, "chocolatey", "bin"));

        return candidates;
    }

    /// <summary>把系统特殊目录与相对片段拼成候选目录。</summary>
    /// <param name="specialFolder">系统特殊目录枚举值。</param>
    /// <param name="segments">相对片段。</param>
    /// <returns>组合后的目录路径，特殊目录不可用时返回空字符串。</returns>
    private static string CombineUnderSpecialFolder(Environment.SpecialFolder specialFolder, params string[] segments)
    {
        var root = Environment.GetFolderPath(specialFolder);
        return string.IsNullOrWhiteSpace(root)
            ? string.Empty
            : Path.Combine(new[] { root }.Concat(segments).ToArray());
    }
}
