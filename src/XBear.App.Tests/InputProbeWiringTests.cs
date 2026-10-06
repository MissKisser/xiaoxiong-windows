using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>输入通道探测在生命周期编排器上的接线测试。</summary>
public class InputProbeWiringTests : IDisposable
{
    private const string InstanceId = "win-probe-01";
    private const string ImageRef = "bliss-os-17-x86_64";

    private readonly List<InstanceManager> _managers = new();
    private readonly TempRoot _temp = new();

    public void Dispose()
    {
        foreach (InstanceManager manager in _managers)
        {
            // 释放可能仍在运行的实例，避免留下占用端口的替身资源。
            try
            {
                manager.StopAsync(InstanceId).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // 停止失败不影响断言结果。
            }
        }

        _temp.Cleanup();
    }

    private static InstanceSpec BuildSpec() => new()
    {
        Id = InstanceId,
        DisplayName = "探测用实例",
        Platform = "windows",
        ImageRef = ImageRef,
        Resources = new ResourceSpec { MemoryMB = 4096, CpuCores = 4, DiskGB = 64 },
    };

    private InstanceManager CreateManager(
        StubInstanceRepository repository,
        IPortAllocator ports,
        StubQmpClient qmp,
        StubAdbClient adb,
        SpecValidator? validator = null)
    {
        var manager = new InstanceManager(
            repository,
            ports,
            new StubArgBuilder(),
            new StubQemuLauncher(),
            new StubQcow2Manager(),
            _temp.NewImagesRootWithBaseImage(ImageRef),
            _temp.New("instances"),
            validator,
            () => qmp,
            () => adb);

        _managers.Add(manager);
        return manager;
    }

    private InstanceManager CreateRunningManager(
        out StubQmpClient qmp,
        out StubAdbClient adb,
        out StubPortAllocator ports)
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        qmp = new StubQmpClient();
        adb = new StubAdbClient();
        ports = new StubPortAllocator();

        return CreateManager(repository, ports, qmp, adb);
    }

    [Fact]
    public async Task ProbeOnStoppedInstanceReportsUnknownWithoutTouchingNetwork()
    {
        InstanceManager manager = CreateRunningManager(out StubQmpClient qmp, out StubAdbClient adb, out _);

        InputProbeResult result = await manager.ProbeInputChannelAsync(InstanceId);

        // 未运行就没有可连的端口，必须如实报告尚未探测，且不得发起任何连接。
        Assert.Equal(InputChannelKind.Unknown, result.Channel);
        Assert.Equal(0, qmp.ConnectCount);
        Assert.Equal(0, adb.ConnectCount);
    }

    [Fact]
    public async Task ProbeOnUnknownInstanceDoesNotThrow()
    {
        InstanceManager manager = CreateRunningManager(out _, out _, out _);

        // 未登记的实例视为已停止，探测必须给出结论而不是抛异常。
        InputProbeResult result = await manager.ProbeInputChannelAsync("never-registered");

        Assert.Equal(InputChannelKind.Unknown, result.Channel);
    }

    [Fact]
    public async Task ProbeRejectsBlankInstanceId()
    {
        InstanceManager manager = CreateRunningManager(out _, out _, out _);

        await Assert.ThrowsAsync<ArgumentException>(() => manager.ProbeInputChannelAsync("  "));
    }

    [Fact]
    public async Task ProbeReportsNativeWhenQmpPathSucceeds()
    {
        InstanceManager manager = CreateRunningManager(out _, out _, out _);
        await manager.StartAsync(InstanceId);

        InputProbeResult result = await manager.ProbeInputChannelAsync(InstanceId);

        Assert.Equal(InputChannelKind.Native, result.Channel);
        Assert.Null(result.ProjectionFailure);
    }

    [Fact]
    public async Task ProbeFallsBackToProjectionWhenNativeFails()
    {
        InstanceManager manager = CreateRunningManager(out StubQmpClient qmp, out _, out _);
        await manager.StartAsync(InstanceId);

        qmp.ConnectFailure = new XBearException(ErrorCategory.Protocol, "QMP 未响应");

        InputProbeResult result = await manager.ProbeInputChannelAsync(InstanceId);

        // 原生不通时降级到投屏，并保留原生失败原因，不得谎报为原生可用。
        Assert.Equal(InputChannelKind.Projection, result.Channel);
        Assert.Contains("QMP 未响应", result.NativeFailure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeReportsUnavailableWithBothReasonsWhenBothPathsFail()
    {
        InstanceManager manager = CreateRunningManager(out StubQmpClient qmp, out StubAdbClient adb, out _);
        await manager.StartAsync(InstanceId);

        qmp.ConnectFailure = new XBearException(ErrorCategory.Protocol, "QMP 未响应");
        adb.ConnectFailure = new XBearException(ErrorCategory.Protocol, "adb 未响应");

        InputProbeResult result = await manager.ProbeInputChannelAsync(InstanceId);

        // 两条通路都不通时必须给出双方原因，绝不能显示成就绪。
        Assert.Equal(InputChannelKind.Unavailable, result.Channel);
        Assert.Contains("QMP 未响应", result.NativeFailure!, StringComparison.Ordinal);
        Assert.Contains("adb 未响应", result.ProjectionFailure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeConnectsToThatInstancesOwnAllocatedPorts()
    {
        InstanceManager manager = CreateRunningManager(out StubQmpClient qmp, out StubAdbClient adb, out StubPortAllocator ports);
        await manager.StartAsync(InstanceId);

        // 让两条通路都失败，从而两条路径都被走到，端口使用情况才可断言。
        qmp.ConnectFailure = new XBearException(ErrorCategory.Protocol, "QMP 未响应");
        adb.ConnectFailure = new XBearException(ErrorCategory.Protocol, "adb 未响应");

        await manager.ProbeInputChannelAsync(InstanceId);

        // 端口必须取自该实例自己的分配结果，而不是硬编码或跨实例复用。
        AllocatedPorts allocated = ports.Held.Contains(InstanceId)
            ? manager.AllocatedPorts[InstanceId]
            : throw new InvalidOperationException("端口未处于占用状态");

        Assert.Equal(allocated.Qmp, qmp.LastConnectPort);
        Assert.Equal(allocated.Adb, adb.LastConnectPort);
    }

    [Fact]
    public async Task SeparateInstancesProbeAgainstTheirOwnPorts()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());
        repository.Add(new InstanceSpec
        {
            Id = "win-probe-02",
            DisplayName = "第二个实例",
            Platform = "windows",
            ImageRef = ImageRef,
            Resources = new ResourceSpec { MemoryMB = 2048, CpuCores = 2, DiskGB = 32 },
        });

        var firstQmp = new StubQmpClient { ConnectFailure = new XBearException(ErrorCategory.Protocol, "首个实例 QMP 未响应") };
        var firstAdb = new StubAdbClient { ConnectFailure = new XBearException(ErrorCategory.Protocol, "首个实例 adb 未响应") };
        var secondQmp = new StubQmpClient { ConnectFailure = new XBearException(ErrorCategory.Protocol, "第二个实例 QMP 未响应") };
        var secondAdb = new StubAdbClient { ConnectFailure = new XBearException(ErrorCategory.Protocol, "第二个实例 adb 未响应") };

        var ports = new StubPortAllocator();
        var imagesRoot = _temp.NewImagesRootWithBaseImage(ImageRef);
        string instancesRoot = _temp.New("instances");

        var manager = new InstanceManager(
            repository,
            ports,
            new StubArgBuilder(),
            new StubQemuLauncher(),
            new StubQcow2Manager(),
            imagesRoot,
            instancesRoot,
            null,
            () => firstQmp,
            () => firstAdb);
        _managers.Add(manager);

        await manager.StartAsync(InstanceId);
        await manager.StartAsync("win-probe-02");

        AllocatedPorts firstPorts = manager.AllocatedPorts[InstanceId];
        AllocatedPorts secondPorts = manager.AllocatedPorts["win-probe-02"];

        // 两个实例拿到的端口必须不同，通道不得跨实例复用同一份客户端。
        Assert.NotEqual(firstPorts.Qmp, secondPorts.Qmp);
        Assert.NotEqual(firstPorts.Adb, secondPorts.Adb);
    }

    [Fact]
    public async Task StoppingInstanceReleasesProbeChannelClients()
    {
        InstanceManager manager = CreateRunningManager(out StubQmpClient qmp, out StubAdbClient adb, out _);
        await manager.StartAsync(InstanceId);

        qmp.ConnectFailure = new XBearException(ErrorCategory.Protocol, "QMP 未响应");
        adb.ConnectFailure = new XBearException(ErrorCategory.Protocol, "adb 未响应");
        await manager.ProbeInputChannelAsync(InstanceId);

        await manager.StopAsync(InstanceId);

        // 通道持有指向实例端口的连接，端口回收时必须一并释放，否则留下悬挂连接。
        Assert.Equal(1, qmp.DisposeCount);
        Assert.Equal(1, adb.DisposeCount);
    }

    [Fact]
    public async Task RestartingAfterProbeBuildsFreshChannel()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        // 每次工厂调用都产出新客户端，可据此判断通道是否被重建而非沿用已释放的旧通道。
        var createdQmpClients = new List<StubQmpClient>();
        var createdAdbClients = new List<StubAdbClient>();
        Func<StubQmpClient> newQmp = () =>
        {
            var client = new StubQmpClient { ConnectFailure = new XBearException(ErrorCategory.Protocol, "QMP 未响应") };
            createdQmpClients.Add(client);
            return client;
        };
        Func<StubAdbClient> newAdb = () =>
        {
            var client = new StubAdbClient { ConnectFailure = new XBearException(ErrorCategory.Protocol, "adb 未响应") };
            createdAdbClients.Add(client);
            return client;
        };

        var manager = new InstanceManager(
            repository,
            new StubPortAllocator(),
            new StubArgBuilder(),
            new StubQemuLauncher(),
            new StubQcow2Manager(),
            _temp.NewImagesRootWithBaseImage(ImageRef),
            _temp.New("instances"),
            null,
            () => newQmp(),
            () => newAdb());

        _managers.Add(manager);

        await manager.StartAsync(InstanceId);
        await manager.ProbeInputChannelAsync(InstanceId);
        await manager.StopAsync(InstanceId);

        Assert.Single(createdQmpClients);
        Assert.Equal(1, createdAdbClients[0].DisposeCount);

        await manager.StartAsync(InstanceId);
        InputProbeResult result = await manager.ProbeInputChannelAsync(InstanceId);

        // 重启后必须新建通道并可用；若沿用已释放的通道，探测会因对象已释放而失败。
        Assert.Equal(2, createdQmpClients.Count);
        Assert.Equal(InputChannelKind.Unavailable, result.Channel);
    }

    [Fact]
    public async Task FailedStartDoesNotProbeAndDoesNotLeakPorts()
    {
        var repository = new StubInstanceRepository();
        repository.Add(BuildSpec());

        var qmp = new StubQmpClient();
        var adb = new StubAdbClient();
        var ports = new StubPortAllocator();

        var manager = new InstanceManager(
            repository,
            ports,
            new StubArgBuilder(),
            new StubQemuLauncher { StartFailure = new XBearException(ErrorCategory.Process, "QEMU 拉起失败") },
            new StubQcow2Manager(),
            _temp.NewImagesRootWithBaseImage(ImageRef),
            _temp.New("instances"),
            null,
            () => qmp,
            () => adb);
        _managers.Add(manager);

        await Assert.ThrowsAsync<XBearException>(() => manager.StartAsync(InstanceId));

        // 启动失败后端口必须归还，且不得残留输入通道客户端。
        Assert.Empty(ports.Held);
        Assert.Equal(0, qmp.DisposeCount);
    }
}
