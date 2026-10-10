using XBear.App.Presentation;
using XBear.App.Services;
using XBear.App.ViewModels;
using XBear.App.Views;
using XBear.Core.Abstractions;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 主视图模型对文件传输、应用管理、模块管理三项特性的接线测试。
/// 验证运行态可开、停止收敛与术语文案合规。
/// </summary>
public class MainWindowFeatureWiringTests : IDisposable
{
    private const string InstanceId = "win-feat-01";
    private const string ImageRef = "bliss-os-17-x86_64";

    private readonly TempRoot _temp = new();

    public void Dispose() => _temp.Cleanup();

    private static InstanceSpec BuildSpec() => new()
    {
        Id = InstanceId,
        DisplayName = "特性接线测试实例",
        Platform = "windows",
        ImageRef = ImageRef,
        Resources = new ResourceSpec { MemoryMB = 2048, CpuCores = 4, DiskGB = 32 },
    };

    private InstanceManager CreateManager(IInstanceRepository repository)
    {
        return new InstanceManager(
            repository,
            new StubPortAllocator(),
            new StubArgBuilder(),
            new StubQemuLauncher(),
            new StubQcow2Manager(),
            _temp.NewImagesRootWithBaseImage(ImageRef),
            _temp.New("instances"),
            imageCatalog: null,
            qmpClientFactory: () => new StubQmpClient(),
            adbClientFactory: () => new StubAdbClient());
    }

    private static MainViewModel CreateViewModel(
        IInstanceRepository repository,
        InstanceManager manager,
        IFileTransferWindowHost? ftHost = null,
        IApplicationWindowHost? appHost = null,
        IModuleWindowHost? modHost = null)
    {
        SpecLoader loader = XBeeSpec.TestSpec();
        return new MainViewModel(
            repository,
            manager,
            new Dictionary<string, ImageSpec>(StringComparer.Ordinal),
            new DiagnosticsExporter(loader),
            new TerminologyCatalog(loader.LoadTerminology()),
            projectionWindows: null,
            fileTransferWindows: ftHost,
            applicationWindows: appHost,
            moduleWindows: modHost);
    }

    [Fact]
    public async Task RunningInstanceEnablesFeatureEntriesAndAllowsOpening()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());
        var manager = CreateManager(repository);

        var ftHost = new StubFileTransferWindowHost();
        var appHost = new StubApplicationWindowHost();
        var modHost = new StubModuleWindowHost();

        MainViewModel viewModel = CreateViewModel(repository, manager, ftHost, appHost, modHost);
        await viewModel.RefreshAsync();

        // 停止态下入口必须全部禁用
        Assert.False(viewModel.CanOpenFileTransfer);
        Assert.False(viewModel.CanOpenApplications);
        Assert.False(viewModel.CanOpenModules);
        Assert.False(viewModel.OpenFileTransferCommand.CanExecute(null));
        Assert.False(viewModel.OpenApplicationsCommand.CanExecute(null));
        Assert.False(viewModel.OpenModulesCommand.CanExecute(null));

        // 启动后全部变为可用
        await viewModel.StartSelectedAsync();

        Assert.True(viewModel.CanOpenFileTransfer);
        Assert.True(viewModel.CanOpenApplications);
        Assert.True(viewModel.CanOpenModules);
        Assert.True(viewModel.OpenFileTransferCommand.CanExecute(null));
        Assert.True(viewModel.OpenApplicationsCommand.CanExecute(null));
        Assert.True(viewModel.OpenModulesCommand.CanExecute(null));

        viewModel.OpenFileTransferCommand.Execute(null);
        viewModel.OpenApplicationsCommand.Execute(null);
        viewModel.OpenModulesCommand.Execute(null);

        Assert.Single(ftHost.Created);
        Assert.Equal(InstanceId, ftHost.OpenInstanceIds.Single());

        Assert.Single(appHost.Created);
        Assert.Equal(InstanceId, appHost.OpenInstanceIds.Single());

        Assert.Single(modHost.Created);
        Assert.Equal(InstanceId, modHost.OpenInstanceIds.Single());
    }

    [Fact]
    public async Task StoppingInstanceClosesAllThreeWindows()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());
        var manager = CreateManager(repository);

        var ftHost = new StubFileTransferWindowHost();
        var appHost = new StubApplicationWindowHost();
        var modHost = new StubModuleWindowHost();

        MainViewModel viewModel = CreateViewModel(repository, manager, ftHost, appHost, modHost);
        await viewModel.RefreshAsync();
        await viewModel.StartSelectedAsync();

        viewModel.OpenFileTransferCommand.Execute(null);
        viewModel.OpenApplicationsCommand.Execute(null);
        viewModel.OpenModulesCommand.Execute(null);

        Assert.Single(ftHost.OpenInstanceIds);
        Assert.Single(appHost.OpenInstanceIds);
        Assert.Single(modHost.OpenInstanceIds);

        await viewModel.StopSelectedAsync();

        // 实例停止后，三扇功能窗口必须全部自动收敛关闭
        Assert.Empty(ftHost.OpenInstanceIds);
        Assert.Empty(appHost.OpenInstanceIds);
        Assert.Empty(modHost.OpenInstanceIds);
        Assert.False(viewModel.CanOpenFileTransfer);
        Assert.False(viewModel.CanOpenApplications);
        Assert.False(viewModel.CanOpenModules);
    }

    [Fact]
    public void ButtonTextsComplyWithTerminology()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());
        var manager = CreateManager(repository);

        TerminologyCatalog terms = new(XBeeSpec.TestSpec().LoadTerminology());
        MainViewModel viewModel = CreateViewModel(
            repository,
            manager,
            new StubFileTransferWindowHost(),
            new StubApplicationWindowHost(),
            new StubModuleWindowHost());

        Assert.Contains(terms.FileTransfer, viewModel.OpenFileTransferText, StringComparison.Ordinal);
        Assert.Contains(terms.Application, viewModel.OpenApplicationsText, StringComparison.Ordinal);
        Assert.Contains(terms.Module, viewModel.OpenModulesText, StringComparison.Ordinal);
    }
}
