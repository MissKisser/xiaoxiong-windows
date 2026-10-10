using System.IO;
using XBear.App.Presentation;
using XBear.App.Theme;
using XBear.App.ViewModels;
using XBear.Core.Abstractions;
using XBear.Core.Instances;
using XBear.Core.Snapshots;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 快照管理窗口与视图模型的单元测试。快照服务经 QMP 替身驱动，不启动 QEMU。
/// </summary>
public class SnapshotWindowTests : IDisposable
{
    private const string InstanceId = "win-snap-ui-01";
    private const string InstanceName = "快照界面实例";
    private const string ImageRef = "bliss-os-17-x86_64";

    private readonly TempRoot _temp = new();
    private readonly StubSnapshotQmpClient _qmp = new();
    private readonly StubInstanceRepository _repository = new();
    private readonly string _instancesRoot;
    private readonly InstanceManager _manager;

    public SnapshotWindowTests()
    {
        _repository.Add(new InstanceSpec
        {
            Id = InstanceId,
            DisplayName = InstanceName,
            Platform = "windows",
            ImageRef = ImageRef,
            Resources = new ResourceSpec { MemoryMB = 2048, CpuCores = 4, DiskGB = 32 },
        });

        _instancesRoot = _temp.New("instances");

        _manager = new InstanceManager(
            _repository,
            new StubPortAllocator(),
            new StubArgBuilder(),
            new StubQemuLauncher(),
            new StubQcow2Manager(),
            _temp.NewImagesRootWithBaseImage(ImageRef),
            _instancesRoot,
            qmpClientFactory: () => _qmp,
            adbClientFactory: () => new StubAdbClient());

        File.WriteAllText(Path.Combine(_instancesRoot, InstanceId + ".qcow2"), "overlay");
    }

    public void Dispose() => _temp.Cleanup();

    private static TerminologyCatalog Terms() =>
        new(XBeeSpec.TestSpec().LoadTerminology());

    private SnapshotService CreateService() => new(
        _manager,
        _repository,
        () => _qmp,
        clock: () => new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.FromHours(8)),
        settleTimeout: TimeSpan.FromMilliseconds(200),
        settleInterval: TimeSpan.FromMilliseconds(10));

    private SnapshotWindowViewModel CreateViewModel(
        Func<string, bool>? confirmRestore = null,
        Func<string, bool>? confirmDelete = null) =>
        new(
            CreateService(),
            InstanceId,
            InstanceName,
            Terms(),
            confirmRestoreHandler: confirmRestore ?? (_ => true),
            confirmDeleteHandler: confirmDelete ?? (_ => true));

    private async Task StartAsync()
    {
        await _manager.StartAsync(InstanceId);
        _qmp.Running = true;
    }

    [Fact]
    public void WindowTitleShowsInstanceNameAndTerminology()
    {
        SnapshotWindowViewModel viewModel = CreateViewModel();

        Assert.Contains(InstanceName, viewModel.WindowTitle, StringComparison.Ordinal);
        Assert.Contains(Terms().Snapshot, viewModel.WindowTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void ButtonTextsComplyWithTerminology()
    {
        SnapshotWindowViewModel viewModel = CreateViewModel();
        TerminologyCatalog terms = Terms();

        Assert.Equal(string.Concat("创建", terms.Snapshot), viewModel.CreateSnapshotText);
        Assert.Equal(string.Concat("恢复到", terms.Snapshot), viewModel.RestoreSnapshotText);
        Assert.Equal(string.Concat("删除", terms.Snapshot), viewModel.DeleteSnapshotText);
    }

    [Fact]
    public async Task RefreshListsSnapshotsWithContractFields()
    {
        await StartAsync();
        SnapshotWindowViewModel viewModel = CreateViewModel();
        viewModel.NewSnapshotName = "刷模块前";
        viewModel.NewSnapshotNote = "留个还原点";

        await viewModel.CreateAsync();
        await viewModel.RefreshAsync();

        SnapshotItemViewModel item = Assert.Single(viewModel.Snapshots);
        Assert.StartsWith("snap-" + InstanceId, item.Id, StringComparison.Ordinal);
        Assert.Equal("刷模块前", item.DisplayName);
        Assert.Equal("就绪", item.StateText);
        Assert.Equal("留个还原点", item.NoteText);
        Assert.Equal("该实例的首个快照", item.ParentText);
        Assert.True(item.IsRestorable);
        Assert.True(item.IsChainRoot);
        Assert.False(viewModel.HasOperationFailure);
    }

    [Fact]
    public async Task CreateRequiresAName()
    {
        await StartAsync();
        SnapshotWindowViewModel viewModel = CreateViewModel();

        Assert.False(viewModel.CanCreate);

        viewModel.NewSnapshotName = "刷模块前";

        Assert.True(viewModel.CanCreate);
    }

    [Fact]
    public async Task CreateOnStoppedInstanceReportsFailureWithoutTouchingQemu()
    {
        SnapshotWindowViewModel viewModel = CreateViewModel();
        viewModel.NewSnapshotName = "停机时创建";

        await viewModel.CreateAsync();

        Assert.True(viewModel.HasOperationFailure);
        Assert.Empty(viewModel.Snapshots);
        Assert.Empty(_qmp.HumanMonitorCommands);
        Assert.Contains(Terms().Snapshot, viewModel.LastOperationResultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateClearsTheInputsAfterSuccess()
    {
        await StartAsync();
        SnapshotWindowViewModel viewModel = CreateViewModel();
        viewModel.NewSnapshotName = "刷模块前";
        viewModel.NewSnapshotNote = "留个还原点";

        await viewModel.CreateAsync();

        Assert.False(viewModel.HasOperationFailure);
        Assert.Equal(string.Empty, viewModel.NewSnapshotName);
        Assert.Equal(string.Empty, viewModel.NewSnapshotNote);
        Assert.Single(viewModel.Snapshots);
    }

    [Fact]
    public async Task RestoreWithoutConfirmationAborts()
    {
        await StartAsync();
        SnapshotWindowViewModel viewModel = CreateViewModel(confirmRestore: _ => false);
        viewModel.NewSnapshotName = "可恢复的一份";
        await viewModel.CreateAsync();

        SnapshotItemViewModel item = Assert.Single(viewModel.Snapshots);
        viewModel.SelectedItem = item;

        Assert.True(viewModel.CanRestore);

        await viewModel.RestoreAsync();

        Assert.DoesNotContain(_qmp.HumanMonitorCommands, line => line.StartsWith("loadvm", StringComparison.Ordinal));
        Assert.Single(viewModel.Snapshots);
    }

    [Fact]
    public async Task RestoreWithConfirmationRestartsTheInstance()
    {
        await StartAsync();
        SnapshotWindowViewModel viewModel = CreateViewModel();
        viewModel.NewSnapshotName = "可恢复的一份";
        await viewModel.CreateAsync();

        string? prompt = null;
        SnapshotWindowViewModel confirmable = new(
            CreateService(),
            InstanceId,
            InstanceName,
            Terms(),
            confirmRestoreHandler: message =>
            {
                prompt = message;
                return true;
            },
            confirmDeleteHandler: _ => true);

        await confirmable.RefreshAsync();
        SnapshotItemViewModel item = Assert.Single(confirmable.Snapshots);
        confirmable.SelectedItem = item;
        await confirmable.RestoreAsync();

        Assert.NotNull(prompt);
        Assert.Contains(Terms().Snapshot, prompt!, StringComparison.Ordinal);
        Assert.False(confirmable.HasOperationFailure);
        Assert.Contains("已重新启动", confirmable.LastOperationResultText, StringComparison.Ordinal);
        Assert.Equal(InstanceState.Running, _manager.GetState(InstanceId));
        Assert.Contains(_qmp.HumanMonitorCommands, line => line.StartsWith("loadvm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoreIsDisabledForSnapshotsThatAreNotRestorable()
    {
        await StartAsync();
        _qmp.HumanMonitorFailure = new Core.Diagnostics.XBearException(
            Core.Diagnostics.ErrorCategory.Storage,
            "磁盘剩余空间不足。");
        _qmp.HumanMonitorFailurePrefix = "savevm";

        SnapshotWindowViewModel viewModel = CreateViewModel();
        viewModel.NewSnapshotName = "会失败的一份";
        await viewModel.CreateAsync();
        _qmp.HumanMonitorFailure = null;

        await viewModel.RefreshAsync();
        SnapshotItemViewModel item = Assert.Single(viewModel.Snapshots);

        Assert.Equal("创建失败", item.StateText);
        Assert.False(item.IsRestorable);

        viewModel.SelectedItem = item;

        Assert.False(viewModel.CanRestore);
    }

    [Fact]
    public async Task DeleteWithoutConfirmationAborts()
    {
        await StartAsync();
        SnapshotWindowViewModel viewModel = CreateViewModel(confirmDelete: _ => false);
        viewModel.NewSnapshotName = "待删除";
        await viewModel.CreateAsync();
        viewModel.SelectedItem = Assert.Single(viewModel.Snapshots);

        Assert.True(viewModel.CanDelete);

        await viewModel.DeleteAsync();

        Assert.Single(viewModel.Snapshots);
        Assert.DoesNotContain(_qmp.HumanMonitorCommands, line => line.StartsWith("delvm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteWithConfirmationRemovesTheSnapshot()
    {
        await StartAsync();
        SnapshotWindowViewModel viewModel = CreateViewModel();
        viewModel.NewSnapshotName = "待删除";
        await viewModel.CreateAsync();
        viewModel.SelectedItem = Assert.Single(viewModel.Snapshots);

        await viewModel.DeleteAsync();

        Assert.False(viewModel.HasOperationFailure);
        Assert.Contains(Terms().Snapshot, viewModel.LastOperationResultText, StringComparison.Ordinal);
        Assert.Empty(viewModel.Snapshots);
        Assert.Empty(_qmp.Tags);
    }

    [Fact]
    public async Task DeleteFailureIsReportedWithRemediation()
    {
        await StartAsync();
        SnapshotWindowViewModel viewModel = CreateViewModel();
        viewModel.NewSnapshotName = "待删除";
        await viewModel.CreateAsync();
        viewModel.SelectedItem = Assert.Single(viewModel.Snapshots);

        _qmp.HumanMonitorFailure = new Core.Diagnostics.XBearException(
            Core.Diagnostics.ErrorCategory.Protocol,
            "delvm 失败。",
            "请确认实例仍在运行后重试。");
        _qmp.HumanMonitorFailurePrefix = "delvm";

        await viewModel.DeleteAsync();

        Assert.True(viewModel.HasOperationFailure);
        Assert.Contains("请确认实例仍在运行后重试。", viewModel.LastOperationResultText, StringComparison.Ordinal);
        Assert.Single(viewModel.Snapshots);
    }

    [Fact]
    public void OpeningSameInstanceTwiceKeepsOneWindow()
    {
        var host = new StubSnapshotWindowHost();

        host.Open("win-a", "实例甲", null);
        host.Open("win-a", "实例甲", null);

        Assert.Single(host.Created);
        Assert.Single(host.Activated);
        Assert.Equal("win-a", host.OpenInstanceIds.Single());
    }

    [Fact]
    public void OpeningDifferentInstancesKeepsSeparateWindows()
    {
        var host = new StubSnapshotWindowHost();

        host.Open("win-a", "实例甲", null);
        host.Open("win-b", "实例乙", null);

        Assert.Equal(2, host.Created.Count);
        Assert.Equal(2, host.OpenInstanceIds.Count);
    }

    [Fact]
    public void CloseRemovesTheWindowAndAllowsReopening()
    {
        var host = new StubSnapshotWindowHost();

        host.Open("win-a", "实例甲", null);
        host.Close("win-a");
        host.Open("win-a", "实例甲", null);

        Assert.Equal(2, host.Created.Count);
        Assert.Single(host.Closed);
        Assert.Equal("win-a", host.OpenInstanceIds.Single());
    }
}

/// <summary>
/// 快照界面的静态守卫：窗口必须真的落在扫描范围内、遵守视觉与排版令牌、走术语正名，
/// 且主界面真的接上了入口。这些错误在窗口被构造之前都不可见，只能靠静态扫描守住。
/// </summary>
public class SnapshotWindowGuardTests
{
    private const string SnapshotWindowPath = "Views/SnapshotWindow.xaml";
    private const string SnapshotViewModelPath = "ViewModels/SnapshotWindowViewModel.cs";

    private static string ReadUiSource(string relativePath)
    {
        string? path = UiSources.Enumerate()
            .FirstOrDefault(candidate => Relative(candidate).Equals(relativePath, StringComparison.OrdinalIgnoreCase));

        Assert.True(path is not null, $"界面源码目录中缺少 {relativePath}，静态守卫无法覆盖它。");
        return File.ReadAllText(path!);
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(UiSources.Root, path).Replace('\\', '/');

    [Fact]
    public void SnapshotWindowAndViewModelArePartOfTheScannedSourceSet()
    {
        var relative = UiSources.Enumerate().Select(Relative).ToList();

        Assert.Contains(SnapshotWindowPath, relative);
        Assert.Contains(SnapshotViewModelPath, relative);
    }

    [Fact]
    public void SnapshotWindowXamlObeysColorAndLayoutGuards()
    {
        string xaml = ReadUiSource(SnapshotWindowPath);

        Assert.Empty(NoLiteralColorTests.FindNamedColorViolations(xaml, SnapshotWindowPath));
        Assert.Empty(NoLiteralLayoutMetricsTests.FindXamlMetricsViolations(xaml, SnapshotWindowPath));
    }

    [Fact]
    public void SnapshotWindowXamlAndViewModelUseTerminologyProperNames()
    {
        TerminologyDocument terminology = XBeeSpec.TestSpec().LoadTerminology();

        Assert.Empty(
            TerminologyComplianceTests.FindXamlTerminologyViolations(
                ReadUiSource(SnapshotWindowPath),
                SnapshotWindowPath,
                terminology));

        Assert.Empty(
            TerminologyComplianceTests.FindCsharpTerminologyViolations(
                ReadUiSource(SnapshotViewModelPath),
                SnapshotViewModelPath,
                terminology));
    }

    [Fact]
    public void SnapshotViewModelSetsNoLiteralTypographyOrSpacingMetrics()
    {
        Assert.Empty(
            NoLiteralLayoutMetricsTests.FindCsharpMetricsViolations(
                ReadUiSource(SnapshotViewModelPath),
                SnapshotViewModelPath));
    }

    [Fact]
    public void SnapshotWindowUsesTheRegisteredSnapshotIcon()
    {
        string xaml = ReadUiSource(SnapshotWindowPath);

        Assert.Contains("Icon.Snapshot", xaml, StringComparison.Ordinal);
        Assert.Contains("Icon.Restore", IconKeys.All);
    }

    [Fact]
    public void MainWindowBindsTheSnapshotEntryBesideTheModuleEntry()
    {
        string xaml = ReadUiSource("MainWindow.xaml");

        Assert.Contains("OpenSnapshotsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenModulesCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Icon.Snapshot", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotViewModelExposesCreateRestoreAndDeleteCommands()
    {
        var commands = typeof(SnapshotWindowViewModel)
            .GetProperties()
            .Where(property => property.Name.EndsWith("Command", StringComparison.Ordinal))
            .Select(property => property.Name)
            .ToList();

        Assert.Contains("CreateCommand", commands);
        Assert.Contains("RestoreCommand", commands);
        Assert.Contains("DeleteCommand", commands);
        Assert.Contains("RefreshCommand", commands);
    }
}