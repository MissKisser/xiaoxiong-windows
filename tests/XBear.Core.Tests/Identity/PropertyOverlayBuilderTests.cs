using XBear.Core.Diagnostics;
using XBear.Core.Identity;
using XBear.Core.Spec;

namespace XBear.Core.Tests.Identity;

/// <summary>
/// 属性覆写生成器（PropertyOverlayBuilder）单元测试。
/// 验证 A-03、A-15、A-22 等定制项在属性生成阶段的完整性与自洽性。
/// </summary>
public sealed class PropertyOverlayBuilderTests
{
    private static DeviceProfile CreateTestProfile() =>
        DeviceProfileCatalog.Require(DeviceProfileCatalog.Vibe12ProfileId);

    [Fact]
    public void 机型族全族属性逐键自洽()
    {
        DeviceProfile profile = CreateTestProfile();
        var props = PropertyOverlayBuilder.BuildProperties(profile);

        // 顶层 ro.product.* 属性
        Assert.Equal(profile.Brand, props["ro.product.brand"]);
        Assert.Equal(profile.Manufacturer, props["ro.product.manufacturer"]);
        Assert.Equal(profile.ProductName, props["ro.product.model"]);
        Assert.Equal(profile.ProductName, props["ro.product.name"]);
        Assert.Equal(profile.Device, props["ro.product.device"]);

        // 四大分区（system, vendor, product, odm）必须完全一致自洽（INV-6 / 消解 M-15）
        foreach (string partition in new[] { "system", "vendor", "product", "odm" })
        {
            Assert.Equal(profile.Brand, props[$"ro.product.{partition}.brand"]);
            Assert.Equal(profile.Manufacturer, props[$"ro.product.{partition}.manufacturer"]);
            Assert.Equal(profile.ProductName, props[$"ro.product.{partition}.model"]);
            Assert.Equal(profile.ProductName, props[$"ro.product.{partition}.name"]);
            Assert.Equal(profile.Device, props[$"ro.product.{partition}.device"]);
        }
    }

    [Fact]
    public void 构建指纹与构建身份属性逐键对齐()
    {
        DeviceProfile profile = CreateTestProfile();
        var build = new BuildIdentitySpec
        {
            Type = "userdebug",
            Tags = "release-keys",
            Flavor = "phone_alpha-userdebug",
            BuildId = "RSV1.210329.052",
            Incremental = "20241012"
        };

        var props = PropertyOverlayBuilder.BuildProperties(profile, buildIdentity: build);

        Assert.Equal("userdebug", props["ro.build.type"]);
        Assert.Equal("release-keys", props["ro.build.tags"]);
        Assert.Equal("phone_alpha-userdebug", props["ro.build.flavor"]);
        Assert.Equal("RSV1.210329.052", props["ro.build.id"]);
        Assert.Equal("20241012", props["ro.build.version.incremental"]);

        string expectedFingerprint = $"{profile.Brand}/{profile.ProductName}/{profile.Device}:11/RSV1.210329.052/20241012:userdebug/release-keys";
        Assert.Equal(expectedFingerprint, props["ro.build.fingerprint"]);
        Assert.Equal(expectedFingerprint, props["ro.system.build.fingerprint"]);
        Assert.Equal(expectedFingerprint, props["ro.vendor.build.fingerprint"]);
        Assert.Equal(expectedFingerprint, props["ro.product.build.fingerprint"]);
        Assert.Equal(expectedFingerprint, props["ro.odm.build.fingerprint"]);
    }

    [Fact]
    public void 显式指定指纹时各分区均采用该指纹()
    {
        DeviceProfile profile = CreateTestProfile();
        const string customFp = "Custom/Device/dev:11/BUILD123/1:userdebug/release-keys";

        var props = PropertyOverlayBuilder.BuildProperties(profile, fingerprint: customFp);

        Assert.Equal(customFp, props["ro.build.fingerprint"]);
        Assert.Equal(customFp, props["ro.system.build.fingerprint"]);
        Assert.Equal(customFp, props["ro.vendor.build.fingerprint"]);
        Assert.Equal(customFp, props["ro.product.build.fingerprint"]);
        Assert.Equal(customFp, props["ro.odm.build.fingerprint"]);
    }

    [Fact]
    public void 启动链弱校验画像自洽()
    {
        DeviceProfile profile = CreateTestProfile();
        var props = PropertyOverlayBuilder.BuildProperties(profile);

        // A-22 方向 A 弱校验画像
        Assert.Equal("0", props["ro.boot.flash.locked"]);
        Assert.Equal("orange", props["ro.boot.vbmeta.device_state"]);
        Assert.Equal("orange", props["ro.boot.verifiedbootstate"]);
        Assert.Equal("disabled", props["ro.boot.veritymode"]);
        Assert.Equal("unknown", props["ro.bootloader"]);
        Assert.Equal("unknown", props["ro.bootmode"]);
        Assert.False(props.ContainsKey("ro.baseband"), "无基带时不应包含 ro.baseband");
    }

    [Fact]
    public void 显式指定启动链参数时正确透传()
    {
        DeviceProfile profile = CreateTestProfile();
        var boot = new BootIdentitySpec
        {
            FlashLocked = "0",
            VbmetaDeviceState = "orange",
            VerifiedBootState = "orange",
            VerityMode = "relaxed",
            Bootloader = "custom-uboot",
            Bootmode = "normal",
            Baseband = "modem-1.0"
        };

        var props = PropertyOverlayBuilder.BuildProperties(profile, bootIdentity: boot);

        Assert.Equal("relaxed", props["ro.boot.veritymode"]);
        Assert.Equal("custom-uboot", props["ro.bootloader"]);
        Assert.Equal("normal", props["ro.bootmode"]);
        Assert.Equal("modem-1.0", props["ro.baseband"]);
    }

    [Fact]
    public void 序列号设置时同步设置只读属性()
    {
        DeviceProfile profile = CreateTestProfile();
        const string serial = "XBSN0123456789AB";

        var props = PropertyOverlayBuilder.BuildProperties(profile, serialNo: serial);

        Assert.Equal(serial, props["ro.serialno"]);
        Assert.Equal(serial, props["ro.boot.serialno"]);
    }

    [Fact]
    public void 平台与主板属性自洽消解Bliss痕迹()
    {
        DeviceProfile profile = CreateTestProfile();
        var props = PropertyOverlayBuilder.BuildProperties(profile);

        Assert.Equal(profile.Device, props["ro.board.platform"]);
        Assert.Equal(profile.Device, props["ro.product.board"]);
    }

    [Fact]
    public void 默认渲染属性保持SwiftShader自洽()
    {
        DeviceProfile profile = CreateTestProfile();
        var props = PropertyOverlayBuilder.BuildProperties(profile, enableRenderSpoofing: false);

        // Q-06 默认保守策略：保持与底层库自报串一致，不制造虚假矛盾
        Assert.Equal(PropertyOverlayBuilder.SwiftShaderEglHardware, props["ro.hardware.egl"]);
        Assert.Equal(PropertyOverlayBuilder.PastelVulkanHardware, props["ro.hardware.vulkan"]);
    }

    [Fact]
    public void 属性值含换行符时拒绝生成()
    {
        DeviceProfile profile = CreateTestProfile();
        var invalidBoot = new BootIdentitySpec
        {
            Bootloader = "invalid\nbootloader"
        };

        Assert.Throws<XBearException>(() =>
            PropertyOverlayBuilder.BuildProperties(profile, bootIdentity: invalidBoot));
    }

    [Fact]
    public void 生成的完整buildProp文本格式正确()
    {
        DeviceProfile profile = CreateTestProfile();
        string content = PropertyOverlayBuilder.BuildBuildPropContent(profile, serialNo: "XBSN0123456789AB");

        Assert.Contains($"ro.product.brand={profile.Brand}", content);
        Assert.Contains($"ro.product.manufacturer={profile.Manufacturer}", content);
        Assert.Contains($"ro.product.model={profile.ProductName}", content);
        Assert.Contains("ro.serialno=XBSN0123456789AB", content);
        Assert.Contains("ro.boot.flash.locked=0", content);
    }
}
