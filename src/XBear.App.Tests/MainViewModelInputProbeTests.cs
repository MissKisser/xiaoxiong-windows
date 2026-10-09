using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>主视图模型对输入通道探测的接线测试。验证启动后结论真的被回填。</summary>
public class MainViewModelInputProbeTests : IDisposable
{
    private const string InstanceId = "win-vm-01";
    private const string ImageRef = "bliss-os-17-x86_64";

    private readonly TempRoot _temp = new();

    public void Dispose() => _temp.Cleanup();

    private static InstanceSpec BuildSpec() => new()
    {
        Id = InstanceId,
        DisplayName = "视图模型探测实例",
        Platform = "windows",
        ImageRef = ImageRef,
        Resources = new ResourceSpec { MemoryMB = 4096, CpuCores = 4, DiskGB = 64 },
    };

    private InstanceManager CreateManager(
        IInstanceRepository repository,
        StubQmpClient qmp,
        StubAdbClient adb,
        SpecValidator? validator = null,
        Exception? startFailure = null)
    {
        var manager = new InstanceManager(
            repository,
            new StubPortAllocator(),
            new StubArgBuilder(),
            new StubQemuLauncher { StartFailure = startFailure },
            new StubQcow2Manager(),
            _temp.NewImagesRootWithBaseImage(ImageRef),
            _temp.New("instances"),
            validator,
            imageCatalog: null,
            qmpClientFactory: () => qmp,
            adbClientFactory: () => adb);

        return manager;
    }

    [Fact]
    public async Task StartBackfillsInputChannelConclusion()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        var qmp = new StubQmpClient();
        var adb = new StubAdbClient();
        InstanceManager manager = CreateManager(repository, qmp, adb);

        MainViewModelHarness harness = MainViewModelHarness.Create(manager, repository);
        await harness.ViewModel.RefreshAsync();

        // 启动前界面停在尚未探测。
        Assert.True(harness.ViewModel.InputChannelUnavailable);

        await harness.ViewModel.StartSelectedAsync();

        // 启动后必须真的发起一次探测并把结论回填，而不是停在尚未探测。
        Assert.Equal(1, qmp.ConnectCount);
        Assert.False(harness.ViewModel.InputChannelUnavailable);
        Assert.NotEqual("尚未探测", harness.ViewModel.InputChannelText);
    }

    [Fact]
    public async Task StartSurfacesBothPathFailuresInsteadOfReady()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        var qmp = new StubQmpClient { ConnectFailure = new XBearException(ErrorCategory.Protocol, "QMP 未响应") };
        var adb = new StubAdbClient { ConnectFailure = new XBearException(ErrorCategory.Protocol, "adb 未响应") };
        InstanceManager manager = CreateManager(repository, qmp, adb);

        MainViewModelHarness harness = MainViewModelHarness.Create(manager, repository);
        await harness.ViewModel.RefreshAsync();
        await harness.ViewModel.StartSelectedAsync();

        // 两条通路都不通时界面必须显示不可用并给出双方原因。
        Assert.True(harness.ViewModel.InputChannelUnavailable);
        Assert.Contains("QMP 未响应", harness.ViewModel.InputChannelDetail, StringComparison.Ordinal);
        Assert.Contains("adb 未响应", harness.ViewModel.InputChannelDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartFailureKeepsInputChannelUnknownAndShowsError()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        var qmp = new StubQmpClient();
        var adb = new StubAdbClient();
        InstanceManager manager = CreateManager(
            repository,
            qmp,
            adb,
            startFailure: new XBearException(ErrorCategory.Process, "QEMU 拉起失败"));

        MainViewModelHarness harness = MainViewModelHarness.Create(manager, repository);
        await harness.ViewModel.RefreshAsync();
        await harness.ViewModel.StartSelectedAsync();

        // 启动失败时不得发起探测，结论保持未知，并由错误横幅说明原因。
        Assert.Equal(0, qmp.ConnectCount);
        Assert.True(harness.ViewModel.InputChannelUnavailable);
        Assert.True(harness.ViewModel.HasError);
    }

    [Fact]
    public async Task ManualProbeCommandRefreshesConclusionForRunningInstance()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        var qmp = new StubQmpClient();
        var adb = new StubAdbClient();
        InstanceManager manager = CreateManager(repository, qmp, adb);

        MainViewModelHarness harness = MainViewModelHarness.Create(manager, repository);
        await harness.ViewModel.RefreshAsync();
        await harness.ViewModel.StartSelectedAsync();

        int afterStart = qmp.ConnectCount;
        await harness.ViewModel.ProbeSelectedInputChannelAsync();

        // 手动重探必须真正再发起一次探测。
        Assert.True(qmp.ConnectCount > afterStart);
    }

    [Fact]
    public async Task ProbeOnStoppedInstanceLeavesConclusionUnknown()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        var qmp = new StubQmpClient();
        InstanceManager manager = CreateManager(repository, qmp, new StubAdbClient());

        MainViewModelHarness harness = MainViewModelHarness.Create(manager, repository);
        await harness.ViewModel.RefreshAsync();
        await harness.ViewModel.ProbeSelectedInputChannelAsync();

        // 未运行时手动探测不得连出连接，界面保持未知。
        Assert.Equal(0, qmp.ConnectCount);
        Assert.True(harness.ViewModel.InputChannelUnavailable);
    }

    [Fact]
    public async Task InputChannelTitleUsesTerminologyCatalog()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        InstanceManager manager = CreateManager(repository, new StubQmpClient(), new StubAdbClient());
        MainViewModelHarness harness = MainViewModelHarness.Create(manager, repository);
        await harness.ViewModel.RefreshAsync();

        // 输入通道相关文案必须取自术语表，不得在视图模型里硬写。
        Assert.Contains(harness.ViewModel.InputChannelTerm, harness.ViewModel.InputChannelTitle, StringComparison.Ordinal);
    }
}
