using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Identity;
using XBear.Core.Spec;

namespace XBear.Core.Tests.Identity;

/// <summary>设备标识生成、格式与查重测试。</summary>
public sealed class DeviceIdentityFactoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "xbear-identity-" + Guid.NewGuid().ToString("N"));

    /// <summary>清理测试产生的临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private TombstoneStore NewTombstones() => new(Path.Combine(_root, "tombstones.json"));

    [Fact]
    public async Task 连续生成1000组标识_三字段均无重复()
    {
        TombstoneStore tombstones = NewTombstones();
        var factory = new DeviceIdentityFactory(new EmptyInstanceRepository(), tombstones);

        var serials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var androidIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imeis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < 1000; i++)
        {
            DeviceIdentity identity = await factory.GenerateAsync();
            Assert.NotNull(identity.SerialNo);
            Assert.NotNull(identity.AndroidId);
            Assert.NotNull(identity.Imei);

            Assert.True(serials.Add(identity.SerialNo!), $"序列号重复：{identity.SerialNo}");
            Assert.True(androidIds.Add(identity.AndroidId!), $"Android ID 重复：{identity.AndroidId}");
            Assert.True(imeis.Add(identity.Imei!), $"IMEI 重复：{identity.Imei}");
        }

        Assert.Equal(1000, serials.Count);
        Assert.Equal(1000, androidIds.Count);
        Assert.Equal(1000, imeis.Count);
    }

    [Fact]
    public async Task 生成标识_三个字段长度均落在契约区间()
    {
        TombstoneStore tombstones = NewTombstones();
        var factory = new DeviceIdentityFactory(new EmptyInstanceRepository(), tombstones);

        for (int i = 0; i < 200; i++)
        {
            DeviceIdentity identity = await factory.GenerateAsync();

            Assert.InRange(identity.SerialNo!.Length, 8, 32);
            Assert.InRange(identity.AndroidId!.Length, 16, 32);
            Assert.InRange(identity.Imei!.Length, 14, 16);
        }
    }

    [Fact]
    public void 生成序列号_为XBSN前缀且后接12位大写十六进制()
    {
        string serialNo = CryptoDeviceIdentityGenerator.GenerateSerialNo();

        Assert.Equal(16, serialNo.Length);
        Assert.StartsWith("XBSN", serialNo, StringComparison.Ordinal);
        Assert.All(serialNo[4..], c => Assert.True("0123456789ABCDEF".Contains(c)));
    }

    [Fact]
    public void 生成AndroidId_为16位小写十六进制()
    {
        string androidId = CryptoDeviceIdentityGenerator.GenerateAndroidId();

        Assert.Equal(16, androidId.Length);
        Assert.All(androidId, c => Assert.True("0123456789abcdef".Contains(c)));
    }

    [Fact]
    public async Task 生成标识_撞上在册实例_重新生成且不返回冲突值()
    {
        TombstoneStore tombstones = NewTombstones();
        var colliding = new DeviceIdentity
        {
            SerialNo = "XBSN00C0FFEE001",
            AndroidId = "00c0ffee00c0ffee",
            Imei = "861234567890127",
        };
        var repository = new SingleInstanceRepository(
            new InstanceSpec { Id = "inst-a", DeviceIdentity = colliding });

        // 首次产出即撞上在册实例，第二次产出全新值。
        var generator = new SequenceDeviceIdentityGenerator(colliding, Fresh());
        var factory = new DeviceIdentityFactory(repository, tombstones, generator);

        DeviceIdentity identity = await factory.GenerateAsync();

        Assert.NotEqual(colliding.Imei, identity.Imei);
        Assert.Equal(2, generator.Calls);
    }

    [Fact]
    public async Task 生成标识_撞上墓碑标识_重新生成且不返回冲突值()
    {
        TombstoneStore tombstones = NewTombstones();
        var colliding = new DeviceIdentity
        {
            SerialNo = "XBSN000000000001",
            AndroidId = "1111111111111111",
            Imei = "861234567890127",
        };
        await tombstones.AddAsync("inst-gone", colliding, CancellationToken.None);

        var generator = new SequenceDeviceIdentityGenerator(colliding, Fresh());
        var factory = new DeviceIdentityFactory(new EmptyInstanceRepository(), tombstones, generator);

        DeviceIdentity identity = await factory.GenerateAsync();

        Assert.NotEqual(colliding.SerialNo, identity.SerialNo);
        Assert.Equal(2, generator.Calls);
    }

    [Fact]
    public async Task 生成标识_持续冲突_抛出身份类异常且带处置建议()
    {
        TombstoneStore tombstones = NewTombstones();
        var colliding = new DeviceIdentity
        {
            SerialNo = "XBSN000000000009",
            AndroidId = "9999999999999999",
            Imei = "861234567890127",
        };
        await tombstones.AddAsync("inst-gone", colliding, CancellationToken.None);

        // 生成器每次都返回同一个值，必然持续冲突。
        var factory = new DeviceIdentityFactory(
            new EmptyInstanceRepository(),
            tombstones,
            new SequenceDeviceIdentityGenerator(colliding),
            maxAttempts: 3);

        Diagnostics.XBearException ex = await Assert.ThrowsAsync<Diagnostics.XBearException>(
            () => factory.GenerateAsync());

        Assert.Equal(Diagnostics.ErrorCategory.Identity, ex.Category);
        Assert.False(string.IsNullOrWhiteSpace(ex.Remediation));
    }

    [Fact]
    public void 校验格式_Imei未过Luhn_抛出身份类异常()
    {
        var identity = new DeviceIdentity
        {
            SerialNo = "XBSN000000000001",
            AndroidId = "a3f9c2e17b8d4056",
            Imei = "861234567890124",
        };

        Diagnostics.XBearException ex = Assert.Throws<Diagnostics.XBearException>(
            () => DeviceIdentityFactory.ValidateFormat(identity));

        Assert.Equal(Diagnostics.ErrorCategory.Identity, ex.Category);
    }

    [Fact]
    public async Task 墓碑_删除后标识进入墓碑且新实例撞上即被拒绝()
    {
        TombstoneStore tombstones = NewTombstones();
        var identity = new DeviceIdentity
        {
            SerialNo = "XBSN00AABBCCDDEE",
            AndroidId = "a3f9c2e17b8d4056",
            Imei = "861234567890127",
        };

        await tombstones.AddAsync("inst-x", identity, CancellationToken.None);

        Assert.True(await tombstones.IsTombstonedAsync("inst-x"));
        Assert.True(await tombstones.IsIdentityTakenAsync(identity.SerialNo, identity.AndroidId, identity.Imei));

        // 用同一组标识重建，新实例必须拿到不同的值。
        var factory = new DeviceIdentityFactory(
            new EmptyInstanceRepository(),
            tombstones,
            new SequenceDeviceIdentityGenerator(identity, Fresh()));

        DeviceIdentity regenerated = await factory.GenerateAsync();

        Assert.NotEqual(identity.SerialNo, regenerated.SerialNo);
        Assert.NotEqual(identity.AndroidId, regenerated.AndroidId);
        Assert.NotEqual(identity.Imei, regenerated.Imei);
    }

    [Fact]
    public async Task 墓碑_持久化到磁盘后重新打开仍可查询()
    {
        TombstoneStore tombstones = NewTombstones();
        var identity = new DeviceIdentity { Imei = "861234567890127" };
        await tombstones.AddAsync("inst-p", identity, CancellationToken.None);

        var reopened = new TombstoneStore(tombstones.FilePath);

        Assert.True(await reopened.IsTombstonedAsync("inst-p"));
        Assert.True(await reopened.IsIdentityTakenAsync(null, null, identity.Imei));
        Assert.Single(await reopened.ListAsync());
    }

    private static DeviceIdentity Fresh() => new()
    {
        SerialNo = CryptoDeviceIdentityGenerator.GenerateSerialNo(),
        AndroidId = CryptoDeviceIdentityGenerator.GenerateAndroidId(),
        Imei = CryptoDeviceIdentityGenerator.GenerateImei(),
    };
}

/// <summary>
/// 按顺序返回预设标识的生成器，用于确定性地触发冲突重生成路径。
/// 单个预设值会持续重复返回，用于构造必然冲突的场景。
/// </summary>
internal sealed class SequenceDeviceIdentityGenerator : IDeviceIdentityGenerator
{
    private readonly Queue<DeviceIdentity> _queue;
    private readonly bool _repeatLast;

    /// <summary>
    /// 初始化序列生成器。传入多个标识时按序产出并用尽后转为随机；
    /// 只传一个标识时该值会被持续重复。
    /// </summary>
    /// <param name="identities">依次返回的标识序列。</param>
    public SequenceDeviceIdentityGenerator(params DeviceIdentity[] identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        _queue = new Queue<DeviceIdentity>(identities);
        _repeatLast = identities.Length == 1;
    }

    /// <summary>已产出标识的次数。</summary>
    public int Calls { get; private set; }

    /// <summary>
    /// 返回队列中的下一个标识，队列为空时返回随机标识。
    /// </summary>
    /// <returns>标识组合。</returns>
    public DeviceIdentity Next()
    {
        Calls++;

        if (_queue.Count > 1)
        {
            return _queue.Dequeue();
        }

        if (_queue.Count == 1)
        {
            return _repeatLast ? _queue.Peek() : _queue.Dequeue();
        }

        return new DeviceIdentity
        {
            SerialNo = CryptoDeviceIdentityGenerator.GenerateSerialNo(),
            AndroidId = CryptoDeviceIdentityGenerator.GenerateAndroidId(),
            Imei = CryptoDeviceIdentityGenerator.GenerateImei(),
        };
    }
}

/// <summary>不含任何实例的仓库替身。</summary>
internal sealed class EmptyInstanceRepository : IInstanceRepository
{
    /// <summary>返回空列表。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>空列表。</returns>
    public Task<IReadOnlyList<InstanceSpec>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InstanceSpec>>(Array.Empty<InstanceSpec>());

    /// <summary>始终返回 null。</summary>
    /// <param name="id">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>null。</returns>
    public Task<InstanceSpec?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult<InstanceSpec?>(null);

    /// <summary>不做任何写入。</summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task SaveAsync(InstanceSpec spec, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>不做任何删除。</summary>
    /// <param name="id">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>始终返回 false。</summary>
    /// <param name="id">实例标识。</param>
    /// <returns>false。</returns>
    public Task<bool> IsTombstonedAsync(string id) => Task.FromResult(false);
}

/// <summary>内置单个实例的仓库替身。</summary>
internal sealed class SingleInstanceRepository : IInstanceRepository
{
    private readonly InstanceSpec _spec;

    /// <summary>
    /// 初始化仓库替身。
    /// </summary>
    /// <param name="spec">内置的实例配置。</param>
    public SingleInstanceRepository(InstanceSpec spec)
    {
        _spec = spec;
    }

    /// <summary>返回内置配置。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>单元素列表。</returns>
    public Task<IReadOnlyList<InstanceSpec>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InstanceSpec>>(new[] { _spec });

    /// <summary>按标识返回内置配置。</summary>
    /// <param name="id">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>匹配时返回配置，否则返回 null。</returns>
    public Task<InstanceSpec?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult<InstanceSpec?>(id == _spec.Id ? _spec : null);

    /// <summary>不做任何写入。</summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task SaveAsync(InstanceSpec spec, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>不做任何删除。</summary>
    /// <param name="id">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>始终返回 false。</summary>
    /// <param name="id">实例标识。</param>
    /// <returns>false。</returns>
    public Task<bool> IsTombstonedAsync(string id) => Task.FromResult(false);
}