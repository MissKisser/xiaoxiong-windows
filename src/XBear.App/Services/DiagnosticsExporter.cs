using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.App.Services;

/// <summary>
/// 诊断包导出器：把实例配置与宿主侧故障证据收进一个 zip，输出到用户选择的目录。
/// </summary>
/// <remarks>
/// 故障证据由 Core 的 <see cref="InstanceDiagnosticsExporter"/> 采集，该导出器自带单步时限、
/// 逐文件字节上限与单步容错；本类只负责选定实例、补齐端口与宿主概要，
/// 并在写入前做字段白名单与路径脱敏。
/// 诊断包会被用户直接转发给他人，因此不写入用户名、计算机名与设备身份标识。
/// </remarks>
public sealed class DiagnosticsExporter
{
    /// <summary>zip 内的故障证据根目录名。</summary>
    private const string EvidenceFolderName = "evidence";

    /// <summary>仓库为空且未选中实例时使用的占位标识。</summary>
    private const string NoInstanceId = "none";

    private readonly SpecLoader _loader;
    private readonly Func<InstanceDiagnosticsRequest, InstanceDiagnosticsRequest>? _requestCustomizer;

    /// <summary>版本契约文档，版本号一律来自契约。</summary>
    public VersionDocument Version { get; }

    /// <summary>统一构建标识，格式为 {productVersion}+{commitShort}。</summary>
    public string BuildId { get; }

    /// <summary>
    /// 构造导出器。
    /// </summary>
/// <param name="loader">规格读取器，用于附带镜像清单与版本契约。</param>
    /// <param name="requestCustomizer">
    /// 故障证据请求的改写钩子，为 null 时直接使用默认请求。生产路径不传，
    /// 供测试注入客户端替身以确定性地制造单步失败。
    /// </param>
    public DiagnosticsExporter(
        SpecLoader loader,
        Func<InstanceDiagnosticsRequest, InstanceDiagnosticsRequest>? requestCustomizer = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
        _requestCustomizer = requestCustomizer;
        Version = _loader.LoadVersion();
        BuildId = Version.FormatBuildId(ResolveCommitShort());
    }

    /// <summary>
    /// 构造诊断导出器并指定自定义提交哈希。
    /// </summary>
    /// <param name="loader">版本加载器。</param>
    /// <param name="customCommitShort">自定义提交短哈希，直接用于构建标识。</param>
    public DiagnosticsExporter(SpecLoader loader, string customCommitShort)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
        Version = _loader.LoadVersion();
        BuildId = Version.FormatBuildId(customCommitShort);
    }

    /// <summary>
    /// 解析提交短哈希。从程序集的 AssemblyInformationalVersion 获取源修订哈希截短，
    /// 无法解析时回退为明确的占位值 "local"。
    /// </summary>
    /// <param name="assembly">目标程序集，为空时取当前导出器所在程序集。</param>
    /// <returns>提交短哈希或 "local"。</returns>
    public static string ResolveCommitShort(Assembly? assembly = null)
    {
        Assembly target = assembly ?? typeof(DiagnosticsExporter).Assembly;
        var attribute = target.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return ResolveCommitShort(attribute?.InformationalVersion);
    }

    /// <summary>
    /// 从 InformationalVersion 字符串解析提交短哈希。
    /// </summary>
    /// <param name="informationalVersion">程序集信息版本字符串。</param>
    /// <returns>提交短哈希或 "local"。</returns>
    public static string ResolveCommitShort(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "local";
        }

        int plusIndex = informationalVersion.IndexOf('+');
        if (plusIndex < 0 || plusIndex >= informationalVersion.Length - 1)
        {
            return "local";
        }

        string rawCommit = informationalVersion[(plusIndex + 1)..].Trim();
        if (string.IsNullOrEmpty(rawCommit))
        {
            return "local";
        }

        return rawCommit.Length > 7 ? rawCommit[..7] : rawCommit;
    }

    /// <summary>
    /// 导出诊断包。
    /// </summary>
    /// <param name="outputDirectory">用户选择的输出目录，不存在时创建。</param>
    /// <param name="instanceId">选中实例标识，可为空表示导出全量配置。</param>
    /// <param name="repository">实例配置仓库。</param>
    /// <param name="manager">生命周期编排器，可为空时跳过端口信息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导出的诊断包 zip 文件路径。</returns>
    /// <exception cref="XBearException">输出目录或诊断包文件不可写时抛出，分类为 <see cref="ErrorCategory.Storage"/>。</exception>
    public async Task<string> ExportAsync(
        string outputDirectory,
        string? instanceId,
        IInstanceRepository repository,
        InstanceManager? manager,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(repository);

        EnsureDirectory(outputDirectory);

        IReadOnlyList<InstanceSpec> specs = await repository.ListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<InstanceSpec> selected = SelectSpecs(specs, instanceId);

        string evidenceInstanceId = instanceId ?? selected.FirstOrDefault()?.Id ?? NoInstanceId;
        string? displayName = selected.FirstOrDefault(spec => spec.Id == evidenceInstanceId)?.DisplayName;

        string stagingRoot = Path.Combine(Path.GetTempPath(), $"xbear-diagnostics-{Guid.NewGuid():N}");
        try
        {
            string evidenceDirectory = await ExportEvidenceAsync(
                stagingRoot,
                evidenceInstanceId,
                displayName,
                manager,
                cancellationToken).ConfigureAwait(false);

            string packagePath = ResolvePackagePath(outputDirectory);
            await WritePackageAsync(
                packagePath,
                evidenceDirectory,
                selected,
                manager,
                instanceId,
                cancellationToken).ConfigureAwait(false);

            return packagePath;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    /// <summary>
    /// 委托 Core 导出器采集宿主侧故障证据。
    /// </summary>
    /// <param name="stagingRoot">暂存根目录，导出完成后由调用方清理。</param>
    /// <param name="instanceId">采集对象实例标识。</param>
    /// <param name="displayName">实例显示名，可为空。</param>
    /// <param name="manager">生命周期编排器，可为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Core 产出的诊断包目录绝对路径。</returns>
    private async Task<string> ExportEvidenceAsync(
        string stagingRoot,
        string instanceId,
        string? displayName,
        InstanceManager? manager,
        CancellationToken cancellationToken)
    {
        InstanceDiagnosticsRequest request = new InstanceDiagnosticsRequest
        {
            InstanceId = instanceId,
            InstanceDisplayName = displayName,

            // 生命周期编排器未对外公开活体进程句柄，此处不采集命令行与运行中日志，
            // 相关步骤由 Core 记为跳过，不影响其余证据产出。
            Process = null,
            Ports = ResolvePorts(manager, instanceId),
        };

        if (_requestCustomizer is not null)
        {
            request = _requestCustomizer(request)
                ?? throw new XBearException(ErrorCategory.Internal, "诊断证据请求改写钩子返回了空值。");
        }

        var exporter = new InstanceDiagnosticsExporter();
        InstanceDiagnosticsResult result = await exporter
            .ExportAsync(stagingRoot, request, cancellationToken)
            .ConfigureAwait(false);

        return result.PackageDirectory;
    }

    /// <summary>
    /// 把故障证据目录与配置、宿主概要写入 zip。
    /// </summary>
    /// <param name="packagePath">zip 文件路径。</param>
    /// <param name="evidenceDirectory">Core 产出的诊断包目录。</param>
    /// <param name="selected">需要写入配置的实例集合。</param>
    /// <param name="manager">生命周期编排器，可为空。</param>
    /// <param name="instanceId">选中实例标识，可为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <exception cref="XBearException">zip 无法创建或写入失败时抛出，分类为 <see cref="ErrorCategory.Storage"/>。</exception>
    private async Task WritePackageAsync(
        string packagePath,
        string evidenceDirectory,
        IReadOnlyList<InstanceSpec> selected,
        InstanceManager? manager,
        string? instanceId,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                packagePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 路径刚被另一次导出占用，此处不得删除不属于本次导出的文件。
            throw new XBearException(
                ErrorCategory.Storage,
                $"无法创建诊断包 {packagePath}：{exception.Message}",
                "请改用其他目录，或稍后重试。",
                exception);
        }

        try
        {
            await using (stream.ConfigureAwait(false))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

                AddDirectory(archive, evidenceDirectory, $"{EvidenceFolderName}/{Path.GetFileName(evidenceDirectory)}");
                AddImageSchema(archive);
                await AddInstanceConfigurationsAsync(archive, selected, cancellationToken).ConfigureAwait(false);
                AddSystemInfo(archive);
                WriteSystemInfo(archive);
                AddPortAllocation(archive, manager, instanceId);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(packagePath);
            throw new XBearException(
                ErrorCategory.Storage,
                $"无法写出诊断包 {packagePath}：{exception.Message}",
                "请改用其他目录，或检查该目录的写入权限。",
                exception);
        }
    }

    /// <summary>
    /// 把一个目录整体收进 zip。
    /// </summary>
    /// <param name="archive">zip 归档。</param>
    /// <param name="directory">源目录。</param>
    /// <param name="entryPrefix">zip 内的条目前缀。</param>
    private static void AddDirectory(ZipArchive archive, string directory, string entryPrefix)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            ZipArchiveEntry entry = archive.CreateEntry(
                $"{entryPrefix}/{Path.GetRelativePath(directory, file).Replace('\\', '/')}",
                CompressionLevel.Optimal);

            using Stream source = File.OpenRead(file);
            using Stream target = entry.Open();
            source.CopyTo(target);
        }
    }

    /// <summary>
    /// 附带镜像清单 Schema，规格缺失时跳过而不影响其余内容。
    /// </summary>
    /// <param name="archive">zip 归档。</param>
    private void AddImageSchema(ZipArchive archive)
    {
        string catalogPath;
        try
        {
            catalogPath = _loader.SchemaPath(SpecLoader.ImageSchemaFileName);
        }
        catch (XBearException)
        {
            // 共享契约缺失属于环境问题，诊断包仍应导出其余内容。
            return;
        }

        if (!File.Exists(catalogPath))
        {
            return;
        }

        ZipArchiveEntry entry = archive.CreateEntry("spec/image.schema.json", CompressionLevel.Optimal);
        using Stream source = File.OpenRead(catalogPath);
        using Stream target = entry.Open();
        source.CopyTo(target);
    }

    /// <summary>
    /// 写入实例配置的脱敏副本，只保留排查必需的字段。
    /// </summary>
    /// <param name="archive">zip 归档。</param>
    /// <param name="selected">需要写入的实例集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 设备身份标识与代理脚本地址可能携带可关联到具体设备或账号的信息，
    /// 不写入诊断包；平台特有字段是结构未知的逃生舱，同样不予收录。
    /// </remarks>
    private static async Task AddInstanceConfigurationsAsync(
        ZipArchive archive,
        IReadOnlyList<InstanceSpec> selected,
        CancellationToken cancellationToken)
    {
        foreach (InstanceSpec spec in selected)
        {
            string json = DiagnosticTextRedactor.Redact(
                JsonSerializer.Serialize(BuildShareableConfiguration(spec), SpecLoader.SerializerOptions));

            ZipArchiveEntry entry = archive.CreateEntry($"instances/{spec.Id}.json", CompressionLevel.Optimal);
            await using Stream target = entry.Open();
            await using var writer = new StreamWriter(target, new UTF8Encoding(false));
            await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

private void WriteSystemInfo(ZipArchive archive)
    {
        var info = new StringBuilder()
            .AppendLine("build: " + BuildId)
            .ToString();

        WriteTextEntry(archive, "system/build.txt", info);
    }

    /// <summary>
    /// 构造可外发的实例配置视图。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <returns>仅含白名单字段的对象树。</returns>
    private static Dictionary<string, object?> BuildShareableConfiguration(InstanceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        ResourceSpec resources = spec.Resources ?? new ResourceSpec();

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = spec.SchemaVersion,
            ["id"] = spec.Id,
            ["displayName"] = spec.DisplayName,
            ["platform"] = spec.Platform,
            ["imageRef"] = spec.ImageRef,
            ["resources"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["memoryMB"] = resources.MemoryMB,
                ["cpuCores"] = resources.CpuCores,
                ["diskGB"] = resources.DiskGB,
                ["cpuModel"] = resources.CpuModel,
            },
        };

        if (spec.Network is { } network)
        {
            var networkView = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["exposure"] = network.Exposure,
                ["fixedAddress"] = network.FixedAddress,
                ["portForwards"] = network.PortForwards
                    .Select(forward => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["hostPort"] = forward.HostPort,
                        ["guestPort"] = forward.GuestPort,
                        ["protocol"] = forward.Protocol,
                        ["bind"] = forward.Bind,
                    })
                    .ToList(),
            };

            if (network.Proxy is { } proxy)
            {
                // 代理脚本地址里常内嵌账号口令，只保留定位代理所需的类型与端点。
                networkView["proxy"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = proxy.Type,
                    ["host"] = proxy.Host,
                    ["port"] = proxy.Port,
                };
            }

            payload["network"] = networkView;
        }

        return payload;
    }

    /// <summary>
    /// 写入宿主概要，只记录定位故障所需的运行时特征。
    /// </summary>
    /// <param name="archive">zip 归档。</param>
    private static void AddSystemInfo(ZipArchive archive)
    {
        var lines = new StringBuilder()
            .AppendLine("os: " + Environment.OSVersion)
            .AppendLine("runtime: " + Environment.Version)
            .AppendLine("64bit: " + Environment.Is64BitProcess)
            .AppendLine("processor_count: " + Environment.ProcessorCount)
            .ToString();

        WriteTextEntry(archive, "system/info.txt", DiagnosticTextRedactor.Redact(lines));
    }

    /// <summary>
    /// 写入端口分配，供把故障现象对应回宿主上的具体端口。
    /// </summary>
    /// <param name="archive">zip 归档。</param>
    /// <param name="manager">生命周期编排器，可为空时跳过该条目。</param>
    /// <param name="instanceId">选中实例标识，可为空表示列出全部。</param>
    private static void AddPortAllocation(ZipArchive archive, InstanceManager? manager, string? instanceId)
    {
        if (manager is null)
        {
            return;
        }

        var lines = new StringBuilder();
        foreach (KeyValuePair<string, AllocatedPorts> entry in manager.AllocatedPorts)
        {
            if (instanceId is not null && !string.Equals(entry.Key, instanceId, StringComparison.Ordinal))
            {
                continue;
            }

            lines.AppendLine($"{entry.Key}: adb={entry.Value.Adb} qmp={entry.Value.Qmp} vnc={entry.Value.Vnc}");
        }

        WriteTextEntry(archive, "runtime/ports.txt", DiagnosticTextRedactor.Redact(lines.ToString()));
    }

    /// <summary>
    /// 写入一条文本条目，内容按 UTF-8 无 BOM 编码。
    /// </summary>
    /// <param name="archive">zip 归档。</param>
    /// <param name="entryName">条目名。</param>
    /// <param name="content">条目内容。</param>
    private static void WriteTextEntry(ZipArchive archive, string entryName, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>
    /// 按实例标识过滤配置集合。
    /// </summary>
    /// <param name="specs">仓库中的全部实例配置。</param>
    /// <param name="instanceId">选中实例标识，可为空表示全量。</param>
    /// <returns>需要写入诊断包的实例配置。</returns>
    private static IReadOnlyList<InstanceSpec> SelectSpecs(IReadOnlyList<InstanceSpec> specs, string? instanceId)
    {
        if (instanceId is null)
        {
            return specs;
        }

        return specs.Where(spec => string.Equals(spec.Id, instanceId, StringComparison.Ordinal)).ToList();
    }

    /// <summary>
    /// 读取实例当前占用的端口，未分配时以 0 表示。
    /// </summary>
    /// <param name="manager">生命周期编排器，可为空。</param>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>实例的端口组。</returns>
    private static AllocatedPorts ResolvePorts(InstanceManager? manager, string instanceId) =>
        manager is not null && manager.AllocatedPorts.TryGetValue(instanceId, out AllocatedPorts? ports)
            ? ports
            : new AllocatedPorts(0, 0, 0);

    /// <summary>
    /// 为本次导出分配一个尚不存在的 zip 路径，同名文件按序号向后取名而不覆盖。
    /// </summary>
    /// <param name="outputDirectory">输出目录。</param>
    /// <returns>未被占用的 zip 文件绝对路径。</returns>
    private static string ResolvePackagePath(string outputDirectory)
    {
        string stem = $"xbear-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}";
        for (int attempt = 0; ; attempt++)
        {
            string candidate = Path.Combine(
                outputDirectory,
                attempt == 0 ? stem + ".zip" : $"{stem}-{attempt + 1}.zip");

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 创建输出目录，失败时按存储类错误上报。
    /// </summary>
    /// <param name="directory">输出目录。</param>
    /// <exception cref="XBearException">目录无法创建时抛出，分类为 <see cref="ErrorCategory.Storage"/>。</exception>
    private static void EnsureDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"无法创建诊断包输出目录 {directory}：{exception.Message}",
                "请改用其他目录，或检查该目录的写入权限。",
                exception);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 残留文件不影响本次导出的结论。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 暂存目录清理失败不影响本次导出，后续由系统回收。
        }
    }
}

/// <summary>高影响操作的审计日志。暴露级别切换到局域网或公网时必须留痕。</summary>
public sealed class AuditLog
{
    private readonly string _filePath;
    private readonly object _gate = new();

    /// <summary>
    /// 构造审计日志。
    /// </summary>
    /// <param name="filePath">审计日志文件路径，父目录不存在时自动创建。</param>
    public AuditLog(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// 追加一条审计记录。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="action">操作名称。</param>
    /// <param name="detail">操作详情。</param>
    public void Record(string instanceId, string action, string detail)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} instance={instanceId} action={action} detail={detail}";

        lock (_gate)
        {
            File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}