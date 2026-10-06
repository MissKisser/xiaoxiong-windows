using XBear.Core.Diagnostics;

namespace XBear.Core.Qemu;

/// <summary>定位宿主上已安装的 QEMU 可执行文件。</summary>
public sealed class QemuPaths
{
    /// <summary>系统模拟器可执行文件名。</summary>
    public const string SystemExecutableName = "qemu-system-x86_64.exe";

    /// <summary>磁盘镜像工具可执行文件名。</summary>
    public const string ImageExecutableName = "qemu-img.exe";

    private const string InstallDirectoryLeafName = "qemu";

    /// <summary>命中目录中系统模拟器可执行文件的绝对路径。</summary>
    public string QemuSystemPath { get; }

    /// <summary>命中目录中磁盘镜像工具可执行文件的绝对路径。</summary>
    public string QemuImgPath { get; }

    /// <summary>命中目录的绝对路径。</summary>
    public string InstallDirectory { get; }

    /// <summary>按候选目录顺序探测 QEMU 安装位置。
    /// 两个可执行文件必须同处一个目录且同时存在，否则继续向后探测。</summary>
    /// <param name="installDirectory">用户显式配置的安装目录，可为空。</param>
    /// <param name="searchDirectories">
    /// 候选目录集合。为 null 时按 PATH 与常见安装目录探测；显式传入时仅在给定目录中查找，便于受控环境复用该探测逻辑。
    /// </param>
    /// <exception cref="XBearException">所有候选目录均缺少可执行文件时抛出 <see cref="ErrorCategory.Dependency"/>。</exception>
    public QemuPaths(string? installDirectory = null, IEnumerable<string>? searchDirectories = null)
    {
        var candidates = BuildCandidates(installDirectory, searchDirectories);

        foreach (var candidate in candidates)
        {
            var systemPath = Path.Combine(candidate, SystemExecutableName);
            var imagePath = Path.Combine(candidate, ImageExecutableName);
            if (File.Exists(systemPath) && File.Exists(imagePath))
            {
                InstallDirectory = candidate;
                QemuSystemPath = systemPath;
                QemuImgPath = imagePath;
                return;
            }
        }

        throw new XBearException(
            ErrorCategory.Dependency,
            $"未找到 QEMU 可执行文件，已探测目录：{string.Join("、", candidates)}",
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
            foreach (var directory in searchDirectories)
            {
                Append(directory);
            }

            return candidates;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in pathVariable.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Append(entry);
        }

        foreach (var specialFolder in new[]
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