using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Identity;
using XBear.Core.Instances;
using XBear.Core.Qemu;
using XBear.Core.Spec;
using XBear.Core.Tests.Spec;

namespace XBear.Core.Tests.Identity;

/// <summary>
/// 对抗取证级检测隐藏栈 P1 期工程 Core 层端到端单元测试。
/// 覆盖机型族画像、设备身份生成、通道下发、迁移自动补齐与检测面指标台账。
/// </summary>
public sealed class AntiForensicsP1Tests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "xbear-antiforensics-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private TombstoneStore CreateTombstones() =>
        new(Path.Combine(_tempDirectory, "tombstones.json"));

    private FileInstanceRepository CreateRepository(TombstoneStore tombstones) =>
        new(Path.Combine(_tempDirectory, "instances"), tombstones);

    #region 1. 机型族画像 (A-02) 与跨字段不变量

    [Fact]
    public void BuiltInProfilesPassAllThreeCrossFieldInvariants()
    {
        Assert.NotEmpty(DeviceProfileCatalog.Profiles);
        Assert.Equal(3, DeviceProfileCatalog.Profiles.Count);

        foreach (var profile in DeviceProfileCatalog.Profiles)
        {
            // 不变量 1：brand 与 manufacturer 同源
            Assert.StartsWith(profile.Brand, profile.Manufacturer, StringComparison.Ordinal);

            // 不变量 2：productFamily 是 productName 的前缀
            Assert.StartsWith(profile.ProductFamily, profile.ProductName, StringComparison.Ordinal);

            // 不变量 3：cpuVersion 不得含 ARM 系列词，与 x86_64 架构自洽
            Assert.DoesNotMatch(
                @"(?i)(arm|aarch64|cortex|exynos|snapdragon|dimensity|kirin|tegra|qualcomm)",
                profile.CpuVersion);
            Assert.Equal(CpuArchitectures.X86_64, profile.CpuArchitecture);

            // MAC OUI 格式
            Assert.Matches(@"^([0-9a-f]{2}:){2}[0-9a-f]{2}$", profile.MacOui);
            Assert.False(string.IsNullOrWhiteSpace(profile.Device));
            Assert.False(string.IsNullOrWhiteSpace(profile.ProductSku));
        }
    }

    [Fact]
    public void DeviceProfileRejectsViolatingBrandManufacturerInvariant()
    {
        var ex = Assert.Throws<XBearException>(() =>
            DeviceProfile.Create(
                profileId: "xb-bad-brand",
                brand: "Xion",
                manufacturer: "Othercorp Devices",
                productName: "Xion Vibe 1",
                productVersion: "1.0",
                productFamily: "Xion Vibe",
                productSku: "SKU-1",
                device: "vibe1",
                cpuManufacturer: "Xion",
                cpuVersion: "XQ-100",
                cpuArchitecture: CpuArchitectures.X86_64,
                boardManufacturer: "Xion Devices",
                boardProduct: "MB-1",
                chassisManufacturer: "Xion Devices",
                chassisAsset: "ASSET-1",
                macOui: "3a:1f:0c",
                profileSeed: 1));

        Assert.Equal(ErrorCategory.Identity, ex.Category);
        Assert.Contains("不同源", ex.Message);
    }

    [Fact]
    public void DeviceProfileRejectsViolatingFamilyPrefixInvariant()
    {
        var ex = Assert.Throws<XBearException>(() =>
            DeviceProfile.Create(
                profileId: "xb-bad-family",
                brand: "Xion",
                manufacturer: "Xion Devices",
                productName: "Xion Apex 12",
                productVersion: "1.0",
                productFamily: "Xion Vibe",
                productSku: "SKU-1",
                device: "apex12",
                cpuManufacturer: "Xion",
                cpuVersion: "XQ-100",
                cpuArchitecture: CpuArchitectures.X86_64,
                boardManufacturer: "Xion Devices",
                boardProduct: "MB-1",
                chassisManufacturer: "Xion Devices",
                chassisAsset: "ASSET-1",
                macOui: "3a:1f:0c",
                profileSeed: 1));

        Assert.Equal(ErrorCategory.Identity, ex.Category);
        Assert.Contains("不是机型销售名", ex.Message);
    }

    [Theory]
    [InlineData("Snapdragon 888")]
    [InlineData("Cortex-A78")]
    [InlineData("Kirin 9000")]
    [InlineData("Exynos 2100")]
    [InlineData("Dimensity 1200")]
    public void DeviceProfileRejectsArmCpuOnX86Architecture(string armCpu)
    {
        var ex = Assert.Throws<XBearException>(() =>
            DeviceProfile.Create(
                profileId: "xb-bad-cpu",
                brand: "Xion",
                manufacturer: "Xion Devices",
                productName: "Xion Vibe 1",
                productVersion: "1.0",
                productFamily: "Xion Vibe",
                productSku: "SKU-1",
                device: "vibe1",
                cpuManufacturer: "Xion",
                cpuVersion: armCpu,
                cpuArchitecture: CpuArchitectures.X86_64,
                boardManufacturer: "Xion Devices",
                boardProduct: "MB-1",
                chassisManufacturer: "Xion Devices",
                chassisAsset: "ASSET-1",
                macOui: "3a:1f:0c",
                profileSeed: 1));

        Assert.Equal(ErrorCategory.Identity, ex.Category);
        Assert.Contains("矛盾", ex.Message);
    }

    [Fact]
    public void ProfileCatalogDeterministicSelectionIsStable()
    {
        var first = DeviceProfileCatalog.Select(0);
        var second = DeviceProfileCatalog.Select(1);
        var third = DeviceProfileCatalog.Select(2);
        var looped = DeviceProfileCatalog.Select(3);

        Assert.NotEqual(first.ProfileId, second.ProfileId);
        Assert.NotEqual(second.ProfileId, third.ProfileId);
        Assert.Equal(first.ProfileId, looped.ProfileId);

        // 实例标识散列映射稳定
        int ordA = DeviceProfileCatalog.OrdinalForInstanceId("inst-win-01");
        int ordB = DeviceProfileCatalog.OrdinalForInstanceId("inst-win-01");
        Assert.Equal(ordA, ordB);
    }

    #endregion

    #region 2. MAC 派生 (A-06) 与生成约束

    [Fact]
    public void MacDerivationIsDeterministicAndUnicastNonZero()
    {
        var profile = DeviceProfileCatalog.Select(0);
        const string serialNo = "XBSN0A1B2C3D4E5F";

        string mac1 = MacAddress.Derive(profile, serialNo);
        string mac2 = MacAddress.Derive(profile, serialNo);

        Assert.Equal(mac1, mac2);
        Assert.StartsWith(profile.MacOui, mac1, StringComparison.Ordinal);

        // 格式合法、非全零、末字节为偶数（单播）
        Assert.True(MacAddress.IsValid(mac1));
        Assert.NotEqual(MacAddress.AllZeroMac, mac1);

        var lastOctet = Convert.ToInt32(mac1.Split(':')[5], 16);
        Assert.Equal(0, lastOctet & 1); // 最低位必须为 0（单播）
    }

    [Theory]
    [InlineData("00:00:00:00:00:00", false)] // 全零被拒
    [InlineData("3a:1f:0c:5e:b8:d1", false)] // 末位奇数（组播位被置 1，被拒）
    [InlineData("01:1f:0c:5e:b8:d0", false)] // 首位组播被拒
    [InlineData("3a:1f:0c:5e:b8:d0", true)]  // 合法单播
    [InlineData("3a:1f:0c:9d:2b:74", true)]  // 合法单播
    [InlineData("not-a-mac", false)]
    public void MacAddressValidationRejectsMulticastAndAllZero(string mac, bool expectedValid)
    {
        Assert.Equal(expectedValid, MacAddress.IsValid(mac));
    }

    [Fact]
    public void CryptoGeneratorProducesCompliantIdentityWithMacAndProfile()
    {
        var generator = new CryptoDeviceIdentityGenerator();
        var profile = DeviceProfileCatalog.Select(0);
        var identity = generator.Next(profile, 0);

        Assert.StartsWith(CryptoDeviceIdentityGenerator.SerialNoPrefix, identity.SerialNo);
        Assert.Equal(16, identity.SerialNo!.Length);
        Assert.Matches("^[0-9a-f]{16}$", identity.AndroidId);
        Assert.True(Luhn.IsValid(identity.Imei));
        Assert.Equal(profile.ProfileId, identity.DeviceProfileId);
        Assert.True(MacAddress.IsValid(identity.Mac));
        Assert.Equal(IdentityContractVersions.Current, identity.IdentityVersion);

        // 格式校验通过
        DeviceIdentityFactory.ValidateFormat(identity);
    }

    #endregion

    #region 3. 身份默认接通与迁移自动补齐 (A-04)

    [Fact]
    public async Task ExistingInstanceWithoutIdentityIsAutomaticallyBackfilledOnLoad()
    {
        var tombstones = CreateTombstones();
        var repository = CreateRepository(tombstones);

        // 构造历史老配置：无 deviceIdentity 段
        var legacy = new InstanceSpec
        {
            Id = "legacy-inst-01",
            DisplayName = "历史实例",
            Platform = "windows",
            ImageRef = "bliss-os-17-x86_64",
            Resources = new ResourceSpec { CpuCores = 4, MemoryMB = 4096 },
            DeviceIdentity = null,
        };

        // 直接写入磁盘跳过仓储保存逻辑
        string filePath = Path.Combine(repository.Directory, legacy.Id + ".json");
        Directory.CreateDirectory(repository.Directory);
        File.WriteAllText(filePath, JsonSerializer.Serialize(legacy, SpecLoader.SerializerOptions));

        // 加载时自动补齐
        var loaded = await repository.GetAsync(legacy.Id);
        Assert.NotNull(loaded);
        Assert.NotNull(loaded.DeviceIdentity);
        Assert.False(string.IsNullOrWhiteSpace(loaded.DeviceIdentity.SerialNo));
        Assert.False(string.IsNullOrWhiteSpace(loaded.DeviceIdentity.AndroidId));
        Assert.False(string.IsNullOrWhiteSpace(loaded.DeviceIdentity.Mac));
        Assert.False(string.IsNullOrWhiteSpace(loaded.DeviceIdentity.DeviceProfileId));
        Assert.True(loaded.DeviceIdentity.HasRequiredFields());

        // 回写确认：磁盘文件已更新含 deviceIdentity
        string updatedJson = File.ReadAllText(filePath);
        Assert.Contains("deviceIdentity", updatedJson, StringComparison.Ordinal);
        Assert.Contains(loaded.DeviceIdentity.SerialNo!, updatedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncompleteIdentityWithOnlySerialNoIsCompletedWithoutLosingSerial()
    {
        var tombstones = CreateTombstones();
        var repository = CreateRepository(tombstones);

        const string existingSerial = "XBSN112233445566";
        var partial = new InstanceSpec
        {
            Id = "partial-inst-01",
            DisplayName = "半截身份实例",
            Platform = "windows",
            ImageRef = "bliss-os-17-x86_64",
            Resources = new ResourceSpec { CpuCores = 4, MemoryMB = 4096 },
            DeviceIdentity = new DeviceIdentity
            {
                SerialNo = existingSerial,
                AndroidId = null,
                Imei = null,
                Mac = null,
            },
        };

        string filePath = Path.Combine(repository.Directory, partial.Id + ".json");
        Directory.CreateDirectory(repository.Directory);
        File.WriteAllText(filePath, JsonSerializer.Serialize(partial, SpecLoader.SerializerOptions));

        var loaded = await repository.GetAsync(partial.Id);
        Assert.NotNull(loaded);
        Assert.NotNull(loaded.DeviceIdentity);
        // 原有序列号必须保留
        Assert.Equal(existingSerial, loaded.DeviceIdentity.SerialNo);
        // 缺失的 Android ID 与 MAC 自动补齐
        Assert.False(string.IsNullOrWhiteSpace(loaded.DeviceIdentity.AndroidId));
        Assert.False(string.IsNullOrWhiteSpace(loaded.DeviceIdentity.Mac));
    }

    [Fact]
    public async Task CompleteIdentityIsNotModifiedOnSubsequentLoads()
    {
        var tombstones = CreateTombstones();
        var repository = CreateRepository(tombstones);
        var factory = new DeviceIdentityFactory(repository, tombstones);

        var identity = await factory.GenerateAsync();
        var spec = new InstanceSpec
        {
            Id = "stable-inst-01",
            DisplayName = "完整实例",
            Platform = "windows",
            ImageRef = "bliss-os-17-x86_64",
            Resources = new ResourceSpec { CpuCores = 4, MemoryMB = 4096 },
            DeviceIdentity = identity,
        };

        await repository.SaveAsync(spec);

        var firstRead = await repository.GetAsync(spec.Id);
        var secondRead = await repository.GetAsync(spec.Id);

        Assert.Equal(firstRead!.DeviceIdentity!.SerialNo, secondRead!.DeviceIdentity!.SerialNo);
        Assert.Equal(firstRead.DeviceIdentity.AndroidId, secondRead.DeviceIdentity.AndroidId);
        Assert.Equal(firstRead.DeviceIdentity.Mac, secondRead.DeviceIdentity.Mac);
    }

    #endregion

    #region 4. QemuArgBuilder 全量伪装参数断言 (A-01/06/07/09/10)

    [Fact]
    public void BuildStartArgumentsEmitsFullSmbiosEntriesCoveringAllAuditKeys()
    {
        var builder = new QemuArgBuilder();
        var profile = DeviceProfileCatalog.Select(0);
        var spec = new InstanceSpec
        {
            Id = "inst-smbios-test",
            Resources = new ResourceSpec { CpuCores = 4, MemoryMB = 4096 },
            DeviceIdentity = new DeviceIdentity
            {
                SerialNo = "XBSN9E4D7C2B6A08",
                AndroidId = "6b02ce81af54d937",
                Imei = "861234567890123",
                Mac = "3a:1f:0c:5e:b8:d0",
                DeviceProfileId = profile.ProfileId,
            },
        };

        var ports = new AllocatedPorts(5555, 5556, 5900);
        var args = builder.BuildStartArguments(spec, @"D:\dummy.qcow2", ports);

        // 收集全部 -smbios 参数
        var smbiosEntries = new List<string>();
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-smbios")
            {
                smbiosEntries.Add(args[i + 1]);
            }
        }

        // 必须下发全部 5 个条目：type=0, 1, 2, 3, 4
        Assert.Equal(5, smbiosEntries.Count);

        var type0 = smbiosEntries.Single(e => e.StartsWith("type=0,", StringComparison.Ordinal));
        Assert.Contains("vendor=" + profile.Manufacturer + " Firmware", type0);
        Assert.Contains("version=" + profile.ProductVersion + ".0", type0);

        var type1 = smbiosEntries.Single(e => e.StartsWith("type=1,", StringComparison.Ordinal));
        Assert.Contains("manufacturer=" + profile.Manufacturer, type1);
        Assert.Contains("product=" + profile.ProductName, type1);
        Assert.Contains("version=" + profile.ProductVersion, type1);
        Assert.Contains("family=" + profile.ProductFamily, type1);
        Assert.Contains("sku=" + profile.ProductSku, type1);
        Assert.Contains("serial=XBSN9E4D7C2B6A08", type1);

        var type2 = smbiosEntries.Single(e => e.StartsWith("type=2,", StringComparison.Ordinal));
        Assert.Contains("manufacturer=" + profile.BoardManufacturer, type2);
        Assert.Contains("product=" + profile.BoardProduct, type2);
        Assert.Contains("serial=XBSN9E4D7C2B6A08", type2);

        var type3 = smbiosEntries.Single(e => e.StartsWith("type=3,", StringComparison.Ordinal));
        Assert.Contains("manufacturer=" + profile.ChassisManufacturer, type3);
        Assert.Contains("serial=XBSN9E4D7C2B6A08", type3);

        var type4 = smbiosEntries.Single(e => e.StartsWith("type=4,", StringComparison.Ordinal));
        Assert.Contains("manufacturer=" + profile.CpuManufacturer, type4);
        Assert.Contains("version=" + profile.CpuVersion, type4);
    }

    [Fact]
    public void BuildStartArgumentsCustomizesNetdevSubnetGatewayDnsDhcpstart()
    {
        var builder = new QemuArgBuilder();
        var spec = new InstanceSpec
        {
            Id = "inst-net-custom",
            Resources = new ResourceSpec { CpuCores = 4, MemoryMB = 4096 },
            Network = new NetworkSpec
            {
                FixedAddress = "192.168.88.25/24",
                Exposure = "loopback",
            },
        };

        var ports = new AllocatedPorts(5555, 5556, 5900);
        var args = builder.BuildStartArguments(spec, @"D:\dummy.qcow2", ports);

        int netdevIdx = args.ToList().IndexOf("-netdev");
        Assert.True(netdevIdx >= 0);
        string netdev = args[netdevIdx + 1];

        // 断言网段、网关、DNS、dhcpstart、搜索域
        Assert.Contains("net=192.168.88.25/24", netdev);
        Assert.Contains("host=192.168.88.2", netdev);
        Assert.Contains("dns=192.168.88.3", netdev);
        Assert.Contains("dhcpstart=192.168.88.20", netdev);
        Assert.Contains("domainname=local", netdev);
        Assert.Contains("dnssearch=local", netdev);
        Assert.Contains("hostfwd=tcp:127.0.0.1:5555-:5555", netdev);
    }

    [Fact]
    public void BuildStartArgumentsPassesMacToVirtioNetDevice()
    {
        var builder = new QemuArgBuilder();
        const string mac = "3a:1f:0c:5e:b8:d0";
        var spec = new InstanceSpec
        {
            Id = "inst-mac-test",
            Resources = new ResourceSpec { CpuCores = 4, MemoryMB = 4096 },
            DeviceIdentity = new DeviceIdentity
            {
                SerialNo = "XBSN9E4D7C2B6A08",
                AndroidId = "6b02ce81af54d937",
                Mac = mac,
            },
        };

        var ports = new AllocatedPorts(5555, 5556, 5900);
        var args = builder.BuildStartArguments(spec, @"D:\dummy.qcow2", ports);

        int netDeviceIdx = args.ToList().IndexOf("virtio-net-pci,netdev=net0,mac=" + mac);
        Assert.True(netDeviceIdx >= 0, $"未找到带 mac 的网卡设备参数：{string.Join(" ", args)}");
    }

    [Fact]
    public void BuildStartArgumentsAppendsHypervisorOffAndVirtualizationSuppressionToCpu()
    {
        var builder = new QemuArgBuilder();
        var spec = new InstanceSpec
        {
            Id = "inst-cpu-flags",
            Resources = new ResourceSpec { CpuCores = 4, MemoryMB = 4096, CpuModel = "Skylake-Client" },
        };

        var ports = new AllocatedPorts(5555, 5556, 5900);
        var args = builder.BuildStartArguments(spec, @"D:\dummy.qcow2", ports);

        int cpuIdx = args.ToList().IndexOf("-cpu");
        Assert.True(cpuIdx >= 0);
        string cpu = args[cpuIdx + 1];

        Assert.StartsWith("Skylake-Client,", cpu, StringComparison.Ordinal);
        Assert.Contains("hypervisor=off", cpu);
        Assert.Contains("invpcid=off", cpu);
        Assert.Contains("fsgsbase=off", cpu);
        Assert.Contains("kvmclock=off", cpu);
        Assert.Contains("kvm-pv-eoi=off", cpu);
    }

    #endregion

    #region 5. KernelCommandLine 默认接通与条件通道

    [Fact]
    public void KernelCommandLineConnectsSerialNoAndAndroidIdByDefault()
    {
        var identity = new DeviceIdentity
        {
            SerialNo = "XBSN0123456789AB",
            AndroidId = "a3f9c2e17b8d4056",
            Imei = null, // 无 telephony 能力时为 null
        };

        string cmdline = KernelCommandLine.Build(identity, VerificationState.Untested);

        Assert.Contains("androidboot.serialno=XBSN0123456789AB", cmdline);
        Assert.Contains("androidboot.android_id=a3f9c2e17b8d4056", cmdline);
        Assert.DoesNotContain("androidboot.imei=", cmdline);
    }

    [Fact]
    public void KernelCommandLineIncludesImeiWhenTelephonyImeiIsPresent()
    {
        var identity = new DeviceIdentity
        {
            SerialNo = "XBSN0123456789AB",
            AndroidId = "a3f9c2e17b8d4056",
            Imei = "861234567890123",
        };

        string cmdline = KernelCommandLine.Build(identity, VerificationState.Pass);

        Assert.Contains("androidboot.serialno=XBSN0123456789AB", cmdline);
        Assert.Contains("androidboot.android_id=a3f9c2e17b8d4056", cmdline);
        Assert.Contains("androidboot.imei=861234567890123", cmdline);
        Assert.Contains("androidboot.writable_system=1", cmdline);
    }

    #endregion

    #region 6. 检测面指标台账完整性 (附录 C)

    [Fact]
    public void DetectionSurfaceLedgerHas43MetricsAndEveryMetricHasValidAuditRefs()
    {
        var surface = SpecTestHost.Loader.LoadDetectionSurface();

        Assert.Equal("1.0.0", surface.Version);
        var allMetrics = surface.AllMetrics().ToList();
        Assert.Equal(43, allMetrics.Count);

        var validAuditRefPattern = new Regex(@"^(?:F|H|M|L|G|X)-[0-9]{2}[a-z]?$", RegexOptions.CultureInvariant);

        foreach (var metric in allMetrics)
        {
            Assert.False(string.IsNullOrWhiteSpace(metric.Id));
            Assert.False(string.IsNullOrWhiteSpace(metric.Name));
            Assert.False(string.IsNullOrWhiteSpace(metric.Command));
            Assert.False(string.IsNullOrWhiteSpace(metric.Expected));

            // 每项指标的审计引用非空且全部符合编号命名格式
            Assert.NotEmpty(metric.AuditRefs);
            foreach (var auditRef in metric.AuditRefs)
            {
                Assert.Matches(validAuditRefPattern, auditRef);
            }

            // 状态为 untested 时 measuredAt 必须为 null
            if (metric.Status == DetectionMetricStatus.Untested)
            {
                Assert.Null(metric.MeasuredAt);
            }
        }
    }

    [Fact]
    public void DetectionSurfacePassesFullSchemaAndStructuralValidation()
    {
        var json = SpecTestHost.Loader.ReadText(
            Path.Combine(SpecTestHost.Loader.SpecRoot, SpecLoader.DetectionSurfaceFileName));

        var result = SpecTestHost.Validator.ValidateDetectionSurface(json);
        Assert.True(result.IsValid, result.DescribeErrors());
    }

    #endregion
}