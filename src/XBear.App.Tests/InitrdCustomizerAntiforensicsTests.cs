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
        // 依次载入，只追加 system 会被其余分区回写，五分区一致性指标将不成立。
        Assert.Contains("/android/vendor/build.prop", initText);
        Assert.Contains("/android/vendor/default.prop", initText);
        Assert.Contains("/android/odm/build.prop", initText);
        Assert.Contains("/android/odm/etc/build.prop", initText);
        Assert.Contains("/android/product/build.prop", initText);
        Assert.Contains("/android/product/etc/build.prop", initText);
        Assert.Contains("/android/system_ext/build.prop", initText);
        Assert.Contains("/android/system_ext/etc/build.prop", initText);
        Assert.Contains("/android/system/etc/prop.default", initText);
        Assert.Contains("> /tmp/device.prop", initText);
        Assert.Contains("build.prop overlay appended: $target", initText);
        Assert.Contains("build.prop overlay write skipped (not writable): $target", initText);
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
    public async Task BuildProp覆写_多分区追加目标包含五分区与降级保护留痕()
    {
        string workspace = _temp.New("bp_multiparts");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);
        var policy = InitrdCustomizationPolicy.CreateAntiforensicsPolicy(
            adbGuestPort: 5555,
            adbPublicKey: HostPublicKey,
            adbApexMountPath: "/apex/com.android.adbd",
            profile: profile);

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);

        // 验证覆盖五分区（system, vendor, odm, product, system_ext）的主要候选路径
        Assert.Contains("/android/system/build.prop", initText);
        Assert.Contains("/android/vendor/build.prop", initText);
        Assert.Contains("/android/odm/build.prop", initText);
        Assert.Contains("/android/product/build.prop", initText);
        Assert.Contains("/android/system_ext/build.prop", initText);

        // 验证包含 prop.default 与 default.prop 变体
        Assert.Contains("/android/system/etc/prop.default", initText);
        Assert.Contains("/android/prop.default", initText);
        Assert.Contains("/android/default.prop", initText);
        Assert.Contains("/android/vendor/default.prop", initText);

        // 验证 device.prop 与 /tmp 清理
        Assert.Contains("/android/system/vendor/etc/device.prop", initText);
        Assert.Contains("[ -f /tmp/device.prop ] && > /tmp/device.prop", initText);

        // 验证降级保护与留痕分支存在
        Assert.Contains("build.prop overlay appended: $target", initText);
        Assert.Contains("build.prop overlay write skipped (not writable): $target", initText);
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

    [Fact]
    public async Task 模块目录版本别名_注入软链指向既有模块目录()
    {
        string workspace = _temp.New("modules_alias");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            ModulesReleaseAlias: "6.1.112-android11-perf");

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);

        // 版本串必须以字面量形式出现在注入块内，并与定制参数逐字一致。
        Assert.Contains("if [ -n \"6.1.112-android11-perf\" ] && [ -d /lib/modules ]; then", initText);
        Assert.Contains("modules_alias_dir=\"/lib/modules/6.1.112-android11-perf\"", initText);

        // 源目录必须在运行时从 /lib/modules 下的真实目录项探测：
        // 写死原版本串会把镜像来源身份带进产品源码，且换镜像即失效。
        Assert.Contains("for modules_cand in /lib/modules/*; do", initText);
        Assert.Contains("[ -d \"$modules_cand\" ] && [ ! -L \"$modules_cand\" ]", initText);
        Assert.Contains("modules_src=\"$modules_cand\"", initText);

        // 关键回归点：软链目标必须是探测到的既有目录本体，不能沿用原版脚本的父目录相对路径。
        // 原版 `ln -s ../system/lib/modules /lib/modules/$(uname -r)` 仅在条目名与既有目录同名时
        // 恰好可用（ln 因 File exists 失败）；版本串改名后沿用该目标会建出悬空软链，
        // modprobe 查 /lib/modules/$(uname -r)/virtio_net.ko 落空，guest 无 eth0、ADB 断链。
        Assert.Contains("ln -sfn \"$modules_src\" \"$modules_alias_dir\"", initText);
        Assert.DoesNotContain("ln -sfn ../system/lib/modules", initText);

        // /lib/modules 经软链落在 system 分区内，镜像 system 为 ext4 ro，
        // 不先 remount,rw 会直接 Read-only file system，别名条目根本落不了盘。
        int remountLine = initText.IndexOf("mount -o remount,rw /android", StringComparison.Ordinal);
        int linkLine = initText.IndexOf("ln -sfn \"$modules_src\"", StringComparison.Ordinal);
        Assert.True(remountLine > 0 && linkLine > remountLine,
            "remount,rw 必须先于软链建立，否则 system 分区只读会导致别名条目创建失败。");

        // 降级保护：探测不到源目录、目标已是真实目录、写入失败三条分支均只告警不阻断引导。
        Assert.Contains("modules release alias source not found under /lib/modules", initText);
        Assert.Contains("modules release alias target already a real directory:", initText);
        Assert.Contains("modules release alias link skipped (not writable):", initText);

        // ln -sfn 天然幂等：重复定制会覆盖既有软链，不会叠加嵌套链接。
        Assert.Contains("-sfn", initText);
    }

    [Fact]
    public async Task 模块目录版本别名_未配置时不注入任何软链语句()
    {
        string workspace = _temp.New("modules_alias_off");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd");

        Assert.False(policy.ModulesReleaseAliasEnabled);

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.DoesNotContain("modules_alias_dir", initText);
        Assert.DoesNotContain("modules release alias", initText);
    }

    [Fact]
    public async Task 模块目录版本别名_参与指纹且别名变化触发重新定制()
    {
        string workspace = _temp.New("modules_alias_fpr");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var baseline = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd");
        var aliased = baseline with { ModulesReleaseAlias = "6.1.112-android11-perf" };
        var otherAlias = baseline with { ModulesReleaseAlias = "6.1.112-android11-hyper" };

        Assert.NotEqual(
            InitrdCustomizerService.ComputeFingerprint(baseline),
            InitrdCustomizerService.ComputeFingerprint(aliased));
        Assert.NotEqual(
            InitrdCustomizerService.ComputeFingerprint(aliased),
            InitrdCustomizerService.ComputeFingerprint(otherAlias));

        var firstService = new InitrdCustomizerService(keyPath, aliased);
        var first = await firstService.CustomizeAsync(initrdPath);
        Assert.Equal(InitrdCustomizationOutcome.Customized, first.Outcome);

        // 同策略重复定制必须命中幂等旁路，不重复解压重打包。
        var second = await firstService.CustomizeAsync(initrdPath);
        Assert.Equal(InitrdCustomizationOutcome.AlreadyCustomized, second.Outcome);

        var alteredService = new InitrdCustomizerService(keyPath, otherAlias);
        var third = await alteredService.CustomizeAsync(initrdPath);
        Assert.Equal(InitrdCustomizationOutcome.Customized, third.Outcome);
        Assert.NotEqual(first.Fingerprint, third.Fingerprint);

        string finalInit = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.Contains("6.1.112-android11-hyper", finalInit);
        Assert.DoesNotContain("6.1.112-android11-perf", finalInit);
    }

    [Theory]
    [InlineData("6.1.112-android11-perf")]
    [InlineData(" 6.1.112-android11-perf ")]
    [InlineData("5.10.198-g1234567")]
    [InlineData("6.1.0_rc1+custom")]
    public void 模块目录版本别名_合法版本串被接受并去除首尾空白(string value)
    {
        Assert.Equal(value.Trim(), InitrdCustomizerService.NormalizeModulesReleaseAlias(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 模块目录版本别名_空白输入归一化为未启用(string? value)
    {
        Assert.Null(InitrdCustomizerService.NormalizeModulesReleaseAlias(value));
    }

    [Theory]
    [InlineData("6.1.112/../etc")]
    [InlineData("$(reboot)")]
    [InlineData("`id`")]
    [InlineData("a\"b")]
    [InlineData("a'b")]
    [InlineData("a b")]
    [InlineData("a;b")]
    [InlineData("a$b")]
    [InlineData("../../android/system/lib/modules")]
    public void 模块目录版本别名_可改变脚本语义的字符被拒绝(string value)
    {
        // 该值会被拼进引导脚本的字面量，必须在宿主侧提前失败，
        // 否则会产出脚本语义被改写或无法引导的实例。
        Assert.Throws<XBearException>(() => InitrdCustomizerService.NormalizeModulesReleaseAlias(value));
    }

    [Fact]
    public async Task 模块目录版本别名_非法版本串在定制阶段即失败且不产出半成品()
    {
        string workspace = _temp.New("modules_alias_invalid");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);
        byte[] original = File.ReadAllBytes(initrdPath);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            ModulesReleaseAlias: "6.1.112-android11-perf; reboot");

        var service = new InitrdCustomizerService(keyPath, policy);

        await Assert.ThrowsAsync<XBearException>(() => service.CustomizeAsync(initrdPath));

        // 校验发生在指纹计算阶段，早于原版留档与归档改写：
        // 失败时 initrd 本身必须保持原样，且不得留下原版留档与临时文件。
        Assert.Equal(original, File.ReadAllBytes(initrdPath));
        Assert.False(File.Exists(initrdPath + InitrdCustomizerService.StockSuffix));
        Assert.False(File.Exists(initrdPath + ".customizing"));
    }

    [Fact]
    public void 模块目录版本别名_工厂方法可透传且默认不启用()
    {
        DeviceProfile profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);

        var withoutAlias = InitrdCustomizationPolicy.CreateAntiforensicsPolicy(
            5555, HostPublicKey, "/apex/com.android.adbd", profile);
        Assert.False(withoutAlias.ModulesReleaseAliasEnabled);
        Assert.Null(withoutAlias.ModulesReleaseAlias);

        var withAlias = InitrdCustomizationPolicy.CreateAntiforensicsPolicy(
            5555, HostPublicKey, "/apex/com.android.adbd", profile,
            modulesReleaseAlias: "6.1.112-android11-perf");
        Assert.True(withAlias.ModulesReleaseAliasEnabled);
        Assert.Equal("6.1.112-android11-perf", withAlias.ModulesReleaseAlias);
        Assert.True(withAlias.HasAntiforensicsCustomizations);
    }

    [Fact]
    public async Task 硬件代号别名_注入软链指向既有平台rc脚本()
    {
        string workspace = _temp.New("hw_alias");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            HardwareAlias: "vibe12pro");

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);

        Assert.Contains("init.vibe12pro.rc", initText);
        Assert.Contains("ln -sfn \"$bname\" \"$rcdir/init.vibe12pro.rc\"", initText);
        Assert.Contains("hardware rc alias linked:", initText);
    }

    [Fact]
    public async Task BuildProp覆写_重复引导先裁剪旧块再追加且补充扫描清单外文件()
    {
        string workspace = _temp.New("bp_idem");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);
        var policy = InitrdCustomizationPolicy.CreateAntiforensicsPolicy(
            adbGuestPort: 5555,
            adbPublicKey: HostPublicKey,
            adbApexMountPath: "/apex/com.android.adbd",
            profile: profile);

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);

        // 幂等：重复引导前先裁掉本产品上一轮追加的覆写块，文件不随引导次数膨胀。
        Assert.Contains(
            $"grep -q '{PropertyOverlayBuilder.OverlayMarkerHeader}' \"$target\" 2>/dev/null && \\",
            initText);
        Assert.Contains(
            "sed -i '/^# --- Auto-generated build\\.prop overlay by XBear ---/,$d' \"$target\" 2>/dev/null",
            initText);

        // 覆盖完整性：清单之外的同名 prop 文件也必须纳入，
        // 否则镜像最后载入的文件会带着原生 ro.build.type 与 ro.boot.* 把覆写盖回。
        Assert.Contains("for propfile in build.prop prop.default default.prop device.prop odm.prop; do", initText);
        Assert.Contains("/android/*/$propfile", initText);
        Assert.Contains("/android/*/*/$propfile", initText);
        Assert.Contains("/android/*/*/*/$propfile", initText);
        Assert.Contains("/android/*/*/*/*/$propfile", initText);
        Assert.Contains(
            "grep -q '# --- Auto-generated build.prop overlay by XBear ---' \"$target\" 2>/dev/null && continue",
            initText);
    }

    [Fact]
    public async Task 镜像私有属性族_注入行级删除与只读降级留痕()
    {
        string workspace = _temp.New("strip_prop");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);
        var policy = InitrdCustomizationPolicy.CreateAntiforensicsPolicy(
            adbGuestPort: 5555,
            adbPublicKey: HostPublicKey,
            adbApexMountPath: "/apex/com.android.adbd",
            profile: profile);

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);

        // 追加式覆写无法消除属性条目，必须从载入源删行。
        Assert.Contains("if [ -n \"ro.bliss\" ]; then", initText);
        Assert.Contains("grep -q \"^ro\\.bliss\\.\" \"$target\" 2>/dev/null || continue", initText);
        Assert.Contains("sed -i '/^ro\\.bliss\\./d' \"$target\" 2>/dev/null", initText);

        // 幂等：零命中即恒等；只读目标降级并留痕。
        Assert.Contains("private property lines stripped: $target", initText);
        Assert.Contains("private property strip skipped (not writable): $target", initText);
    }

    [Fact]
    public async Task 镜像私有属性族_未配置时不注入任何删除语句()
    {
        string workspace = _temp.New("strip_prop_off");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd");

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);

        Assert.DoesNotContain("private property lines stripped", initText);
        Assert.DoesNotContain("sed -i '/^ro", initText);
    }

    [Theory]
    [InlineData("ro.bliss.", "ro.bliss")]
    [InlineData("ro.vendor.private", "ro.vendor.private")]
    [InlineData("  ro.custom.  ", "ro.custom")]
    [InlineData("   ", null)]
    public void 属性前缀归一化与空值处理(string input, string? expected)
    {
        Assert.Equal(expected, InitrdCustomizerService.NormalizePropertyPrefix(input));
    }

    [Theory]
    [InlineData("ro.bliss.*")]
    [InlineData("ro.bliss/[a-z]")]
    [InlineData("ro bliss")]
    [InlineData("ro.bliss{1}")]
    public void 可改变正则语义或含非法字符的属性前缀被拒绝(string value)
    {
        Assert.Throws<XBearException>(() => InitrdCustomizerService.NormalizePropertyPrefix(value));
    }

    [Fact]
    public async Task 根级属性改写_归档写入改写清单且init注入行级取值改写()
    {
        string workspace = _temp.New("root_rewrite");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);
        var policy = InitrdCustomizationPolicy.CreateAntiforensicsPolicy(
            adbGuestPort: 5555,
            adbPublicKey: HostPublicKey,
            adbApexMountPath: "/apex/com.android.adbd",
            profile: profile);

        Assert.True(policy.RootPropertyRewriteEnabled);

        var service = new InitrdCustomizerService(keyPath, policy);
        var result = await service.CustomizeAsync(initrdPath);

        Assert.Contains(InitrdCustomizerService.RootPropertyRewriteEntryName, result.CustomizedEntries);

        var rewriteEntry = CpioNewcArchive.Find(
            ReadEntries(initrdPath),
            InitrdCustomizerService.RootPropertyRewriteEntryName);
        Assert.NotNull(rewriteEntry);

        string rewriteText = Encoding.UTF8.GetString(rewriteEntry.Content);
        Assert.StartsWith(
            PropertyOverlayBuilder.RootPropertyRewriteMarkerHeader,
            rewriteText,
            StringComparison.Ordinal);
        Assert.Contains("ro.build.type=userdebug", rewriteText);
        Assert.Contains("ro.boot.flash.locked=0", rewriteText);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);

        // 追加式覆写压不住镜像已有的根级键，必须逐键做行级取值改写。
        Assert.Contains("if [ -f /etc/prop.rewrite ]; then", initText);
        Assert.Contains("grep -q '^ro\\.build\\.type=' \"$target\" 2>/dev/null", initText);
        Assert.Contains(
            "sed -i 's|^ro\\.build\\.type=.*|ro.build.type=userdebug|' \"$target\" 2>/dev/null",
            initText);
        Assert.Contains(
            "sed -i 's|^ro\\.build\\.tags=.*|ro.build.tags=release-keys|' \"$target\" 2>/dev/null",
            initText);
        Assert.Contains(
            "sed -i 's|^ro\\.boot\\.flash\\.locked=.*|ro.boot.flash.locked=0|' \"$target\" 2>/dev/null",
            initText);
        Assert.Contains(
            "sed -i 's|^ro\\.boot\\.vbmeta\\.device_state=.*|ro.boot.vbmeta.device_state=orange|' \"$target\" 2>/dev/null",
            initText);
        Assert.Contains(
            "sed -i 's|^ro\\.boot\\.verifiedbootstate=.*|ro.boot.verifiedbootstate=orange|' \"$target\" 2>/dev/null",
            initText);
        Assert.Contains(
            "sed -i 's|^ro\\.boot\\.veritymode=.*|ro.boot.veritymode=disabled|' \"$target\" 2>/dev/null",
            initText);

        // 沿用与追加覆写一致的 glob 扫描集，清单之外的后载入文件同样纳入。
        Assert.Contains("for propfile in build.prop prop.default default.prop device.prop odm.prop; do", initText);
        Assert.Contains("/android/$propfile /android/*/$propfile \\", initText);
        Assert.Contains("/android/*/*/$propfile /android/*/*/*/$propfile \\", initText);
        Assert.Contains("/android/*/*/*/*/$propfile; do", initText);

        // 幂等与降级：改写目标值即写回自身，重复引导结果一致；只读目标留痕。
        Assert.Contains("root property rewritten: ro.build.type=userdebug in $target", initText);
        Assert.Contains("root property rewrite skipped (not writable): ro.build.type in $target", initText);
    }

    [Fact]
    public async Task 根级属性改写_未配置清单时不注入任何改写语句()
    {
        string workspace = _temp.New("root_rewrite_off");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd");

        Assert.False(policy.RootPropertyRewriteEnabled);

        var service = new InitrdCustomizerService(keyPath, policy);
        var result = await service.CustomizeAsync(initrdPath);

        Assert.DoesNotContain(InitrdCustomizerService.RootPropertyRewriteEntryName, result.CustomizedEntries);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.DoesNotContain("root property rewritten", initText);
        Assert.DoesNotContain("/etc/prop.rewrite", initText);
    }

    [Fact]
    public async Task 根级属性改写_显式关闭清单时归档与脚本均不落地()
    {
        string workspace = _temp.New("root_rewrite_disabled");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);
        var policy = InitrdCustomizationPolicy.CreateAntiforensicsPolicy(
            adbGuestPort: 5555,
            adbPublicKey: HostPublicKey,
            adbApexMountPath: "/apex/com.android.adbd",
            profile: profile,
            enableRootPropertyRewrite: false);

        Assert.Null(policy.RootPropertyRewriteContent);

        var service = new InitrdCustomizerService(keyPath, policy);
        var result = await service.CustomizeAsync(initrdPath);

        Assert.DoesNotContain(InitrdCustomizerService.RootPropertyRewriteEntryName, result.CustomizedEntries);
        Assert.Null(CpioNewcArchive.Find(
            ReadEntries(initrdPath),
            InitrdCustomizerService.RootPropertyRewriteEntryName));
        Assert.DoesNotContain("root property rewritten", ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName));
    }

    [Theory]
    [InlineData("ro.build.type")]
    [InlineData("=userdebug")]
    [InlineData("ro.build.type=")]
    [InlineData("ro.build.type=user debug;rm -rf /")]
    [InlineData("ro.build.type=user'debug")]
    [InlineData("ro.build type=userdebug")]
    [InlineData(".ro.build.type=userdebug")]
    public void 可改变脚本语义或形态非法的改写清单行被拒绝(string line)
    {
        string content = PropertyOverlayBuilder.RootPropertyRewriteMarkerHeader + Environment.NewLine + line + Environment.NewLine;

        Assert.Throws<XBearException>(() => InitrdCustomizerService.ParseRootPropertyRewrites(content));
    }

    [Fact]
    public void 改写清单解析忽略空行注释并保持顺序()
    {
        string content = string.Join(
            Environment.NewLine,
            PropertyOverlayBuilder.RootPropertyRewriteMarkerHeader,
            string.Empty,
            "   ",
            "ro.build.type=userdebug",
            "# 注释行",
            "ro.boot.veritymode=disabled");

        var rules = InitrdCustomizerService.ParseRootPropertyRewrites(content);

        Assert.Equal(2, rules.Count);
        Assert.Equal("ro.build.type", rules[0].Key);
        Assert.Equal("userdebug", rules[0].Value);
        Assert.Equal("ro.boot.veritymode", rules[1].Key);
        Assert.Equal("disabled", rules[1].Value);
        Assert.Empty(InitrdCustomizerService.ParseRootPropertyRewrites("   "));
    }

    [Fact]
    public void 改写清单参与指纹且变化触发重新定制()
    {
        var profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);

        var withRewrite = InitrdCustomizationPolicy.CreateAntiforensicsPolicy(
            adbGuestPort: 5555,
            adbPublicKey: HostPublicKey,
            adbApexMountPath: "/apex/com.android.adbd",
            profile: profile);

        var withoutRewrite = withRewrite with { RootPropertyRewriteContent = null };

        Assert.NotEqual(
            InitrdCustomizerService.ComputeFingerprint(withRewrite),
            InitrdCustomizerService.ComputeFingerprint(withoutRewrite));

        // 仅首尾空白差异不应改变指纹，改写清单本身才是指纹要素。
        string spaced = PropertyOverlayBuilder.BuildRootPropertyRewriteContent(profile)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "  " + Environment.NewLine, StringComparison.Ordinal);

        Assert.Equal(
            InitrdCustomizerService.ComputeFingerprint(withRewrite),
            InitrdCustomizerService.ComputeFingerprint(withRewrite with { RootPropertyRewriteContent = spaced }));

        var changed = withRewrite with
        {
            RootPropertyRewriteContent = PropertyOverlayBuilder.RootPropertyRewriteMarkerHeader +
                                         Environment.NewLine +
                                         "ro.build.type=eng" +
                                         Environment.NewLine,
        };

        Assert.NotEqual(
            InitrdCustomizerService.ComputeFingerprint(withRewrite),
            InitrdCustomizerService.ComputeFingerprint(changed));
    }

    [Fact]
    public async Task 硬件代号别名_HAL库目录按原生代号补建代号别名软链()
    {
        string workspace = _temp.New("hw_hal");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            HardwareAlias: "vibe12pro",
            HardwareSourceAlias: "bliss_x86_64");

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);

        Assert.Contains("for hwdir in /android/vendor/lib/hw /android/vendor/lib64/hw \\", initText);
        Assert.Contains("/android/system/lib/hw /android/system/lib64/hw \\", initText);
        Assert.Contains("for hwlib in \"$hwdir\"/*\"bliss_x86_64\"*; do", initText);
        Assert.Contains("hwalias=`echo \"$hwbase\" | sed 's/bliss_x86_64/vibe12pro/g'`", initText);

        // 幂等与降级保护：已存在的条目不覆盖，不可写时留痕。
        Assert.Contains("[ -e \"$hwdir/$hwalias\" ] && continue", initText);
        Assert.Contains("hardware hal alias linked: $hwdir/$hwalias -> $hwbase", initText);
        Assert.Contains("hardware hal alias skipped (not writable): $hwdir/$hwalias", initText);
    }

    [Fact]
    public async Task 硬件代号别名_未给出原生代号时不注入HAL软链()
    {
        string workspace = _temp.New("hw_hal_off");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            HardwareAlias: "vibe12pro");

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);

        Assert.Contains("init.vibe12pro.rc", initText);
        Assert.DoesNotContain("hardware hal alias linked", initText);
    }

    [Fact]
    public async Task 硬件代号别名_补建平台rc内嵌代号的分区表别名()
    {
        string workspace = _temp.New("hw_fstab");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            HardwareAlias: "vibe12pro",
            HardwareSourceAlias: "bliss_x86_64");

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);

        // 平台 rc 内部以 mount_all /fstab.${ro.hardware} 与 swapon_all 同源拼接分区表文件名，
        // 代号改名后必须同时补建 fstab 条目别名，否则 mount_all 空转、/data 不挂载、框架起不来。
        Assert.Contains("for hwpart in fstab; do", initText);
        Assert.Contains("if [ -f \"/android/$hwpart.bliss_x86_64\" ]; then", initText);
        Assert.Contains(
            "ln -sfn \"$hwpart.bliss_x86_64\" \"/android/$hwpart.vibe12pro\" 2>/dev/null",
            initText);
        Assert.Contains(
            "echo \"hardware partition alias linked: /android/$hwpart.vibe12pro -> $hwpart.bliss_x86_64\"",
            initText);
    }

    [Fact]
    public async Task 硬件代号别名_目标代号与原生代号相同时不注入HAL软链()
    {
        string workspace = _temp.New("hw_hal_same");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            HardwareAlias: "bliss_x86_64",
            HardwareSourceAlias: "bliss_x86_64");

        Assert.False(policy.HardwareHalAliasEnabled);

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.DoesNotContain("hardware hal alias linked", initText);
    }

    [Fact]
    public async Task 硬件代号别名_rc优先指向镜像原生代号平台脚本()
    {
        string workspace = _temp.New("hw_rc_source");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd",
            HardwareAlias: "vibe12pro",
            HardwareSourceAlias: "bliss_x86_64");

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);

        Assert.Contains("if [ -f \"$rcdir/init.bliss_x86_64.rc\" ]; then", initText);
        Assert.Contains(
            "ln -sfn \"init.bliss_x86_64.rc\" \"$rcdir/init.vibe12pro.rc\" 2>/dev/null",
            initText);

        // 同名文件缺失时仍保留按首个非保留平台 rc 的回落路径。
        Assert.Contains("ln -sfn \"$bname\" \"$rcdir/init.vibe12pro.rc\" 2>/dev/null", initText);
    }

    [Fact]
    public async Task 主机名_注入开机前后两处落定与回读()
    {
        string workspace = _temp.New("hostname");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);
        var policy = InitrdCustomizationPolicy.CreateAntiforensicsPolicy(
            adbGuestPort: 5555,
            adbPublicKey: HostPublicKey,
            adbApexMountPath: "/apex/com.android.adbd",
            profile: profile);

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);

        // initrd 阶段：先设定内核 UTS 节点名，覆盖内核起来到用户空间接管之间的窗口。
        Assert.Contains("hostname vibe12pro 2>/dev/null", initText);

        // 开机后：与 Android ID 同一套辅助进程机制，等 sys.boot_completed 之后再落定。
        Assert.Contains("chroot /android /system/bin/getprop sys.boot_completed", initText);
        Assert.Contains("chroot /android /system/bin/hostname vibe12pro 2>&1", initText);
        Assert.Contains("chroot /android /system/bin/setprop net.hostname vibe12pro 2>&1", initText);
        Assert.Contains("chroot /android /system/bin/setprop persist.sys.hostname vibe12pro 2>&1", initText);
        Assert.Contains("actual_host=`chroot /android /system/bin/hostname 2>/dev/null`", initText);
        Assert.Contains("/android/data/hostname.log", initText);
    }

    [Fact]
    public async Task 主机名_身份契约显式声明时优先于画像代号()
    {
        string workspace = _temp.New("hostname_spec");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);
        var identity = new IdentitySpec
        {
            SerialNo = "XBSN0123456789AB",
            AndroidId = "a1b2c3d4e5f60718",
            Mac = "3a:1f:0c:67:89:ba",
            DeviceProfile = profile.ToSpec(),
            HostName = "vibe12pro-n",
        };

        var policy = InitrdCustomizationPolicy.CreateAntiforensicsPolicy(
            adbGuestPort: 5555,
            adbPublicKey: HostPublicKey,
            adbApexMountPath: "/apex/com.android.adbd",
            profile: profile,
            identity: identity);

        Assert.Equal("vibe12pro-n", policy.HostName);

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.Contains("hostname vibe12pro-n 2>/dev/null", initText);
        Assert.Contains("setprop net.hostname vibe12pro-n", initText);
    }

    [Fact]
    public async Task 主机名_未配置时不注入任何主机名语句()
    {
        string workspace = _temp.New("hostname_off");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var policy = new InitrdCustomizationPolicy(
            AdbGuestPort: 5555,
            AdbPublicKey: HostPublicKey,
            AdbApexMountPath: "/apex/com.android.adbd");

        Assert.False(policy.HostNameEnabled);

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        string initText = ReadEntryText(initrdPath, InitrdCustomizerService.InitEntryName);
        Assert.DoesNotContain("net.hostname", initText);
        Assert.DoesNotContain("hostname.log", initText);
    }

    [Fact]
    public async Task 新增定制项参与指纹且变化触发重新定制()
    {
        string workspace = _temp.New("fp_new_items");
        string keyPath = WriteHostKey(workspace);

        var profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);
        var identity = new IdentitySpec
        {
            SerialNo = "XBSN0123456789AB",
            AndroidId = "a1b2c3d4e5f60718",
            Mac = "3a:1f:0c:67:89:ba",
            DeviceProfile = profile.ToSpec(),
        };

        var baseline = InitrdCustomizationPolicy.CreateAntiforensicsPolicy(
            adbGuestPort: 5555,
            adbPublicKey: HostPublicKey,
            adbApexMountPath: "/apex/com.android.adbd",
            profile: profile,
            identity: identity);

        string baselineFingerprint = InitrdCustomizerService.ComputeFingerprint(baseline);
        string initrdPath = WriteInitrd(workspace);

        var service = new InitrdCustomizerService(keyPath, baseline);
        var first = await service.CustomizeAsync(initrdPath);
        Assert.Equal(InitrdCustomizationOutcome.Customized, first.Outcome);
        Assert.Equal(baselineFingerprint, first.Fingerprint);

        // 同策略重跑判定为已定制，指纹不变。
        var second = await service.CustomizeAsync(initrdPath);
        Assert.Equal(InitrdCustomizationOutcome.AlreadyCustomized, second.Outcome);

        // 主机名、删除前缀与原生代号任一变化都必须让指纹变化并触发重新定制。
        var hostChanged = baseline with { HostName = "vibe12pro-n" };
        Assert.NotEqual(baselineFingerprint, InitrdCustomizerService.ComputeFingerprint(hostChanged));

        var prefixChanged = baseline with { StripPrivatePropertyPrefix = "ro.custom." };
        Assert.NotEqual(baselineFingerprint, InitrdCustomizerService.ComputeFingerprint(prefixChanged));

        var sourceChanged = baseline with { HardwareSourceAlias = "other_hw" };
        Assert.NotEqual(baselineFingerprint, InitrdCustomizerService.ComputeFingerprint(sourceChanged));

        var third = await new InitrdCustomizerService(keyPath, hostChanged).CustomizeAsync(initrdPath);
        Assert.Equal(InitrdCustomizationOutcome.Customized, third.Outcome);
    }

    [Fact]
    public void 工厂方法默认补齐私有属性前缀与原生代号()
    {
        var profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);

        var policy = InitrdCustomizationPolicy.CreateAntiforensicsPolicy(
            adbGuestPort: 5555,
            adbPublicKey: HostPublicKey,
            adbApexMountPath: "/apex/com.android.adbd",
            profile: profile);

        Assert.Equal(profile.Device, policy.HardwareAlias);
        Assert.Equal(InitrdCustomizationPolicy.DefaultHardwareSourceAlias, policy.HardwareSourceAlias);
        Assert.Equal(InitrdCustomizationPolicy.DefaultStripPrivatePropertyPrefix, policy.StripPrivatePropertyPrefix);
        Assert.Equal(profile.Device, policy.HostName);
        Assert.True(policy.HardwareHalAliasEnabled);
        Assert.True(policy.StripPrivatePropertiesEnabled);
        Assert.True(policy.HostNameEnabled);
        Assert.True(policy.RootPropertyRewriteEnabled);
        Assert.Equal(
            PropertyOverlayBuilder.BuildRootPropertyRewriteContent(profile),
            policy.RootPropertyRewriteContent);
    }

    [Fact]
    public async Task 全套抗取证定制组合注入后归档格式仍合法()
    {
        string workspace = _temp.New("combo_new");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);
        var policy = InitrdCustomizationPolicy.CreateAntiforensicsPolicy(
            adbGuestPort: 5555,
            adbPublicKey: HostPublicKey,
            adbApexMountPath: "/apex/com.android.adbd",
            profile: profile,
            defaultGateway: "172.25.146.2",
            embeddedSrc: "system",
            enableRenderSpoofing: false,
            modulesReleaseAlias: "6.1.112-android11-perf");

        var service = new InitrdCustomizerService(keyPath, policy);
        await service.CustomizeAsync(initrdPath);

        var entries = ReadEntries(initrdPath);
        Assert.NotEmpty(entries);
        Assert.NotNull(CpioNewcArchive.Find(entries, "init"));
        Assert.NotNull(CpioNewcArchive.Find(entries, InitrdCustomizerService.BuildPropOverrideEntryName));
        Assert.NotNull(CpioNewcArchive.Find(entries, InitrdCustomizerService.RootPropertyRewriteEntryName));
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
