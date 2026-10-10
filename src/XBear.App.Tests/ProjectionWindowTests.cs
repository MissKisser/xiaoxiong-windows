using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using XBear.App.Presentation;
using XBear.App.Services;
using XBear.App.ViewModels;
using XBear.App.Views;
using XBear.Core.Abstractions;
using XBear.Core.Instances;
using XBear.Core.Projection;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 投屏窗口视图模型的行为测试。覆盖画面到达的界面编组、分辨率变化、
/// 指针坐标换算、按键映射与关闭时的会话收敛。
/// </summary>
public class ProjectionWindowViewModelTests
{
    private const string InstanceId = "win-proj-01";
    private const string InstanceName = "投屏测试实例";
    private const int Width = 800;
    private const int Height = 600;

    private static TerminologyCatalog Terms() =>
        new(XBeeSpec.TestSpec().LoadTerminology());

    /// <summary>
    /// 构造被测视图模型。编组委托由参数决定：传入空集合表示动作只入队不执行，
    /// 传 null 表示动作在当前线程直接执行。
    /// </summary>
    /// <param name="service">投屏服务替身。</param>
    /// <param name="qmp">记录注入命令的 QMP 客户端，为空表示会话不承载输入。</param>
    /// <param name="deferToQueue">是否把界面编组动作推迟到手动执行。</param>
    /// <returns>被测视图模型。</returns>
    private static (ProjectionWindowViewModel ViewModel, Queue<Action> Queue) Create(
        StubProjectionService service,
        RecordingInjectionQmpClient? qmp = null,
        bool deferToQueue = false)
    {
        var size = new ScreenGeometry(Width, Height);
        ProjectionInputInjector? injector = qmp is null
            ? null
            : new ProjectionInputInjector(qmp, () => size);

        service.Register(InstanceId, size.Width, size.Height, injector);

        var queue = new Queue<Action>();
        var viewModel = new ProjectionWindowViewModel(
            service,
            InstanceId,
            InstanceName,
            Terms(),
            action =>
            {
                if (deferToQueue)
                {
                    queue.Enqueue(action);
                    return;
                }

                action();
            });

        return (viewModel, queue);
    }

    private static string JsonOf(object? payload) =>
        System.Text.Json.JsonSerializer.Serialize(payload);

    [Fact]
    public void WindowTitleShowsInstanceNameAndProjectionTerm()
    {
        var service = new StubProjectionService();
        (ProjectionWindowViewModel viewModel, _) = Create(service);

        Assert.Contains(InstanceName, viewModel.WindowTitle, StringComparison.Ordinal);
        Assert.Contains(Terms().Projection, viewModel.WindowTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolutionComesFromHandshakeBeforeFirstFrameButFrameIsNotClaimed()
    {
        var service = new StubProjectionService();
        (ProjectionWindowViewModel viewModel, _) = Create(service);

        // 分辨率取自 VNC 握手时服务端公布的桌面尺寸，是真实观测；
        // 但画面本身尚未取到，不得显示成已经呈现。
        Assert.Contains(
            $"{Width}×{Height}",
            viewModel.ResolutionText,
            StringComparison.Ordinal);
        Assert.False(viewModel.HasFrame);
        Assert.Null(viewModel.CaptureFrame());
    }

    [Fact]
    public void FrameRateIsNeverClaimedBeforeAnyMeasurement()
    {
        var service = new StubProjectionService();
        (ProjectionWindowViewModel viewModel, _) = Create(service);

        // 尚无任何观测时不得出现实测数值，只能说明未产生。
        Assert.Contains("尚未产生", viewModel.FrameRateText, StringComparison.Ordinal);
        Assert.DoesNotContain("实测 ", viewModel.FrameRateText, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameArrivalIsMarshalledThroughTheUiDelegate()
    {
        var service = new StubProjectionService();
        (ProjectionWindowViewModel viewModel, Queue<Action> queue) = Create(service, deferToQueue: true);

        var raised = 0;
        viewModel.FrameReady += (_, _) => raised++;

        service.PublishFrame(InstanceId, Width, Height, sequence: 1);

        // 编组委托只入队不执行，界面线程尚未处理，画面不得被当作已呈现。
        Assert.Equal(0, raised);
        Assert.False(viewModel.HasFrame);
        Assert.Single(queue);

        queue.Dequeue()();

        Assert.Equal(1, raised);
        Assert.True(viewModel.HasFrame);
    }

    [Fact]
    public void FrameArrivalUpdatesSnapshotResolutionAndFrameRate()
    {
        var service = new StubProjectionService();
        (ProjectionWindowViewModel viewModel, _) = Create(service);

        var raised = 0;
        viewModel.FrameReady += (_, _) => raised++;

        service.PublishFrame(InstanceId, Width, Height, sequence: 2);

        Assert.Equal(1, raised);
        Assert.True(viewModel.HasFrame);
        Assert.NotNull(viewModel.LatestFrame);
        Assert.Equal(Width, viewModel.LatestFrame!.Width);
        Assert.Equal(Height, viewModel.LatestFrame.Height);

        Assert.Contains($"{Width}×{Height}", viewModel.ResolutionText, StringComparison.Ordinal);

        // 实测帧率必须来自服务返回的真实观测值。
        Assert.Contains("实测", viewModel.FrameRateText, StringComparison.Ordinal);
        Assert.Contains("1.0 fps", viewModel.FrameRateText, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolutionChangeRebuildsSizeAndReportsNewGeometry()
    {
        var service = new StubProjectionService();
        (ProjectionWindowViewModel viewModel, _) = Create(service);

        ScreenGeometry? reported = null;
        viewModel.FrameSizeChanged += (_, geometry) => reported = geometry;

        service.RaiseResolutionChanged(InstanceId, Width, Height, 1280, 720);

        Assert.NotNull(reported);
        Assert.Equal(1280, reported!.Value.Width);
        Assert.Equal(720, reported.Value.Height);
        Assert.Equal(1280, viewModel.FrameWidth);
        Assert.Equal(720, viewModel.FrameHeight);
        Assert.Contains("1280×720", viewModel.ResolutionText, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionStateChangeIsReflectedInStatusLine()
    {
        var service = new StubProjectionService();
        (ProjectionWindowViewModel viewModel, _) = Create(service);

        service.RaiseSessionStateChanged(InstanceId, ProjectionState.Active, ProjectionState.Stopped);

        Assert.False(viewModel.IsActive);
        Assert.Contains(
            ProjectionStateText.Describe(ProjectionState.Stopped),
            viewModel.StatusText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SessionTerminationByErrorIsSurfacedRatherThanSwallowed()
    {
        var service = new StubProjectionService();
        (ProjectionWindowViewModel viewModel, _) = Create(service);

        var failure = new InvalidOperationException("与实例的连接已中断");
        service.RaiseSessionStateChanged(InstanceId, ProjectionState.Active, ProjectionState.Stopped, failure);

        Assert.True(viewModel.HasFailure);
        Assert.Contains("已中断", viewModel.FailureText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PointerDownMoveUpAreDeliveredAsSeparatePhases()
    {
        var qmp = new RecordingInjectionQmpClient();
        var service = new StubProjectionService();
        (ProjectionWindowViewModel viewModel, _) = Create(service, qmp);

        var point = new InputPoint(400, 300);

        await viewModel.PointerDownAsync(point);
        await viewModel.PointerMoveAsync(point);
        await viewModel.PointerUpAsync(point);

        Assert.Equal(3, qmp.Commands.Count);
        Assert.All(qmp.Commands, command => Assert.Equal("input-send-event", command));

        Assert.Contains("\"down\":true", JsonOf(qmp.Arguments[0]), StringComparison.Ordinal);
        Assert.Contains("\"down\":false", JsonOf(qmp.Arguments[2]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PointerMoveAndUpAreDroppedWhenNotPressed()
    {
        var qmp = new RecordingInjectionQmpClient();
        var service = new StubProjectionService();
        (ProjectionWindowViewModel viewModel, _) = Create(service, qmp);

        var point = new InputPoint(100, 100);

        await viewModel.PointerMoveAsync(point);
        await viewModel.PointerUpAsync(point);

        // 悬停与未按下的抬起不构成一次投递，否则实例侧会收到没有按下前提的抬起。
        Assert.Empty(qmp.Commands);
    }

    [Fact]
    public void PointerCoordinatesAreClampedIntoTheFrame()
    {
        var qmp = new RecordingInjectionQmpClient();
        var service = new StubProjectionService();
        (ProjectionWindowViewModel viewModel, _) = Create(service, qmp);

        Assert.True(viewModel.TryMapPoint(1, 1, Width, Height, out InputPoint mapped));
        Assert.InRange(mapped.X, 0, Width - 1);
        Assert.InRange(mapped.Y, 0, Height - 1);
    }

    [Fact]
    public async Task WheelIsDeliveredAsDirectionKeyPair()
    {
        var qmp = new RecordingInjectionQmpClient();
        var service = new StubProjectionService();
        (ProjectionWindowViewModel viewModel, _) = Create(service, qmp);

        await viewModel.WheelAsync(120);

        // 滚轮以方向键表达，按下与抬起为两次独立投递。
        Assert.Equal(2, qmp.Commands.Count);
        Assert.Contains("\"down\":true", JsonOf(qmp.Arguments[0]), StringComparison.Ordinal);
        Assert.Contains("\"down\":false", JsonOf(qmp.Arguments[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeyDownAndUpAreDeliveredSeparately()
    {
        var qmp = new RecordingInjectionQmpClient();
        var service = new StubProjectionService();
        (ProjectionWindowViewModel viewModel, _) = Create(service, qmp);

        await viewModel.KeyDownAsync(AndroidKey.Back);
        await viewModel.KeyUpAsync(AndroidKey.Back);

        Assert.Equal(2, qmp.Commands.Count);
        Assert.Contains("\"down\":true", JsonOf(qmp.Arguments[0]), StringComparison.Ordinal);
        Assert.Contains("\"down\":false", JsonOf(qmp.Arguments[1]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Key.Escape, AndroidKey.Back)]
    [InlineData(Key.Back, AndroidKey.Back)]
    [InlineData(Key.Home, AndroidKey.Home)]
    [InlineData(Key.Enter, AndroidKey.Enter)]
    [InlineData(Key.Left, AndroidKey.ArrowLeft)]
    [InlineData(Key.Right, AndroidKey.ArrowRight)]
    [InlineData(Key.Up, AndroidKey.ArrowUp)]
    [InlineData(Key.Down, AndroidKey.ArrowDown)]
    public void HostKeysMapToInstanceKeys(Key key, AndroidKey expected)
    {
        Assert.True(ProjectionKeyMap.TryResolve(key, out AndroidKey actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnmappedHostKeyIsNotForwarded()
    {
        Assert.False(ProjectionKeyMap.TryResolve(Key.F12, out _));
    }

    [Fact]
    public async Task CloseStopsTheSessionAndDetachesFromTheService()
    {
        var service = new StubProjectionService();
        (ProjectionWindowViewModel viewModel, _) = Create(service);

        await viewModel.CloseAsync();

        Assert.Equal(1, service.StopCounts[InstanceId]);
        Assert.Null(service.GetSession(InstanceId));

        // 退订后到达的画面不再影响视图模型，否则窗口关闭后仍在改写状态。
        bool raised = false;
        viewModel.FrameReady += (_, _) => raised = true;
        int before = service.FrameArrivedCount;
        service.PublishFrame(InstanceId, Width, Height, sequence: 9);

        Assert.Equal(before + 1, service.FrameArrivedCount);
        Assert.False(raised);
    }

    [Fact]
    public async Task InjectionAfterCloseDoesNotReachTheInstance()
    {
        var qmp = new RecordingInjectionQmpClient();
        var service = new StubProjectionService();
        (ProjectionWindowViewModel viewModel, _) = Create(service, qmp);

        await viewModel.CloseAsync();
        await viewModel.PointerDownAsync(new InputPoint(10, 10));

        Assert.Empty(qmp.Commands);
    }

    [Fact]
    public async Task InjectionWithoutActiveInjectorIsReportedNotCrashed()
    {
        var service = new StubProjectionService();
        (ProjectionWindowViewModel viewModel, _) = Create(service);

        await service.StopAsync(InstanceId);
        await viewModel.PointerDownAsync(new InputPoint(10, 10));

        Assert.True(viewModel.HasFailure);
        Assert.Contains(Terms().ProjectionSession, viewModel.FailureText, StringComparison.Ordinal);
    }
}

/// <summary>投屏窗口宿主的去重与置前行为测试。</summary>
public class ProjectionWindowHostTests
{
    [Fact]
    public void OpeningSameInstanceTwiceKeepsOneWindow()
    {
        var host = new StubProjectionWindowHost();

        host.Open("win-a", "实例甲", null);
        host.Open("win-a", "实例甲", null);
        host.Open("win-a", "实例甲", null);

        Assert.Single(host.Created);
        Assert.Equal(2, host.Activated.Count);
    }

    [Fact]
    public void OpeningDifferentInstancesKeepsSeparateWindows()
    {
        var host = new StubProjectionWindowHost();

        host.Open("win-a", "实例甲", null);
        host.Open("win-b", "实例乙", null);

        Assert.Equal(2, host.Created.Count);
    }

    [Fact]
    public void CloseRemovesTheWindowAndAllowsReopening()
    {
        var host = new StubProjectionWindowHost();

        host.Open("win-a", "实例甲", null);
        host.Close("win-a");
        host.Open("win-a", "实例甲", null);

        // 关闭后必须能重新打开，且新开的是一扇新窗口而不是复活旧窗口。
        Assert.Equal(2, host.Created.Count);
        Assert.Empty(host.Activated);
        Assert.Equal("win-a", host.OpenInstanceIds.Single());
    }
}

/// <summary>
/// 投屏窗口宿主测试替身。真实窗口需要 STA 线程，无法在单元测试进程中构造，
/// 因此把「同实例去重、重复打开置前、关闭后可重开」这三条规则单独固化。
/// </summary>
internal sealed class StubProjectionWindowHost : IProjectionWindowHost
{
    private readonly Dictionary<string, object> _open = new(StringComparer.Ordinal);

    /// <summary>按创建顺序记录的已打开实例标识。</summary>
    public List<string> Created { get; } = new();

    /// <summary>被置前的实例标识序列。</summary>
    public List<string> Activated { get; } = new();

    /// <summary>当前已打开的实例标识。</summary>
    public IReadOnlyCollection<string> OpenInstanceIds => _open.Keys;

    /// <inheritdoc />
    public void Open(string instanceId, string instanceName, Window? owner)
    {
        if (_open.ContainsKey(instanceId))
        {
            Activated.Add(instanceId);
            return;
        }

        _open[instanceId] = new object();
        Created.Add(instanceId);
    }

    /// <inheritdoc />
    public void Close(string instanceId) => _open.Remove(instanceId);
}

/// <summary>主视图模型对投屏入口的接线测试。</summary>
public class MainViewModelProjectionTests : IDisposable
{
    private const string InstanceId = "win-proj-01";
    private const string ImageRef = "bliss-os-17-x86_64";

    private readonly TempRoot _temp = new();

    public void Dispose() => _temp.Cleanup();

    private static InstanceSpec BuildSpec() => new()
    {
        Id = InstanceId,
        DisplayName = "投屏接线实例",
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
        IProjectionWindowHost? host)
    {
        SpecLoader loader = XBeeSpec.TestSpec();
        return new MainViewModel(
            repository,
            manager,
            new Dictionary<string, ImageSpec>(StringComparer.Ordinal),
            new DiagnosticsExporter(loader),
            new TerminologyCatalog(loader.LoadTerminology()),
            projectionWindows: host);
    }

    [Fact]
    public async Task RunningInstanceCanOpenProjection()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        var manager = CreateManager(repository);
        var host = new StubProjectionWindowHost();

        MainViewModel viewModel = CreateViewModel(repository, manager, host);
        await viewModel.RefreshAsync();

        // 未运行时投屏入口必须禁用，投屏要求实例处于运行态。
        Assert.False(viewModel.CanOpenProjection);

        await viewModel.StartSelectedAsync();

        Assert.True(viewModel.CanOpenProjection);
        Assert.True(viewModel.OpenProjectionCommand.CanExecute(null));

        viewModel.OpenProjectionCommand.Execute(null);

        Assert.Single(host.Created);
        Assert.Equal(InstanceId, host.OpenInstanceIds.Single());
    }

    [Fact]
    public async Task StoppingInstanceClosesItsProjectionWindow()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        var manager = CreateManager(repository);
        var host = new StubProjectionWindowHost();

        MainViewModel viewModel = CreateViewModel(repository, manager, host);
        await viewModel.RefreshAsync();
        await viewModel.StartSelectedAsync();
        viewModel.OpenProjectionCommand.Execute(null);

        Assert.Single(host.Created);

        await viewModel.StopSelectedAsync();

        // 实例停止后画面不再更新，投屏窗口必须随之关闭。
        Assert.Empty(host.OpenInstanceIds);
        Assert.False(viewModel.CanOpenProjection);
    }

    [Fact]
    public async Task ProjectionEntryIsAbsentWithoutHost()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        var manager = CreateManager(repository);
        MainViewModel viewModel = CreateViewModel(repository, manager, host: null);

        await viewModel.RefreshAsync();
        await viewModel.StartSelectedAsync();

        // 未装配投屏窗口宿主时不提供投屏入口，避免界面给出点了没反应的按钮。
        Assert.False(viewModel.CanOpenProjection);
        Assert.False(viewModel.OpenProjectionCommand.CanExecute(null));
    }

    [Fact]
    public void ProjectionButtonTextUsesTerminology()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        var manager = CreateManager(repository);
        MainViewModel viewModel =
            CreateViewModel(repository, manager, new StubProjectionWindowHost());

        TerminologyCatalog terms = new(XBeeSpec.TestSpec().LoadTerminology());
        Assert.Contains(terms.Projection, viewModel.OpenProjectionText, StringComparison.Ordinal);
    }
}

/// <summary>呈现面与画面之间的坐标换算测试。</summary>
public class ProjectionViewportTests
{
    private static readonly ScreenGeometry Landscape = new(1024, 768);

    [Fact]
    public void SameAspectSurfaceFillsEntireArea()
    {
        ProjectionSurfaceRect rect = ProjectionViewport.Fit(Landscape, 1024, 768);

        Assert.Equal(0, rect.OffsetX, 3);
        Assert.Equal(0, rect.OffsetY, 3);
        Assert.Equal(1024, rect.Width, 3);
        Assert.Equal(768, rect.Height, 3);
    }

    [Fact]
    public void WiderSurfaceKeepsAspectRatioAndCenters()
    {
        ProjectionSurfaceRect rect = ProjectionViewport.Fit(Landscape, 2048, 768);

        // 高度受限，画面按高度铺满，左右各留一半边。
        Assert.Equal(768, rect.Height, 3);
        Assert.Equal(1024, rect.Width, 3);
        Assert.Equal(512, rect.OffsetX, 3);
        Assert.Equal(0, rect.OffsetY, 3);
    }

    [Fact]
    public void SurfaceCenterMapsToFrameCenter()
    {
        ProjectionSurfaceRect rect = ProjectionViewport.Fit(Landscape, 2048, 768);
        InputPoint? point = ProjectionViewport.ToFramePoint(Landscape, rect, 1024, 384);

        Assert.NotNull(point);

        // 画面按高度等比铺满，落位矩形为 512..1536；正中点换算回画面正中。
        Assert.Equal(512, point!.Value.X, 1);
        Assert.Equal(384, point.Value.Y, 1);
    }

    [Fact]
    public void LetterboxAreaMapsToNothing()
    {
        ProjectionSurfaceRect rect = ProjectionViewport.Fit(Landscape, 2048, 768);

        // 左侧留边不对应画面内任何位置，投递过去没有意义。
        Assert.Null(ProjectionViewport.ToFramePoint(Landscape, rect, 10, 384));
    }

    [Fact]
    public void FrameTopLeftMapsToOrigin()
    {
        ProjectionSurfaceRect rect = ProjectionViewport.Fit(Landscape, 1024, 768);
        InputPoint? point = ProjectionViewport.ToFramePoint(Landscape, rect, 0, 0);

        Assert.NotNull(point);
        Assert.Equal(0, point!.Value.X, 3);
        Assert.Equal(0, point.Value.Y, 3);
    }

    [Fact]
    public void PortraitFrameInLandscapeSurfaceKeepsAspectRatio()
    {
        var portrait = new ScreenGeometry(600, 800);
        ProjectionSurfaceRect rect = ProjectionViewport.Fit(portrait, 1024, 768);

        Assert.Equal(768, rect.Height, 3);
        Assert.Equal(576, rect.Width, 3);
        Assert.Equal(224, rect.OffsetX, 3);
    }

    [Fact]
    public void InvalidGeometryProducesNoMapping()
    {
        var invalid = new ScreenGeometry(0, 0);

        Assert.Equal(default, ProjectionViewport.Fit(invalid, 1024, 768));
        Assert.Null(ProjectionViewport.ToFramePoint(invalid, ProjectionViewport.Fit(invalid, 1024, 768), 1, 1));
    }

    [Fact]
    public void WindowSizingKeepsAspectRatioWithinAvailableArea()
    {
        (double Width, double Height)? fitted = ProjectionWindowSizing.Fit(Landscape, 512, 512);

        Assert.NotNull(fitted);
        Assert.Equal(512, fitted!.Value.Width, 3);
        Assert.Equal(384, fitted.Value.Height, 3);
    }

    [Fact]
    public void WindowSizingRefusesWhenNoAreaAvailable()
    {
        Assert.Null(ProjectionWindowSizing.Fit(Landscape, 0, 0));
    }
}