using XBear.Core.Diagnostics;
using XBear.Core.Identity;
using XBear.Core.Qemu;
using XBear.Core.Spec;

namespace XBear.Core.Tests.Qemu;

/// <summary>
/// 标识与保真度到内核引导命令行映射的单元测试。
/// 该映射是实例在 guest 侧呈现设备身份与系统可写能力的唯一引导期通路，
/// 测试负责守住映射键名、取值与顺序，防止静默改写后实例以错误身份启动。
/// </summary>
public sealed class KernelCommandLineTests
{
    private const string SerialNo = "XBSN0123456789AB";
    private const string AndroidId = "a3f9c2e17b8d4056";
    private const string Imei = "861234567890123";

    private static DeviceIdentity CreateIdentity(
        string? serialNo = SerialNo,
        string? androidId = AndroidId,
        string? imei = Imei)
        => new() { SerialNo = serialNo, AndroidId = androidId, Imei = imei };

    [Fact]
    public void 标识为null时不产出任何片段()
    {
        Assert.Empty(KernelCommandLine.BuildIdentityFragments(null));
    }

    [Fact]
    public void 三个字段齐全时按固定顺序产出三个片段()
    {
        var fragments = KernelCommandLine.BuildIdentityFragments(CreateIdentity());

        Assert.Equal(
            new[]
            {
                "androidboot.serialno=" + SerialNo,
                "androidboot.android_id=" + AndroidId,
                "androidboot.imei=" + Imei,
            },
            fragments);
    }

    [Fact]
    public void 空字段不产出片段且不影响其余字段顺序()
    {
        var fragments = KernelCommandLine.BuildIdentityFragments(
            CreateIdentity(serialNo: SerialNo, androidId: null, imei: null));

        Assert.Equal(new[] { "androidboot.serialno=" + SerialNo }, fragments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 全空标识不产出任何片段(string? serialNo)
    {
        var fragments = KernelCommandLine.BuildIdentityFragments(
            CreateIdentity(serialNo: serialNo, androidId: null, imei: null));

        Assert.Empty(fragments);
    }

    [Fact]
    public void 标识取值两侧空白被裁剪()
    {
        var fragments = KernelCommandLine.BuildIdentityFragments(
            CreateIdentity(serialNo: $"  {SerialNo}  ", androidId: null, imei: null));

        Assert.Equal(new[] { "androidboot.serialno=" + SerialNo }, fragments);
    }

    /// <summary>
    /// 回归测试：引导命令行键名属于跨端约定，改写后 guest 侧不再识别，
    /// 因此键名必须被显式钉住，而不是只断言片段数量。
    /// </summary>
    [Fact]
    public void 引导键名与取值前缀固定不变()
    {
        Assert.Equal("androidboot.", KernelCommandLine.AndroidBootPropertyPrefix);
        Assert.Equal("androidboot.serialno", KernelCommandLine.SerialNoKey);
        Assert.Equal("androidboot.android_id", KernelCommandLine.AndroidIdKey);
        Assert.Equal("androidboot.imei", KernelCommandLine.ImeiKey);
        Assert.Equal("androidboot.flash.locked", KernelCommandLine.FlashLockedKey);
        Assert.Equal("androidboot.vbmeta.device_state", KernelCommandLine.VbmetaDeviceStateKey);
        Assert.Equal("androidboot.verifiedbootstate", KernelCommandLine.VerifiedBootStateKey);
        Assert.Equal("androidboot.veritymode", KernelCommandLine.VerityModeKey);
        Assert.Equal("androidboot.bootloader", KernelCommandLine.BootloaderKey);
        Assert.Equal("androidboot.baseband", KernelCommandLine.BasebandKey);
        Assert.Equal("androidboot.bootmode", KernelCommandLine.BootmodeKey);
        Assert.Equal("androidboot.hardware", KernelCommandLine.HardwareKey);
        Assert.Equal("androidboot.writable_system", KernelCommandLine.WritableSystemKey);
        Assert.Equal("1", KernelCommandLine.WritableSystemEnabledValue);
    }

    /// <summary>
    /// 回归测试：内核按空白切分命令行，标识一旦掺入空白就会被拆成额外的引导项，
    /// 导致实例以错误的标识启动，因此必须在生成阶段拒绝而不是原样透传。
    /// </summary>
    [Theory]
    [InlineData("XBSN 0001")]
    [InlineData("XBSN\"0001")]
    [InlineData("XBSN,0001")]
    [InlineData("XBSN=0001")]
    [InlineData("XBSN\\0001")]
    public void 标识取值含非法字符时抛出规格错误(string serialNo)
    {
        var exception = Assert.Throws<XBearException>(() =>
            KernelCommandLine.BuildIdentityFragments(CreateIdentity(serialNo: serialNo)));

        Assert.Equal(ErrorCategory.Spec, exception.Category);
        Assert.False(string.IsNullOrWhiteSpace(exception.Remediation));
    }

    [Fact]
    public void 系统可写实测通过时产出可写开关()
    {
        Assert.Equal(
            "androidboot.writable_system=1",
            KernelCommandLine.BuildWritableSystemFragment(VerificationState.Pass));
    }

    [Theory]
    [InlineData(VerificationState.Untested)]
    [InlineData(VerificationState.Fail)]
    public void 系统可写未实测或实测失败时不产出可写开关(VerificationState state)
    {
        Assert.Null(KernelCommandLine.BuildWritableSystemFragment(state));
    }

    [Fact]
    public void 标识与可写开关共存时标识在前且以单个空白分隔()
    {
        var commandLine = KernelCommandLine.Build(CreateIdentity(), VerificationState.Pass);

        Assert.Equal(
            "androidboot.serialno=" + SerialNo
            + " androidboot.android_id=" + AndroidId
            + " androidboot.imei=" + Imei
            + " androidboot.writable_system=1",
            commandLine);
        Assert.DoesNotContain("  ", commandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void 仅系统可写可写时只含可写开关()
    {
        var commandLine = KernelCommandLine.Build(
            CreateIdentity(serialNo: null, androidId: null, imei: null),
            VerificationState.Pass);

        Assert.Equal("androidboot.writable_system=1", commandLine);
    }

    [Theory]
    [InlineData(null, VerificationState.Untested)]
    [InlineData(null, VerificationState.Fail)]
    public void 标识缺失且系统不可写时合成结果为空串(DeviceIdentity? identity, VerificationState state)
    {
        Assert.Equal(string.Empty, KernelCommandLine.Build(identity, state));
    }

    [Fact]
    public void 合成结果是纯函数可重复调用()
    {
        var identity = CreateIdentity();

        var first = KernelCommandLine.Build(identity, VerificationState.Pass);
        var second = KernelCommandLine.Build(identity, VerificationState.Pass);

        Assert.Equal(first, second);
    }

    [Fact]
    public void 拼接空片段集合得到空串()
    {
        Assert.Equal(string.Empty, KernelCommandLine.Compose(Array.Empty<string>()));
        Assert.Equal(string.Empty, KernelCommandLine.Compose(new[] { string.Empty, null! }));
    }

    [Fact]
    public void 拼接片段时跳过空片段()
    {
        Assert.Equal(
            "androidboot.serialno=" + SerialNo + " androidboot.writable_system=1",
            KernelCommandLine.Compose(new[]
            {
                string.Empty,
                "androidboot.serialno=" + SerialNo,
                "   ",
                "androidboot.writable_system=1",
            }));
    }

    [Fact]
    public void 机型族画像下发生效弱校验启动链且硬件代号不下发()
    {
        var profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);
        var identity = CreateIdentity();

        var fragments = KernelCommandLine.BuildBootFragments(profile);

        Assert.Contains("androidboot.flash.locked=0", fragments);
        Assert.Contains("androidboot.vbmeta.device_state=orange", fragments);
        Assert.Contains("androidboot.verifiedbootstate=orange", fragments);
        Assert.Contains("androidboot.veritymode=disabled", fragments);
        Assert.Contains($"androidboot.bootloader={profile.Device}-boot-1.0", fragments);
        Assert.Contains("androidboot.bootmode=normal", fragments);
        Assert.DoesNotContain(fragments, f => f.StartsWith("androidboot.hardware="));
        Assert.DoesNotContain(fragments, f => f.StartsWith("androidboot.baseband="));

        string cmdline = KernelCommandLine.Build(identity, VerificationState.Untested, profile);
        Assert.Contains("androidboot.serialno=" + SerialNo, cmdline);
        Assert.Contains("androidboot.flash.locked=0", cmdline);
        Assert.DoesNotContain("androidboot.hardware=", cmdline);
    }

    [Fact]
    public void 显式启动链参数覆盖默认弱画像取值()
    {
        var profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);
        var bootIdentity = new BootIdentitySpec
        {
            FlashLocked = "0",
            VbmetaDeviceState = "orange",
            VerifiedBootState = "orange",
            VerityMode = "relaxed",
            Bootloader = "custom-bl-2.0",
            Bootmode = "factory",
            Baseband = "modem-xyz-1.0",
        };

        var fragments = KernelCommandLine.BuildBootFragments(profile, bootIdentity);

        Assert.Contains("androidboot.flash.locked=0", fragments);
        Assert.Contains("androidboot.vbmeta.device_state=orange", fragments);
        Assert.Contains("androidboot.verifiedbootstate=orange", fragments);
        Assert.Contains("androidboot.veritymode=relaxed", fragments);
        Assert.Contains("androidboot.bootloader=custom-bl-2.0", fragments);
        Assert.Contains("androidboot.bootmode=factory", fragments);
        Assert.Contains("androidboot.baseband=modem-xyz-1.0", fragments);
        Assert.DoesNotContain(fragments, f => f.StartsWith("androidboot.hardware="));
    }

    [Fact]
    public void 通过DeviceProfileId自动解析画像下发启动链参数()
    {
        var identity = new DeviceIdentity
        {
            SerialNo = SerialNo,
            AndroidId = AndroidId,
            DeviceProfileId = DeviceProfileCatalog.Vibe12ProfileId,
        };

        string cmdline = KernelCommandLine.Build(identity, VerificationState.Pass);

        Assert.Contains("androidboot.serialno=" + SerialNo, cmdline);
        Assert.Contains("androidboot.android_id=" + AndroidId, cmdline);
        Assert.Contains("androidboot.flash.locked=0", cmdline);
        Assert.Contains("androidboot.vbmeta.device_state=orange", cmdline);
        Assert.Contains("androidboot.verifiedbootstate=orange", cmdline);
        Assert.Contains("androidboot.veritymode=disabled", cmdline);
        Assert.Contains("androidboot.bootloader=vibe12pro-boot-1.0", cmdline);
        Assert.Contains("androidboot.bootmode=normal", cmdline);
        Assert.DoesNotContain("androidboot.hardware=", cmdline);
        Assert.Contains("androidboot.writable_system=1", cmdline);
    }

    [Fact]
    public void 未绑定画像时不产出启动链片段()
    {
        var identity = CreateIdentity();

        Assert.Empty(KernelCommandLine.BuildBootFragments(null));

        // 既无画像也无启动链规格时，整条命令行只剩标识通道，不存在机型族派生取值。
        string cmdline = KernelCommandLine.Build(identity, VerificationState.Untested);
        Assert.DoesNotContain("androidboot.hardware=", cmdline);
        Assert.DoesNotContain("androidboot.flash.locked=", cmdline);
    }

    [Fact]
    public void 硬件代号只在属性覆写层下发且命令行通道刻意不下发()
    {
        DeviceProfile profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);

        var fragments = KernelCommandLine.BuildBootFragments(profile);
        var props = PropertyOverlayBuilder.BuildProperties(profile);

        // 硬件代号压制只走属性覆写层：实机证实 androidboot.hardware 会把 guest 的 ro.hardware
        // 钉死为机型代号，击穿镜像内以原生代号硬编码的 HAL/VINTF 解析，
        // 导致 gralloc mapper 缺失、surfaceflinger 崩溃、整机不可用。命令行通道必须保持关闭。
        Assert.DoesNotContain(fragments, f => f.StartsWith("androidboot.hardware="));
        Assert.Equal(profile.Device, props["ro.hardware"]);
        Assert.Equal(profile.Device, props["ro.product.device"]);
    }

    [Fact]
    public void 启动链片段与属性覆写层的弱画像取值逐键一致()
    {
        DeviceProfile profile = DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);
        var bootIdentity = new BootIdentitySpec
        {
            FlashLocked = "0",
            VbmetaDeviceState = "orange",
            VerifiedBootState = "orange",
            VerityMode = "disabled",
            Bootloader = "vibe12pro-boot-1.0",
            Bootmode = "normal",
        };

        var fragments = KernelCommandLine.BuildBootFragments(profile, bootIdentity);
        var props = PropertyOverlayBuilder.BuildProperties(profile, bootIdentity: bootIdentity);

        // 两条通道取值同源：命令行片段去掉前缀后即为对应 ro.boot.* 的取值。
        Assert.Equal(bootIdentity.FlashLocked, Value(fragments, KernelCommandLine.FlashLockedKey));
        Assert.Equal(bootIdentity.VbmetaDeviceState, Value(fragments, KernelCommandLine.VbmetaDeviceStateKey));
        Assert.Equal(bootIdentity.VerifiedBootState, Value(fragments, KernelCommandLine.VerifiedBootStateKey));
        Assert.Equal(bootIdentity.VerityMode, Value(fragments, KernelCommandLine.VerityModeKey));
        Assert.Equal(bootIdentity.Bootloader, Value(fragments, KernelCommandLine.BootloaderKey));

        Assert.Equal(props["ro.boot.flash.locked"], Value(fragments, KernelCommandLine.FlashLockedKey));
        Assert.Equal(props["ro.boot.vbmeta.device_state"], Value(fragments, KernelCommandLine.VbmetaDeviceStateKey));
        Assert.Equal(props["ro.boot.verifiedbootstate"], Value(fragments, KernelCommandLine.VerifiedBootStateKey));
        Assert.Equal(props["ro.boot.veritymode"], Value(fragments, KernelCommandLine.VerityModeKey));
        Assert.Equal(props["ro.bootloader"], Value(fragments, KernelCommandLine.BootloaderKey));
    }

    private static string Value(IEnumerable<string> fragments, string key)
    {
        string prefix = key + "=";
        string? fragment = fragments.FirstOrDefault(f => f.StartsWith(prefix, StringComparison.Ordinal));

        Assert.NotNull(fragment);
        return fragment[prefix.Length..];
    }
}