using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Identity;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.Core.Tests.Instances;

/// <summary>文件实例仓库的读写、删除与原子性测试。</summary>
public sealed class FileInstanceRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "xbear-repo-" + Guid.NewGuid().ToString("N"));

    /// <summary>清理测试产生的临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FileInstanceRepository NewRepository(out TombstoneStore tombstones)
    {
        tombstones = new TombstoneStore(Path.Combine(_root, "tombstones.json"));
        return new FileInstanceRepository(Path.Combine(_root, "instances"), tombstones);
    }

    private static InstanceSpec NewSpec(string id = "inst-01") => new()
    {
        Id = id,
        DisplayName = "主力实例",
        ImageRef = "bliss-os-17-x86_64",
        Resources = new ResourceSpec { MemoryMB = 6144, CpuCores = 6, DiskGB = 64 },
        Network = new NetworkSpec
        {
            FixedAddress = "10.0.2.15",
            Exposure = "loopback",
            PortForwards =
            {
                new PortForward { HostPort = 15555, GuestPort = 5555, Protocol = "tcp", Bind = "loopback" },
            },
            Proxy = new ProxySpec { Type = "http", Host = "127.0.0.1", Port = 7890 },
        },
        DeviceIdentity = new DeviceIdentity
        {
            SerialNo = "XBSN00AABBCCDDEE",
            AndroidId = "a3f9c2e17b8d4056",
            Imei = "861234567890127",
        },
    };

    [Fact]
    public async Task 保存后读回_字段无损()
    {
        FileInstanceRepository repository = NewRepository(out _);
        InstanceSpec original = NewSpec();

        await repository.SaveAsync(original);
        InstanceSpec? loaded = await repository.GetAsync("inst-01");

        Assert.NotNull(loaded);
        Assert.Equal(original.SchemaVersion, loaded!.SchemaVersion);
        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(original.DisplayName, loaded.DisplayName);
        Assert.Equal(original.Platform, loaded.Platform);
        Assert.Equal(original.ImageRef, loaded.ImageRef);
        Assert.Equal(6144, loaded.Resources.MemoryMB);
        Assert.Equal(6, loaded.Resources.CpuCores);
        Assert.Equal(64, loaded.Resources.DiskGB);
        Assert.Equal("10.0.2.15", loaded.Network!.FixedAddress);
        Assert.Single(loaded.Network.PortForwards);
        Assert.Equal(15555, loaded.Network.PortForwards[0].HostPort);
        Assert.Equal(7890, loaded.Network.Proxy!.Port);
        Assert.Equal("XBSN00AABBCCDDEE", loaded.DeviceIdentity!.SerialNo);
        Assert.Equal("a3f9c2e17b8d4056", loaded.DeviceIdentity.AndroidId);
        Assert.Equal("861234567890127", loaded.DeviceIdentity.Imei);
    }

    [Fact]
    public async Task 序列化_字段名与契约一致()
    {
        FileInstanceRepository repository = NewRepository(out _);
        await repository.SaveAsync(NewSpec());

        string json = await File.ReadAllTextAsync(Path.Combine(repository.Directory, "inst-01.json"));
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("schemaVersion", out _));
        Assert.True(root.TryGetProperty("displayName", out _));
        Assert.True(root.TryGetProperty("imageRef", out _));
        Assert.True(root.TryGetProperty("resources", out JsonElement resources));
        Assert.True(resources.TryGetProperty("memoryMB", out _));
        Assert.True(root.TryGetProperty("deviceIdentity", out JsonElement identity));
        Assert.True(identity.TryGetProperty("serialNo", out _));
        Assert.True(identity.TryGetProperty("androidId", out _));
        Assert.True(identity.TryGetProperty("imei", out _));
    }

    [Fact]
    public async Task 读取不存在的实例_返回null而非抛异常()
    {
        FileInstanceRepository repository = NewRepository(out _);

        Assert.Null(await repository.GetAsync("never-existed"));
    }

    [Fact]
    public async Task 目录不存在时_自动创建且列出为空()
    {
        TombstoneStore tombstones = new(Path.Combine(_root, "deep", "nested", "tombstones.json"));
        var repository = new FileInstanceRepository(Path.Combine(_root, "a", "b", "instances"), tombstones);

        Assert.True(Directory.Exists(repository.Directory));
        Assert.Empty(await repository.ListAsync());
    }

    [Fact]
    public async Task 删除后再读_返回null且标识进入墓碑()
    {
        FileInstanceRepository repository = NewRepository(out TombstoneStore tombstones);
        await repository.SaveAsync(NewSpec());

        await repository.DeleteAsync("inst-01");

        Assert.Null(await repository.GetAsync("inst-01"));
        Assert.Empty(await repository.ListAsync());
        Assert.True(await repository.IsTombstonedAsync("inst-01"));
        Assert.True(await tombstones.IsIdentityTakenAsync("XBSN00AABBCCDDEE", "a3f9c2e17b8d4056", "861234567890127"));
    }

    [Fact]
    public async Task 重复删除_幂等且不抛异常()
    {
        FileInstanceRepository repository = NewRepository(out _);
        await repository.SaveAsync(NewSpec());
        await repository.DeleteAsync("inst-01");

        await repository.DeleteAsync("inst-01");

        Assert.True(await repository.IsTombstonedAsync("inst-01"));
    }

    [Fact]
    public async Task 保存多个实例_列出可全部读回()
    {
        FileInstanceRepository repository = NewRepository(out _);
        await repository.SaveAsync(NewSpec("inst-01"));
        await repository.SaveAsync(NewSpec("inst-02"));
        await repository.SaveAsync(NewSpec("inst-03"));

        IReadOnlyList<InstanceSpec> all = await repository.ListAsync();

        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task 保存成功后_不残留临时文件()
    {
        FileInstanceRepository repository = NewRepository(out _);

        await repository.SaveAsync(NewSpec());
        await repository.SaveAsync(NewSpec("inst-02"));

        Assert.Empty(Directory.GetFiles(repository.Directory, "*.tmp"));
        Assert.Equal(2, Directory.GetFiles(repository.Directory, "*.json").Length);
    }

    [Fact]
    public async Task 模拟写入中断_目标文件仍是完整的旧内容且列表不受影响()
    {
        FileInstanceRepository repository = NewRepository(out _);
        await repository.SaveAsync(NewSpec());

        string path = Path.Combine(repository.Directory, "inst-01.json");
        string before = await File.ReadAllTextAsync(path);

        // 模拟进程在原子替换前崩溃：留下一个半截的临时文件，但目标文件未被触碰。
        string tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, "{\"schemaVersion\":\"1.0.0\",\"id\":\"inst-");

        Assert.Equal(before, await File.ReadAllTextAsync(path));

        // 临时文件不应被当作实例配置读入。
        InstanceSpec? loaded = await repository.GetAsync("inst-01");
        Assert.NotNull(loaded);
        Assert.Equal("主力实例", loaded!.DisplayName);
        Assert.Single(await repository.ListAsync());

        File.Delete(tempPath);
    }

    [Fact]
    public async Task 覆盖保存_不产生半截文件()
    {
        FileInstanceRepository repository = NewRepository(out _);
        InstanceSpec spec = NewSpec();
        await repository.SaveAsync(spec);

        spec.DisplayName = "改过名字";
        await repository.SaveAsync(spec);

        Assert.Equal("改过名字", (await repository.GetAsync("inst-01"))!.DisplayName);
        Assert.Empty(Directory.GetFiles(repository.Directory, "*.tmp"));
    }

    [Fact]
    public async Task 目录中存在损坏文件_列表跳过不抛异常()
    {
        FileInstanceRepository repository = NewRepository(out _);
        await repository.SaveAsync(NewSpec());
        await File.WriteAllTextAsync(Path.Combine(repository.Directory, "broken.json"), "{ 这不是合法 JSON");

        IReadOnlyList<InstanceSpec> all = await repository.ListAsync();

        Assert.Single(all);
    }

    [Theory]
    [InlineData("Inst_Upper")]
    [InlineData("-leading-dash")]
    [InlineData("has space")]
    [InlineData("a")]
    public async Task 保存非法实例标识_抛出契约类异常(string id)
    {
        FileInstanceRepository repository = NewRepository(out _);
        InstanceSpec spec = NewSpec();
        spec.Id = id;

        XBearException ex = await Assert.ThrowsAsync<XBearException>(() => repository.SaveAsync(spec));

        Assert.Equal(ErrorCategory.Spec, ex.Category);
        Assert.False(string.IsNullOrWhiteSpace(ex.Remediation));
    }

    [Fact]
    public async Task 删除不存在的实例_仅写墓碑不抛异常()
    {
        FileInstanceRepository repository = NewRepository(out _);

        await repository.DeleteAsync("ghost");

        Assert.True(await repository.IsTombstonedAsync("ghost"));
    }
}