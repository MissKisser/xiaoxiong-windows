using System.IO;
using XBear.App;
using XBear.App.Presentation;
using XBear.App.Services;
using XBear.App.ViewModels;
using XBear.Core.Abstractions;
using XBear.Core.Identity;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 实例标识的装配与生成测试。创建实例必须发放独立标识并随实例配置落盘；
/// 删除过的实例标识进入墓碑后，后续实例不得再用。
/// </summary>
public class DeviceIdentityWiringTests : IDisposable
{
    private const string ImageRef = "bliss-os-17-x86_64";

    private readonly TempRoot _temp = new();

    /// <summary>清理测试产生的临时目录。</summary>
    public void Dispose() => _temp.Cleanup();

    private (FileInstanceRepository Repository, string DataRoot) CreateRepository()
    {
        string dataRoot = _temp.New("data");
        var repository = new FileInstanceRepository(
            Path.Combine(dataRoot, AppComposition.InstancesDirectoryName),
            new TombstoneStore(Path.Combine(dataRoot, AppComposition.TombstoneFileName)));

        return (repository, dataRoot);
    }

    private TombstoneStore TombstonesAt(string dataRoot) =>
        new(Path.Combine(dataRoot, AppComposition.TombstoneFileName));

    private InstanceEditorViewModel CreateEditor(
        IInstanceRepository repository,
        IDeviceIdentityFactory? identityFactory = null)
    {
        SpecLoader loader = XBeeSpec.TestSpec();
        var editor = new InstanceEditorViewModel(
            repository,
            new Dictionary<string, ImageSpec>(StringComparer.Ordinal),
            new AuditLog(Path.Combine(_temp.New("audit"), "audit.log")),
            new TerminologyCatalog(loader.LoadTerminology()),
            identityFactory)
        {
            DisplayName = "标识用例实例",
            ImageRef = ImageRef,
        };

        return editor;
    }

    [Fact]
    public async Task CreateAssignsIdentityAndWritesItIntoTheInstanceFile()
    {
        (FileInstanceRepository repository, _) = CreateRepository();

        Assert.True(await CreateEditor(repository).CreateAsync());

        InstanceSpec saved = Assert.Single(await repository.ListAsync());
        DeviceIdentity identity = Assert.IsType<DeviceIdentity>(saved.DeviceIdentity);

        // 三个字段都必须落在契约区间内，且 IMEI 通过 Luhn 校验。
        DeviceIdentityFactory.ValidateFormat(identity);
        Assert.StartsWith(
            CryptoDeviceIdentityGenerator.SerialNoPrefix,
            identity.SerialNo,
            StringComparison.Ordinal);

        // 标识必须真的写进实例配置文件，而不是只留在内存里。
        string instanceFile = Path.Combine(repository.Directory, saved.Id + ".json");
        string json = File.ReadAllText(instanceFile);

        Assert.Contains("deviceIdentity", json, StringComparison.Ordinal);
        Assert.Contains(identity.SerialNo!, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachCreatedInstanceGetsItsOwnIdentity()
    {
        (FileInstanceRepository repository, _) = CreateRepository();

        Assert.True(await CreateEditor(repository).CreateAsync());
        Assert.True(await CreateEditor(repository).CreateAsync());

        IReadOnlyList<InstanceSpec> saved = await repository.ListAsync();
        Assert.Equal(2, saved.Count);

        DeviceIdentity first = saved[0].DeviceIdentity!;
        DeviceIdentity second = saved[1].DeviceIdentity!;

        // 序列号、Android ID、IMEI 三者都不得重复，否则宿主应用会把两个实例当成同一台设备。
        Assert.NotEqual(first.SerialNo, second.SerialNo);
        Assert.NotEqual(first.AndroidId, second.AndroidId);
        Assert.NotEqual(first.Imei, second.Imei);
    }

    [Fact]
    public async Task DeletedInstanceLeavesTombstoneThatBlocksIdentityReuse()
    {
        (FileInstanceRepository repository, string dataRoot) = CreateRepository();

        Assert.True(await CreateEditor(repository).CreateAsync());
        InstanceSpec saved = Assert.Single(await repository.ListAsync());
        DeviceIdentity retired = saved.DeviceIdentity!;

        await repository.DeleteAsync(saved.Id);

        // 删除动作把标识写入墓碑，与标识工厂读的是同一份文件。
        Assert.True(await TombstonesAt(dataRoot)
            .IsIdentityTakenAsync(retired.SerialNo, retired.AndroidId, retired.Imei));

        Assert.True(await CreateEditor(repository).CreateAsync());
        DeviceIdentity fresh = Assert.Single(await repository.ListAsync()).DeviceIdentity!;

        Assert.NotEqual(retired.SerialNo, fresh.SerialNo);
        Assert.NotEqual(retired.AndroidId, fresh.AndroidId);
        Assert.NotEqual(retired.Imei, fresh.Imei);
    }

    [Fact]
    public async Task CreationRetriesWhenGeneratedIdentityCollidesWithTombstone()
    {
        (FileInstanceRepository repository, string dataRoot) = CreateRepository();

        var retired = new DeviceIdentity
        {
            SerialNo = "XBSN00C0FFEE001",
            AndroidId = "00c0ffee00c0ffee",
            Imei = "861234567890127",
        };

        await TombstonesAt(dataRoot).AddAsync("inst-gone", retired);

        // 首次产出即撞上墓碑中的标识，工厂必须重新生成而不是把冲突值写进实例配置。
        var factory = new DeviceIdentityFactory(
            repository,
            TombstonesAt(dataRoot),
            new SequenceDeviceIdentityGenerator(retired, new DeviceIdentity
            {
                SerialNo = "XBSN00C0FFEE002",
                AndroidId = "00c0ffee00c0ffe2",
                Imei = "861234567890135",
            }));

        Assert.True(await CreateEditor(repository, factory).CreateAsync());

        DeviceIdentity issued = Assert.Single(await repository.ListAsync()).DeviceIdentity!;
        Assert.NotEqual(retired.SerialNo, issued.SerialNo);
        Assert.NotEqual(retired.AndroidId, issued.AndroidId);
        Assert.NotEqual(retired.Imei, issued.Imei);
    }

    [Fact]
    public async Task CreationFailsWithIdentityErrorWhenGenerationKeepsColliding()
    {
        (FileInstanceRepository repository, string dataRoot) = CreateRepository();

        var retired = new DeviceIdentity
        {
            SerialNo = "XBSN000000000009",
            AndroidId = "9999999999999999",
            Imei = "861234567890127",
        };

        await TombstonesAt(dataRoot).AddAsync("inst-gone", retired);

        var factory = new DeviceIdentityFactory(
            repository,
            TombstonesAt(dataRoot),
            new SequenceDeviceIdentityGenerator(retired),
            maxAttempts: 3);

        InstanceEditorViewModel editor = CreateEditor(repository, factory);

        Assert.False(await editor.CreateAsync());
        Assert.Empty(await repository.ListAsync());
        Assert.Contains("标识", editor.ValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompositionWiresIdentityFactoryBackedByTheSameTombstone()
    {
        AppServices services = AppComposition.Create(
            _temp.New("composition-data"),
            _temp.New("images"),
            XBeeSpec.TestSpec());

        IDeviceIdentityFactory factory = services.IdentityFactory;
        Assert.NotNull(factory);

        DeviceIdentity first = await factory.GenerateAsync();
        DeviceIdentity second = await factory.GenerateAsync();

        DeviceIdentityFactory.ValidateFormat(first);
        DeviceIdentityFactory.ValidateFormat(second);
        Assert.NotEqual(first.SerialNo, second.SerialNo);

        var spec = new InstanceSpec
        {
            Id = "composition-instance",
            DisplayName = "组合根标识用例",
            ImageRef = ImageRef,
            DeviceIdentity = first,
        };

        await services.Repository.SaveAsync(spec);
        await services.Repository.DeleteAsync(spec.Id);

        // 删除动作写入的墓碑必须被同一个工厂读到，否则防复用会被绕过。
        var wired = Assert.IsType<DeviceIdentityFactory>(factory);
        Assert.True(await wired.IsIdentityInUseAsync(first));
    }
}

/// <summary>
/// 按顺序返回预设标识的生成器，用于确定性地触发冲突重生成与冲突耗尽两条路径。
/// 单个预设值会被持续重复返回。
/// </summary>
internal sealed class SequenceDeviceIdentityGenerator : IDeviceIdentityGenerator
{
    private readonly Queue<DeviceIdentity> _queue;
    private readonly bool _repeatLast;

    /// <summary>
    /// 初始化序列生成器。
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
    /// 返回队列中的下一个标识。
    /// </summary>
    /// <returns>预设标识组合。</returns>
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