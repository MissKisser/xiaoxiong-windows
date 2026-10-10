using System.IO.Compression;
using System.Text;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Modules;

/// <summary>模块操作的宿主侧目标：一个实例及其在宿主上映射的 adb 端口。</summary>
/// <param name="InstanceId">实例标识，用于日志与异常上下文。</param>
/// <param name="AdbPort">该实例在宿主上映射的 adb 端口。</param>
public sealed record ModuleTarget(string InstanceId, int AdbPort);

/// <summary>实例内已安装的一个模块。</summary>
/// <param name="Id">模块标识，即 /data/adb/modules 下的目录名。</param>
/// <param name="RemotePath">模块在实例内的完整目录路径。</param>
public sealed record InstalledModule(string Id, string RemotePath);

/// <summary>一次模块安装的结果。</summary>
/// <param name="ModuleId">落盘使用的模块标识。</param>
/// <param name="RemotePath">模块在实例内的目录路径。</param>
/// <param name="FileCount">已推送的文件数量。</param>
/// <param name="Files">已推送文件在模块内的相对路径，按推送顺序排列。</param>
public sealed record ModuleInstallResult(
    string ModuleId,
    string RemotePath,
    int FileCount,
    IReadOnlyList<string> Files);

/// <summary>
/// 宿主侧模块安装服务：把 ZIP 模块包校验、解压并推送到实例的 /data/adb/modules，
/// 供开机模块加载器在下次引导时叠加 system/ 子树并执行各阶段脚本。
/// 该服务不新增依赖，全部经 <see cref="IAdbClient"/> 完成，连接按次创建并随次释放。
/// </summary>
public sealed class ModuleInstallerService
{
    /// <summary>实例内的模块根目录，与开机模块加载器扫描的路径一致。</summary>
    public const string ModulesRootPath = "/data/adb/modules";

    /// <summary>模块描述文件名，其 id 字段优先作为模块标识。</summary>
    public const string ModulePropFileName = "module.prop";

    /// <summary>模块的 system overlay 子树目录名。</summary>
    public const string SystemDirectoryName = "system";

    /// <summary>开机模块加载器会依次执行的阶段脚本。</summary>
    public static IReadOnlyList<string> StageScriptNames { get; } =
        ["post-fs-data.sh", "service.sh", "boot-completed.sh"];

    /// <summary>安装或卸载完成后下发的 guest 刷盘命令。</summary>
    private const string ShutdownSyncCommand = "sync";

    private readonly Func<IAdbClient> _adbClientFactory;
    private readonly TimeSpan _connectTimeout;

    /// <summary>
    /// 初始化模块安装服务。
    /// </summary>
    /// <param name="adbClientFactory">
    /// adb 客户端工厂，缺省时按实例新建客户端。每次操作各自创建并在结束时释放，
    /// 不跨调用复用，避免留下指向已回收端口的悬挂连接。
    /// </param>
    /// <param name="connectTimeout">连接与握手的等待上限，为 null 时使用客户端默认上限。</param>
    public ModuleInstallerService(Func<IAdbClient>? adbClientFactory = null, TimeSpan? connectTimeout = null)
    {
        _adbClientFactory = adbClientFactory ?? (static () => new Adb.AdbClient());
        _connectTimeout = connectTimeout ?? Adb.AdbClient.DefaultConnectTimeout;
    }

    /// <summary>
    /// 安装一个 ZIP 模块包。先校验归档结构，抽出模块标识，解压到宿主暂存目录后
    /// 逐个推送到实例的模块目录，最后让 guest 执行一次 sync 把页缓存刷盘。
    /// </summary>
    /// <remarks>
    /// 模块包必须含 module.prop 或至少含 system/ 子树：开机加载器只按目录名、
    /// system/ 子树与阶段脚本生效，两者都没有的包装进去也不会被加载，属于无效输入。
    /// 模块标识取 module.prop 的 id 字段，缺省时退回 ZIP 文件名。
    /// </remarks>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="zipPath">宿主上的模块 ZIP 路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>安装结果，含模块标识、实例内路径与已装文件数。</returns>
    /// <exception cref="XBearException">
    /// 本地包不可读或归档损坏时为 <see cref="ErrorCategory.Storage"/>；
    /// 归档结构不满足模块约定或模块标识非法时为 <see cref="ErrorCategory.Spec"/>；
    /// 与实例的交互失败为 <see cref="ErrorCategory.Protocol"/>。
    /// </exception>
    public async Task<ModuleInstallResult> InstallAsync(
        ModuleTarget target,
        string zipPath,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);

        string stagingRoot = CreateStagingDirectory();
        try
        {
            ModulePackage package = await PreparePackageAsync(zipPath, stagingRoot, cancellationToken)
                .ConfigureAwait(false);

            await RunShellAsync(target, $"mkdir -p {package.RemotePath}", cancellationToken).ConfigureAwait(false);

            foreach (ModuleFile file in package.Files)
            {
                await PushFileAsync(
                        target,
                        file.LocalPath,
                        package.RemotePath + "/" + file.RelativePath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await ApplyScriptPermissionsAsync(target, package, cancellationToken).ConfigureAwait(false);

            // 推送完成后必须让 guest 刷盘：QEMU 直接退出时未落盘的模块文件会变成 0 字节。
            await RunShellAsync(target, ShutdownSyncCommand, cancellationToken).ConfigureAwait(false);

            return new ModuleInstallResult(
                package.ModuleId,
                package.RemotePath,
                package.Files.Count,
                package.Files.Select(file => file.RelativePath).ToArray());
        }
        finally
        {
            TryDeleteStagingDirectory(stagingRoot);
        }
    }

    /// <summary>
    /// 卸载实例内的模块：删除其目录并让 guest 刷盘，使改动立即落到磁盘。
    /// 模块不存在时不做任何改动。
    /// </summary>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="moduleId">模块标识，即模块目录名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>确实删除了一个已存在的模块时返回 true。</returns>
    /// <exception cref="XBearException">
    /// 模块标识非法时为 <see cref="ErrorCategory.Spec"/>；
    /// 与实例的交互失败为 <see cref="ErrorCategory.Protocol"/>。
    /// </exception>
    public async Task<bool> UninstallAsync(
        ModuleTarget target,
        string moduleId,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        string id = NormalizeModuleId(moduleId);
        string remotePath = CombineRemotePath(id);

        if (!await DirectoryExistsAsync(target, remotePath, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await RunShellAsync(target, $"rm -rf {remotePath}", cancellationToken).ConfigureAwait(false);
        await RunShellAsync(target, ShutdownSyncCommand, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 列出实例内已安装的模块。模块根目录不存在时返回空列表，
    /// 不把「尚未安装过模块」当作失败。
    /// </summary>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已安装模块列表，按实例内目录名的字典序排列。</returns>
    /// <exception cref="XBearException">与实例的交互失败为 <see cref="ErrorCategory.Protocol"/>。</exception>
    public async Task<IReadOnlyList<InstalledModule>> ListAsync(
        ModuleTarget target,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);

        if (!await DirectoryExistsAsync(target, ModulesRootPath, cancellationToken).ConfigureAwait(false))
        {
            return Array.Empty<InstalledModule>();
        }

        string output = await RunShellAsync(target, $"ls -1 {ModulesRootPath}", cancellationToken)
            .ConfigureAwait(false);

        return output
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name != "." && name != ".." && !name.StartsWith('.'))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new InstalledModule(name, CombineRemotePath(name)))
            .ToArray();
    }

    /// <summary>
    /// 按模块包约定校验归档、解压到暂存目录并确定模块标识。
    /// </summary>
    /// <param name="zipPath">宿主上的模块 ZIP 路径。</param>
    /// <param name="stagingRoot">宿主暂存目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解压完成的模块包。</returns>
    private static async Task<ModulePackage> PreparePackageAsync(
        string zipPath,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(zipPath))
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"模块包 {zipPath} 不存在。",
                "请先选择宿主上已下载完成的模块 ZIP 包，再执行安装。");
        }

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(zipPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"模块包 {zipPath} 不是可读的 ZIP 归档。",
                "请确认所选文件是完整的模块 ZIP 包，必要时重新下载后再试。",
                ex);
        }

        using (archive)
        {
            IReadOnlyList<ZipArchiveEntry> entries = SelectEntries(archive, zipPath);
            string rootPrefix = ResolveRootPrefix(entries);
            ValidatePackageStructure(entries, rootPrefix, zipPath);

            string moduleId = ResolveModuleId(archive, entries, rootPrefix, zipPath);
            IReadOnlyList<ModuleFile> files = await ExtractAsync(entries, rootPrefix, stagingRoot, cancellationToken)
                .ConfigureAwait(false);

            return new ModulePackage(moduleId, CombineRemotePath(moduleId), files);
        }
    }

    /// <summary>
    /// 取出待安装的文件条目，剔除目录占位与打包工具的元数据。
    /// </summary>
    /// <param name="archive">已打开的归档。</param>
    /// <param name="zipPath">归档路径，仅用于异常上下文。</param>
    /// <returns>参与安装的文件条目。</returns>
    private static IReadOnlyList<ZipArchiveEntry> SelectEntries(ZipArchive archive, string zipPath)
    {
        var entries = new List<ZipArchiveEntry>();
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName;
            if (name.Length == 0 || name.EndsWith('/') || name.EndsWith('\\'))
            {
                continue;
            }

            if (name.Contains("__MACOSX/", StringComparison.Ordinal)
                || name.EndsWith(".DS_Store", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(NormalizeSeparators(name), name, StringComparison.Ordinal))
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"模块包 {zipPath} 中的条目 {entry.FullName} 使用了非规范的路径分隔符。",
                    "请使用标准的 ZIP 模块包重新打包后再安装。");
            }

            entries.Add(entry);
        }

        if (entries.Count == 0)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"模块包 {zipPath} 中没有任何文件。",
                "请确认所选 ZIP 确实是模块包，且未在打包时清空内容。");
        }

        return entries;
    }

    /// <summary>
    /// 判定模块内容的根前缀。模块包有时整体套一层同名目录，
    /// 此时需要剥掉这层外壳，否则 module.prop 与 system/ 都不在预期位置。
    /// </summary>
    /// <param name="entries">参与安装的文件条目。</param>
    /// <returns>根前缀，含结尾斜杠；内容直挂归档根时返回空串。</returns>
    private static string ResolveRootPrefix(IReadOnlyList<ZipArchiveEntry> entries)
    {
        foreach (ZipArchiveEntry entry in entries)
        {
            string name = entry.FullName;
            if (name == ModulePropFileName || name.StartsWith(SystemDirectoryName + "/", StringComparison.Ordinal))
            {
                return string.Empty;
            }
        }

        HashSet<string> topLevels = new(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in entries)
        {
            int slash = entry.FullName.IndexOf('/');
            topLevels.Add(slash < 0 ? entry.FullName : entry.FullName[..slash]);
        }

        if (topLevels.Count != 1)
        {
            return string.Empty;
        }

        string candidate = topLevels.First();
        string prefix = candidate + "/";
        foreach (ZipArchiveEntry entry in entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            string relative = entry.FullName[prefix.Length..];
            if (relative == ModulePropFileName || relative.StartsWith(SystemDirectoryName + "/", StringComparison.Ordinal))
            {
                return prefix;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 校验模块结构：必须含 module.prop 或至少含 system/ 子树。
    /// 开机加载器只按目录名、system/ 子树与阶段脚本生效，两者都没有的包装上去也不会被加载。
    /// </summary>
    /// <param name="entries">参与安装的文件条目。</param>
    /// <param name="rootPrefix">模块内容根前缀。</param>
    /// <param name="zipPath">归档路径，仅用于异常上下文。</param>
    /// <exception cref="XBearException">结构不满足模块约定时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private static void ValidatePackageStructure(
        IReadOnlyList<ZipArchiveEntry> entries,
        string rootPrefix,
        string zipPath)
    {
        foreach (ZipArchiveEntry entry in entries)
        {
            if (!entry.FullName.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string relative = entry.FullName[rootPrefix.Length..];
            if (relative == ModulePropFileName || relative.StartsWith(SystemDirectoryName + "/", StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new XBearException(
            ErrorCategory.Spec,
            $"模块包 {zipPath} 缺少 {ModulePropFileName}，也没有 {SystemDirectoryName}/ 子树。",
            $"请确认所选 ZIP 是模块包：至少应包含 {ModulePropFileName} 或 {SystemDirectoryName}/ 目录。");
    }

    /// <summary>
    /// 解析模块标识。优先取 module.prop 的 id 字段，缺省时退回 ZIP 文件名。
    /// </summary>
    /// <param name="archive">已打开的归档。</param>
    /// <param name="entries">参与安装的文件条目。</param>
    /// <param name="rootPrefix">模块内容根前缀。</param>
    /// <param name="zipPath">归档路径，用于在 module.prop 不可读时取文件名。</param>
    /// <returns>合法的模块标识。</returns>
    private static string ResolveModuleId(
        ZipArchive archive,
        IReadOnlyList<ZipArchiveEntry> entries,
        string rootPrefix,
        string zipPath)
    {
        ZipArchiveEntry? prop = entries.FirstOrDefault(
            entry => string.Equals(entry.FullName, rootPrefix + ModulePropFileName, StringComparison.Ordinal));

        if (prop is not null)
        {
            string declared = ReadModulePropId(archive, prop, zipPath);
            if (!string.IsNullOrWhiteSpace(declared))
            {
                return NormalizeModuleId(declared);
            }
        }

        return NormalizeModuleId(Path.GetFileNameWithoutExtension(zipPath));
    }

    /// <summary>
    /// 从 module.prop 读取 id 字段。文件不可读时按未声明处理，由调用方退回文件名。
    /// </summary>
    /// <param name="archive">已打开的归档。</param>
    /// <param name="entry">module.prop 条目。</param>
    /// <param name="zipPath">归档路径，仅用于异常上下文。</param>
    /// <returns>id 字段的值，未声明时返回空串。</returns>
    private static string ReadModulePropId(ZipArchive archive, ZipArchiveEntry entry, string zipPath)
    {
        try
        {
            using Stream stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string content = reader.ReadToEnd();

            foreach (string line in content.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("id=", StringComparison.Ordinal))
                {
                    return trimmed["id=".Length..].Trim();
                }
            }

            return string.Empty;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"模块包 {zipPath} 中的 {ModulePropFileName} 无法读取：{ex.Message}",
                "请重新获取完整的模块包后再安装。",
                ex);
        }
    }

    /// <summary>
    /// 把条目解压到宿主暂存目录。条目路径先归一化并校验不得越出暂存目录，
    /// 杜绝归档用 ../ 写到暂存目录之外的穿越写法。
    /// </summary>
    /// <param name="entries">参与安装的文件条目。</param>
    /// <param name="rootPrefix">模块内容根前缀。</param>
    /// <param name="stagingRoot">宿主暂存目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解压后的文件清单，按归档顺序排列。</returns>
    private static async Task<IReadOnlyList<ModuleFile>> ExtractAsync(
        IReadOnlyList<ZipArchiveEntry> entries,
        string rootPrefix,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        string stagingPrefix = Path.GetFullPath(stagingRoot) + Path.DirectorySeparatorChar;
        var files = new List<ModuleFile>();

        foreach (ZipArchiveEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relative = NormalizeRelativePath(entry.FullName[rootPrefix.Length..]);
            string destination = Path.GetFullPath(Path.Combine(stagingRoot, relative));
            if (!destination.StartsWith(stagingPrefix, StringComparison.Ordinal))
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"模块包中的条目 {entry.FullName} 指向暂存目录之外。",
                    "该模块包包含越界路径，已拒绝安装；请使用来源可靠的模块包。");
            }

            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (Stream source = entry.Open())
            await using (var target = new FileStream(
                destination,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            files.Add(new ModuleFile(relative, destination));
        }

        return files;
    }

    /// <summary>
    /// 为已推送的阶段脚本补上可执行位。adbd 的推送按固定权限位落盘，
    /// 不补这一位会让脚本在依赖自身权限的加载器下无法执行。
    /// </summary>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="package">已推送的模块包。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示权限调整已结束的异步任务。</returns>
    private Task ApplyScriptPermissionsAsync(
        ModuleTarget target,
        ModulePackage package,
        CancellationToken cancellationToken)
    {
        string[] scripts = package.Files
            .Where(file => StageScriptNames.Contains(file.RelativePath, StringComparer.Ordinal))
            .Select(file => package.RemotePath + "/" + file.RelativePath)
            .ToArray();

        return scripts.Length == 0
            ? Task.CompletedTask
            : RunShellAsync(target, $"chmod 0755 {string.Join(' ', scripts)}", cancellationToken);
    }

    /// <summary>
    /// 在 guest 上执行一条 shell 命令。
    /// adbd 在一条 shell 命令跑完后即关闭该连接，因此每条命令都单独建立并释放一次连接，
    /// 与宿主 adb 逐条执行命令的行为一致，也避免第二条命令落在已被关闭的连接上。
    /// </summary>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="command">命令与参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命令的标准输出。</returns>
    private async Task<string> RunShellAsync(
        ModuleTarget target,
        string command,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
                target,
                client => client.ShellAsync(command, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// 推送单个文件。推送同样按次独占连接，sync 子协议的一次传输在 DONE 后即结束。
    /// </summary>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="localPath">宿主文件路径。</param>
    /// <param name="remotePath">实例内目标路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示推送已结束的异步任务。</returns>
    private Task PushFileAsync(
        ModuleTarget target,
        string localPath,
        string remotePath,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            target,
            async client =>
            {
                await client.PushAsync(localPath, remotePath, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// 在 guest 上执行一次操作：按次建立连接并在结束时释放，不跨调用复用连接。
    /// </summary>
    /// <typeparam name="T">操作的返回类型。</typeparam>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="action">在已连接客户端上执行的操作。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作的结果。</returns>
    private async Task<T> ExecuteAsync<T>(
        ModuleTarget target,
        Func<IAdbClient, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await using IAdbClient client = _adbClientFactory();
        try
        {
            await client.ConnectAsync(target.AdbPort, cancellationToken, _connectTimeout).ConfigureAwait(false);
            return await action(client).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消保持取消语义，不谎报为实例内交互失败。
            throw;
        }
        catch (XBearException ex)
        {
            throw new XBearException(
                ex.Category,
                $"实例 {target.InstanceId} 模块操作失败：{ex.Message}",
                string.IsNullOrWhiteSpace(ex.Remediation) ? DefaultRemediation(target) : ex.Remediation,
                ex);
        }
    }

    /// <summary>
    /// 判定 guest 上某个目录是否存在。命令以非零退出码表示不存在，属预期分支而非失败。
    /// </summary>
    /// <param name="target">目标实例及其 adb 端口。</param>
    /// <param name="path">实例内目录路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>目录存在时返回 true。</returns>
    private async Task<bool> DirectoryExistsAsync(
        ModuleTarget target,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunShellAsync(target, $"test -d {path}", cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (XBearException)
        {
            return false;
        }
    }

    /// <summary>校验目标实例，并生成缺省处置建议。</summary>
    /// <param name="target">待校验的目标实例。</param>
    private static void ValidateTarget(ModuleTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.InstanceId);

        if (target.AdbPort is < 1 or > 65535)
        {
            throw new ArgumentException(
                $"实例 {target.InstanceId} 的 adb 端口 {target.AdbPort} 不是合法端口。",
                nameof(target));
        }
    }

    /// <summary>生成模块操作失败时的缺省处置建议。</summary>
    /// <param name="target">目标实例。</param>
    /// <returns>处置建议文本。</returns>
    private static string DefaultRemediation(ModuleTarget target) =>
        $"请确认实例 {target.InstanceId} 已启动到 adbd 就绪，再重试该模块操作。";

    /// <summary>
    /// 校验并归一化模块标识。标识直接进入实例内路径，
    /// 因此只接受字母、数字、点、下划线与连字符，杜绝路径穿越与命令注入。
    /// </summary>
    /// <param name="moduleId">待归一化的模块标识。</param>
    /// <returns>归一化后的模块标识。</returns>
    /// <exception cref="XBearException">标识为空或含不允许的字符时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private static string NormalizeModuleId(string? moduleId)
    {
        string id = (moduleId ?? string.Empty).Trim();
        if (id.Length == 0)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "模块标识为空。",
                "请提供非空的模块标识，或改用带 module.prop 的模块包以自动取标识。");
        }

        foreach (char c in id)
        {
            bool allowed = char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';
            if (!allowed)
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"模块标识 {id} 含有不允许的字符。",
                    "模块标识只允许字母、数字、点、下划线与连字符；请改名后重新打包。");
            }
        }

        if (id.StartsWith('.') || id.Contains("..", StringComparison.Ordinal))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"模块标识 {id} 不是合法目录名。",
                "模块标识不得以点开头或包含连续的点；请改名后重新打包。");
        }

        return id;
    }

    /// <summary>拼出模块在实例内的目录路径。</summary>
    /// <param name="moduleId">模块标识。</param>
    /// <returns>实例内模块目录路径。</returns>
    private static string CombineRemotePath(string moduleId) => ModulesRootPath + "/" + moduleId;

    /// <summary>把归档条目的路径分隔符归一为正斜杠。</summary>
    /// <param name="name">归档条目路径。</param>
    /// <returns>归一化后的路径。</returns>
    private static string NormalizeSeparators(string name) => name.Replace('\\', '/');

    /// <summary>
    /// 归一化模块内的相对路径：统一分隔符、去掉可能存在的当前目录段。
    /// </summary>
    /// <param name="path">归档条目相对路径。</param>
    /// <returns>可用于拼接宿主路径与实例内路径的相对路径。</returns>
    private static string NormalizeRelativePath(string path)
    {
        string normalized = NormalizeSeparators(path);
        var segments = new List<string>();
        foreach (string segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"模块包中的条目 {path} 含有上级目录段。",
                    "该模块包包含越界路径，已拒绝安装；请使用来源可靠的模块包。");
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"模块包中的条目 {path} 不是有效文件路径。",
                "该模块包包含无法解析的条目，已拒绝安装。");
        }

        return string.Join('/', segments);
    }

    /// <summary>创建本次安装专用的宿主暂存目录。</summary>
    /// <returns>暂存目录路径。</returns>
    private static string CreateStagingDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "xbear-module-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>清理暂存目录，清理失败不影响已经完成的安装。</summary>
    /// <param name="stagingRoot">暂存目录路径。</param>
    private static void TryDeleteStagingDirectory(string stagingRoot)
    {
        try
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 暂存目录清理失败只是留下宿主垃圾文件，不应让安装结果变为失败。
        }
    }

    /// <summary>解压后的一个文件。</summary>
    /// <param name="RelativePath">模块内相对路径，使用正斜杠。</param>
    /// <param name="LocalPath">宿主暂存目录中的绝对路径。</param>
    private sealed record ModuleFile(string RelativePath, string LocalPath);

    /// <summary>校验完成并解压到暂存目录的模块包。</summary>
    /// <param name="ModuleId">模块标识。</param>
    /// <param name="RemotePath">模块在实例内的目录路径。</param>
    /// <param name="Files">待推送的文件清单。</param>
    private sealed record ModulePackage(
        string ModuleId,
        string RemotePath,
        IReadOnlyList<ModuleFile> Files);
}