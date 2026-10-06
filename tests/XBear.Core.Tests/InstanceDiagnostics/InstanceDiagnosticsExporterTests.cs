using System.Text;
using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Tests.InstanceDiagnostics;

/// <summary>
/// 实例诊断包导出测试。全部针对内存内的替身，不启动 QEMU、不创建磁盘镜像。
/// </summary>
public sealed class InstanceDiagnosticsExporterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "xbear-diagnostics-" + Guid.NewGuid().ToString("N"));

    /// <summary>构造测试夹具并准备好临时根目录。</summary>
    public InstanceDiagnosticsExporterTests() => Directory.CreateDirectory(_root);

    /// <summary>清理测试产生的临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string NewBaseDirectory()
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private string WriteLog(string content)
    {
        string path = Path.Combine(_root, "qemu.log");
        File.WriteAllText(path, content);
        return path;
    }

    private static InstanceDiagnosticsRequest NewRequest(
        QemuProcessHandle? process = null,
        AllocatedPorts? ports = null,
        Func<int, IQmpClient>? qmpFactory = null,
        Func<IAdbClient>? adbFactory = null) => new()
    {
        InstanceId = "inst-01",
        InstanceDisplayName = "主力实例",
        Process = process,
        Ports = ports ?? new AllocatedPorts(15555, 15556, 5900),
        QmpClientFactory = qmpFactory,
        AdbClientFactory = adbFactory,
    };

    private string ArtifactPath(InstanceDiagnosticsResult result, string stepName)
    {
        DiagnosticsStepResult step = result.Find(stepName)
            ?? throw new InvalidOperationException($"诊断包中缺少步骤 {stepName}。");
        return Path.Combine(result.PackageDirectory, step.ArtifactRelativePath ?? throw new InvalidOperationException(
            $"步骤 {stepName} 没有产物路径。"));
    }

    [Fact]
    public async Task ExportAsync_全链路成功时产出全部应有证据()
    {
        string logPath = WriteLog("qemu 日志首行\nWHPX 加速不可用\n启动完成");
        string commandLine = "qemu-system-x86_64 -machine q35,accel=whpx -m 6144";
        var process = new StubQemuProcessHandle(commandLine, logPath);
        var exporter = new InstanceDiagnosticsExporter();

        InstanceDiagnosticsResult result = await exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(process, qmpFactory: _ => new FakeQmpClient(), adbFactory: () => new FakeAdbClient()));

        Assert.False(result.HasFailure);
        Assert.Equal(
            [
                InstanceDiagnosticsExporter.ProcessStepName,
                InstanceDiagnosticsExporter.QemuLogStepName,
                InstanceDiagnosticsExporter.QmpSnapshotStepName,
                InstanceDiagnosticsExporter.QmpScreenshotStepName,
                InstanceDiagnosticsExporter.AdbProbeStepName,
                InstanceDiagnosticsExporter.ManifestStepName,
            ],
            result.Steps.Select(step => step.Name));
        Assert.All(result.Steps, step => Assert.Equal(DiagnosticsStepOutcome.Succeeded, step.Outcome));

        // 启动命令行必须是原文，不做任何改写。
        Assert.Contains(commandLine, await File.ReadAllTextAsync(ArtifactPath(result, InstanceDiagnosticsExporter.ProcessStepName)));

        // 截图必须是 QMP 让 QEMU 落盘的那份内容。
        Assert.Equal(FakeQmpClient.FakePpm, await File.ReadAllBytesAsync(ArtifactPath(result, InstanceDiagnosticsExporter.QmpScreenshotStepName)));

        // 快照要能解析，且含实际取到的运行状态。
        using JsonDocument snapshot = JsonDocument.Parse(
            await File.ReadAllTextAsync(ArtifactPath(result, InstanceDiagnosticsExporter.QmpSnapshotStepName)));
        Assert.Equal(
            "running",
            snapshot.RootElement.GetProperty("queries").GetProperty("query-status").GetProperty("status").GetString());

        // adb 探测必须给出明确的连接状态，而不是笼统的「失败」。
        Assert.Contains(
            "state: device",
            await File.ReadAllTextAsync(ArtifactPath(result, InstanceDiagnosticsExporter.AdbProbeStepName)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_清单记录实例来源导出时间与各步骤结论()
    {
        string logPath = WriteLog("日志");
        var process = new StubQemuProcessHandle("qemu-system-x86_64 -m 6144", logPath);
        var exporter = new InstanceDiagnosticsExporter();

        InstanceDiagnosticsResult result = await exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(process, qmpFactory: _ => new FakeQmpClient(), adbFactory: () => new FakeAdbClient()));

        string manifest = await File.ReadAllTextAsync(ArtifactPath(result, InstanceDiagnosticsExporter.ManifestStepName));

        Assert.Contains("inst-01", manifest, StringComparison.Ordinal);
        Assert.Contains("主力实例", manifest, StringComparison.Ordinal);
        Assert.Contains(DateTime.Now.Year.ToString(), manifest, StringComparison.Ordinal);

        // 清单要逐条列出每个采集步骤及其结论。
        foreach (DiagnosticsStepResult step in result.Steps)
        {
            if (string.Equals(step.Name, InstanceDiagnosticsExporter.ManifestStepName, StringComparison.Ordinal))
            {
                continue;
            }

            Assert.Contains(step.Name, manifest, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExportAsync_命令行中的用户目录被脱敏()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string logPath = WriteLog($"从 {profile} 加载 base 镜像");
        var process = new StubQemuProcessHandle(
            $"qemu-system-x86_64 -drive file={profile}\\images\\disk.qcow2",
            logPath);
        var exporter = new InstanceDiagnosticsExporter();

        InstanceDiagnosticsResult result = await exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(process, qmpFactory: _ => new FakeQmpClient(), adbFactory: () => new FakeAdbClient()));

        Assert.False(string.IsNullOrEmpty(profile), "测试前提：宿主用户目录可定位。");

        // 产物里不能出现上报者的用户名；带路径的产物还要能看到脱敏占位符。
        foreach (string stepName in new[] { InstanceDiagnosticsExporter.ProcessStepName, InstanceDiagnosticsExporter.QemuLogStepName })
        {
            string content = await File.ReadAllTextAsync(ArtifactPath(result, stepName));
            Assert.DoesNotContain(profile, content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<USERPROFILE>", content, StringComparison.Ordinal);
        }

        string manifest = await File.ReadAllTextAsync(ArtifactPath(result, InstanceDiagnosticsExporter.ManifestStepName));
        Assert.DoesNotContain(profile, manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportAsync_QMP不可达时其余步骤仍产出并记录失败原因()
    {
        string logPath = WriteLog("日志");
        var process = new StubQemuProcessHandle("qemu-system-x86_64 -m 6144", logPath);
        var exporter = new InstanceDiagnosticsExporter();

        InstanceDiagnosticsResult result = await exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(
                process,
                qmpFactory: _ => new FakeQmpClient
                {
                    ConnectFailure = new XBearException(ErrorCategory.Protocol, "无法连接 QMP 端口 15556。"),
                },
                adbFactory: () => new FakeAdbClient()));

        Assert.True(result.HasFailure);
        Assert.Equal(
            DiagnosticsStepOutcome.Failed,
            result.Find(InstanceDiagnosticsExporter.QmpSnapshotStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Failed,
            result.Find(InstanceDiagnosticsExporter.QmpScreenshotStepName)!.Outcome);
        Assert.Contains(
            "无法连接 QMP 端口",
            result.Find(InstanceDiagnosticsExporter.QmpSnapshotStepName)!.Detail,
            StringComparison.Ordinal);

        // 失败不能牵连其他步骤。
        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.ProcessStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.QemuLogStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.AdbProbeStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.ManifestStepName)!.Outcome);
        Assert.True(File.Exists(ArtifactPath(result, InstanceDiagnosticsExporter.ProcessStepName)));
        Assert.True(File.Exists(ArtifactPath(result, InstanceDiagnosticsExporter.AdbProbeStepName)));
    }

    [Fact]
    public async Task ExportAsync_screendump失败不影响同一次连接之外的状态快照()
    {
        string logPath = WriteLog("日志");
        var process = new StubQemuProcessHandle("qemu-system-x86_64 -m 6144", logPath);
        var exporter = new InstanceDiagnosticsExporter();

        InstanceDiagnosticsResult result = await exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(
                process,
                qmpFactory: _ => new FakeQmpClient((command, arguments, token) =>
                    string.Equals(command, "screendump", StringComparison.Ordinal)
                        ? throw new XBearException(ErrorCategory.Protocol, "screendump 被拒绝：无显示输出。")
                        : FakeQmpClient.DefaultRespond(command, arguments, token)),
                adbFactory: () => new FakeAdbClient()));

        DiagnosticsStepResult screenshot = result.Find(InstanceDiagnosticsExporter.QmpScreenshotStepName)!;
        Assert.Equal(DiagnosticsStepOutcome.Failed, screenshot.Outcome);
        Assert.Contains("screendump 被拒绝", screenshot.Detail, StringComparison.Ordinal);

        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.QmpSnapshotStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.ManifestStepName)!.Outcome);
    }

    [Fact]
    public async Task ExportAsync_单条状态查询失败时快照步骤仍成功并留下失败原因()
    {
        string logPath = WriteLog("日志");
        var process = new StubQemuProcessHandle("qemu-system-x86_64 -m 6144", logPath);
        var exporter = new InstanceDiagnosticsExporter();

        InstanceDiagnosticsResult result = await exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(
                process,
                qmpFactory: _ => new FakeQmpClient((command, arguments, token) =>
                    string.Equals(command, "query-kvm", StringComparison.Ordinal)
                        ? throw new XBearException(ErrorCategory.Protocol, "CommandNotFound")
                        : FakeQmpClient.DefaultRespond(command, arguments, token)),
                adbFactory: () => new FakeAdbClient()));

        DiagnosticsStepResult snapshot = result.Find(InstanceDiagnosticsExporter.QmpSnapshotStepName)!;
        Assert.Equal(DiagnosticsStepOutcome.Succeeded, snapshot.Outcome);
        Assert.Contains("硬件加速状态", snapshot.Detail, StringComparison.Ordinal);

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(ArtifactPath(result, InstanceDiagnosticsExporter.QmpSnapshotStepName)));
        Assert.Contains(
            "CommandNotFound",
            document.RootElement.GetProperty("queries").GetProperty("query-kvm").GetProperty("error").GetString()!,
            StringComparison.Ordinal);
        Assert.Equal(
            "running",
            document.RootElement.GetProperty("queries").GetProperty("query-status").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ExportAsync_所有状态查询失败时快照步骤记为失败但仍留下产物()
    {
        string logPath = WriteLog("日志");
        var process = new StubQemuProcessHandle("qemu-system-x86_64 -m 6144", logPath);
        var exporter = new InstanceDiagnosticsExporter();

        InstanceDiagnosticsResult result = await exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(
                process,
                qmpFactory: _ => new FakeQmpClient((command, arguments, token) =>
                    command.StartsWith("query-", StringComparison.Ordinal)
                        ? throw new XBearException(ErrorCategory.Protocol, "CommandNotFound")
                        : FakeQmpClient.DefaultRespond(command, arguments, token)),
                adbFactory: () => new FakeAdbClient()));

        DiagnosticsStepResult snapshot = result.Find(InstanceDiagnosticsExporter.QmpSnapshotStepName)!;
        Assert.Equal(DiagnosticsStepOutcome.Failed, snapshot.Outcome);
        Assert.True(File.Exists(ArtifactPath(result, InstanceDiagnosticsExporter.QmpSnapshotStepName)));
        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.QmpScreenshotStepName)!.Outcome);
    }

    [Fact]
    public async Task ExportAsync_adb连不上时记为不可达且其余步骤照常产出()
    {
        string logPath = WriteLog("日志");
        var process = new StubQemuProcessHandle("qemu-system-x86_64 -m 6144", logPath);
        var exporter = new InstanceDiagnosticsExporter();

        InstanceDiagnosticsResult result = await exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(
                process,
                qmpFactory: _ => new FakeQmpClient(),
                adbFactory: () => FakeAdbClient.Unreachable()));

        DiagnosticsStepResult probe = result.Find(InstanceDiagnosticsExporter.AdbProbeStepName)!;
        Assert.Equal(DiagnosticsStepOutcome.Failed, probe.Outcome);
        Assert.Contains("unreachable", probe.Detail, StringComparison.Ordinal);

        string content = await File.ReadAllTextAsync(ArtifactPath(result, InstanceDiagnosticsExporter.AdbProbeStepName));
        Assert.Contains("unreachable", content, StringComparison.Ordinal);

        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.QmpSnapshotStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.ManifestStepName)!.Outcome);
    }

    [Fact]
    public async Task ExportAsync_adb传输层通但shell无响应时记为离线()
    {
        string logPath = WriteLog("日志");
        var process = new StubQemuProcessHandle("qemu-system-x86_64 -m 6144", logPath);
        var exporter = new InstanceDiagnosticsExporter();

        InstanceDiagnosticsResult result = await exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(
                process,
                qmpFactory: _ => new FakeQmpClient(),
                adbFactory: () => FakeAdbClient.Offline()));

        DiagnosticsStepResult probe = result.Find(InstanceDiagnosticsExporter.AdbProbeStepName)!;
        Assert.Equal(DiagnosticsStepOutcome.Failed, probe.Outcome);
        Assert.Contains("offline", probe.Detail, StringComparison.Ordinal);
        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.ManifestStepName)!.Outcome);
    }

    [Fact]
    public async Task ExportAsync_日志文件不存在时该步骤跳过其余步骤照常产出()
    {
        var process = new StubQemuProcessHandle(
            "qemu-system-x86_64 -m 6144",
            Path.Combine(_root, "never-written.log"));
        var exporter = new InstanceDiagnosticsExporter();

        InstanceDiagnosticsResult result = await exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(process, qmpFactory: _ => new FakeQmpClient(), adbFactory: () => new FakeAdbClient()));

        DiagnosticsStepResult log = result.Find(InstanceDiagnosticsExporter.QemuLogStepName)!;
        Assert.Equal(DiagnosticsStepOutcome.Skipped, log.Outcome);
        Assert.False(result.HasFailure);

        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.ProcessStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.QmpScreenshotStepName)!.Outcome);
    }

    [Fact]
    public async Task ExportAsync_实例根本没有启动时仍产出QMP与adb证据()
    {
        var exporter = new InstanceDiagnosticsExporter();

        InstanceDiagnosticsResult result = await exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(process: null, qmpFactory: _ => new FakeQmpClient(), adbFactory: () => new FakeAdbClient()));

        Assert.False(result.HasFailure);
        Assert.Equal(
            DiagnosticsStepOutcome.Skipped,
            result.Find(InstanceDiagnosticsExporter.ProcessStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Skipped,
            result.Find(InstanceDiagnosticsExporter.QemuLogStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.QmpSnapshotStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.QmpScreenshotStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.AdbProbeStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.ManifestStepName)!.Outcome);
    }

    [Fact]
    public async Task ExportAsync_端口未分配时相关步骤跳过而不报错()
    {
        string logPath = WriteLog("日志");
        var process = new StubQemuProcessHandle("qemu-system-x86_64 -m 6144", logPath);
        var exporter = new InstanceDiagnosticsExporter();

        InstanceDiagnosticsResult result = await exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(
                process,
                ports: new AllocatedPorts(0, 0, 0),
                qmpFactory: _ => throw new InvalidOperationException("端口未分配时不应建立任何连接。"),
                adbFactory: () => throw new InvalidOperationException("端口未分配时不应建立任何连接。")));

        Assert.False(result.HasFailure);
        Assert.Equal(
            DiagnosticsStepOutcome.Skipped,
            result.Find(InstanceDiagnosticsExporter.QmpSnapshotStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Skipped,
            result.Find(InstanceDiagnosticsExporter.QmpScreenshotStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Skipped,
            result.Find(InstanceDiagnosticsExporter.AdbProbeStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.ProcessStepName)!.Outcome);
    }

    [Fact]
    public async Task ExportAsync_QMP无响应时按单步时限记为超时并继续后续步骤()
    {
        string logPath = WriteLog("日志");
        var process = new StubQemuProcessHandle("qemu-system-x86_64 -m 6144", logPath);
        var exporter = new InstanceDiagnosticsExporter(new InstanceDiagnosticsOptions
        {
            StepTimeout = TimeSpan.FromMilliseconds(200),
        });

        InstanceDiagnosticsResult result = await exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(
                process,
                qmpFactory: _ => new FakeQmpClient { HangOnConnect = true },
                adbFactory: () => new FakeAdbClient()));

        foreach (string stepName in new[]
                 {
                     InstanceDiagnosticsExporter.QmpSnapshotStepName,
                     InstanceDiagnosticsExporter.QmpScreenshotStepName,
                 })
        {
            DiagnosticsStepResult step = result.Find(stepName)!;
            Assert.Equal(DiagnosticsStepOutcome.Failed, step.Outcome);
            Assert.Contains("时限", step.Detail, StringComparison.Ordinal);
        }

        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.QemuLogStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.AdbProbeStepName)!.Outcome);
        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.ManifestStepName)!.Outcome);
    }

    [Fact]
    public async Task ExportAsync_日志超长时截断到上限并只保留尾部()
    {
        const int Limit = 4096;
        var body = new StringBuilder();
        body.Append("头部标记-不应出现在产物里-");
        body.Append('中', 40_000);
        body.Append("-尾部标记-必须出现在产物里");
        string logPath = WriteLog(body.ToString());
        var process = new StubQemuProcessHandle("qemu-system-x86_64 -m 6144", logPath);
        var exporter = new InstanceDiagnosticsExporter(new InstanceDiagnosticsOptions { MaxLogBytes = Limit });

        InstanceDiagnosticsResult result = await exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(process, qmpFactory: _ => new FakeQmpClient(), adbFactory: () => new FakeAdbClient()));

        DiagnosticsStepResult log = result.Find(InstanceDiagnosticsExporter.QemuLogStepName)!;
        Assert.Equal(DiagnosticsStepOutcome.Truncated, log.Outcome);

        string artifact = await File.ReadAllTextAsync(ArtifactPath(result, InstanceDiagnosticsExporter.QemuLogStepName));
        Assert.DoesNotContain("头部标记", artifact, StringComparison.Ordinal);
        Assert.Contains("尾部标记", artifact, StringComparison.Ordinal);

        // 保留的字节数不得超过上限，文本编码本身的开销不参与计算。
        byte[] bytes = await File.ReadAllBytesAsync(ArtifactPath(result, InstanceDiagnosticsExporter.QemuLogStepName));
        Assert.InRange(bytes.Length, 1, Limit * 3);
        Assert.False(result.HasFailure);
    }

    [Fact]
    public async Task ExportAsync_日志未超长时完整保留()
    {
        string content = "qemu 日志首行\nWHPX 加速不可用\n启动完成";
        string logPath = WriteLog(content);
        var process = new StubQemuProcessHandle("qemu-system-x86_64 -m 6144", logPath);
        var exporter = new InstanceDiagnosticsExporter();

        InstanceDiagnosticsResult result = await exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(process, qmpFactory: _ => new FakeQmpClient(), adbFactory: () => new FakeAdbClient()));

        Assert.Equal(
            DiagnosticsStepOutcome.Succeeded,
            result.Find(InstanceDiagnosticsExporter.QemuLogStepName)!.Outcome);
        Assert.Equal(content, await File.ReadAllTextAsync(ArtifactPath(result, InstanceDiagnosticsExporter.QemuLogStepName)));
    }

    [Fact]
    public async Task ExportAsync_截图超过上限时整体丢弃而不留下无法解码的残缺文件()
    {
        string logPath = WriteLog("日志");
        var process = new StubQemuProcessHandle("qemu-system-x86_64 -m 6144", logPath);
        var exporter = new InstanceDiagnosticsExporter(new InstanceDiagnosticsOptions
        {
            MaxScreenshotBytes = 4,
        });

        InstanceDiagnosticsResult result = await exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(process, qmpFactory: _ => new FakeQmpClient(), adbFactory: () => new FakeAdbClient()));

        DiagnosticsStepResult screenshot = result.Find(InstanceDiagnosticsExporter.QmpScreenshotStepName)!;
        Assert.Equal(DiagnosticsStepOutcome.Truncated, screenshot.Outcome);
        Assert.Null(screenshot.ArtifactRelativePath);
        Assert.Empty(Directory.GetFiles(result.PackageDirectory, "*.ppm"));
    }

    [Fact]
    public async Task ExportAsync_根目录下已有诊断包时不覆盖而是另起新目录()
    {
        string baseDirectory = NewBaseDirectory();
        string logPath = WriteLog("日志");
        var process = new StubQemuProcessHandle("qemu-system-x86_64 -m 6144", logPath);
        var exporter = new InstanceDiagnosticsExporter();

        InstanceDiagnosticsResult first = await exporter.ExportAsync(
            baseDirectory,
            NewRequest(process, qmpFactory: _ => new FakeQmpClient(), adbFactory: () => new FakeAdbClient()));
        string firstManifestPath = ArtifactPath(first, InstanceDiagnosticsExporter.ManifestStepName);
        byte[] firstManifest = await File.ReadAllBytesAsync(firstManifestPath);

        InstanceDiagnosticsResult second = await exporter.ExportAsync(
            baseDirectory,
            NewRequest(process, qmpFactory: _ => new FakeQmpClient(), adbFactory: () => new FakeAdbClient()));

        Assert.NotEqual(first.PackageDirectory, second.PackageDirectory);
        Assert.True(Directory.Exists(first.PackageDirectory));
        Assert.Equal(2, Directory.GetDirectories(baseDirectory).Length);

        // 先前那一份必须原样保留：上一份诊断包本身就是上一次报障的证据。
        Assert.Equal(firstManifest, await File.ReadAllBytesAsync(firstManifestPath));
        Assert.True(File.Exists(ArtifactPath(second, InstanceDiagnosticsExporter.ManifestStepName)));
    }

    [Fact]
    public async Task ExportAsync_实例标识含非法文件名字符时仍能建出目录()
    {
        var exporter = new InstanceDiagnosticsExporter();
        InstanceDiagnosticsRequest request = NewRequest(
            process: null,
            qmpFactory: _ => new FakeQmpClient(),
            adbFactory: () => new FakeAdbClient()) with { InstanceId = "inst/01:黑屏" };

        InstanceDiagnosticsResult result = await exporter.ExportAsync(NewBaseDirectory(), request);

        Assert.True(Directory.Exists(result.PackageDirectory));
        Assert.DoesNotContain('/', Path.GetFileName(result.PackageDirectory));
        Assert.Contains("inst/01:黑屏", await File.ReadAllTextAsync(ArtifactPath(result, InstanceDiagnosticsExporter.ManifestStepName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_调用方取消时中止整体导出而不是记为步骤失败()
    {
        string logPath = WriteLog("日志");
        var process = new StubQemuProcessHandle("qemu-system-x86_64 -m 6144", logPath);
        var exporter = new InstanceDiagnosticsExporter();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(
            NewBaseDirectory(),
            NewRequest(process, qmpFactory: _ => new FakeQmpClient(), adbFactory: () => new FakeAdbClient()),
            cancellation.Token));
    }

    [Fact]
    public async Task ExportAsync_根目录参数非法时抛出而非静默产出()
    {
        var exporter = new InstanceDiagnosticsExporter();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            exporter.ExportAsync("   ", NewRequest(qmpFactory: _ => new FakeQmpClient())));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            exporter.ExportAsync(NewBaseDirectory(), NewRequest(qmpFactory: _ => new FakeQmpClient()) with { InstanceId = "  " }));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            exporter.ExportAsync(NewBaseDirectory(), null!));
    }
}