using System.IO;
using System.Text;
using XBear.App.Services;
using XBear.Core.Diagnostics;

namespace XBear.App.Tests;

/// <summary>
/// InitrdCustomizerService 的定制行为测试。
/// 使用进程内构造的最小 initrd 样本，不依赖真实 Bliss 镜像产物。
/// </summary>
public class InitrdCustomizerServiceTests : IDisposable
{
    /// <summary>测试用的宿主 ADB 公钥内容，形态与 adbkey.pub 一致。</summary>
    private const string HostPublicKey =
        "QAAAtestKeyMaterialForInitrdCustomizationTestsAAAAAAAAAAAAAA== user@host";

    /// <summary>与真实 Android-x86 引导脚本同形的自动探测脚本。</summary>
    private const string AutoDetectScript = """
        #
        # By Chih-Wei Huang <cwhuang@linux.org.tw>
        #

        # An auto detect function provided by kinneko
        auto_detect()
        {
        	tmp=/tmp/dev2mod
        	echo 'dev2mod() { while read dev; do case $dev in' > $tmp
        	for f in $(grep -Eh "drm_kms|sound.core|hyperv" /lib/modules/`uname -r`/modules.dep | cut -d. -f1); do
        		sed -i "/$(basename $f | sed 's/-/_/g')/d" $tmp
        	done
        	source $tmp
        }

        # Based on Alpine Linux's hwdrivers.initd
        auto_detect_alpine()
        {
        	tmp=/tmp/dev2mod
        	find /sys -name modalias -type f -print0 2> /dev/null | xargs -0 sort -u | dev2mod
        }

        load_modules()
        {
        	case "$AUTO_LOAD" in
        		alpine)
        			auto_detect_alpine
        		;;
        		old)
        			auto_detect
        		;;
        	esac

        	# 3G modules
        	for m in $EXTMOD; do
        		busybox modprobe $m
        	done
        }

        """;

    /// <summary>与真实引导脚本同形的根脚本，锚点与上游一致。</summary>
    private const string InitScript = """
        #!/bin/busybox sh
        #
        # License: GNU Public License
        #

        mount_data()
        {
        	mount -t ext4 /dev/sda1 /android/data
        }

        load_modules()
        {
        	:
        }

        text_art()
        {
        	echo "art"
        }

        text_art
        load_modules
        mount_data
        mount_sdcard
        mount_grub
        exec ${SWITCH:-switch_root} /android /init

        """;

    private readonly TempRoot _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Cleanup();

    /// <summary>
    /// 定制后两个目标条目被改写，其余条目逐字节保持不变。
    /// 未改写条目若发生变化，说明重打包破坏了归档内容。
    /// </summary>
    [Fact]
    public async Task CustomizationRewritesOnlyTargetEntries()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var before = ReadEntries(initrdPath);

        InitrdCustomizationResult result =
            await new InitrdCustomizerService(keyPath).CustomizeAsync(initrdPath);

        Assert.Equal(InitrdCustomizationOutcome.Customized, result.Outcome);

        var after = ReadEntries(initrdPath);
        Assert.Equal(before.Count, after.Count);

        List<string> changed = new();
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Name, after[i].Name);
            Assert.Equal(before[i].Mode, after[i].Mode);
            Assert.Equal(before[i].Ino, after[i].Ino);
            Assert.Equal(before[i].Mtime, after[i].Mtime);

            if (!before[i].Content.AsSpan().SequenceEqual(after[i].Content))
            {
                changed.Add(before[i].Name);
            }
        }

        Assert.Equal(
            new[] { InitrdCustomizerService.InitEntryName, InitrdCustomizerService.AutoDetectEntryName },
            changed.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// 两个自动探测函数被替换为空实现，原有的同步 modprobe 逻辑不再存在。
    /// </summary>
    [Fact]
    public async Task AutoDetectFunctionsAreReplacedByNoOps()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        await new InitrdCustomizerService(keyPath).CustomizeAsync(initrdPath);

        string script = Encoding.UTF8.GetString(
            CpioNewcArchive.Find(ReadEntries(initrdPath), InitrdCustomizerService.AutoDetectEntryName)!.Content);

        Assert.Contains("auto_detect()", script);
        Assert.Contains("auto_detect_alpine()", script);
        Assert.Contains("return 0", script);

        // 打桩后函数体不再有会导致引导卡死的同步 modprobe 逻辑。
        Assert.DoesNotContain("dev2mod", script);
        Assert.DoesNotContain("modalias", script);

        // 上游的其余逻辑必须保留：load_modules 及其 3G 模块加载仍在工作。
        Assert.Contains("load_modules()", script);
        Assert.Contains("busybox modprobe $m", script);

        // 函数结构仍然闭合，缺一个花括号就会让整个 initramfs 起不来。
        // 脚本共三个函数：两个被打桩，load_modules 保持原样。
        Assert.Equal(3, CountStandaloneLines(script, "{"));
        Assert.Equal(3, CountStandaloneLines(script, "}"));

        // 每个被打桩的函数体都只包含桩标记与返回，不得残留上游逻辑。
        foreach (string name in new[] { "auto_detect", "auto_detect_alpine" })
        {
            string[] lines = script.Split('\n');
            int header = Array.FindIndex(lines, l => l.Trim() == name + "()");
            Assert.True(header >= 0, $"未找到函数 {name}");

            int open = Array.FindIndex(lines, header + 1, l => l.Trim() == "{");
            int close = Array.FindIndex(lines, open + 1, l => l.Trim() == "}");

            Assert.True(open > header, $"{name} 的左花括号丢失");
            Assert.True(close > open, $"{name} 的右花括号丢失");

            string[] body = lines[(open + 1)..close];
            Assert.All(body, l => Assert.DoesNotContain("modprobe", l));
        }
    }

    /// <summary>
    /// 自动探测脚本中缺少目标函数时必须报错，不允许产出只打了一半的产物。
    /// </summary>
    [Fact]
    public async Task MissingAutoDetectFunctionThrowsSpecException()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);

        const string ReducedScript = """
            load_modules()
            {
            	:
            }

            """;

        string initrdPath = WriteInitrd(workspace, autoDetectScript: ReducedScript);

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => new InitrdCustomizerService(keyPath).CustomizeAsync(initrdPath));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.Contains("auto_detect", error.Message);

        // 失败时不得留下半成品。
        Assert.Equal(ExtractOriginal(workspace), File.ReadAllBytes(initrdPath));
    }

    /// <summary>
    /// 根脚本缺少挂载锚点时必须报错。
    /// </summary>
    [Fact]
    public async Task MissingMountAnchorThrowsSpecException()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);

        const string NoAnchorScript = """
            #!/bin/busybox sh
            echo "no anchor here"
            exec switch_root /android /init

            """;

        string initrdPath = WriteInitrd(workspace, initScript: NoAnchorScript);

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => new InitrdCustomizerService(keyPath).CustomizeAsync(initrdPath));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.Contains(InitrdCustomizerService.InitEntryName, error.Message);
        Assert.NotNull(error.Remediation);
    }

    /// <summary>
    /// 归档中缺少被改写的条目时必须报错。
    /// </summary>
    [Fact]
    public async Task MissingTargetEntryThrowsSpecException()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);

        var entries = new List<CpioNewcEntry>
        {
            new CpioNewcEntry
            {
                Name = "init",
                Mode = 0x81ed,
                Nlink = 1,
                Content = Encoding.UTF8.GetBytes(InitScript)
            },
            new CpioNewcEntry { Name = CpioNewcArchive.TrailerName, Content = Array.Empty<byte>() }
        };

        string initrdPath = Path.Combine(workspace, "initrd.img");
        File.WriteAllBytes(initrdPath, InitrdImageCodec.Compress(
            CpioNewcArchive.Build(entries), InitrdCompression.GZip));

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => new InitrdCustomizerService(keyPath).CustomizeAsync(initrdPath));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.Contains(InitrdCustomizerService.AutoDetectEntryName, error.Message);
    }

    /// <summary>
    /// 宿主 ADB 公钥缺失时中止定制，并给出可执行的指引；
    /// 不得代为生成密钥，密钥归属是用户决策。
    /// </summary>
    [Fact]
    public async Task MissingHostKeyAbortsWithGuidanceAndDoesNotGenerateKey()
    {
        string workspace = _temp.New("workspace");
        string initrdPath = WriteInitrd(workspace);
        string missingKeyPath = Path.Combine(workspace, "no-such-dir", "adbkey.pub");

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => new InitrdCustomizerService(missingKeyPath).CustomizeAsync(initrdPath));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.Contains("adbkey.pub", error.Message);
        Assert.Contains("adb keygen", error.Remediation!);

        // 不创建密钥文件，也不产出定制产物。
        Assert.False(File.Exists(missingKeyPath));
        Assert.False(File.Exists(initrdPath + InitrdCustomizerService.StockSuffix));
        Assert.Equal(ExtractOriginal(workspace), File.ReadAllBytes(initrdPath));
    }

    /// <summary>
    /// 宿主 ADB 公钥为空文件时同样中止并给出指引。
    /// </summary>
    [Fact]
    public async Task EmptyHostKeyAbortsWithGuidance()
    {
        string workspace = _temp.New("workspace");
        string keyPath = Path.Combine(workspace, "adbkey.pub");
        File.WriteAllText(keyPath, "   \n");

        string initrdPath = WriteInitrd(workspace);

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => new InitrdCustomizerService(keyPath).CustomizeAsync(initrdPath));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.Contains("为空", error.Message);
        Assert.Equal(ExtractOriginal(workspace), File.ReadAllBytes(initrdPath));
    }

    /// <summary>
    /// 公钥被写入 guest 的授权列表，属主与权限必须与 adbd 的运行身份匹配。
    /// </summary>
    [Fact]
    public async Task HostKeyIsPlantedWithMatchingOwnership()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        await new InitrdCustomizerService(keyPath).CustomizeAsync(initrdPath);

        string init = Encoding.UTF8.GetString(
            CpioNewcArchive.Find(ReadEntries(initrdPath), InitrdCustomizerService.InitEntryName)!.Content);

        Assert.Contains($"echo \"{HostPublicKey}\" > /android/data/misc/adb/adb_keys", init);
        Assert.Contains("mkdir -p /android/data/misc/adb", init);
        Assert.Contains("chown -R 2000:2000 /android/data/misc/adb", init);
        Assert.Contains("chmod 0640 /android/data/misc/adb/adb_keys", init);
    }

    /// <summary>
    /// APEX 就绪后拉起 adbd 的辅助进程被注入，且 adbd 走 APEX 挂载点与 chroot。
    /// </summary>
    [Fact]
    public async Task ApexAdbdBootstrapIsInjected()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        await new InitrdCustomizerService(keyPath).CustomizeAsync(initrdPath);

        string init = Encoding.UTF8.GetString(
            CpioNewcArchive.Find(ReadEntries(initrdPath), InitrdCustomizerService.InitEntryName)!.Content);

        Assert.Contains("service.adb.tcp.port=5555", init);
        Assert.Contains("SWITCH=chroot", init);
        Assert.Contains($"chroot /android {InitrdCustomizerService.DefaultAdbApexMountPath}/bin/adbd", init);
        Assert.Contains(
            $"[ -x /android{InitrdCustomizerService.DefaultAdbApexMountPath}/bin/adbd ] && break",
            init);
    }

    /// <summary>
    /// 注入块必须落在数据分区挂载之后、其余引导步骤之前。
    /// </summary>
    [Fact]
    public async Task InjectionSitsBetweenMountDataAndMountSdcard()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        await new InitrdCustomizerService(keyPath).CustomizeAsync(initrdPath);

        string init = Encoding.UTF8.GetString(
            CpioNewcArchive.Find(ReadEntries(initrdPath), InitrdCustomizerService.InitEntryName)!.Content);

        int mountData = init.IndexOf("\nmount_data\n", StringComparison.Ordinal);
        int injected = init.IndexOf("service.adb.tcp.port", StringComparison.Ordinal);
        int mountSdcard = init.IndexOf("\nmount_sdcard\n", StringComparison.Ordinal);
        int exec = init.IndexOf("\nexec ", StringComparison.Ordinal);

        Assert.True(mountData >= 0, "mount_data 锚点丢失");
        Assert.True(injected > mountData, "注入块必须位于数据分区挂载之后");
        Assert.True(mountSdcard > injected, "注入块必须位于 sdcard 挂载之前");
        Assert.True(exec > mountSdcard, "执行交接必须在注入块之后");
    }

    /// <summary>
    /// 幂等：策略未变时重复调用不重做定制，产物字节保持不变。
    /// </summary>
    [Fact]
    public async Task RepeatedRunWithSamePolicyIsIdempotent()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var service = new InitrdCustomizerService(keyPath);
        await service.CustomizeAsync(initrdPath);
        byte[] firstPass = File.ReadAllBytes(initrdPath);

        InitrdCustomizationResult second = await service.CustomizeAsync(initrdPath);

        Assert.Equal(InitrdCustomizationOutcome.AlreadyCustomized, second.Outcome);
        Assert.Empty(second.CustomizedEntries);
        Assert.Equal(firstPass, File.ReadAllBytes(initrdPath));

        // 第三次同样跳过：幂等判定不能只在首次留档建立后才生效。
        InitrdCustomizationResult third = await service.CustomizeAsync(initrdPath);
        Assert.Equal(InitrdCustomizationOutcome.AlreadyCustomized, third.Outcome);
        Assert.Equal(firstPass, File.ReadAllBytes(initrdPath));
    }

    /// <summary>
    /// 幂等判定内建在产物里：把定制产物单独拷到新位置后仍然识别为已定制，
    /// 不会出现二次注入导致引导脚本无法解析。
    /// </summary>
    [Fact]
    public async Task CustomizedArtifactCarriesItsOwnIdempotencyMarker()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var service = new InitrdCustomizerService(keyPath);
        await service.CustomizeAsync(initrdPath);

        string relocated = Path.Combine(_temp.New("relocated"), "initrd.img");
        File.Copy(initrdPath, relocated);

        InitrdCustomizationResult result = await service.CustomizeAsync(relocated);

        Assert.Equal(InitrdCustomizationOutcome.AlreadyCustomized, result.Outcome);
        Assert.Equal(File.ReadAllBytes(initrdPath), File.ReadAllBytes(relocated));
    }

    /// <summary>
    /// 宿主公钥轮换后策略指纹变化，必须基于原版留档重新定制。
    /// </summary>
    [Fact]
    public async Task RotatedHostKeyRegeneratesFromStock()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        await new InitrdCustomizerService(keyPath).CustomizeAsync(initrdPath);

        string rotatedKey = "QAAArotatedKeyMaterialAfterUserKeyRotationBBBBBBBBBB== user@host";
        File.WriteAllText(keyPath, rotatedKey);

        InitrdCustomizationResult result =
            await new InitrdCustomizerService(keyPath).CustomizeAsync(initrdPath);

        Assert.Equal(InitrdCustomizationOutcome.Customized, result.Outcome);

        string init = Encoding.UTF8.GetString(
            CpioNewcArchive.Find(ReadEntries(initrdPath), InitrdCustomizerService.InitEntryName)!.Content);

        Assert.Contains(rotatedKey, init);
        Assert.DoesNotContain(HostPublicKey, init);

        // 重新定制必须基于原版留档，不能在上一版产物上叠加。
        Assert.Equal(1, CountLinesContaining(init, "service.adb.tcp.port="));
    }

    /// <summary>
    /// 原版留档在首次定制后落盘，内容与定制前的 initrd 完全一致。
    /// </summary>
    [Fact]
    public async Task StockCopyPreservesOriginalImage()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        byte[] original = File.ReadAllBytes(initrdPath);

        await new InitrdCustomizerService(keyPath).CustomizeAsync(initrdPath);

        Assert.Equal(original, File.ReadAllBytes(initrdPath + InitrdCustomizerService.StockSuffix));
    }

    /// <summary>
    /// 已定制但原版留档缺失时必须报错，而不是拿定制产物冒充原版再叠一层。
    /// </summary>
    [Fact]
    public async Task MissingStockForCustomizedArtifactIsReported()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var service = new InitrdCustomizerService(keyPath);
        await service.CustomizeAsync(initrdPath);
        File.Delete(initrdPath + InitrdCustomizerService.StockSuffix);

        File.WriteAllText(keyPath, "QAAAdifferentKeyMaterialAAAAAAAAAAAAAA== user@host");

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => service.CustomizeAsync(initrdPath));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.Contains("留档缺失", error.Message);
    }

    /// <summary>
    /// 外层为 gzip 与未压缩两种 initrd 都能定制，压缩格式在产物中保持不变。
    /// </summary>
    [Theory]
    [InlineData(InitrdCompression.GZip)]
    [InlineData(InitrdCompression.None)]
    public async Task CompressionFormatIsPreserved(InitrdCompression compression)
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace, compression: compression);

        await new InitrdCustomizerService(keyPath).CustomizeAsync(initrdPath);

        byte[] customized = File.ReadAllBytes(initrdPath);
        Assert.Equal(compression, InitrdImageCodec.Detect(customized));
        Assert.NotEmpty(CpioNewcArchive.Parse(InitrdImageCodec.Decompress(customized, compression)));
    }

    /// <summary>
    /// 定制失败时不得留下临时产物。
    /// </summary>
    [Fact]
    public async Task FailureLeavesNoTemporaryArtifacts()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);

        const string NoAnchorScript = "#!/bin/busybox sh\nexec switch_root /android /init\n";
        string initrdPath = WriteInitrd(workspace, initScript: NoAnchorScript);

        await Assert.ThrowsAsync<XBearException>(
            () => new InitrdCustomizerService(keyPath).CustomizeAsync(initrdPath));

        Assert.False(File.Exists(initrdPath + ".customizing"));
        Assert.False(File.Exists(initrdPath + ".stocking"));
    }

    /// <summary>
    /// initrd 文件缺失时抛出带指引的规格异常。
    /// </summary>
    [Fact]
    public async Task MissingInitrdFileThrowsSpecException()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => new InitrdCustomizerService(keyPath).CustomizeAsync(
                Path.Combine(workspace, "absent.img")));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.NotNull(error.Remediation);
    }

    /// <summary>
    /// 策略指纹随任一要素变化，公钥、端口、APEX 挂载点、root 开关与模块加载器开关变化必须改变指纹。
    /// </summary>
    [Fact]
    public void FingerprintTracksPolicyElements()
    {
        var baseline = new InitrdCustomizationPolicy(5555, "key-a", "/apex/com.android.adbd");

        Assert.Equal(
            InitrdCustomizerService.ComputeFingerprint(baseline),
            InitrdCustomizerService.ComputeFingerprint(baseline with { }));

        Assert.NotEqual(
            InitrdCustomizerService.ComputeFingerprint(baseline),
            InitrdCustomizerService.ComputeFingerprint(baseline with { AdbPublicKey = "key-b" }));

        Assert.NotEqual(
            InitrdCustomizerService.ComputeFingerprint(baseline),
            InitrdCustomizerService.ComputeFingerprint(baseline with { AdbGuestPort = 5556 }));

        Assert.NotEqual(
            InitrdCustomizerService.ComputeFingerprint(baseline),
            InitrdCustomizerService.ComputeFingerprint(baseline with { AdbApexMountPath = "/apex/other" }));

        Assert.NotEqual(
            InitrdCustomizerService.ComputeFingerprint(baseline),
            InitrdCustomizerService.ComputeFingerprint(baseline with { EnableRoot = false }));

        Assert.NotEqual(
            InitrdCustomizerService.ComputeFingerprint(baseline),
            InitrdCustomizerService.ComputeFingerprint(baseline with { EnableModuleLoader = false }));
    }

    /// <summary>
    /// root 使能：local.prop 写入 service.adb.root=1，且在 adbd 启动前经 setprop 断言。
    /// </summary>
    [Fact]
    public async Task RootAdbBootstrapIsInjected()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        await new InitrdCustomizerService(keyPath).CustomizeAsync(initrdPath);

        string init = Encoding.UTF8.GetString(
            CpioNewcArchive.Find(ReadEntries(initrdPath), InitrdCustomizerService.InitEntryName)!.Content);

        Assert.Contains("service.adb.root=1", init);
        Assert.Contains("property_service", init);
        Assert.Contains("chroot /android /system/bin/setprop service.adb.root 1", init);
        Assert.Contains("getprop service.adb.root", init);
    }

    /// <summary>
    /// 模块加载器：开机扫描 /data/adb/modules，overlay system/ 子树并执行各阶段脚本及 sync。
    /// </summary>
    [Fact]
    public async Task ModuleLoaderBootstrapIsInjected()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        await new InitrdCustomizerService(keyPath).CustomizeAsync(initrdPath);

        string init = Encoding.UTF8.GetString(
            CpioNewcArchive.Find(ReadEntries(initrdPath), InitrdCustomizerService.InitEntryName)!.Content);

        Assert.Contains("/data/adb/modules", init);
        Assert.Contains("mount -o remount,rw /android", init);
        Assert.Contains("system overlay applied", init);
        Assert.Contains("post-fs-data.sh", init);
        Assert.Contains("service.sh", init);
        Assert.Contains("boot-completed.sh", init);
        Assert.Contains("sync", init);
    }

    /// <summary>
    /// 旧策略产物识别与重生成：当 initrd 内嵌旧策略指纹时，识别为非当前策略并基于 stock 重新生成。
    /// </summary>
    [Fact]
    public async Task OutdatedPolicyFingerprintRegeneratesFromStock()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        // 先以仅启用 ADB（未启用 root 与模块加载器）定制一次
        var oldService = new InitrdCustomizerService(keyPath, enableRoot: false, enableModuleLoader: false);
        InitrdCustomizationResult first = await oldService.CustomizeAsync(initrdPath);
        Assert.Equal(InitrdCustomizationOutcome.Customized, first.Outcome);

        string initFirst = Encoding.UTF8.GetString(
            CpioNewcArchive.Find(ReadEntries(initrdPath), InitrdCustomizerService.InitEntryName)!.Content);
        Assert.DoesNotContain("setprop service.adb.root", initFirst);
        Assert.DoesNotContain("/data/adb/modules", initFirst);

        // 再以完整产品策略定制：识别为旧策略并基于 stock 重新生成
        var newService = new InitrdCustomizerService(keyPath, enableRoot: true, enableModuleLoader: true);
        InitrdCustomizationResult second = await newService.CustomizeAsync(initrdPath);

        Assert.Equal(InitrdCustomizationOutcome.Customized, second.Outcome);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);

        string initSecond = Encoding.UTF8.GetString(
            CpioNewcArchive.Find(ReadEntries(initrdPath), InitrdCustomizerService.InitEntryName)!.Content);
        Assert.Contains("setprop service.adb.root 1", initSecond);
        Assert.Contains("/data/adb/modules", initSecond);

        // 原版留档保持不变
        Assert.True(File.Exists(initrdPath + InitrdCustomizerService.StockSuffix));
    }

    /// <summary>
    /// 可选关闭 root 与模块加载器时，脚本中不生成对应代码块。
    /// </summary>
    [Fact]
    public async Task DisabledOptionsOmitRespectiveBlocks()
    {
        string workspace = _temp.New("workspace");
        string keyPath = WriteHostKey(workspace);
        string initrdPath = WriteInitrd(workspace);

        var service = new InitrdCustomizerService(keyPath, enableRoot: false, enableModuleLoader: false);
        await service.CustomizeAsync(initrdPath);

        string init = Encoding.UTF8.GetString(
            CpioNewcArchive.Find(ReadEntries(initrdPath), InitrdCustomizerService.InitEntryName)!.Content);

        Assert.DoesNotContain("service.adb.root=1", init);
        Assert.DoesNotContain("setprop service.adb.root", init);
        Assert.DoesNotContain("/data/adb/modules", init);
        // 但基础 ADB 调试通路仍在
        Assert.Contains("service.adb.tcp.port=5555", init);
    }

    /// <summary>写出宿主 ADB 公钥文件。</summary>
    /// <param name="workspace">工作目录。</param>
    /// <returns>公钥文件路径。</returns>
    private string WriteHostKey(string workspace)
    {
        string path = Path.Combine(workspace, "adbkey.pub");
        File.WriteAllText(path, HostPublicKey + Environment.NewLine);
        return path;
    }

    /// <summary>写出最小 initrd 样本，内容为未定制前的原版。</summary>
    /// <param name="workspace">工作目录。</param>
    /// <param name="autoDetectScript">自动探测脚本内容。</param>
    /// <param name="initScript">根脚本内容。</param>
    /// <param name="compression">外层压缩格式。</param>
    /// <returns>initrd 文件路径。</returns>
    private string WriteInitrd(
        string workspace,
        string autoDetectScript = AutoDetectScript,
        string initScript = InitScript,
        InitrdCompression compression = InitrdCompression.GZip)
    {
        var entries = new List<CpioNewcEntry>
        {
            new CpioNewcEntry
            {
                Name = "init",
                Ino = 2,
                Mode = 0x81ed,
                Nlink = 1,
                Mtime = 1700000001,
                Content = Encoding.UTF8.GetBytes(initScript)
            },
            new CpioNewcEntry
            {
                Name = "scripts",
                Ino = 1,
                Mode = 0x41ed,
                Nlink = 2,
                Mtime = 1700000000,
                Content = Array.Empty<byte>()
            },
            new CpioNewcEntry
            {
                Name = InitrdCustomizerService.AutoDetectEntryName,
                Ino = 3,
                Mode = 0x81ed,
                Nlink = 1,
                Mtime = 1700000002,
                Content = Encoding.UTF8.GetBytes(autoDetectScript)
            },
            new CpioNewcEntry
            {
                Name = "bin/busybox",
                Ino = 4,
                Mode = 0x81ed,
                Nlink = 1,
                Mtime = 1700000003,
                Content = new byte[] { 0x7f, 0x45, 0x4c, 0x46, 0x02, 0x01, 0x01, 0x00 }
            },
            new CpioNewcEntry
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
        byte[] image = InitrdImageCodec.Compress(CpioNewcArchive.Build(entries), compression);
        File.WriteAllBytes(path, image);

        // 原版副本用于校验失败路径确实没有改动目标文件。
        File.WriteAllBytes(Path.Combine(workspace, "original.bin"), image);
        return path;
    }

    /// <summary>读回 initrd 的归档条目。</summary>
    /// <param name="initrdPath">initrd 文件路径。</param>
    /// <returns>归档条目列表。</returns>
    private static List<CpioNewcEntry> ReadEntries(string initrdPath)
    {
        byte[] image = File.ReadAllBytes(initrdPath);
        InitrdCompression compression = InitrdImageCodec.Detect(image);
        return CpioNewcArchive.Parse(InitrdImageCodec.Decompress(image, compression)).ToList();
    }

    /// <summary>重建与样本等价的原版字节，用于校验失败路径未改动目标文件。</summary>
    /// <param name="workspace">工作目录。</param>
    /// <returns>原版字节。</returns>
    private byte[] ExtractOriginal(string workspace) =>
        File.ReadAllBytes(Path.Combine(workspace, "original.bin"));

    /// <summary>统计文本中独立成行的目标行数量。</summary>
    /// <param name="text">待统计文本。</param>
    /// <param name="line">目标行内容，前后空白不计。</param>
    /// <returns>命中行数。</returns>
    private static int CountStandaloneLines(string text, string line) =>
        text.Split('\n').Count(l => string.Equals(l.Trim(), line, StringComparison.Ordinal));

    /// <summary>统计包含指定片段的行数。</summary>
    /// <param name="text">待统计文本。</param>
    /// <param name="fragment">目标片段。</param>
    /// <returns>命中行数。</returns>
    private static int CountLinesContaining(string text, string fragment) =>
        text.Split('\n').Count(l => l.Contains(fragment, StringComparison.Ordinal));
}