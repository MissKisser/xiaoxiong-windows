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

        // 五大分区（system, vendor, odm, product, system_ext）必须完全一致自洽
        foreach (string partition in new[] { "system", "vendor", "odm", "product", "system_ext" })
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
        Assert.Equal(expectedFingerprint, props["ro.odm.build.fingerprint"]);
        Assert.Equal(expectedFingerprint, props["ro.product.build.fingerprint"]);
        Assert.Equal(expectedFingerprint, props["ro.system_ext.build.fingerprint"]);
        Assert.Equal(expectedFingerprint, props["ro.bootimage.build.fingerprint"]);
        Assert.Equal("RSV1.210329.052", props["ro.bootimage.build.id"]);
        Assert.Equal("release-keys", props["ro.bootimage.build.tags"]);
        Assert.Equal("userdebug", props["ro.bootimage.build.type"]);
        Assert.Equal("20241012", props["ro.bootimage.build.version.incremental"]);
        Assert.Equal("phone_alpha-userdebug 11 RSV1.210329.052 20241012 release-keys", props["ro.build.description"]);
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
        Assert.Equal(customFp, props["ro.odm.build.fingerprint"]);
        Assert.Equal(customFp, props["ro.product.build.fingerprint"]);
        Assert.Equal(customFp, props["ro.system_ext.build.fingerprint"]);
        Assert.Equal(customFp, props["ro.bootimage.build.fingerprint"]);
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
        Assert.Equal(profile.Device, props["ro.hardware"]);
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

    [Fact]
    public void 五分区与bootimage在生成文本中完整呈现()
    {
        DeviceProfile profile = CreateTestProfile();
        string content = PropertyOverlayBuilder.BuildBuildPropContent(profile);

        foreach (string partition in new[] { "system", "vendor", "odm", "product", "system_ext" })
        {
            Assert.Contains($"ro.product.{partition}.brand={profile.Brand}", content);
            Assert.Contains($"ro.{partition}.build.fingerprint=", content);
        }

        Assert.Contains("ro.bootimage.build.fingerprint=", content);
        Assert.Contains($"ro.hardware={profile.Device}", content);
        Assert.Contains("ro.build.description=", content);
    }

    [Fact]
    public void 分区级构建身份随类型标签同步覆写()
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

        // 后载入的分区会带着镜像原生的 ro.build.type 覆盖先前的取值，
        // 只覆写 ro.build.type 会让「指纹类型段 userdebug、类型属性 user」的矛盾重新出现。
        foreach (string partition in new[] { "system", "vendor", "odm", "product", "system_ext" })
        {
            Assert.Equal("userdebug", props[$"ro.{partition}.build.type"]);
            Assert.Equal("release-keys", props[$"ro.{partition}.build.tags"]);
            Assert.Equal("phone_alpha-userdebug", props[$"ro.{partition}.build.flavor"]);
            Assert.Equal("RSV1.210329.052", props[$"ro.{partition}.build.id"]);
            Assert.Equal("20241012", props[$"ro.{partition}.build.version.incremental"]);
            Assert.Equal(props["ro.build.description"], props[$"ro.{partition}.build.description"]);
        }
    }

    [Fact]
    public void 指纹类型段与类型属性恒等()
    {
        DeviceProfile profile = CreateTestProfile();
        var props = PropertyOverlayBuilder.BuildProperties(profile);

        // 指纹格式：<brand>/<model>/<device>:<release>/<buildId>/<incremental>:<type>/<tags>
        string[] segments = props["ro.build.fingerprint"].Split(':');
        Assert.Equal(3, segments.Length);
        Assert.Equal(props["ro.build.type"], segments[2].Split('/')[0]);
        Assert.Equal(props["ro.build.tags"], segments[2].Split('/')[1]);
    }

    [Fact]
    public void 覆写文本首行为可识别标记()
    {
        DeviceProfile profile = CreateTestProfile();
        string content = PropertyOverlayBuilder.BuildBuildPropContent(profile);

        Assert.StartsWith(PropertyOverlayBuilder.OverlayMarkerHeader, content, StringComparison.Ordinal);
    }

    [Fact]
    public void 根级改写键清单即判据要求的六键()
    {
        Assert.Equal(
            new[]
            {
                "ro.build.type",
                "ro.build.tags",
                "ro.boot.flash.locked",
                "ro.boot.vbmeta.device_state",
                "ro.boot.verifiedbootstate",
                "ro.boot.veritymode",
            },
            PropertyOverlayBuilder.RootPropertyRewriteKeyList);
    }

    [Fact]
    public void 根级改写取值与覆写字典逐键同源()
    {
        DeviceProfile profile = CreateTestProfile();
        var props = PropertyOverlayBuilder.BuildProperties(profile);
        var rewrites = PropertyOverlayBuilder.BuildRootPropertyRewrites(profile);

        // 改写目标值必须与追加覆写块的目标值完全一致：两套取值一旦分叉，
        // 行级改写与追加覆写会互相覆盖，构建类型与启动链画像将再次自相矛盾。
        Assert.Equal(PropertyOverlayBuilder.RootPropertyRewriteKeyList.Count, rewrites.Count);
        foreach (string key in PropertyOverlayBuilder.RootPropertyRewriteKeyList)
        {
            Assert.Equal(props[key], rewrites[key]);
        }
    }

    [Fact]
    public void 根级改写取值随身份规格同步变化()
    {
        DeviceProfile profile = CreateTestProfile();
        var build = new BuildIdentitySpec { Type = "eng", Tags = "test-keys" };
        var boot = new BootIdentitySpec
        {
            FlashLocked = "1",
            VbmetaDeviceState = "red",
            VerifiedBootState = "red",
            VerityMode = "restart"
        };

        var rewrites = PropertyOverlayBuilder.BuildRootPropertyRewrites(profile, buildIdentity: build, bootIdentity: boot);

        Assert.Equal("eng", rewrites["ro.build.type"]);
        Assert.Equal("test-keys", rewrites["ro.build.tags"]);
        Assert.Equal("1", rewrites["ro.boot.flash.locked"]);
        Assert.Equal("red", rewrites["ro.boot.vbmeta.device_state"]);
        Assert.Equal("red", rewrites["ro.boot.verifiedbootstate"]);
        Assert.Equal("restart", rewrites["ro.boot.veritymode"]);
    }

    [Fact]
    public void 根级改写键值消除构建类型与指纹的断裂()
    {
        DeviceProfile profile = CreateTestProfile();
        var identity = new IdentitySpec
        {
            SerialNo = "XBSN0123456789AB",
            AndroidId = "a1b2c3d4e5f60718",
            Mac = "3a:1f:0c:67:89:ba",
            DeviceProfile = profile.ToSpec(),
        };

        var props = PropertyOverlayBuilder.BuildProperties(identity);
        var rewrites = PropertyOverlayBuilder.BuildRootPropertyRewrites(identity);

        // 指纹类型段与被改写的根级类型键必须是同一个取值，否则一致性校验可直接命中破绽。
        string[] segments = props["ro.build.fingerprint"].Split(':');
        Assert.Equal(3, segments.Length);
        Assert.Equal(rewrites["ro.build.type"], segments[2].Split('/')[0]);
        Assert.Equal(rewrites["ro.build.tags"], segments[2].Split('/')[1]);

        // 启动链弱画像三项同样以改写清单为准。
        Assert.Equal(PropertyOverlayBuilder.DefaultFlashLocked, rewrites["ro.boot.flash.locked"]);
        Assert.Equal(PropertyOverlayBuilder.DefaultVbmetaDeviceState, rewrites["ro.boot.vbmeta.device_state"]);
        Assert.Equal(PropertyOverlayBuilder.DefaultVerifiedBootState, rewrites["ro.boot.verifiedbootstate"]);
        Assert.Equal(PropertyOverlayBuilder.DefaultVerityMode, rewrites["ro.boot.veritymode"]);
    }

    [Fact]
    public void 根级改写清单文本首行为标记且逐行可解析()
    {
        DeviceProfile profile = CreateTestProfile();
        string content = PropertyOverlayBuilder.BuildRootPropertyRewriteContent(profile);

        Assert.StartsWith(PropertyOverlayBuilder.RootPropertyRewriteMarkerHeader, content, StringComparison.Ordinal);

        string[] dataLines = content
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();

        Assert.Equal(PropertyOverlayBuilder.RootPropertyRewriteKeyList.Count, dataLines.Length);
        for (int i = 0; i < dataLines.Length; i++)
        {
            Assert.StartsWith(PropertyOverlayBuilder.RootPropertyRewriteKeyList[i] + "=", dataLines[i], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 身份契约导出的根级改写与画像导出的完全一致()
    {
        DeviceProfile profile = CreateTestProfile();
        var identity = new IdentitySpec
        {
            SerialNo = "XBSN0123456789AB",
            AndroidId = "a1b2c3d4e5f60718",
            Mac = "3a:1f:0c:67:89:ba",
            DeviceProfile = profile.ToSpec(),
        };

        Assert.Equal(
            PropertyOverlayBuilder.BuildRootPropertyRewrites(profile),
            PropertyOverlayBuilder.BuildRootPropertyRewrites(identity));
    }
}
