namespace XBear.Core.Diagnostics;

/// <summary>
/// 应用程序路径的统一管理。
/// 实例数据、镜像、外部工具（QEMU 与 adb）与日志的落盘位置在此集中维护，
/// 优先定位安装目录内捆绑的副本，避免各组件自行假设宿主全局环境。
/// </summary>
public static class AppPaths
{
    /// <summary>应用专属数据目录叶子名。</summary>
    public const string AppDataLeafName = "XBear";

    /// <summary>实例配置与运行时数据的子目录名。</summary>
    public const string InstancesDirectoryName = "instances";

    /// <summary>日志输出的子目录名。</summary>
    public const string LogsDirectoryName = "logs";

    /// <summary>崩溃日志的文件名。</summary>
    public const string CrashLogFileName = "crash.log";

    /// <summary>安装包内捆绑 QEMU 的子目录名。</summary>
    public const string BundledQemuDirectoryName = "qemu";

    /// <summary>安装包内捆绑 adb 的子目录名。</summary>
    public const string BundledAdbDirectoryName = "platform-tools";

    /// <summary>安装包内 base 镜像的子目录名。</summary>
    public const string ImagesDirectoryName = "images";

    /// <summary>覆写数据根目录的环境变量名。</summary>
    public const string DataRootEnvironmentVariable = "XBEAR_DATA_ROOT";

    /// <summary>覆写镜像根目录的环境变量名。</summary>
    public const string ImagesRootEnvironmentVariable = "XBEAR_IMAGES_ROOT";

    /// <summary>覆写 QEMU 安装目录的环境变量名。</summary>
    public const string QemuDirectoryEnvironmentVariable = "XBEAR_QEMU_DIR";

    /// <summary>覆写 adb 安装目录的环境变量名。</summary>
    public const string AdbDirectoryEnvironmentVariable = "XBEAR_ADB_DIR";

    /// <summary>
    /// 解析可写数据根目录，默认落在当前用户的 LocalAppData 下。
    /// 实例配置、墓碑、overlay 与日志均在此根目录下展开，保证无管理员权限时仍可正常读写。
    /// </summary>
    /// <returns>数据根目录绝对路径。</returns>
    public static string ResolveDataRoot()
    {
        string? env = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env.Trim());
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDataLeafName);
    }

    /// <summary>
    /// 解析实例配置根目录。
    /// </summary>
    /// <returns>实例配置根目录绝对路径。</returns>
    public static string ResolveInstancesRoot() =>
        Path.Combine(ResolveDataRoot(), InstancesDirectoryName);

    /// <summary>
    /// 解析日志输出根目录。
    /// </summary>
    /// <returns>日志目录绝对路径。</returns>
    public static string ResolveLogRoot() =>
        Path.Combine(ResolveDataRoot(), LogsDirectoryName);

    /// <summary>
    /// 解析崩溃日志文件路径。
    /// </summary>
    /// <returns>崩溃日志绝对路径。</returns>
    public static string ResolveCrashLogPath() =>
        Path.Combine(ResolveLogRoot(), CrashLogFileName);

    /// <summary>
    /// 解析只读 base 镜像根目录。
    /// 优先使用环境变量，其次按安装目录（与程序同级或父级）探测，最后回退到程序目录下的 images。
    /// </summary>
    /// <param name="baseDirectory">程序目录，可为空表示使用 <see cref="AppContext.BaseDirectory"/>。</param>
    /// <returns>镜像根目录绝对路径。</returns>
    public static string ResolveImagesRoot(string? baseDirectory = null)
    {
        string? env = Environment.GetEnvironmentVariable(ImagesRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env.Trim());
        }

        string root = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        string[] candidates =
        {
            Path.Combine(root, ImagesDirectoryName),
            Path.Combine(root, "..", ImagesDirectoryName),
            Path.Combine(root, "..", "..", ImagesDirectoryName),
        };

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return Path.Combine(root, ImagesDirectoryName);
    }

    /// <summary>
    /// 构造 QEMU 的优先探测目录列表。
    /// 环境变量与安装目录下捆绑的 qemu 目录排在最前，宿主已有安装作为回退。
    /// </summary>
    /// <param name="baseDirectory">程序所在目录。</param>
    /// <returns>QEMU 候选目录列表。</returns>
    public static IReadOnlyList<string> BuildPreferredQemuDirectories(string? baseDirectory = null)
    {
        var list = new List<string>();

        void Append(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                string full = Path.GetFullPath(path.Trim());
                if (!list.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(full);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 单个非法路径不中断其余候选。
            }
        }

        Append(Environment.GetEnvironmentVariable(QemuDirectoryEnvironmentVariable));

        string root = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        Append(Path.Combine(root, BundledQemuDirectoryName));
        Append(Path.Combine(root, "..", BundledQemuDirectoryName));
        Append(Path.Combine(root, "..", "..", BundledQemuDirectoryName));

        return list;
    }

    /// <summary>
    /// 构造 adb 的优先探测目录列表。
    /// 环境变量与安装目录下捆绑的 platform-tools 目录排在最前，系统 PATH 作为回退。
    /// </summary>
    /// <param name="baseDirectory">程序所在目录。</param>
    /// <returns>adb 候选目录列表。</returns>
    public static IReadOnlyList<string> BuildPreferredAdbDirectories(string? baseDirectory = null)
    {
        var list = new List<string>();

        void Append(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                string full = Path.GetFullPath(path.Trim());
                if (!list.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(full);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 单个非法路径不中断其余候选。
            }
        }

        Append(Environment.GetEnvironmentVariable(AdbDirectoryEnvironmentVariable));

        string root = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        Append(Path.Combine(root, BundledAdbDirectoryName));
        Append(Path.Combine(root, "..", BundledAdbDirectoryName));
        Append(Path.Combine(root, "..", "..", BundledAdbDirectoryName));

        return list;
    }
}