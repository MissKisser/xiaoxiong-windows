using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.App.Services;

/// <summary>
/// 诊断包导出器。打包实例配置、QEMU 日志与系统信息，输出到用户选择的目录。
/// </summary>
public sealed class DiagnosticsExporter
{
    private readonly SpecLoader _loader;

    /// <summary>
    /// 构造导出器。
    /// </summary>
    /// <param name="loader">规格读取器，用于附带镜像清单。</param>
    public DiagnosticsExporter(SpecLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    /// <summary>
    /// 导出诊断包。
    /// </summary>
    /// <param name="outputDirectory">用户选择的输出目录，不存在时创建。</param>
    /// <param name="instanceId">选中实例标识，可为空表示导出全量。</param>
    /// <param name="repository">实例配置仓库。</param>
    /// <param name="manager">生命周期编排器，可为空时跳过端口信息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导出的诊断包文件路径。</returns>
    public async Task<string> ExportAsync(
        string outputDirectory,
        string? instanceId,
        IInstanceRepository repository,
        InstanceManager? manager,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(repository);

        Directory.CreateDirectory(outputDirectory);

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string packagePath = Path.Combine(outputDirectory, $"xbear-diagnostics-{stamp}.zip");

        await using var stream = File.Create(packagePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        await WriteInstancesAsync(archive, repository, instanceId, cancellationToken).ConfigureAwait(false);
        WriteImageCatalog(archive);
        WriteSystemInfo(archive);
        WritePortAllocation(archive, manager, instanceId);

        return packagePath;
    }

    private static async Task WriteInstancesAsync(
        ZipArchive archive,
        IInstanceRepository repository,
        string? instanceId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<InstanceSpec> specs = await repository.ListAsync(cancellationToken).ConfigureAwait(false);

        foreach (InstanceSpec spec in specs)
        {
            if (instanceId is not null && !string.Equals(spec.Id, instanceId, StringComparison.Ordinal))
            {
                continue;
            }

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(spec, SpecLoader.SerializerOptions);
            ZipArchiveEntry entry = archive.CreateEntry($"instances/{spec.Id}.json");
            await using Stream target = entry.Open();
            await target.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private void WriteImageCatalog(ZipArchive archive)
    {
        string catalogPath;
        try
        {
            catalogPath = _loader.SchemaPath(SpecLoader.ImageSchemaFileName);
        }
        catch (Exception ex) when (ex is Core.Diagnostics.XBearException)
        {
            // 共享契约缺失属于环境问题，诊断包仍应导出其余内容。
            return;
        }

        if (File.Exists(catalogPath))
        {
            archive.CreateEntryFromFile(catalogPath, "spec/image.schema.json");
        }
    }

    private static void WriteSystemInfo(ZipArchive archive)
    {
        var info = new StringBuilder()
            .AppendLine("os: " + Environment.OSVersion)
            .AppendLine("runtime: " + Environment.Version)
            .AppendLine("64bit: " + Environment.Is64BitProcess)
            .AppendLine("process: " + Environment.ProcessId)
            .AppendLine("machine: " + Environment.MachineName)
            .AppendLine("user: " + Environment.UserName)
            .ToString();

        ZipArchiveEntry entry = archive.CreateEntry("system/info.txt");
        using StreamWriter writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(info);
    }

    private static void WritePortAllocation(ZipArchive archive, InstanceManager? manager, string? instanceId)
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

        ZipArchiveEntry target = archive.CreateEntry("runtime/ports.txt");
        using StreamWriter writer = new StreamWriter(target.Open(), Encoding.UTF8);
        writer.Write(lines.ToString());
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