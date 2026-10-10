using XBear.Core.Abstractions;
using XBear.Core.Identity;
using XBear.Core.Instances;
using XBear.Core.Snapshots;
using XBear.Core.Spec;
using XBear.Core.Tests.Instances;
using XBear.Core.Tests.Spec;

namespace XBear.Core.Tests.Snapshots;

/// <summary>
/// 快照元数据仓库的单元测试。全部落在临时目录，不启动 QEMU。
/// </summary>
public class SnapshotStoreTests : IDisposable
{
    private const string InstanceId = "win-main-01";
    private const string ImageRef = "bliss-os-17-x86_64";

    private readonly string _temp = Path.Combine(
        Path.GetTempPath(),
        "xbear-snapshot-store-" + Guid.NewGuid().ToString("N"));

    public SnapshotStoreTests() => Directory.CreateDirectory(_temp);

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

    private SnapshotStore CreateStore() => new(Path.Combine(_temp, InstanceId), SpecTestHost.Validator);

    private static SnapshotSpec BuildSpec(string id, SnapshotState state = SnapshotState.Ready, string? parentRef = null) => new()
    {
        SchemaVersion = "1.0.0",
        Id = id,
        DisplayName = "刷模块前",
        InstanceRef = InstanceId,
        ImageRef = ImageRef,
        CreatedAt = "2026-10-07T14:05:30+08:00",
        State = state,
        ParentRef = parentRef,
        Note = "刷入 Magisk 前留的还原点",
        PlatformConfig = System.Text.Json.JsonDocument.Parse(
            """{"diskChain":{"depth":1,"layers":[{"path":"instances/win-main-01/win-main-01.qcow2","role":"top"}]}}"""
        ).RootElement.Clone(),
    };

    [Fact]
    public async Task SaveThenReadRoundTripsEveryContractField()
    {
        SnapshotStore store = CreateStore();
        SnapshotSpec spec = BuildSpec("snap-win-main-01-0001");

        await store.SaveAsync(spec);

        SnapshotSpec? loaded = await store.GetAsync(spec.Id);

        Assert.NotNull(loaded);
        Assert.Equal(spec.SchemaVersion, loaded!.SchemaVersion);
        Assert.Equal(spec.Id, loaded.Id);
        Assert.Equal(spec.DisplayName, loaded.DisplayName);
        Assert.Equal(spec.InstanceRef, loaded.InstanceRef);
        Assert.Equal(spec.ImageRef, loaded.ImageRef);
        Assert.Equal(spec.CreatedAt, loaded.CreatedAt);
        Assert.Equal(SnapshotState.Ready, loaded.State);
        Assert.Equal(spec.Note, loaded.Note);
        Assert.NotNull(loaded.PlatformConfig);
        Assert.Equal("top", loaded.PlatformConfig!.Value.GetProperty("diskChain").GetProperty("layers")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task PersistedMetadataPassesSharedContractSchema()
    {
        SnapshotStore store = CreateStore();
        SnapshotSpec spec = BuildSpec("snap-win-main-01-0002", SnapshotState.Ready, "snap-win-main-01-0001");

        await store.SaveAsync(spec);

        string json = await File.ReadAllTextAsync(Path.Combine(store.Directory, spec.Id + ".json"));
        ValidationResult result = SpecTestHost.Validator.ValidateSnapshot(json);

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public async Task MinimalMetadataWithoutOptionalFieldsPassesSharedContractSchema()
    {
        SnapshotStore store = CreateStore();
        var spec = new SnapshotSpec
        {
            Id = "snap-win-main-01-0001",
            InstanceRef = InstanceId,
            ImageRef = ImageRef,
            CreatedAt = "2026-10-07T11:20:00+08:00",
            State = SnapshotState.Ready,
        };

        await store.SaveAsync(spec);

        string json = await File.ReadAllTextAsync(Path.Combine(store.Directory, spec.Id + ".json"));
        ValidationResult result = SpecTestHost.Validator.ValidateSnapshot(json);

        Assert.True(result.IsValid, result.DescribeErrors());
        Assert.DoesNotContain("displayName", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListReturnsSnapshotsOrderedByCreationMoment()
    {
        SnapshotStore store = CreateStore();
        await store.SaveAsync(BuildSpec("snap-win-main-01-0002"));
        await store.SaveAsync(BuildSpec("snap-win-main-01-0001"));

        IReadOnlyList<SnapshotSpec> list = await store.ListAsync();

        Assert.Equal(2, list.Count);
        Assert.All(list, spec => Assert.Equal("2026-10-07T14:05:30+08:00", spec.CreatedAt));
        Assert.Equal(
            new[] { "snap-win-main-01-0001", "snap-win-main-01-0002" },
            list.Select(spec => spec.Id).ToArray());
    }

    [Fact]
    public async Task DeleteRemovesMetadataAndTombstonesTheIdentifier()
    {
        SnapshotStore store = CreateStore();
        await store.SaveAsync(BuildSpec("snap-win-main-01-0001"));

        bool removed = await store.DeleteAsync("snap-win-main-01-0001");

        Assert.True(removed);
        Assert.Null(await store.GetAsync("snap-win-main-01-0001"));
        Assert.True(await store.IsTombstonedAsync("snap-win-main-01-0001"));
    }

    [Fact]
    public async Task AllocatedIdentifierNeverReusesTombstonedOne()
    {
        SnapshotStore store = CreateStore();
        string first = await store.AllocateIdAsync(InstanceId);
        await store.DeleteAsync(first);

        string second = await store.AllocateIdAsync(InstanceId);

        Assert.NotEqual(first, second);
        Assert.False(await store.IsTombstonedAsync(second));
    }

    [Fact]
    public async Task AllocatedIdentifierStaysWithinContractShape()
    {
        SnapshotStore store = new(Path.Combine(_temp, "a-very-long-instance-identifier-for-shape-check"));

        string id = await store.AllocateIdAsync("a-very-long-instance-identifier-for-shape-check", CancellationToken.None);

        Assert.True(SnapshotStore.IsIdShape(id));
        Assert.True(id.Length <= SnapshotStore.IdMaxLength);
        Assert.StartsWith("snap-", id, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AB")]
    [InlineData("Snap_1")]
    [InlineData("-snap-1")]
    [InlineData("snap-1-")]
    [InlineData("")]
    public async Task IllegalIdentifierIsRejected(string id)
    {
        SnapshotStore store = CreateStore();
        var spec = BuildSpec("snap-win-main-01-0001");
        spec.Id = id;

        await XBearExceptionSnapshotAssert.ThrowsSpecAsync(async () => await store.SaveAsync(spec));
    }

    [Fact]
    public async Task DisplayNameBeyondContractLimitIsRejected()
    {
        SnapshotStore store = CreateStore();
        var spec = BuildSpec("snap-win-main-01-0001");
        spec.DisplayName = new string('a', SnapshotStore.DisplayNameMaxLength + 1);

        await XBearExceptionSnapshotAssert.ThrowsSpecAsync(async () => await store.SaveAsync(spec));
    }

    [Fact]
    public async Task TimestampWithoutTimezoneOffsetIsRejected()
    {
        SnapshotStore store = CreateStore();
        var spec = BuildSpec("snap-win-main-01-0001");
        spec.CreatedAt = "2026-10-07T14:05:30";

        await XBearExceptionSnapshotAssert.ThrowsSpecAsync(async () => await store.SaveAsync(spec));
    }

    [Fact]
    public void PlatformConfigMustBeAnObject()
    {
        var spec = BuildSpec("snap-win-main-01-0001");
        spec.PlatformConfig = System.Text.Json.JsonDocument.Parse("\"not-an-object\"").RootElement.Clone();

        XBearExceptionSnapshotAssert.ThrowsSpec(() => SnapshotStore.EnsureShape(spec));
    }

    [Fact]
    public void StateMachineAllowsOnlyCreatingToTerminal()
    {
        Assert.True(SnapshotStateTransition.IsAllowed(SnapshotState.Creating, SnapshotState.Ready));
        Assert.True(SnapshotStateTransition.IsAllowed(SnapshotState.Creating, SnapshotState.Failed));

        Assert.False(SnapshotStateTransition.IsAllowed(SnapshotState.Ready, SnapshotState.Creating));
        Assert.False(SnapshotStateTransition.IsAllowed(SnapshotState.Ready, SnapshotState.Failed));
        Assert.False(SnapshotStateTransition.IsAllowed(SnapshotState.Failed, SnapshotState.Ready));
        Assert.False(SnapshotStateTransition.IsAllowed(SnapshotState.Ready, SnapshotState.Ready));
    }

    [Fact]
    public void IllegalStateTransitionThrowsStateCategory()
    {
        XBearExceptionSnapshotAssert.ThrowsState(() =>
            SnapshotStateTransition.EnsureAllowed(
                "snap-win-main-01-0001",
                SnapshotState.Ready,
                SnapshotState.Creating));
    }

    [Fact]
    public void FormattedTimestampMatchesContractShape()
    {
        string text = SnapshotStore.FormatTimestamp(new DateTimeOffset(2026, 10, 7, 14, 5, 30, TimeSpan.FromHours(8)));

        Assert.Equal("2026-10-07T14:05:30+08:00", text);
    }
}

/// <summary>快照相关的异常断言助手，统一断言技术分类。</summary>
internal static class XBearExceptionSnapshotAssert
{
    /// <summary>
    /// 断言异步操作抛出配置错误类别的异常。
    /// </summary>
    /// <param name="action">待执行的操作。</param>
    public static async Task ThrowsSpecAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<Core.Diagnostics.XBearException>(action);
        Assert.Equal(Core.Diagnostics.ErrorCategory.Spec, exception.Category);
    }

    /// <summary>
    /// 断言同步操作抛出配置错误类别的异常。
    /// </summary>
    /// <param name="action">待执行的操作。</param>
    public static void ThrowsSpec(Action action) =>
        Assert.Equal(Core.Diagnostics.ErrorCategory.Spec, Assert.Throws<Core.Diagnostics.XBearException>(action).Category);

    /// <summary>
    /// 断言同步操作抛出状态错误类别的异常。
    /// </summary>
    /// <param name="action">待执行的操作。</param>
    public static void ThrowsState(Action action) =>
        Assert.Equal(Core.Diagnostics.ErrorCategory.State, Assert.Throws<Core.Diagnostics.XBearException>(action).Category);
}