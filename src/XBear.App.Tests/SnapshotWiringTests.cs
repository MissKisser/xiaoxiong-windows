using XBear.App.Presentation;
using XBear.App.Services;
using XBear.App.ViewModels;
using XBear.Core.Abstractions;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 主视图模型对快照管理入口的接线测试。
/// 验证入口的可用条件、打开行为、停止时的窗口存续与按钮文案合规。
/// </summary>
public class SnapshotWiringTests : IDisposable
{
    private const string InstanceId = "win-snap-wire-01";
    private const string ImageRef = "bliss-os-17-x86_64";

    private readonly TempRoot _temp = new();

    public void Dispose() => _temp.Cleanup();

    private static InstanceSpec BuildSpec() => new()
    {
        Id = InstanceId,
        DisplayName = "快照接线实例",
        Platform = "windows",
        ImageRef = ImageRef,
        Resources = new ResourceSpec { MemoryMB = 2048, CpuCores = 4, DiskGB = 32 },
    };

    private InstanceManager CreateManager(IInstanceRepository repository) =>
        new(
            repository,
            new StubPortAllocator(),
            new StubArgBuilder(),
            new StubQemuLauncher(),
            new StubQcow2Manager(),
            _temp.NewImagesRootWithBaseImage(ImageRef),
            _temp.New("instances"),
            qmpClientFactory: () => new StubQmpClient(),
            adbClientFactory: () => new StubAdbClient());

    private static MainViewModel CreateViewModel(
        IInstanceRepository repository,
        InstanceManager manager,
        Views.ISnapshotWindowHost? snapshotHost = null)
    {
        SpecLoader loader = XBeeSpec.TestSpec();
        return new MainViewModel(
            repository,
            manager,
            new Dictionary<string, ImageSpec>(StringComparer.Ordinal),
            new DiagnosticsExporter(loader),
            new TerminologyCatalog(loader.LoadTerminology()),
            snapshotWindows: snapshotHost);
    }

    [Fact]
    public async Task SnapshotEntryIsAvailableOnceAnInstanceIsSelected()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());
        var manager = CreateManager(repository);
        var host = new StubSnapshotWindowHost();

        MainViewModel viewModel = CreateViewModel(repository, manager, host);
        await viewModel.RefreshAsync();

        // 未接入窗口宿主时入口必须禁用。
        MainViewModel withoutHost = CreateViewModel(repository, manager);
        Assert.False(withoutHost.CanOpenSnapshots);

        Assert.True(viewModel.CanOpenSnapshots);
        Assert.True(viewModel.OpenSnapshotsCommand.CanExecute(null));

        viewModel.OpenSnapshotsCommand.Execute(null);

        Assert.Single(host.Created);
        Assert.Equal(InstanceId, host.OpenInstanceIds.Single());
    }

    [Fact]
    public async Task SnapshotEntryStaysAvailableWhileTheInstanceIsStopped()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());
        var manager = CreateManager(repository);
        var host = new StubSnapshotWindowHost();

        MainViewModel viewModel = CreateViewModel(repository, manager, host);
        await viewModel.RefreshAsync();
        await viewModel.StartSelectedAsync();

        viewModel.OpenSnapshotsCommand.Execute(null);
        await viewModel.StopSelectedAsync();

        // 快照窗口不在实例停止时收敛：恢复流程本身要让实例经历停止与重新启动。
        Assert.Single(host.OpenInstanceIds);
        Assert.Empty(host.Closed);
        Assert.True(viewModel.CanOpenSnapshots);
    }

    [Fact]
    public async Task SnapshotWindowConvergesWhenTheInstanceFaults()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());
        var launcher = new StubQemuLauncher();
        InstanceManager manager = new(
            repository,
            new StubPortAllocator(),
            new StubArgBuilder(),
            launcher,
            new StubQcow2Manager(),
            _temp.NewImagesRootWithBaseImage(ImageRef),
            _temp.New("instances"),
            qmpClientFactory: () => new StubQmpClient(),
            adbClientFactory: () => new StubAdbClient());

        var host = new StubSnapshotWindowHost();
        MainViewModel viewModel = CreateViewModel(repository, manager, host);
        await viewModel.RefreshAsync();
        await viewModel.StartSelectedAsync();

        viewModel.OpenSnapshotsCommand.Execute(null);
        Assert.Single(host.OpenInstanceIds);

        await viewModel.StopSelectedAsync();
        Assert.Single(host.OpenInstanceIds);

        // 启动失败使实例进入故障态，快照窗口随之收敛关闭。
        launcher.StartFailure = new Core.Diagnostics.XBearException(
            Core.Diagnostics.ErrorCategory.Process,
            "进程拉起失败。");

        try
        {
            await manager.StartAsync(InstanceId);
        }
        catch (Core.Diagnostics.XBearException)
        {
            // 启动失败由编排器转为故障态并向上抛，异常本身不是本测试的断言目标。
        }

        Assert.Equal(InstanceState.Faulted, manager.GetState(InstanceId));
        Assert.Empty(host.OpenInstanceIds);
        Assert.Equal(InstanceId, Assert.Single(host.Closed));
    }

    [Fact]
    public async Task ButtonTextCompliesWithTerminology()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());
        var manager = CreateManager(repository);

        TerminologyCatalog terms = new(XBeeSpec.TestSpec().LoadTerminology());
        MainViewModel viewModel = CreateViewModel(repository, manager, new StubSnapshotWindowHost());
        await viewModel.RefreshAsync();

        Assert.Equal(string.Concat("管理", terms.Snapshot), viewModel.OpenSnapshotsText);
    }
}