using System.IO;
using System.Text;
using XBear.App.Services;
using XBear.Core.Diagnostics;
using XBear.Core.Identity;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// initrd 抗取证定制扩展（Antiforensics）单元测试。
/// 验证 A-03 (build.prop 覆写)、A-05 (Android ID 消费/回读)、A-08 (接口重命名/默认路由)、
/// A-13 (cmdline 收敛内嵌 SRC)、A-14 (KernelSU 痕迹清理)、A-16 (root 去明文持久化)、
/// A-22 (启动链自洽画像) 等新增定制项的独立开关、组合、幂等性与归档合法性。
/// </summary>
public sealed class InitrdCustomizerAntiforensicsTests : IDisposable
{
    private const string HostPublicKey =
        "QAAAtestKeyMaterialForAntiforensicsCustomizationTestsAAAAAAAA== user@host";

    private const string AutoDetectScript = """
        # Auto detect script
        auto_detect()
        {
        	echo "detect"
        }

        auto_detect_alpine()
        {
        	echo "alpine"
        }

        load_modules()
        {
        	:
        }

        """;

    private const string InitScript = """
        #!/bin/busybox sh
        mount_data()
        {
        	mount -t ext4 /dev/sda1 /android/data
        }

        mount_data
        mount_sdcard
        mount_grub
        exec ${SWITCH:-switch_root} /android /init

        """;

    private readonly TempRoot _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Cleanup();

    [Fact]
    public async Task BuildProp覆写_归档新增条目且内容逐键断言()
    {
        string workspace = _temp.New("bp_test");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        DeviceProfile profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);
        string propContent = PropertyOverlayBuilder.BuildBuildPropContent(
            profile,
            serialNo: "XBSN0123456789AB");

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            EnableBuildPropOverlay: true,
            BuildPropContent: propContent);

        var service = new InitrdCustomizerService(keyPath, policy);
        var result = await service.CustomizeAsync(initrdPath);

        Assert.Equal(InitrdCustomizationOutcome.Customized, result.Outcome);
        Assert.Contains(InitrdCustomizerService.BuildPropOverrideEntryName, result.CustomizedEntries);

        var entries = ReadEntries(initrdPath);
        var bpEntry = CpioNewcArchive.Find(entries, InitrdCustomizerService.BuildPropOverrideEntryName);
        Assert.NotNull(bpEntry);

        string writtenProps = Encoding.UTF8.GetString(bpEntry.Content);
        Assert.Contains($"ro.product.brand={profile.Brand}", writtenProps);
        Assert.Contains($"ro.product.manufacturer={profile.Manufacturer}", writtenProps);
        Assert.Contains($"ro.product.model={profile.ProductName}", writtenProps);
        Assert.Contains($"ro.product.system.brand={profile.Brand}", writtenProps);
        Assert.Contains($"ro.product.vendor.manufacturer={profile.Manufacturer}", writtenProps);
        Assert.Contains("ro.serialno=XBSN0123456789AB", writtenProps);
        Assert.Contains("ro.boot.flash.locked=0", writtenProps);
        Assert.Contains("ro.boot.vbmeta.device_state=orange", writtenProps);

        // 验证 init 脚本以追加方式生效：整体替换会抹掉 SDK 版本与 ABI 列表等必需属性，
        // 导致 system_server 无法启动；build.prop 中后出现的同名键覆盖先出现的同名键。
        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.Contains("cat /etc/build.prop.override >> \"$target\"", initText);
        Assert.DoesNotContain("mount --bind /etc/build.prop.override", initText);
        Assert.DoesNotContain("cp -f /etc/build.prop.override \"$target\"", initText);

        // 覆写必须覆盖全部分区 prop 文件：属性服务按 system→vendor→odm→product→system_ext
        // 依次载入，只追加 system 会被其余分区回写，四分区一致性指标将不成立。
        Assert.Contains("/android/vendor/build.prop", initText);
        Assert.Contains("/android/odm/etc/build.prop", initText);
        Assert.Contains("/android/product/etc/build.prop", initText);
        Assert.Contains("/android/system_ext/etc/build.prop", initText);
        Assert.Contains("/android/system/etc/prop.default", initText);
    }

    [Fact]
    public async Task BuildProp覆写_开关旁路时不生成覆盖条目()
    {
        string workspace = _temp.New("bp_bypass");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            EnableBuildPropOverlay: false);

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        var entries = ReadEntries(initrdPath);
        Assert.Null(CpioNewcArchive.Find(entries, InitrdCustomizerService.BuildPropOverrideEntryName));

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.DoesNotContain("cat /etc/build.prop.override >> \"$target\"", initText);
        Assert.DoesNotContain("mount --bind /etc/build.prop.override", initText);
    }

    [Fact]
    public async Task AndroidId消费与回读_脚本断言与无基带不碰IMEI()
    {
        string workspace = _temp.New("aid_test");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        const string targetAid = "a1b2c3d4e5f60718";
        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            EnableAndroidIdProvisioning: true,
            TargetAndroidId: targetAid);

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.Contains($"settings put secure android_id \"{targetAid}\"", initText);
        Assert.Contains("settings get secure android_id", initText);
        Assert.Contains("/android/data/android_id.log", initText);

        // 无基带时不碰 IMEI 语义验证：绝不得包含 imei 相关设置
        Assert.DoesNotContain("imei", initText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AndroidId消费_开关旁路()
    {
        string workspace = _temp.New("aid_bypass");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            EnableAndroidIdProvisioning: false,
            TargetAndroidId: "a1b2c3d4e5f60718");

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.DoesNotContain("settings put secure android_id", initText);
    }

    [Fact]
    public async Task 网络收敛_接口改名与默认路由补齐组合断言()
    {
        string workspace = _temp.New("net_test");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        const string gateway = "172.19.58.2";
        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            EnableNetworkConvergence: true,
            RenameInterfaceToWlan0: true,
            DefaultGateway: gateway);

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.Contains("ip link set eth0 name wlan0", initText);

        // 默认路由必须在开机完成后补齐：mount_data 锚点处网卡尚未取得地址，
        // 此时下发的默认路由会被内核丢弃，实例将始终缺失默认路由。
        Assert.Contains($"ip route add default via {gateway}", initText);
        Assert.Contains("sys.boot_completed", initText);
        Assert.Contains("chroot /android /system/bin/ip route", initText);
    }

    [Fact]
    public async Task 网络收敛_独立开关与旁路断言()
    {
        string workspace = _temp.New("net_bypass");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        // 仅设网关，不改接口名
        var policyGatewayOnly = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            EnableNetworkConvergence: true,
            RenameInterfaceToWlan0: false,
            DefaultGateway: "10.0.2.2");

        var service = new InitrdCustomizerService(keyPath, policyGatewayOnly);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.DoesNotContain("ip link set eth0 name wlan0", initText);
        Assert.Contains("ip route add default via 10.0.2.2", initText);
        Assert.Contains("chroot /android /system/bin/ip route", initText);

        // 网络收敛完全关闭
        string initrdPath2 = WriteInitrd(workspace, initScript: InitScript);
        var policyDisabled = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            EnableNetworkConvergence: false,
            RenameInterfaceToWlan0: true,
            DefaultGateway: "10.0.2.2");

        var serviceDisabled = new InitrdCustomizerService(keyPath, policyDisabled);
        await serviceDisabled.CustomizeAsync(initrdPath2);

        string initText2 = ReadEntryText(initrdPath2, InitrdCustomizerService.InitEntryName);
        Assert.DoesNotContain("ip link set eth0 name wlan0", initText2);
        Assert.DoesNotContain("ip route add default via", initText2);
        Assert.DoesNotContain("chroot /android /system/bin/ip route", initText2);
    }

    [Fact]
    public async Task Root痕迹收敛_去明文持久化仅保留运行时断言()
    {
        string workspace = _temp.New("root_stealth");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            EnableRoot: true,
            RootStealth: true);

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);

        // 不应再向 local.prop 明文追加 service.adb.root
        Assert.DoesNotContain("echo \"service.adb.root=1\" >> /android/data/local.prop", initText);

        // 应包含清理历史明文语句
        Assert.Contains("sed -i '/service\\.adb\\.root/d' /android/data/local.prop", initText);

        // 后台辅助进程中仍通过 setprop 断言，保证 root 功能不损伤
        Assert.Contains("setprop service.adb.root 1", initText);
        Assert.Contains("setprop service.adb.tcp.port 5555", initText);
    }

    [Fact]
    public async Task Root痕迹收敛_关闭时保留明文持久化()
    {
        string workspace = _temp.New("root_stock");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            EnableRoot: true,
            RootStealth: false);

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.Contains("echo \"service.adb.root=1\" >> /android/data/local.prop", initText);
    }

    [Fact]
    public async Task 模块目录低代价清理_清理残缺KernelSU痕迹()
    {
        string workspace = _temp.New("ksu_clean");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            CleanupKernelSuRemnants: true);

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.Contains("rm -f /android/data/adb/ksud", initText);
        Assert.Contains("rm -rf /android/data/adb/ksu", initText);

        // 关闭时无此清理
        string initrdPath2 = WriteInitrd(workspace);
        var policyOff = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            CleanupKernelSuRemnants: false);

        await new InitrdCustomizerService(keyPath, policyOff).CustomizeAsync(initrdPath2);
        string initText2 = ReadEntryText(initrdPath2, InitrdCustomizerService.InitEntryName);
        Assert.DoesNotContain("rm -f /android/data/adb/ksud", initText2);
    }

    [Fact]
    public async Task Cmdline收敛_内嵌SRC等价迁移()
    {
        string workspace = _temp.New("src_test");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        const string expectedSrc = "android-2024-10-12";
        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            EmbeddedSrc: expectedSrc);

        var service = new InitrdCustomizerService(keyPath, policy);
        var result = await service.CustomizeAsync(initrdPath);

        Assert.Contains(InitrdCustomizerService.BootSrcEntryName, result.CustomizedEntries);

        var entries = ReadEntries(initrdPath);
        var srcEntry = CpioNewcArchive.Find(entries, InitrdCustomizerService.BootSrcEntryName);
        Assert.NotNull(srcEntry);
        Assert.Equal(expectedSrc, Encoding.UTF8.GetString(srcEntry.Content));

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.Contains("if [ -z \"$SRC\" ] && [ -f /etc/boot_src ]; then", initText);
        Assert.Contains("SRC=`cat /etc/boot_src 2>/dev/null`", initText);

        // 未配置内嵌 SRC 时无此条目
        string initrdPath2 = WriteInitrd(workspace);
        var policyOff = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            EmbeddedSrc: null);

        await new InitrdCustomizerService(keyPath, policyOff).CustomizeAsync(initrdPath2);
        var entries2 = ReadEntries(initrdPath2);
        Assert.Null(CpioNewcArchive.Find(entries2, InitrdCustomizerService.BootSrcEntryName));
    }

    [Fact]
    public async Task 内嵌SRC恢复_存在根目录探测循环锚点时在其之前注入()
    {
        string workspace = _temp.New("boot_src_root_loop");
        string keyPath = WriteHostKey(workspace);

        string initScriptWithRootLoop = """
            #!/bin/busybox sh
            echo "Detecting..."
            cd /
            while :; do
            	check_root $device && break 2
            done
            mount_data
            mount_sdcard
            exec ${SWITCH:-switch_root} /android /init
            """;

        string initrdPath = WriteInitrd(workspace, initScript: initScriptWithRootLoop);
        const string expectedSrc = "android-2024-10-12";
        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            EmbeddedSrc: expectedSrc);

        var service = new InitrdCustomizerService(keyPath, policy);
        var result = await service.CustomizeAsync(initrdPath);

        Assert.Contains(InitrdCustomizerService.BootSrcEntryName, result.CustomizedEntries);
        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        int srcIndex = initText.IndexOf("SRC=`cat /etc/boot_src 2>/dev/null`", StringComparison.Ordinal);
        int loopIndex = initText.IndexOf("cd /\nwhile :; do", StringComparison.Ordinal);
        Assert.True(srcIndex >= 0);
        Assert.True(loopIndex >= 0);
        Assert.True(srcIndex < loopIndex, "SRC 恢复必须先于根目录探测循环生效以提供 $SRC 参数");
    }

    [Fact]
    public async Task 全套抗取证定制组合与指纹幂等性()
    {
        string workspace = _temp.New("antiforensics_full");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        DeviceProfile profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);
        var identity = new IdentitySpec
        {
            SerialNo = "XBSN0123456789AB",
            AndroidId = "1122334455667788",
            Mac = "3a:1f:0c:12:34:56",
            DeviceProfile = profile.ToSpec()
        };

        var policy = InitrdCustomizationPolicy.CreateAntiforensicsPolicy(
            adbGuestPort: 5555,
            adbPublicKey: HostPublicKey,
            adbApexMountPath: "/apex/com.android.adbd",
            profile: profile,
            identity: identity,
            defaultGateway: "172.19.58.2",
            embeddedSrc: "android-2024-10-12",
            enableRoot: true,
            rootStealth: true,
            enableModuleLoader: true,
            cleanupKernelSuRemnants: true,
            renameInterfaceToWlan0: true,
            enableRenderSpoofing: false);

        var service = new InitrdCustomizerService(keyPath, policy);

        // 第一次定制
        var firstResult = await service.CustomizeAsync(initrdPath);
        Assert.Equal(InitrdCustomizationOutcome.Customized, firstResult.Outcome);
        byte[] firstBytes = File.ReadAllBytes(initrdPath);

        // 验证条目
        Assert.Contains(InitrdCustomizerService.BuildPropOverrideEntryName, firstResult.CustomizedEntries);
        Assert.Contains(InitrdCustomizerService.BootSrcEntryName, firstResult.CustomizedEntries);

        // 第二次定制（相同策略，幂等跳过，字节完全一致）
        var secondResult = await service.CustomizeAsync(initrdPath);
        Assert.Equal(InitrdCustomizationOutcome.AlreadyCustomized, secondResult.Outcome);
        Assert.Equal(firstResult.Fingerprint, secondResult.Fingerprint);
        Assert.Equal(firstBytes, File.ReadAllBytes(initrdPath));

        // 第三次定制（策略变更，触发重新定制）
        var alteredPolicy = policy with { DefaultGateway = "172.19.58.254" };
        var alteredService = new InitrdCustomizerService(keyPath, alteredPolicy);
        var thirdResult = await alteredService.CustomizeAsync(initrdPath);

        Assert.Equal(InitrdCustomizationOutcome.Customized, thirdResult.Outcome);
        Assert.NotEqual(firstResult.Fingerprint, thirdResult.Fingerprint);

        string finalInit = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.Contains("172.19.58.254", finalInit);
    }

    [Fact]
    public async Task Cpio归档格式合法性与重打包往返()
    {
        string workspace = _temp.New("cpio_roundtrip");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            EnableBuildPropOverlay: true,
            BuildPropContent: "ro.product.brand=Test\nro.serialno=XBSN1234567890AB\n",
            EmbeddedSrc: "android-2024-10-12");

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        byte[] rawImage = File.ReadAllBytes(initrdPath);
        InitrdCompression compression = InitrdImageCodec.Detect(rawImage);
        byte[] decompressed = InitrdImageCodec.Decompress(rawImage, compression);

        var parsedEntries = CpioNewcArchive.Parse(decompressed);
        Assert.True(parsedEntries.Count >= 5);
        Assert.Equal(CpioNewcArchive.TrailerName, parsedEntries[^1].Name);

        // 重新打包并解包断言一致
        byte[] rebuilt = CpioNewcArchive.Build(parsedEntries);
        var reparsed = CpioNewcArchive.Parse(rebuilt);
        Assert.Equal(parsedEntries.Count, reparsed.Count);
        for (int i = 0; i < parsedEntries.Count; i++)
        {
            Assert.Equal(parsedEntries[i].Name, reparsed[i].Name);
            Assert.Equal(parsedEntries[i].Content, reparsed[i].Content);
        }
    }

    [Fact]
    public async Task Adbd拉起脚本导出系统环境以保障运行时工具正常工作()
    {
        string workspace = _temp.New("adbd_environ");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd");

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.Contains("init.environ.rc", initText);
    }

    private string WriteHostKey(string workspace)
    {
        string path = Path.Combine(workspace, "adbkey.pub");
        File.WriteAllText(path, HostPublicKey + Environment.NewLine);
        return path;
    }

    private string WriteInitrd(
        string workspace,
        string autoDetectScript = AutoDetectScript,
        string initScript = InitScript)
    {
        var entries = new List<CpioNewcEntry>
        {
            new()
            {
                Name = "init",
                Ino = 2,
                Mode = 0x81ed,
                Nlink = 1,
                Mtime = 1700000001,
                Content = Encoding.UTF8.GetBytes(initScript)
            },
            new()
            {
                Name = "scripts",
                Ino = 1,
                Mode = 0x41ed,
                Nlink = 2,
                Mtime = 1700000000,
                Content = Array.Empty<byte>()
            },
            new()
            {
                Name = InitrdCustomizerService.AutoDetectEntryName,
                Ino = 3,
                Mode = 0x81ed,
                Nlink = 1,
                Mtime = 1700000002,
                Content = Encoding.UTF8.GetBytes(autoDetectScript)
            },
            new()
            {
                Name = "bin/busybox",
                Ino = 4,
                Mode = 0x81ed,
                Nlink = 1,
                Mtime = 1700000003,
                Content = new byte[] { 0x7f, 0x45, 0x4c, 0x46 }
            },
            new()
            {
                Name = CpioNewcArchive.TrailerName,
                Ino = 5,
                Mode = 0,
                Nlink = 1,
                Mtime = 0,
                Content = Array.Empty<byte>()
            }
        };

        string path = Path.Combine(workspace, "initrd.img");
        byte[] image = InitrdImageCodec.Compress(CpioNewcArchive.Build(entries), InitrdCompression.GZip);
        File.WriteAllBytes(path, image);
        return path;
    }

    private static IReadOnlyList<CpioNewcEntry> ReadEntries(string path)
    {
        byte[] image = File.ReadAllBytes(path);
        InitrdCompression compression = InitrdImageCodec.Detect(image);
        byte[] archive = InitrdImageCodec.Decompress(image, compression);
        return CpioNewcArchive.Parse(archive);
    }

    private static string ReadEntryText(string path, string entryName)
    {
        var entries = ReadEntries(path);
        var entry = CpioNewcArchive.Find(entries, entryName);
        Assert.NotNull(entry);
        return Encoding.UTF8.GetString(entry.Content);
    }
}
