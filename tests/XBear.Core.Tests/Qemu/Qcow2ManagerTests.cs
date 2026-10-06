using XBear.Core.Diagnostics;
using XBear.Core.Qemu;

namespace XBear.Core.Tests.Qemu;

/// <summary>base 镜像导入与格式探测的单元测试，全部经替身执行边界，不真正拉起 qemu-img。</summary>
public sealed class Qcow2ManagerTests
{
    /// <summary>ISO 之类的安装介质导入后应成为可叠 overlay 的 qcow2 base。</summary>
    [Fact]
    public async Task ISO可导入为qcow2Base()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner();
        var manager = CreateManager(workspace, runner);

        var source = workspace.GetPath("Bliss.iso");
        File.WriteAllBytes(source, new byte[2048]);
        var basePath = workspace.GetPath("images/bliss-base.qcow2");

        var converted = await manager.ImportBaseImageAsync(source, basePath);

        Assert.True(converted);
        Assert.True(File.Exists(basePath));

        var convert = Assert.Single(runner.Invocations.Where(call => call[0] == "convert"));
        Assert.Equal(
            new[] { "convert", "-O", "qcow2", "-c", source },
            convert.Take(5).ToArray());

        // 转换先写临时产物再落位，临时路径必须与最终 base 路径不同。
        Assert.Equal(basePath + ".importing", convert[5]);
        Assert.Equal(Qcow2Manager.DefaultImportTimeout, runner.LastTimeout);
        Assert.Empty(FindImportingFiles(workspace));
    }

    /// <summary>目标已是可用的 qcow2 base 时不重复转换，避免重复付出整盘转换代价。</summary>
    [Fact]
    public async Task 目标已是有效qcow2时跳过重复转换()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner();
        var logs = new List<string>();
        var manager = CreateManager(workspace, runner, logs.Add);

        var source = workspace.GetPath("Bliss.iso");
        File.WriteAllBytes(source, new byte[2048]);
        var basePath = workspace.GetPath("images/bliss-base.qcow2");
        File.WriteAllText(basePath, "已有的 base 内容");
        runner.Formats[basePath] = "qcow2";

        var converted = await manager.ImportBaseImageAsync(source, basePath);

        Assert.False(converted);
        Assert.Equal("已有的 base 内容", File.ReadAllText(basePath));
        Assert.DoesNotContain(runner.Invocations, call => call[0] == "convert");
        Assert.Contains(logs, line => line.Contains("跳过重复转换", StringComparison.Ordinal));
    }

    /// <summary>转换失败必须把 qemu-img 的输出带进异常，便于定位磁盘与镜像问题。</summary>
    [Fact]
    public async Task 转换失败时报出含qemuImg输出的存储错误()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner
        {
            ConvertExitCode = 1,
            ConvertStandardError = "qemu-img: write failed: No space left on device",
        };
        var manager = CreateManager(workspace, runner);

        var source = workspace.GetPath("Bliss.iso");
        File.WriteAllBytes(source, new byte[2048]);
        var basePath = workspace.GetPath("images/bliss-base.qcow2");

        var exception = await Assert.ThrowsAsync<XBearException>(
            () => manager.ImportBaseImageAsync(source, basePath));

        Assert.Equal(ErrorCategory.Storage, exception.Category);
        Assert.Contains("No space left on device", exception.Message, StringComparison.Ordinal);
        Assert.Contains("空间", exception.Remediation!, StringComparison.Ordinal);
        Assert.False(File.Exists(basePath));
        Assert.Empty(FindImportingFiles(workspace));
    }

    /// <summary>转换中断不得覆盖原有 base，也不得把半成品留在临时路径上。</summary>
    [Fact]
    public async Task 转换失败不覆盖原有目标也不残留临时文件()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner
        {
            ConvertExitCode = 1,
            ConvertStandardError = "qemu-img: unexpected error while writing output",
        };
        var manager = CreateManager(workspace, runner);

        var source = workspace.GetPath("Bliss.iso");
        File.WriteAllBytes(source, new byte[2048]);
        var basePath = workspace.GetPath("images/bliss-base.qcow2");
        File.WriteAllText(basePath, "原有的 base 内容");
        runner.Formats[basePath] = "raw";

        await Assert.ThrowsAsync<XBearException>(() => manager.ImportBaseImageAsync(source, basePath));

        Assert.Equal("原有的 base 内容", File.ReadAllText(basePath));
        Assert.Empty(FindImportingFiles(workspace));
    }

    /// <summary>取消转换同样不得留下半成品 base。</summary>
    [Fact]
    public async Task 转换被取消时不残留半成品()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner { ConvertFailure = new OperationCanceledException() };
        var manager = CreateManager(workspace, runner);

        var source = workspace.GetPath("Bliss.iso");
        File.WriteAllBytes(source, new byte[2048]);
        var basePath = workspace.GetPath("images/bliss-base.qcow2");
        File.WriteAllText(basePath, "原有的 base 内容");
        runner.Formats[basePath] = "raw";

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => manager.ImportBaseImageAsync(source, basePath));

        Assert.Equal("原有的 base 内容", File.ReadAllText(basePath));
        Assert.Empty(FindImportingFiles(workspace));
    }

    /// <summary>base 为 raw 时 create 必须带 raw 的格式参数，这是硬编码 qcow2 的缺陷根因。</summary>
    [Fact]
    public async Task raw格式base会带上raw的格式参数()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner();
        var manager = CreateManager(workspace, runner);

        var basePath = workspace.GetPath("base.raw");
        File.WriteAllBytes(basePath, new byte[4096]);
        runner.Formats[basePath] = "raw";

        await manager.CreateOverlayAsync(basePath, workspace.GetPath("instance/overlay.qcow2"));

        var create = Assert.Single(runner.Invocations.Where(call => call[0] == "create"));
        Assert.Equal(basePath, ReadValue(create, "-b"));
        Assert.Equal("raw", ReadValue(create, "-F"));
        Assert.Equal("qcow2", ReadValue(create, "-f"));
    }

    /// <summary>探测到的格式原样传给 create，不做猜测或改写。</summary>
    /// <param name="format">qemu-img 报告的 base 格式。</param>
    [Theory]
    [InlineData("qcow2")]
    [InlineData("vmdk")]
    [InlineData("vhdx")]
    public async Task 探测到的base格式直接作为create的格式参数(string format)
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner();
        var manager = CreateManager(workspace, runner);

        var basePath = workspace.GetPath("base.img");
        File.WriteAllBytes(basePath, new byte[4096]);
        runner.Formats[basePath] = format;

        await manager.CreateOverlayAsync(basePath, workspace.GetPath("instance/overlay.qcow2"));

        var create = Assert.Single(runner.Invocations.Where(call => call[0] == "create"));
        Assert.Equal(format, ReadValue(create, "-F"));
    }

    /// <summary>不受支持的 base 格式要报出可诊断错误，而不是硬塞 qcow2 继续执行。</summary>
    [Fact]
    public async Task base格式不受支持时报出可诊断错误()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner();
        var manager = CreateManager(workspace, runner);

        var basePath = workspace.GetPath("base.img");
        File.WriteAllBytes(basePath, new byte[4096]);
        runner.Formats[basePath] = "vdi";

        var exception = await Assert.ThrowsAsync<XBearException>(
            () => manager.CreateOverlayAsync(basePath, workspace.GetPath("instance/overlay.qcow2")));

        Assert.Equal(ErrorCategory.Storage, exception.Category);
        Assert.Contains("vdi", exception.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(exception.Remediation));
        Assert.DoesNotContain(runner.Invocations, call => call[0] == "create");
    }

    /// <summary>格式探测失败时不得继续创建 overlay。</summary>
    /// <param name="scenario">探测失败的场景。</param>
    [Theory]
    [InlineData("镜像无法打开")]
    [InlineData("探测超时")]
    [InlineData("输出无法解析")]
    [InlineData("输出缺少格式字段")]
    public async Task base格式探测失败时报出中文异常且不创建overlay(string scenario)
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner();
        var manager = CreateManager(workspace, runner);

        var basePath = workspace.GetPath("base.img");
        File.WriteAllBytes(basePath, new byte[4096]);

        switch (scenario)
        {
            case "探测超时":
                runner.InfoTimedOut = true;
                break;
            case "输出无法解析":
                runner.InfoStandardOutput = "qemu-img: version 11.1.0";
                break;
            case "输出缺少格式字段":
                runner.InfoStandardOutput = "{\"virtual-size\":1024}";
                break;
            default:
                break;
        }

        var exception = await Assert.ThrowsAsync<XBearException>(
            () => manager.CreateOverlayAsync(basePath, workspace.GetPath("instance/overlay.qcow2")));

        Assert.Equal(ErrorCategory.Storage, exception.Category);
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        Assert.False(string.IsNullOrWhiteSpace(exception.Remediation));
        Assert.DoesNotContain(runner.Invocations, call => call[0] == "create");
    }

    /// <summary>源镜像缺失时给出导入路径的中文指引。</summary>
    [Fact]
    public async Task 源镜像不存在时报出导入错误()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner();
        var manager = CreateManager(workspace, runner);

        var exception = await Assert.ThrowsAsync<XBearException>(
            () => manager.ImportBaseImageAsync(
                workspace.GetPath("missing.iso"),
                workspace.GetPath("images/bliss-base.qcow2")));

        Assert.Equal(ErrorCategory.Storage, exception.Category);
        Assert.Contains("missing.iso", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ISO", exception.Remediation!, StringComparison.Ordinal);
    }

    /// <summary>源与目标同路径会覆盖源文件，必须拒绝。</summary>
    [Fact]
    public async Task 导入目标与源同路径时报出存储错误()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner();
        var manager = CreateManager(workspace, runner);

        var source = workspace.GetPath("Bliss.iso");
        File.WriteAllBytes(source, new byte[2048]);

        var exception = await Assert.ThrowsAsync<XBearException>(
            () => manager.ImportBaseImageAsync(source, source));

        Assert.Equal(ErrorCategory.Storage, exception.Category);
        Assert.DoesNotContain(runner.Invocations, call => call[0] == "convert");
    }

    /// <summary>整盘转换不是瞬时操作，日志要能看出转换已发生并给出耗时。</summary>
    [Fact]
    public async Task 导入过程会输出可观察的转换日志()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner();
        var logs = new List<string>();
        var manager = CreateManager(workspace, runner, logs.Add);

        var source = workspace.GetPath("Bliss.iso");
        File.WriteAllBytes(source, new byte[2048]);
        var basePath = workspace.GetPath("images/bliss-base.qcow2");

        await manager.ImportBaseImageAsync(source, basePath);

        Assert.Contains(
            logs,
            line => line.Contains(source, StringComparison.Ordinal)
                     && line.Contains(basePath, StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("耗时", StringComparison.Ordinal));
    }

    /// <summary>qcow2 链顶上的快照层保持原有的 create 参数序列。</summary>
    [Fact]
    public async Task 快照层在qcow2链顶上保持原有创建参数()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner();
        var manager = CreateManager(workspace, runner);

        var currentTop = workspace.GetPath("instance/overlay.qcow2");
        File.WriteAllText(currentTop, "overlay");
        runner.Formats[currentTop] = "qcow2";
        var snapshotPath = workspace.GetPath("snapshots/snap-1.qcow2");

        await manager.CreateSnapshotAsync(currentTop, snapshotPath);

        var create = Assert.Single(runner.Invocations.Where(call => call[0] == "create"));
        Assert.Equal(
            new[] { "create", "-f", "qcow2", "-b", currentTop, "-F", "qcow2", snapshotPath },
            create.ToArray());
    }

    /// <summary>快照恢复仍写回原链顶，产物保持 qcow2。</summary>
    [Fact]
    public async Task 恢复快照写回原链顶()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner();
        var manager = CreateManager(workspace, runner);

        var currentTop = workspace.GetPath("instance/overlay.qcow2");
        File.WriteAllText(currentTop, "overlay");
        runner.Formats[currentTop] = "qcow2";
        var snapshotPath = workspace.GetPath("snapshots/snap-1.qcow2");

        await manager.CreateSnapshotAsync(currentTop, snapshotPath);
        await manager.RestoreAsync(snapshotPath);

        var convert = Assert.Single(runner.Invocations.Where(call => call[0] == "convert"));
        Assert.Equal(
            new[] { "convert", "-O", "qcow2", snapshotPath, currentTop },
            convert.ToArray());
    }

    /// <summary>链校验仍走 qemu-img check，并按退出码判定结果。</summary>
    [Fact]
    public async Task 校验链沿用check命令并按退出码判定()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner();
        var manager = CreateManager(workspace, runner);

        var overlay = workspace.GetPath("instance/overlay.qcow2");
        File.WriteAllText(overlay, "overlay");

        Assert.True(await manager.ValidateChainAsync(overlay));

        runner.CheckExitCode = 2;
        Assert.False(await manager.ValidateChainAsync(overlay));

        Assert.All(
            runner.Invocations.Where(call => call[0] == "check"),
            call => Assert.Equal(new[] { "check", overlay }, call.ToArray()));
    }

    /// <summary>校验目标缺失时仍归为存储错误。</summary>
    [Fact]
    public async Task 校验镜像缺失时报出存储错误()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner();
        var manager = CreateManager(workspace, runner);

        var exception = await Assert.ThrowsAsync<XBearException>(
            () => manager.ValidateChainAsync(workspace.GetPath("instance/missing.qcow2")));

        Assert.Equal(ErrorCategory.Storage, exception.Category);
        Assert.DoesNotContain(runner.Invocations, call => call[0] == "check");
    }

    /// <summary>快照缺失时恢复仍归为存储错误。</summary>
    [Fact]
    public async Task 快照缺失时恢复报错()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeQemuImgRunner();
        var manager = CreateManager(workspace, runner);

        var exception = await Assert.ThrowsAsync<XBearException>(
            () => manager.RestoreAsync(workspace.GetPath("snapshots/missing.qcow2")));

        Assert.Equal(ErrorCategory.Storage, exception.Category);
        Assert.False(string.IsNullOrWhiteSpace(exception.Remediation));
        Assert.DoesNotContain(runner.Invocations, call => call[0] == "convert");
    }

    /// <summary>构造一个只指向替身执行边界的磁盘管理器。</summary>
    private static Qcow2Manager CreateManager(
        TempWorkspace workspace,
        FakeQemuImgRunner runner,
        Action<string>? log = null)
    {
        var installDirectory = workspace.CreateSubdirectory("qemu");
        File.WriteAllBytes(Path.Combine(installDirectory, QemuPaths.SystemExecutableName), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(installDirectory, QemuPaths.ImageExecutableName), Array.Empty<byte>());

        var paths = new QemuPaths(installDirectory, Array.Empty<string>());
        return new Qcow2Manager(paths, null, runner, log);
    }

    /// <summary>取参数序列中指定标志的取值。</summary>
    private static string ReadValue(IReadOnlyList<string> arguments, string flag)
    {
        var index = arguments.ToList().IndexOf(flag);
        Assert.True(index >= 0, $"参数中缺少 {flag}：{string.Join(" ", arguments)}");
        Assert.True(index + 1 < arguments.Count, $"{flag} 之后缺少取值。");
        return arguments[index + 1];
    }

    /// <summary>列出工作区内全部导入临时产物。</summary>
    private static string[] FindImportingFiles(TempWorkspace workspace)
        => Directory.GetFiles(workspace.Root, "*.importing", SearchOption.AllDirectories);

    /// <summary>测试用临时工作区，退出时清理全部产物。</summary>
    private sealed class TempWorkspace : IDisposable
    {
        /// <summary>工作区根目录。</summary>
        public string Root { get; }

        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "xbear-qcow2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        /// <summary>取工作区内的绝对路径，父目录按需创建。</summary>
        /// <param name="relative">相对路径。</param>
        /// <returns>规范化后的绝对路径。</returns>
        public string GetPath(string relative)
        {
            var full = Path.GetFullPath(Path.Combine(Root, relative));
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return full;
        }

        /// <summary>在工作区内创建一个子目录。</summary>
        /// <param name="name">子目录名。</param>
        /// <returns>子目录绝对路径。</returns>
        public string CreateSubdirectory(string name)
        {
            var full = Path.Combine(Root, name);
            Directory.CreateDirectory(full);
            return full;
        }

        /// <summary>清理工作区，清理失败不影响断言结果。</summary>
        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不应让测试失败。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>qemu-img 执行边界替身，按子命令分发结果并记录全部调用。</summary>
    private sealed class FakeQemuImgRunner : IQemuImgCommandRunner
    {
        /// <summary>已登记格式的镜像路径到格式名的映射，未登记的镜像按打开失败作答。</summary>
        public Dictionary<string, string> Formats { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>info 命令是否超时。</summary>
        public bool InfoTimedOut { get; set; }

        /// <summary>info 命令的输出原文，为 null 时按格式映射生成。</summary>
        public string? InfoStandardOutput { get; set; }

        /// <summary>convert 的退出码，非零表示转换失败。</summary>
        public int ConvertExitCode { get; set; }

        /// <summary>convert 失败时写入标准错误的文本。</summary>
        public string ConvertStandardError { get; set; } = string.Empty;

        /// <summary>convert 失败或中断前是否已写出部分产物，真实 qemu-img 同样会留下残缺文件。</summary>
        public bool ConvertLeavesPartialOutput { get; set; } = true;

        /// <summary>convert 过程中抛出，用于模拟转换中断。</summary>
        public Exception? ConvertFailure { get; set; }

        /// <summary>check 命令的退出码。</summary>
        public int CheckExitCode { get; set; }

        /// <summary>全部调用记录，按调用顺序排列。</summary>
        public List<IReadOnlyList<string>> Invocations { get; } = new();

        /// <summary>最近一次调用的执行上限时长。</summary>
        public TimeSpan LastTimeout { get; private set; }

        /// <summary>记录一次调用并按子命令返回预设结果。</summary>
        /// <param name="executablePath">可执行文件绝对路径。</param>
        /// <param name="arguments">参数序列。</param>
        /// <param name="timeout">等待退出的上限时长。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>预设的执行结果。</returns>
        public Task<QemuImgCommandResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Invocations.Add(arguments.ToArray());
            LastTimeout = timeout;
            cancellationToken.ThrowIfCancellationRequested();

            var subject = arguments[^1];
            var result = arguments[0] switch
            {
                "info" => RunInfo(subject),
                "convert" => RunConvert(subject),
                "check" => new QemuImgCommandResult(CheckExitCode, string.Empty, string.Empty, false),
                "create" => RunCreate(subject),
                _ => new QemuImgCommandResult(0, string.Empty, string.Empty, false),
            };

            return Task.FromResult(result);
        }

        private QemuImgCommandResult RunInfo(string path)
        {
            if (InfoTimedOut)
            {
                return new QemuImgCommandResult(-1, string.Empty, "terminated by timeout", true);
            }

            if (InfoStandardOutput is not null)
            {
                return new QemuImgCommandResult(0, InfoStandardOutput, string.Empty, false);
            }

            return Formats.TryGetValue(path, out var format)
                ? new QemuImgCommandResult(
                    0,
                    $"{{\"format\":\"{format}\",\"virtual-size\":1073741824}}",
                    string.Empty,
                    false)
                : new QemuImgCommandResult(
                    1,
                    string.Empty,
                    $"qemu-img: Could not open '{path}': No such file or directory",
                    false);
        }

        private QemuImgCommandResult RunConvert(string outputPath)
        {
            if (ConvertFailure is not null)
            {
                if (ConvertLeavesPartialOutput)
                {
                    File.WriteAllText(outputPath, "partial");
                }

                throw ConvertFailure;
            }

            if (ConvertExitCode != 0)
            {
                if (ConvertLeavesPartialOutput)
                {
                    File.WriteAllText(outputPath, "partial");
                }

                return new QemuImgCommandResult(ConvertExitCode, string.Empty, ConvertStandardError, false);
            }

            File.WriteAllText(outputPath, "fake qcow2");
            return new QemuImgCommandResult(0, string.Empty, string.Empty, false);
        }

        private QemuImgCommandResult RunCreate(string layerPath)
        {
            File.WriteAllText(layerPath, "fake overlay");
            return new QemuImgCommandResult(0, string.Empty, string.Empty, false);
        }
    }
}