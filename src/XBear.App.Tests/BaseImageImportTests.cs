using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using XBear.App.Presentation;
using XBear.App.Services;
using XBear.App.ViewModels;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Qemu;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// base 镜像导入的界面层行为。
///
/// 覆盖四条主线：进度回调确实驱动状态变化、失败原因可读、取消可用且不留半成品、正常路径可见完成。
/// 原子落位与幂等是 Core 的契约，界面层依赖它，因此在这里用真实 Core 实现加替身执行边界复核一遍。
/// </summary>
public class BaseImageImportServiceTests : IDisposable
{
    private readonly TempRoot _temp = new();

    public void Dispose() => _temp.Cleanup();

    /// <summary>用替身管理器驱动导入服务，只替换 Core 的执行边界，其余逻辑走真实代码。</summary>
    [Fact]
    public async Task LogCallbackFromCoreDrivesRunningProgress()
    {
        string images = _temp.New("images");
        string source = WriteSource(images, "android-14.iso", 4096);
        var reports = new ConcurrentQueue<BaseImageImportProgress>();

        var service = new BaseImageImportService(
            images,
            log => new ScriptedQcow2Manager((s, b, ct) =>
            {
                log("开始把镜像导入为 qcow2 base");
                log($"镜像导入完成：{b}");
                return Task.FromResult(true);
            }),
            TimeSpan.FromMilliseconds(10));

        BaseImageImportProgress result = await service.ImportAsync(source, reports.Enqueue);

        List<BaseImageImportProgress> all = reports.ToList();

        Assert.Equal(BaseImageImportPhase.Succeeded, result.Phase);
        Assert.Contains(all, r => r.Phase == BaseImageImportPhase.Running && r.Detail.Contains("开始把镜像导入"));
        Assert.Equal(100, result.Percent!.Value);
    }

    /// <summary>百分比必须来自临时产物的真实体积增长，不能是凭空推进的假进度。</summary>
    [Fact]
    public async Task PercentIsMeasuredFromRealTemporaryFileGrowth()
    {
        string images = _temp.New("images");
        string source = WriteSource(images, "bliss.iso", 10_000);
        var reports = new ConcurrentQueue<BaseImageImportProgress>();

        var service = new BaseImageImportService(
            images,
            log => new ScriptedQcow2Manager(async (s, b, ct) =>
            {
                // 模拟 qemu-img 边转换边写出：先写到一半，再写满。
                File.WriteAllBytes(b + BaseImageImportService.ImportingSuffix, new byte[5_000]);
                await Task.Delay(120, ct);
                File.WriteAllBytes(b + BaseImageImportService.ImportingSuffix, new byte[10_000]);
                await Task.Delay(120, ct);
                return true;
            }),
            TimeSpan.FromMilliseconds(10));

        await service.ImportAsync(source, reports.Enqueue);

        BaseImageImportProgress result = reports.Last();

        List<int?> measured = reports
            .Where(r => r.Phase == BaseImageImportPhase.Running && r.Percent is not null)
            .Select(r => r.Percent)
            .ToList();

        // 采样期只允许 0 至 99：100 只在转换真正结束后出现，避免进度条提前跑满。
        Assert.NotEmpty(measured);
        Assert.All(measured, p => Assert.InRange(p!.Value, 0, 99));
        Assert.Contains(measured, p => p!.Value is >= 40 and <= 60);
        Assert.Equal(100, result.Percent!.Value);
    }

    /// <summary>目标已是可用 base 镜像时不重复转换，终态必须如实说明。</summary>
    [Fact]
    public async Task ReusableTargetIsReportedAsAlreadyImported()
    {
        string images = _temp.New("images");
        string source = WriteSource(images, "android.iso", 2048);
        var service = new BaseImageImportService(
            images,
            log => new ScriptedQcow2Manager((s, b, ct) =>
            {
                log("base 镜像已是可用的 qcow2，跳过重复转换");
                return Task.FromResult(false);
            }),
            TimeSpan.FromMilliseconds(10));

        BaseImageImportProgress result = await service.ImportAsync(source, _ => { });

        Assert.Equal(BaseImageImportPhase.AlreadyImported, result.Phase);
        Assert.Equal(BaseImageImportService.ReusedText, result.StageText);
    }

    /// <summary>失败必须原样上抛存储分类与处置建议，界面才能呈现原因。</summary>
    [Fact]
    public async Task FailureKeepsStorageCategoryAndRemediation()
    {
        string images = _temp.New("images");
        string source = WriteSource(images, "broken.iso", 2048);

        var service = new BaseImageImportService(
            images,
            log => new ScriptedQcow2Manager((s, b, ct) =>
                throw new XBearException(
                    ErrorCategory.Storage,
                    "镜像导入为 qcow2 base 失败：qemu-img 退出码 1",
                    "请确认目标磁盘剩余空间足够后重试。")),
            TimeSpan.FromMilliseconds(10));

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => service.ImportAsync(source, _ => { }));

        Assert.Equal(ErrorCategory.Storage, error.Category);
        Assert.Contains("剩余空间", error.Remediation!);
    }

    /// <summary>取消以取消异常结束，不产生终态成功报告。</summary>
    [Fact]
    public async Task CancellationPropagatesWithoutTerminalSuccessReport()
    {
        string images = _temp.New("images");
        string source = WriteSource(images, "android.iso", 2048);
        var reports = new ConcurrentQueue<BaseImageImportProgress>();

        var service = new BaseImageImportService(
            images,
            log => new ScriptedQcow2Manager(async (s, b, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return true;
            }),
            TimeSpan.FromMilliseconds(10));

        using var cts = new CancellationTokenSource();
        Task<BaseImageImportProgress> importing = service.ImportAsync(source, reports.Enqueue, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importing);
        Assert.DoesNotContain(reports.ToList(), r => r.Phase == BaseImageImportPhase.Succeeded);
    }

    /// <summary>目标路径由源文件名派生到镜像根目录，扩展名固定为 qcow2。</summary>
    [Fact]
    public void BaseImagePathIsDerivedIntoImagesRoot()
    {
        string images = _temp.New("images");
        var service = new BaseImageImportService(images, log => throw new InvalidOperationException(), TimeSpan.FromMilliseconds(10));

        string path = service.BuildBaseImagePath(Path.Combine("D:", "downloads", "android-14.iso"));

        Assert.Equal(Path.Combine(images, "android-14.qcow2"), path);
    }

    private static string WriteSource(string root, string name, int length)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }
}

/// <summary>
/// Core 的导入契约复核：取消与失败都不得在 base 镜像路径上留下半成品，成功才落位。
/// 这些保证界面层依赖，因此用真实 <see cref="Qcow2Manager"/> 加替身执行边界验证，而不是在界面层重建一套判断。
/// </summary>
public class ImportAtomicityContractTests : IDisposable
{
    private readonly TempRoot _temp = new();

    public void Dispose() => _temp.Cleanup();

    /// <summary>转换中途取消时，目标与临时产物都不应残留。</summary>
    [Fact]
    public async Task CanceledConversionLeavesNoPartialOutput()
    {
        string workspace = _temp.New("workspace");
        string source = Path.Combine(workspace, "android.iso");
        string baseImage = Path.Combine(workspace, "android.qcow2");
        File.WriteAllBytes(source, new byte[4096]);

        var converting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = CreateManager((arguments, ct) =>
        {
            File.WriteAllBytes(arguments[^1], new byte[1024]);
            converting.TrySetResult(true);
            return WaitForeverAsync(ct);
        });

        using var cts = new CancellationTokenSource();
        Task<bool> importing = manager.ImportBaseImageAsync(source, baseImage, cts.Token);

        await converting.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importing);

        Assert.False(File.Exists(baseImage), "取消后不得在 base 镜像路径留下半成品。");
        Assert.False(
            File.Exists(baseImage + BaseImageImportService.ImportingSuffix),
            "取消后不得残留临时产物。");
    }

    /// <summary>转换失败时同样不得留下半成品，且失败原因带存储分类。</summary>
    [Fact]
    public async Task FailedConversionLeavesNoPartialOutput()
    {
        string workspace = _temp.New("workspace");
        string source = Path.Combine(workspace, "android.iso");
        string baseImage = Path.Combine(workspace, "android.qcow2");
        File.WriteAllBytes(source, new byte[4096]);

        var manager = CreateManager((arguments, ct) =>
        {
            File.WriteAllBytes(arguments[^1], new byte[1024]);
            return Task.FromResult(new QemuImgCommandResult(1, string.Empty, "no space left on device", false));
        });

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => manager.ImportBaseImageAsync(source, baseImage));

        Assert.Equal(ErrorCategory.Storage, error.Category);
        Assert.False(File.Exists(baseImage));
        Assert.False(File.Exists(baseImage + BaseImageImportService.ImportingSuffix));
    }

    /// <summary>成功时产物落到目标路径，临时文件被清理，并至少产出一条过程日志供界面消费。</summary>
    [Fact]
    public async Task SuccessfulConversionPublishesTargetAndReportsProgress()
    {
        string workspace = _temp.New("workspace");
        string source = Path.Combine(workspace, "android.iso");
        string baseImage = Path.Combine(workspace, "android.qcow2");
        File.WriteAllBytes(source, new byte[4096]);

        var logs = new List<string>();
        var manager = CreateManager(
            (arguments, ct) =>
            {
                File.WriteAllBytes(arguments[^1], new byte[2048]);
                return Task.FromResult(new QemuImgCommandResult(0, string.Empty, string.Empty, false));
            },
            logs);

        bool converted = await manager.ImportBaseImageAsync(source, baseImage);

        Assert.True(converted);
        Assert.True(File.Exists(baseImage));
        Assert.False(File.Exists(baseImage + BaseImageImportService.ImportingSuffix));
        Assert.NotEmpty(logs);
    }

    /// <summary>目标已是可用 qcow2 时不重复转换，这是界面可以安全重复点击的前提。</summary>
    [Fact]
    public async Task ExistingQcow2TargetSkipsRepeatedConversion()
    {
        string workspace = _temp.New("workspace");
        string source = Path.Combine(workspace, "android.iso");
        string baseImage = Path.Combine(workspace, "android.qcow2");
        File.WriteAllBytes(source, new byte[4096]);

        int convertCount = 0;
        var manager = CreateManager((arguments, ct) =>
        {
            if (arguments[0] == "convert")
            {
                convertCount++;
                File.WriteAllBytes(arguments[^1], new byte[2048]);
                return Task.FromResult(new QemuImgCommandResult(0, string.Empty, string.Empty, false));
            }

            string json = JsonSerializer.Serialize(new { format = "qcow2", virtual_size = 4096 });
            return Task.FromResult(new QemuImgCommandResult(0, json, string.Empty, false));
        });

        await manager.ImportBaseImageAsync(source, baseImage);
        bool second = await manager.ImportBaseImageAsync(source, baseImage);

        Assert.False(second);
        Assert.Equal(1, convertCount);
    }

    private static async Task<QemuImgCommandResult> WaitForeverAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new QemuImgCommandResult(0, string.Empty, string.Empty, false);
    }

    /// <summary>以替身执行边界装配真实镜像管理器，避免测试依赖本机 QEMU 安装。</summary>
    private static IQcow2Manager CreateManager(
        Func<IReadOnlyList<string>, CancellationToken, Task<QemuImgCommandResult>> script,
        List<string>? log = null)
    {
        string installDirectory = Path.Combine(
            Path.GetTempPath(),
            "xbear-apptests-qemu",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDirectory);
        File.WriteAllBytes(Path.Combine(installDirectory, QemuPaths.SystemExecutableName), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(installDirectory, QemuPaths.ImageExecutableName), Array.Empty<byte>());

        var paths = new QemuPaths(installDirectory, Array.Empty<string>());
        var runner = new ScriptedQemuImgCommandRunner(script);

        return new Qcow2Manager(paths, null, runner, log is null ? null : message => log.Add(message));
    }
}

/// <summary>
/// 主窗口对导入过程的呈现：进行中可见、失败可读、取消可触发。
/// 这些测试走真实的导入服务与真实视图模型，只替换 Core 的执行边界。
/// </summary>
public class MainViewModelImportTests : IDisposable
{
    private readonly TempRoot _temp = new();

    public void Dispose() => _temp.Cleanup();

    /// <summary>进度回调必须真的改变界面状态，否则几十秒的转换在用户眼里就是卡死。</summary>
    [Fact]
    public async Task ImportProgressUpdatesBoundStatusText()
    {
        string images = _temp.New("images");
        string source = WriteSource(images, "android-14.iso", 4096);
        string? observedStage = null;

        MainViewModel viewModel = CreateViewModel(images, log => new ScriptedQcow2Manager(async (s, b, ct) =>
        {
            log("开始把镜像导入为 qcow2 base");
            await Task.Delay(60, ct);
            return true;
        }));

        viewModel.ImportBaseImageCommand.Execute(source);
        await WaitUntilAsync(() => viewModel.IsImporting && viewModel.ImportDetailText.Length > 0);

        Assert.True(viewModel.IsImporting);
        Assert.True(viewModel.HasImportProgress);
        Assert.Contains("开始把镜像导入", viewModel.ImportDetailText);
        observedStage = viewModel.ImportStageText;

        await viewModel.ImportBaseImageCommand.ExecutionTask!;

        Assert.False(viewModel.IsImporting);
        Assert.NotNull(observedStage);
        Assert.Equal(BaseImageImportService.ImportedText, viewModel.ImportStageText);
        Assert.False(viewModel.HasError);
    }

    /// <summary>失败必须把分类标题、原因与处置建议都摆到界面上。</summary>
    [Fact]
    public async Task FailedImportPresentsReasonThroughErrorPresenter()
    {
        string images = _temp.New("images");
        string source = WriteSource(images, "broken.iso", 4096);

        MainViewModel viewModel = CreateViewModel(images, log => new ScriptedQcow2Manager((s, b, ct) =>
            throw new XBearException(
                ErrorCategory.Storage,
                "镜像导入为 qcow2 base 失败：qemu-img 操作失败，退出码 1。",
                "请确认目标磁盘剩余空间不小于源镜像体积后重新导入。")));

        await viewModel.ImportBaseImageCommand.ExecuteAsync(source);

        Assert.False(viewModel.IsImporting);
        Assert.True(viewModel.HasError);
        var terms = new TerminologyCatalog(XBeeSpec.TestSpec().LoadTerminology());
        Assert.Equal(ErrorPresenter.Describe(ErrorCategory.Storage, terms).Title, viewModel.ErrorTitle);
        Assert.Contains("退出码 1", viewModel.ImportFailureText);
        Assert.Contains("剩余空间", viewModel.ImportFailureText);
        Assert.Contains("失败", viewModel.ImportStageText);
    }

    /// <summary>取消是可用的，且取消不是失败：不呈现错误，明确告知没有产出。</summary>
    [Fact]
    public async Task CancelStopsImportWithoutPresentingError()
    {
        string images = _temp.New("images");
        string source = WriteSource(images, "android.iso", 4096);
        string baseImage = Path.Combine(images, "android.qcow2");

        MainViewModel viewModel = CreateViewModel(images, log => new ScriptedQcow2Manager(async (s, b, ct) =>
        {
            File.WriteAllBytes(b + BaseImageImportService.ImportingSuffix, new byte[512]);
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        }));

        viewModel.ImportBaseImageCommand.Execute(source);
        await WaitUntilAsync(() => viewModel.IsImporting);

        viewModel.CancelImportCommand.Execute(null);
        await viewModel.ImportBaseImageCommand.ExecutionTask!;

        Assert.False(viewModel.IsImporting);
        Assert.False(viewModel.HasError);
        Assert.Contains("取消", viewModel.ImportStageText);

        // 替身管理器不实现 Core 的临时产物清理，原子落位由 ImportAtomicityContractTests 对真实 Core 复核。
        Assert.False(File.Exists(baseImage), "取消后不得在 base 镜像路径留下产物。");
    }

    /// <summary>导入过程中禁止重复触发，取消按钮只在有导入在跑时可用。</summary>
    [Fact]
    public async Task ImportButtonIsDisabledWhileImportIsRunning()
    {
        string images = _temp.New("images");
        string source = WriteSource(images, "android.iso", 4096);

        MainViewModel viewModel = CreateViewModel(images, log => new ScriptedQcow2Manager(async (s, b, ct) =>
        {
            await Task.Delay(150, ct);
            return true;
        }));

        Assert.True(viewModel.ImportBaseImageCommand.CanExecute(null));
        Assert.False(viewModel.CancelImportCommand.CanExecute(null));

        viewModel.ImportBaseImageCommand.Execute(source);
        await WaitUntilAsync(() => viewModel.IsImporting);

        Assert.False(viewModel.ImportBaseImageCommand.CanExecute(null));
        Assert.True(viewModel.CancelImportCommand.CanExecute(null));

        await viewModel.ImportBaseImageCommand.ExecutionTask!;

        Assert.True(viewModel.ImportBaseImageCommand.CanExecute(null));
        Assert.False(viewModel.CancelImportCommand.CanExecute(null));
    }

    private MainViewModel CreateViewModel(string imagesRoot, Func<Action<string>, IQcow2Manager> factory)
    {
        SpecLoader loader = XBeeSpec.TestSpec();
        var importer = new BaseImageImportService(imagesRoot, factory, TimeSpan.FromMilliseconds(10));

        return new MainViewModel(
            new StubInstanceRepository(),
            null,
            new Dictionary<string, ImageSpec>(StringComparer.Ordinal),
            new DiagnosticsExporter(loader),
            new TerminologyCatalog(loader.LoadTerminology()),
            null,
            importer);
    }

    private static string WriteSource(string root, string name, int length)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
    }
}

/// <summary>
/// 导入入口必须在界面上真实可达：按钮、进度、取消三者都被绑定，且代码后置确实触发了导入命令。
/// 单测不构造真实 WPF 窗口，因此以源码交叉核对守住「写了服务没人调」这一类回归。
/// </summary>
public class ImportEntryPointGuardTests
{
    private static string ReadUiFile(string fileName) => File.ReadAllText(
        UiSources.Enumerate().First(p =>
            p.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)));

    /// <summary>主窗口必须同时提供导入按钮、取消按钮与进度展示。</summary>
    [Fact]
    public void MainWindowExposesImportProgressAndCancel()
    {
        string xaml = ReadUiFile("MainWindow.xaml");

        Assert.Contains("OnChooseBaseImageToImport", xaml, StringComparison.Ordinal);
        Assert.Contains("ImportBaseImageText", xaml, StringComparison.Ordinal);
        Assert.Contains("CancelImportCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ProgressBar", xaml, StringComparison.Ordinal);
        Assert.Contains("ImportPercent", xaml, StringComparison.Ordinal);
        Assert.Contains("ImportStageText", xaml, StringComparison.Ordinal);
        Assert.Contains("ImportFailureText", xaml, StringComparison.Ordinal);
    }

    /// <summary>代码后置必须真的执行导入命令，否则按钮点了没反应。</summary>
    [Fact]
    public void MainWindowCodeBehindInvokesImportCommand()
    {
        string code = ReadUiFile("MainWindow.xaml.cs");

        Assert.Contains("OpenFileDialog", code, StringComparison.Ordinal);
        Assert.Contains("ImportBaseImageCommand.Execute", code, StringComparison.Ordinal);
    }
}

/// <summary>按脚本执行 qemu-img 的替身，用于验证 Core 的导入契约而不依赖本机 QEMU。</summary>
internal sealed class ScriptedQemuImgCommandRunner : IQemuImgCommandRunner
{
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<QemuImgCommandResult>> _script;

    /// <summary>构造替身执行边界。</summary>
    /// <param name="script">按参数序列与取消令牌产出执行结果的脚本。</param>
    public ScriptedQemuImgCommandRunner(
        Func<IReadOnlyList<string>, CancellationToken, Task<QemuImgCommandResult>> script)
    {
        _script = script;
    }

    /// <summary>执行脚本并原样返回其结果。</summary>
    /// <param name="executablePath">可执行文件绝对路径。</param>
    /// <param name="arguments">参数序列。</param>
    /// <param name="timeout">等待退出的上限时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果。</returns>
    public Task<QemuImgCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken) => _script(arguments, cancellationToken);
}

/// <summary>按脚本执行导入的替身管理器，除导入外的成员在本组测试中都不参与。</summary>
internal sealed class ScriptedQcow2Manager : IQcow2Manager
{
    private readonly Func<string, string, CancellationToken, Task<bool>> _import;

    /// <summary>构造替身管理器。</summary>
    /// <param name="import">导入脚本，参数为源路径、目标路径与取消令牌。</param>
    public ScriptedQcow2Manager(Func<string, string, CancellationToken, Task<bool>> import)
    {
        _import = import;
    }

    /// <summary>执行导入脚本。</summary>
    /// <param name="sourceImagePath">源镜像路径。</param>
    /// <param name="baseImagePath">目标 base 镜像路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否真正执行了转换。</returns>
    public Task<bool> ImportBaseImageAsync(
        string sourceImagePath,
        string baseImagePath,
        CancellationToken cancellationToken = default)
        => _import(sourceImagePath, baseImagePath, cancellationToken);

    /// <summary>本组测试不涉及 overlay 创建。</summary>
    /// <param name="baseImagePath">下层镜像路径。</param>
    /// <param name="overlayPath">待创建的 overlay 路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>不返回。</returns>
    public Task CreateOverlayAsync(
        string baseImagePath,
        string overlayPath,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>本组测试不涉及链式校验。</summary>
    /// <param name="overlayPath">overlay 路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>不返回。</returns>
    public Task<bool> ValidateChainAsync(string overlayPath, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
