using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Projection;
using XBear.Core.Spec;
using XBear.Core.Tests.Input;

namespace XBear.Core.Tests.Projection;

/// <summary>
/// 投屏服务集成与生命周期测试，覆盖端口绑定、会话状态流转、画面流式消费与输入注入。
/// </summary>
public sealed class ProjectionServiceTests
{
    private const string InstanceId = "win-test-01";
    private const int Width = 100;
    private const int Height = 100;

    [Fact]
    public async Task 实例未运行_启动投屏抛出State异常()
    {
        var service = new ProjectionService(
            portResolver: id => new AllocatedPorts(5555, 5556, 5900),
            stateResolver: id => InstanceState.Stopped);

        XBearException ex = await Assert.ThrowsAsync<XBearException>(
            () => service.StartAsync(InstanceId));

        Assert.Equal(ErrorCategory.State, ex.Category);
        Assert.Contains("未处于运行态", ex.Message);
    }

    [Fact]
    public async Task 端口未分配_启动投屏抛出Port异常()
    {
        var service = new ProjectionService(
            portResolver: id => null,
            stateResolver: id => InstanceState.Running);

        XBearException ex = await Assert.ThrowsAsync<XBearException>(
            () => service.StartAsync(InstanceId));

        Assert.Equal(ErrorCategory.Port, ex.Category);
        Assert.Contains("未找到实例", ex.Message);
    }

    [Fact]
    public async Task 正常启动会话_建立连接并激活会话_可读首帧快照()
    {
        byte[] framePixels = new byte[Width * Height * 4];
        for (int i = 0; i < Width * Height; i++)
        {
            framePixels[i * 4 + 0] = 50;  // B
            framePixels[i * 4 + 1] = 100; // G
            framePixels[i * 4 + 2] = 150; // R
            framePixels[i * 4 + 3] = 0xFF; // A
        }

        var frameReadyTcs = new TaskCompletionSource<bool>();
        await using var vncServer = FakeVncServer.CreateStandard(Width, Height, "proj-desktop", async conn =>
        {
            await conn.ReceiveFramebufferUpdateRequestAsync();
            await conn.SendRawFrameAsync(Width, Height, framePixels);
            await conn.ReceiveFramebufferUpdateRequestAsync();
            frameReadyTcs.TrySetResult(true);
        });

        var qmp = new RecordingQmpClient();
        var ports = new AllocatedPorts(5555, 5556, vncServer.Port);

        await using var service = new ProjectionService(
            portResolver: id => ports,
            stateResolver: id => InstanceState.Running,
            qmpClientFactory: () => qmp);

        // 取帧纪律为「先补发挂起请求、再上报画面到达」，
        // 因此判定首帧就绪必须以画面到达事件为准，而不是以服务端收到请求为准。
        var arrivedTcs = new TaskCompletionSource<ProjectionFrameArrivedEventArgs>();
        ProjectionFrameArrivedEventArgs? arrivedArgs = null;
        service.FrameArrived += (s, e) =>
        {
            arrivedArgs = e;
            arrivedTcs.TrySetResult(e);
        };

        ProjectionSession session = await service.StartAsync(InstanceId, targetFps: 30);
        Assert.Equal(ProjectionState.Active, session.State);
        Assert.Equal(InstanceId, session.InstanceRef);
        Assert.True(session.ToSpec().IsPresenting());

        await arrivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await frameReadyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(arrivedArgs);
        Assert.Equal(InstanceId, arrivedArgs.InstanceId);
        Assert.Equal(Width, arrivedArgs.Geometry.Width);
        Assert.Equal(Height, arrivedArgs.Geometry.Height);

        ProjectionFrame? frame = service.GetLatestFrame(InstanceId);
        Assert.NotNull(frame);
        Assert.Equal(Width, frame.Width);
        Assert.Equal(Height, frame.Height);
        var p = frame.GetPixel(0, 0);
        Assert.Equal(50, p.B);
        Assert.Equal(100, p.G);
        Assert.Equal(150, p.R);
        Assert.Equal(0xFF, p.A);

        // 验证重复启动被拦截
        XBearException duplicateEx = await Assert.ThrowsAsync<XBearException>(
            () => service.StartAsync(InstanceId));
        Assert.Equal(ErrorCategory.State, duplicateEx.Category);

        await service.StopAsync(InstanceId);
        Assert.Equal(ProjectionState.Stopped, session.State);
        Assert.Null(service.GetSession(InstanceId));
    }

    [Fact]
    public async Task 分辨率变化_正确触发事件并更新会话规格()
    {
        const int w1 = 50;
        const int h1 = 50;
        const int w2 = 80;
        const int h2 = 60;

        byte[] pixels1 = new byte[w1 * h1 * 4];
        byte[] pixels2 = new byte[w2 * h2 * 4];

        var resizeTcs = new TaskCompletionSource<bool>();
        await using var vncServer = FakeVncServer.CreateStandard(w1, h1, "resize-desktop", async conn =>
        {
            await conn.ReceiveFramebufferUpdateRequestAsync();
            await conn.SendRawFrameAsync(w1, h1, pixels1);

            await conn.ReceiveFramebufferUpdateRequestAsync();
            await conn.SendResolutionChangeAsync(w2, h2);
            await conn.SendRawFrameAsync(w2, h2, pixels2);

            await conn.ReceiveFramebufferUpdateRequestAsync();
            resizeTcs.TrySetResult(true);
        });

        var qmp = new RecordingQmpClient();
        var ports = new AllocatedPorts(5555, 5556, vncServer.Port);

        await using var service = new ProjectionService(
            portResolver: id => ports,
            stateResolver: id => InstanceState.Running,
            qmpClientFactory: () => qmp);

        ProjectionResolutionChangedEventArgs? resChanged = null;
        service.ResolutionChanged += (s, e) => resChanged = e;

        ProjectionSession session = await service.StartAsync(InstanceId);
        await resizeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(resChanged);
        Assert.Equal(w1, resChanged.Previous.Width);
        Assert.Equal(h1, resChanged.Previous.Height);
        Assert.Equal(w2, resChanged.Current.Width);
        Assert.Equal(h2, resChanged.Current.Height);

        Assert.Equal(w2, session.Width);
        Assert.Equal(h2, session.Height);

        await service.StopAsync(InstanceId);
    }

    [Fact]
    public async Task 连续喂帧_实测帧率可在服务上直接查得()
    {
        const int w = 10;
        const int h = 10;
        byte[] pixels = new byte[w * h * 4];

        var feedTcs = new TaskCompletionSource<bool>();
        await using var vncServer = FakeVncServer.CreateStandard(w, h, "fps-desktop", async conn =>
        {
            // 连续发送 5 帧
            for (int i = 0; i < 5; i++)
            {
                await conn.ReceiveFramebufferUpdateRequestAsync();
                await conn.SendRawFrameAsync(w, h, pixels);
            }
            feedTcs.TrySetResult(true);
        });

        var qmp = new RecordingQmpClient();
        var ports = new AllocatedPorts(5555, 5556, vncServer.Port);

        await using var service = new ProjectionService(
            portResolver: id => ports,
            stateResolver: id => InstanceState.Running,
            qmpClientFactory: () => qmp,
            options: new ProjectionServiceOptions
            {
                FrameRateWindow = TimeSpan.FromSeconds(5),
            });

        ProjectionSession session = await service.StartAsync(InstanceId);
        await feedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        double? fps = service.GetMeasuredFps(InstanceId);
        Assert.NotNull(fps);
        Assert.True(fps.Value > 0);
        Assert.Equal(fps, session.MeasuredFps);

        await service.StopAsync(InstanceId);
    }

    [Fact]
    public async Task 输入注入_经由QMP成功发送且坐标换算正确()
    {
        byte[] pixels = new byte[Width * Height * 4];
        await using var vncServer = FakeVncServer.CreateStandard(Width, Height, "input-desktop", async conn =>
        {
            await conn.ReceiveFramebufferUpdateRequestAsync();
            await conn.SendRawFrameAsync(Width, Height, pixels);
        });

        var qmp = new RecordingQmpClient();
        var ports = new AllocatedPorts(5555, 5556, vncServer.Port);

        await using var service = new ProjectionService(
            portResolver: id => ports,
            stateResolver: id => InstanceState.Running,
            qmpClientFactory: () => qmp);

        await service.StartAsync(InstanceId);

        ProjectionInputInjector? injector = service.GetInputInjector(InstanceId);
        Assert.NotNull(injector);

        // 注入指针点击
        await injector.TapAsync(new InputPoint(Width - 1, Height - 1));
        Assert.Equal(1, qmp.CountOf("input-send-event"));

        // 注入按键
        await injector.KeyAsync(AndroidKey.Home, KeyAction.Press);
        Assert.Equal(2, qmp.CountOf("input-send-event"));

        await service.StopAsync(InstanceId);
    }

    [Fact]
    public async Task 服务端断线_会话自动收敛为Stopped并传递异常()
    {
        var disconnectedTcs = new TaskCompletionSource<ProjectionSessionStateChangedEventArgs>();
        await using var vncServer = FakeVncServer.CreateStandard(Width, Height, "crash-desktop", async conn =>
        {
            await conn.ReceiveFramebufferUpdateRequestAsync();
            // 直接关闭套接字
            conn.Disconnect();
        });

        var qmp = new RecordingQmpClient();
        var ports = new AllocatedPorts(5555, 5556, vncServer.Port);

        await using var service = new ProjectionService(
            portResolver: id => ports,
            stateResolver: id => InstanceState.Running,
            qmpClientFactory: () => qmp);

        service.SessionStateChanged += (s, e) =>
        {
            if (e.CurrentState == ProjectionState.Stopped)
            {
                disconnectedTcs.TrySetResult(e);
            }
        };

        ProjectionSession session = await service.StartAsync(InstanceId);

        var stoppedEvent = await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProjectionState.Stopped, stoppedEvent.CurrentState);
        Assert.NotNull(stoppedEvent.Error);
        Assert.IsType<XBearException>(stoppedEvent.Error);

        Assert.Equal(ProjectionState.Stopped, session.State);
        Assert.Null(service.GetSession(InstanceId));
    }

    [Fact]
    public async Task 会话存续期间_启动保活损伤通道且停止会话时被清理()
    {
        byte[] pixels = new byte[Width * Height * 4];
        await using var vncServer = FakeVncServer.CreateStandard(Width, Height, "keepalive-desktop", async conn =>
        {
            await conn.ReceiveFramebufferUpdateRequestAsync();
            await conn.SendRawFrameAsync(Width, Height, pixels);
            while (!conn.CancellationToken.IsCancellationRequested)
            {
                await Task.Delay(20, conn.CancellationToken);
            }
        });

        var qmp = new RecordingQmpClient();
        var adb = new RecordingAdbClient();
        var ports = new AllocatedPorts(5555, 5556, vncServer.Port);

        await using var service = new ProjectionService(
            portResolver: id => ports,
            stateResolver: id => InstanceState.Running,
            qmpClientFactory: () => qmp,
            options: new ProjectionServiceOptions
            {
                KeepaliveEnabled = true,
                KeepaliveFrequencyHz = 30,
                KeepaliveStartupProbeTimeout = TimeSpan.FromSeconds(2),
            },
            adbClientFactory: () => adb);

        await service.StartAsync(InstanceId);

        ProjectionStreamDiagnostics? running = service.GetStreamDiagnostics(InstanceId);
        Assert.NotNull(running);
        Assert.True(running.KeepaliveActive);
        Assert.Equal(30, running.KeepaliveFrequencyHz);
        Assert.Equal(RecordingAdbClient.Pid, running.KeepalivePid);
        Assert.Null(running.KeepaliveError);

        Assert.Contains(ProjectionKeepalive.BuildStartCommand(), adb.ShellCommands);
        Assert.Contains($"mkdir -p {ProjectionKeepalive.GuestDirectory}", adb.ShellCommands);
        Assert.Equal(ProjectionKeepalive.BuildScript(30), adb.PushedScripts[ProjectionKeepalive.GuestScriptPath]);

        await service.StopAsync(InstanceId);

        Assert.Contains(ProjectionKeepalive.BuildStopCommand(), adb.ShellCommands);
        Assert.Null(service.GetStreamDiagnostics(InstanceId));
        Assert.True(adb.Disposed);
    }

    [Fact]
    public async Task 关闭保活开关_会话照常建立且不向实例发起任何adb命令()
    {
        byte[] pixels = new byte[Width * Height * 4];
        await using var vncServer = FakeVncServer.CreateStandard(Width, Height, "nokeepalive-desktop", async conn =>
        {
            await conn.ReceiveFramebufferUpdateRequestAsync();
            await conn.SendRawFrameAsync(Width, Height, pixels);
        });

        var qmp = new RecordingQmpClient();
        var adb = new RecordingAdbClient();
        var ports = new AllocatedPorts(5555, 5556, vncServer.Port);

        await using var service = new ProjectionService(
            portResolver: id => ports,
            stateResolver: id => InstanceState.Running,
            qmpClientFactory: () => qmp,
            options: new ProjectionServiceOptions { KeepaliveEnabled = false },
            adbClientFactory: () => adb);

        ProjectionSession session = await service.StartAsync(InstanceId);
        Assert.Equal(ProjectionState.Active, session.State);
        Assert.Empty(adb.ShellCommands);

        ProjectionStreamDiagnostics? diagnostics = service.GetStreamDiagnostics(InstanceId);
        Assert.NotNull(diagnostics);
        Assert.False(diagnostics.KeepaliveActive);
        Assert.Equal(0, diagnostics.KeepaliveFrequencyHz);

        await service.StopAsync(InstanceId);
    }
}
