using XBear.Core.Diagnostics;
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
}