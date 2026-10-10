using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Identity;
using XBear.Core.Instances;
using XBear.Core.Snapshots;
using XBear.Core.Spec;
using XBear.Core.Tests.Instances;
using XBear.Core.Tests.Spec;

namespace XBear.Core.Tests.Snapshots;

/// <summary>
/// 快照生命周期服务的单元测试。QMP 侧由 <see cref="FakeSnapshotQmpClient"/> 顶替，
/// 实例生命周期由编排器与既有的启动、停止替身驱动，全程不启动 QEMU。
/// </summary>
public class SnapshotServiceTests : IDisposable
{
    private const string InstanceId = "win-snap-01";
    private const string ImageRef = "bliss-os-17-x86_64";

    private readonly string _temp = Path.Combine(
        Path.GetTempPath(),
        "xbear-snapshot-service-" + Guid.NewGuid().ToString("N"));

    private readonly FakeSnapshotQmpClient _qmp = new();
    private readonly FakeQemuLauncher _launcher = new();
    private readonly FileInstanceRepository _repository;
    private readonly InstanceManager _manager;

    public SnapshotServiceTests()
    {
        string imagesRoot = Path.Combine(_temp, "images");
        string instancesRoot = Path.Combine(_temp, "instances");
        Directory.CreateDirectory(imagesRoot);
        Directory.CreateDirectory(instancesRoot);
        File.WriteAllText(Path.Combine(imagesRoot, ImageRef + ".qcow2"), "base");

        var tombstones = new TombstoneStore(Path.Combine(_temp, "tombstones.json"));
        _repository = new FileInstanceRepository(instancesRoot, tombstones);
        _repository.SaveAsync(new InstanceSpec
        {
            Id = InstanceId,
            DisplayName = "快照接线实例",
            Platform = "windows",
            ImageRef = ImageRef,
            Resources = new ResourceSpec { MemoryMB = 2048, CpuCores = 4, DiskGB = 32 },
        }).GetAwaiter().GetResult();

        _manager = new InstanceManager(
            _repository,
            new FakePortAllocator(),
            new FakeQemuArgBuilder(),
            _launcher,
            new FakeQcow2Manager(),
            imagesRoot,
            instancesRoot,
            qmpClientFactory: () => _qmp);

        File.WriteAllText(Path.Combine(instancesRoot, InstanceId + ".qcow2"), "overlay");
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            try
            {
                Directory.Delete(_temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private SnapshotService CreateService() => new(
        _manager,
        _repository,
        () => _qmp,
        SpecTestHost.Validator,
        () => new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.FromHours(8)),
        settleTimeout: TimeSpan.FromMilliseconds(200),
        settleInterval: TimeSpan.FromMilliseconds(10));

    private async Task StartAsync()
    {
        await _manager.StartAsync(InstanceId);
        _qmp.Running = true;
    }

    [Fact]
    public async Task CreateIssuesSavevmAndPersistsReadyMetadata()
    {
        await StartAsync();
        SnapshotService service = CreateService();

        SnapshotSpec created = await service.CreateAsync(InstanceId, "刷模块前", "留个还原点");

        Assert.Equal(SnapshotState.Ready, created.State);
        Assert.Contains("savevm " + created.Id, _qmp.HumanMonitorCommands);
        Assert.Contains("info snapshots", _qmp.HumanMonitorCommands);
        Assert.Equal("2026-10-10T09:30:00+08:00", created.CreatedAt);
        Assert.Equal(ImageRef, created.ImageRef);
        Assert.Equal("刷模块前", created.DisplayName);
        Assert.True(created.IsChainRoot());

        SnapshotSpec? loaded = await service.GetAsync(InstanceId, created.Id);
        Assert.NotNull(loaded);
        Assert.Equal(SnapshotState.Ready, loaded!.State);

        string json = await File.ReadAllTextAsync(Path.Combine(service.GetStore(InstanceId).Directory, created.Id + ".json"));
        Assert.True(SpecTestHost.Validator.ValidateSnapshot(json).IsValid);
    }

    [Fact]
    public async Task CreateLinksTheNewestReadySnapshotAsParent()
    {
        await StartAsync();
        SnapshotService service = CreateService();

        SnapshotSpec first = await service.CreateAsync(InstanceId, "第一份", null);
        SnapshotSpec second = await service.CreateAsync(InstanceId, "第二份", null);

        Assert.Equal(first.Id, second.ParentRef);
        Assert.False(second.IsChainRoot());
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task CreateOnStoppedInstanceIsRejected()
    {
        SnapshotService service = CreateService();

        var exception = await Assert.ThrowsAsync<XBearException>(
            () => service.CreateAsync(InstanceId, "停机时创建", null));

        Assert.Equal(ErrorCategory.State, exception.Category);
        Assert.Empty(_qmp.HumanMonitorCommands);
        Assert.Empty(await service.ListAsync(InstanceId));
    }

    [Fact]
    public async Task CreateWithIllegalNameIsRejected()
    {
        await StartAsync();
        SnapshotService service = CreateService();

        var exception = await Assert.ThrowsAsync<XBearException>(
            () => service.CreateAsync(InstanceId, new string('x', SnapshotStore.DisplayNameMaxLength + 1), null));

        Assert.Equal(ErrorCategory.Spec, exception.Category);
        Assert.DoesNotContain(_qmp.HumanMonitorCommands, line => line.StartsWith("savevm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateFailureMarksMetadataFailedAndStillReports()
    {
        await StartAsync();
        SnapshotService service = CreateService();
        _qmp.HumanMonitorFailure = new XBearException(ErrorCategory.Storage, "磁盘剩余空间不足。");
        _qmp.HumanMonitorFailurePrefix = "savevm";

        var exception = await Assert.ThrowsAsync<XBearException>(
            () => service.CreateAsync(InstanceId, "会失败的一份", null));

        Assert.Equal(ErrorCategory.Storage, exception.Category);

        IReadOnlyList<SnapshotSpec> list = await service.ListAsync(InstanceId);
        SnapshotSpec failed = Assert.Single(list);
        Assert.Equal(SnapshotState.Failed, failed.State);
        Assert.False(failed.IsRestorable());
    }

    [Fact]
    public async Task EnumerationParsesQemuSnapshotTable()
    {
        await StartAsync();
        SnapshotService service = CreateService();
        _qmp.Seed("snap-win-snap-01-0001", "1.5 GiB", "00:02:03.456");

        IReadOnlyList<SnapshotRuntimeEntry> table = await service.ListRuntimeAsync(InstanceId);

        SnapshotRuntimeEntry entry = Assert.Single(table);
        Assert.Equal("snap-win-snap-01-0001", entry.Tag);
        Assert.Equal("1.5 GiB", entry.VmSize);
        Assert.Equal("00:02:03.456", entry.VmClock);
        Assert.True(entry.Matches("snap-win-snap-01-0001"));
    }

    [Fact]
    public async Task EnumerationWithoutRunningInstanceIsRejected()
    {
        SnapshotService service = CreateService();

        var exception = await Assert.ThrowsAsync<XBearException>(() => service.ListRuntimeAsync(InstanceId));

        Assert.Equal(ErrorCategory.State, exception.Category);
    }

    [Fact]
    public async Task RestoreStopsThenRestartsTheInstanceAndLoadsTheSnapshot()
    {
        await StartAsync();
        SnapshotService service = CreateService();
        SnapshotSpec created = await service.CreateAsync(InstanceId, "可恢复的一份", null);

        int startCountBeforeRestore = _launcher.StartCount;

        SnapshotRestoreOutcome outcome = await service.RestoreAsync(InstanceId, created.Id);

        Assert.True(outcome.Restarted);
        Assert.True(outcome.IsRunning);
        Assert.Equal(InstanceState.Running, _manager.GetState(InstanceId));
        Assert.Equal(startCountBeforeRestore + 1, _launcher.StartCount);
        Assert.Contains("loadvm " + created.Id, _qmp.HumanMonitorCommands);
        Assert.Contains(SnapshotService.PauseCommandName, _qmp.QmpCommands);
    }

    [Fact]
    public async Task RestoreOfAStoppedInstanceStillRestartsThroughTheProductPath()
    {
        await StartAsync();
        SnapshotService service = CreateService();
        SnapshotSpec created = await service.CreateAsync(InstanceId, "停机后恢复", null);
        await _manager.StopAsync(InstanceId);

        int startCountBeforeRestore = _launcher.StartCount;

        SnapshotRestoreOutcome outcome = await service.RestoreAsync(InstanceId, created.Id);

        Assert.True(outcome.Restarted);
        Assert.True(outcome.IsRunning);
        Assert.Equal(startCountBeforeRestore + 1, _launcher.StartCount);
    }

    [Fact]
    public async Task RestoreRejectsSnapshotsThatAreNotRestorable()
    {
        await StartAsync();
        SnapshotService service = CreateService();
        _qmp.HumanMonitorFailure = new XBearException(ErrorCategory.Storage, "磁盘剩余空间不足。");
        _qmp.HumanMonitorFailurePrefix = "savevm";

        await Assert.ThrowsAsync<XBearException>(() => service.CreateAsync(InstanceId, "失败的一份", null));
        _qmp.HumanMonitorFailure = null;

        SnapshotSpec failed = Assert.Single(await service.ListAsync(InstanceId));
        Assert.Equal(SnapshotState.Failed, failed.State);

        var exception = await Assert.ThrowsAsync<XBearException>(
            () => service.RestoreAsync(InstanceId, failed.Id));

        Assert.Equal(ErrorCategory.State, exception.Category);

        // 拒绝发生在触碰实例之前，实例不应被停止或重新启动。
        Assert.Equal(InstanceState.Running, _manager.GetState(InstanceId));
        Assert.DoesNotContain(_qmp.HumanMonitorCommands, line => line.StartsWith("loadvm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoreRejectsUnknownSnapshot()
    {
        await StartAsync();
        SnapshotService service = CreateService();

        var exception = await Assert.ThrowsAsync<XBearException>(
            () => service.RestoreAsync(InstanceId, "snap-win-snap-01-9999"));

        Assert.Equal(ErrorCategory.Spec, exception.Category);
    }

    [Fact]
    public async Task RestoreConvergesToStoppedWhenLoadFails()
    {
        await StartAsync();
        SnapshotService service = CreateService();
        SnapshotSpec created = await service.CreateAsync(InstanceId, "恢复会失败", null);

        _qmp.HumanMonitorFailure = new XBearException(ErrorCategory.Protocol, "loadvm: snapshot is corrupt");
        _qmp.HumanMonitorFailurePrefix = "loadvm";

        await Assert.ThrowsAsync<XBearException>(() => service.RestoreAsync(InstanceId, created.Id));

        Assert.Equal(InstanceState.Stopped, _manager.GetState(InstanceId));
    }

    [Fact]
    public async Task DeleteIssuesDelvmAndTombstonesTheIdentifier()
    {
        await StartAsync();
        SnapshotService service = CreateService();
        SnapshotSpec created = await service.CreateAsync(InstanceId, "待删除", null);

        SnapshotDeleteOutcome outcome = await service.DeleteAsync(InstanceId, created.Id);

        Assert.True(outcome.MetadataRemoved);
        Assert.True(outcome.RuntimeRemoved);
        Assert.Contains("delvm " + created.Id, _qmp.HumanMonitorCommands);
        Assert.DoesNotContain(created.Id, _qmp.Tags);
        Assert.Empty(await service.ListAsync(InstanceId));
        Assert.True(await service.GetStore(InstanceId).IsTombstonedAsync(created.Id));
    }

    [Fact]
    public async Task DeleteIsRefusedWhileChildSnapshotsExist()
    {
        await StartAsync();
        SnapshotService service = CreateService();
        SnapshotSpec parent = await service.CreateAsync(InstanceId, "父", null);
        await service.CreateAsync(InstanceId, "子", null);

        var exception = await Assert.ThrowsAsync<XBearException>(() => service.DeleteAsync(InstanceId, parent.Id));

        Assert.Equal(ErrorCategory.State, exception.Category);
        Assert.Contains("子快照", exception.Message, StringComparison.Ordinal);
        Assert.Contains(parent.Id, _qmp.Tags);
    }

    [Fact]
    public async Task DeleteOnAStoppedInstanceRemovesMetadataOnly()
    {
        await StartAsync();
        SnapshotService service = CreateService();
        SnapshotSpec created = await service.CreateAsync(InstanceId, "停机时删除", null);
        await _manager.StopAsync(InstanceId);

        SnapshotDeleteOutcome outcome = await service.DeleteAsync(InstanceId, created.Id);

        Assert.True(outcome.MetadataRemoved);
        Assert.False(outcome.RuntimeRemoved);
        Assert.DoesNotContain(_qmp.HumanMonitorCommands, line => line.StartsWith("delvm", StringComparison.Ordinal));
    }
}