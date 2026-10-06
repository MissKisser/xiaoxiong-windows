using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using XBear.App.Services;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 诊断包导出的行为测试。覆盖 zip 产出、故障证据落盘、隐私属性与冲突不覆盖。
/// </summary>
public class DiagnosticsExportTests : IDisposable
{
    private const string InstanceId = "win-vm-01";
    private const string ImageRef = "bliss-os-17-x86_64";
    private const string Imei = "356938035643809";
    private const string AndroidId = "9d2a1f7c4b6e8035";
    private const string SerialNo = "SN-4f21ab90";
    private const string ProxyUrl = "http://user:pass@pac.example.internal/proxy.pac";

    private readonly TempRoot _temp = new();

    public void Dispose() => _temp.Cleanup();

    private static InstanceSpec BuildSpec() => new()
    {
        Id = InstanceId,
        DisplayName = "导出诊断实例",
        Platform = "windows",
        ImageRef = ImageRef,
        Resources = new ResourceSpec { MemoryMB = 4096, CpuCores = 4, DiskGB = 64, CpuModel = "qemu64" },
        Network = new NetworkSpec
        {
            Exposure = "loopback",
            FixedAddress = null,
            PortForwards =
            {
                new PortForward { HostPort = 15555, GuestPort = 5555, Protocol = "tcp", Bind = "127.0.0.1" },
            },
            Proxy = new ProxySpec { Type = "pac", Host = "pac.example.internal", Port = 8080, Url = ProxyUrl },
        },
        DeviceIdentity = new DeviceIdentity { Imei = Imei, AndroidId = AndroidId, SerialNo = SerialNo },
    };

    private InstanceManager CreateManager(
        IInstanceRepository repository,
        StubQmpClient? qmp = null,
        StubAdbClient? adb = null)
    {
        var manager = new InstanceManager(
            repository,
            new StubPortAllocator(),
            new StubArgBuilder(),
            new StubQemuLauncher(),
            new StubQcow2Manager(),
            _temp.NewImagesRootWithBaseImage(ImageRef),
            _temp.New("instances"),
            null,
            () => qmp ?? new StubQmpClient(),
            () => adb ?? new StubAdbClient());

        return manager;
    }

    /// <summary>
    /// 读取 zip 内全部条目名与文本内容，供断言使用。
    /// </summary>
    /// <param name="packagePath">zip 文件路径。</param>
    /// <returns>条目名到内容的对照表。</returns>
    private static Dictionary<string, string> ReadEntries(string packagePath)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using Stream stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            entries[entry.FullName] = reader.ReadToEnd();
        }

        return entries;
    }

    private static DiagnosticsExporter CreateExporter(
        Func<InstanceDiagnosticsRequest, InstanceDiagnosticsRequest>? customizer = null) =>
        new(XBeeSpec.TestSpec(), customizer ?? WorkingClients());

    /// <summary>
    /// 把 QMP 与 adb 都换成必定失败的客户端，用于制造单步失败。
    /// </summary>
    /// <returns>请求改写钩子。</returns>
    private static Func<InstanceDiagnosticsRequest, InstanceDiagnosticsRequest> FailingClients() =>
        request => request with
        {
            AdbClientFactory = () => new StubAdbClient
            {
                ConnectFailure = new XBearException(ErrorCategory.Protocol, "adbd 端口无响应"),
            },
            QmpClientFactory = _ => new StubQmpClient
            {
                ConnectFailure = new XBearException(ErrorCategory.Protocol, "QMP 端口无响应"),
            },
        };

    /// <summary>
    /// 把 QMP 与 adb 换成可用的内存替身，避免导出时真的去连宿主端口。
    /// </summary>
    /// <returns>请求改写钩子。</returns>
    private static Func<InstanceDiagnosticsRequest, InstanceDiagnosticsRequest> WorkingClients() =>
        request => request with
        {
            AdbClientFactory = () => new StubAdbClient(),
            QmpClientFactory = _ => new ScriptedQmpClient(),
        };

    [Fact]
    public async Task ExportProducesZipContainingCoreEvidenceAndPortInformation()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        InstanceManager manager = CreateManager(repository);
        await manager.StartAsync(InstanceId);

        string directory = _temp.New("export-ok");
        string packagePath = await CreateExporter().ExportAsync(directory, InstanceId, repository, manager);

        Assert.True(File.Exists(packagePath), $"诊断包未产出：{packagePath}");

        Dictionary<string, string> entries = ReadEntries(packagePath);

        // 故障证据必须真的来自 Core 导出器，而不是另起一套实现。
        Assert.Contains(entries, entry => entry.Key.EndsWith("/manifest.txt", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Key.EndsWith("/adb-probe.txt", StringComparison.Ordinal));

        string manifest = entries.First(pair => pair.Key.EndsWith("/manifest.txt", StringComparison.Ordinal)).Value;
        Assert.Contains(InstanceId, manifest, StringComparison.Ordinal);

        // 端口信息落在探测产物里，用于把故障现象对应回宿主上的具体端口。
        string probe = entries.First(pair => pair.Key.EndsWith("/adb-probe.txt", StringComparison.Ordinal)).Value;
        AllocatedPorts ports = manager.AllocatedPorts[InstanceId];
        Assert.Contains($"adb_port: {ports.Adb}", probe, StringComparison.Ordinal);
        Assert.Contains("system/info.txt", entries.Keys);
        Assert.Contains("runtime/ports.txt", entries.Keys);
        Assert.Contains($"adb={ports.Adb}", entries["runtime/ports.txt"], StringComparison.Ordinal);

        // 实例配置视图仍然随包提供，便于对照故障发生时的配置。
        Assert.Contains($"instances/{InstanceId}.json", entries.Keys);
        Assert.Contains(ImageRef, entries[$"instances/{InstanceId}.json"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportKeepsUserMachineDeviceIdentityAndProxyCredentialsOutOfPackage()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        InstanceManager manager = CreateManager(repository);
        await manager.StartAsync(InstanceId);

        string directory = _temp.New("export-privacy");
        string packagePath = await CreateExporter().ExportAsync(directory, InstanceId, repository, manager);

        // 防止主机名或用户名为空导致断言被跳过，测试就此空转。
        Assert.False(string.IsNullOrEmpty(Environment.UserName));
        Assert.False(string.IsNullOrEmpty(Environment.MachineName));

        var forbidden = new[]
        {
            Environment.UserName,
            Environment.MachineName,
            Imei,
            AndroidId,
            SerialNo,
            ProxyUrl,
            "user:pass",
        };

        foreach (KeyValuePair<string, string> entry in ReadEntries(packagePath))
        {
            foreach (string secret in forbidden.Where(value => !string.IsNullOrEmpty(value)))
            {
                Assert.False(
                    entry.Value.Contains(secret, StringComparison.OrdinalIgnoreCase),
                    $"诊断包条目 {entry.Key} 泄露了 {secret}。");
            }
        }

        // 脱敏不能靠整体丢弃内容蒙混过关：配置视图本身必须仍在包里。
        string configuration = ReadEntries(packagePath)[$"instances/{InstanceId}.json"];
        Assert.Contains($"\"id\": \"{InstanceId}\"", configuration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportDoesNotOverwriteExistingDirectoryOrPackage()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        InstanceManager manager = CreateManager(repository);
        string directory = _temp.New("export-conflict");

        string existing = Path.Combine(directory, "xbear-diagnostics-20200101-000000.zip");
        File.WriteAllText(existing, "原有诊断包");

        await manager.StartAsync(InstanceId);
        string packagePath = await CreateExporter().ExportAsync(directory, InstanceId, repository, manager);

        Assert.NotEqual(Path.GetFullPath(existing), Path.GetFullPath(packagePath));
        Assert.Equal("原有诊断包", File.ReadAllText(existing));
        Assert.True(File.Exists(packagePath));
    }

    [Fact]
    public async Task ExportStillProducesPackageWhenEveryEvidenceStepFails()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        InstanceManager manager = CreateManager(repository);
        await manager.StartAsync(InstanceId);

        string directory = _temp.New("export-step-failure");
        string packagePath = await CreateExporter(FailingClients())
            .ExportAsync(directory, InstanceId, repository, manager);

        Assert.True(File.Exists(packagePath), "单步失败时诊断包仍应产出。");

        Dictionary<string, string> entries = ReadEntries(packagePath);
        string manifest = entries.First(pair => pair.Key.EndsWith("/manifest.txt", StringComparison.Ordinal)).Value;

        // 失败被记录在清单里，而不是中断整体导出。
        Assert.Contains("失败", manifest, StringComparison.Ordinal);
        Assert.Contains($"instances/{InstanceId}.json", entries.Keys);
    }

    [Fact]
    public async Task TwoExportsWithinTheSameSecondDoNotOverwriteEachOther()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        InstanceManager manager = CreateManager(repository);
        await manager.StartAsync(InstanceId);

        string directory = _temp.New("export-same-second");
        DiagnosticsExporter exporter = CreateExporter();

        string first = await exporter.ExportAsync(directory, InstanceId, repository, manager);
        string second = await exporter.ExportAsync(directory, InstanceId, repository, manager);

        Assert.NotEqual(Path.GetFullPath(first), Path.GetFullPath(second));
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.NotEmpty(ReadEntries(first).Keys);
        Assert.NotEmpty(ReadEntries(second).Keys);
    }

    [Fact]
    public async Task ExportClassifiesUnwritableOutputDirectoryAsStorageFailure()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        string occupied = Path.Combine(_temp.New("export-unwritable"), "occupied");
        File.WriteAllText(occupied, "占位文件");

        var exception = await Assert.ThrowsAsync<XBearException>(() =>
            CreateExporter().ExportAsync(occupied, InstanceId, repository, CreateManager(repository)));

        Assert.Equal(ErrorCategory.Storage, exception.Category);
        Assert.False(string.IsNullOrEmpty(exception.Remediation));
    }
}

/// <summary>
/// QMP 客户端替身：状态查询回空对象，screendump 在指定路径写出一张极小的图片。
/// </summary>
internal sealed class ScriptedQmpClient : IQmpClient
{
    /// <summary>连接时抛出的异常，置空表示连接成功。</summary>
    public Exception? ConnectFailure { get; init; }

    /// <summary>连接时永不返回，用于验证单步时限。</summary>
    public bool HangOnConnect { get; init; }

    /// <summary>
    /// 记录连接并返回能力集。
    /// </summary>
    /// <param name="port">QMP 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>能力集。</returns>
    public async Task<IReadOnlySet<string>> ConnectAsync(int port, CancellationToken cancellationToken = default)
    {
        if (HangOnConnect)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        return ConnectFailure is not null
            ? throw ConnectFailure
            : new HashSet<string>(StringComparer.Ordinal) { "oob" };
    }

    /// <summary>
    /// 按命令名返回脚本化结果。
    /// </summary>
    /// <param name="command">命令名。</param>
    /// <param name="arguments">命令参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命令回包。</returns>
    public Task<JsonElement> ExecuteAsync(
        string command,
        object? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(command, "screendump", StringComparison.Ordinal))
        {
            string? path = ReadStringArgument(arguments, "filename");
            if (path is not null)
            {
                File.WriteAllBytes(path, TinyImage);
            }
        }

        return Task.FromResult(Parse("{}"));
    }

    /// <summary>
    /// 恒为 false，导出流程不使用该便捷查询。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>false。</returns>
    public Task<bool> QueryRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>
    /// 恒为 false，导出流程不使用该便捷查询。
    /// </summary>
    /// <param name="timeout">等待超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>false。</returns>
    public Task<bool> RequestShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>
    /// 不做实际释放。
    /// </summary>
    /// <returns>异步任务。</returns>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>一张 1x1 图片的最小字节序列。</summary>
    private static byte[] TinyImage { get; } = "P6\n1 1\n255\n\0\0\0"u8.ToArray();

    /// <summary>
    /// 从命令参数中取出字符串字段。
    /// </summary>
    /// <param name="arguments">命令参数。</param>
    /// <param name="name">字段名。</param>
    /// <returns>字段值，不存在时返回 null。</returns>
    private static string? ReadStringArgument(object? arguments, string name)
    {
        if (arguments is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(arguments, arguments.GetType()));
        return document.RootElement.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
    }

    /// <summary>
    /// 把 JSON 文本解析为可序列化的元素。
    /// </summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>解析结果。</returns>
    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}