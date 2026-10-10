using System.Text.Json;
using System.Text.Json.Nodes;
using XBear.Core.Spec;
using XBear.Core.Tests.Spec;

namespace XBear.Core.Tests.Compat;

/// <summary>
/// 双向兼容测试：对方端产出的样例必须能被本端强类型模型完整解析，
/// 且解析后重新序列化再解析，语义等价且不丢字段。
/// </summary>
public class ModelRoundTripTests
{
    /// <summary>两端实例样例文件名。</summary>
    public static TheoryData<string> InstanceSampleFiles =>
        new()
        {
            SpecLoader.WindowsInstanceFixtureName,
            SpecLoader.AndroidInstanceFixtureName
        };

    /// <summary>两端实例样例及其平台标识与实例标识。</summary>
    public static TheoryData<string, string, string> InstanceSamplesWithIdentity =>
        new()
        {
            { SpecLoader.WindowsInstanceFixtureName, "windows", "win-main-01" },
            { SpecLoader.AndroidInstanceFixtureName, "android", "and-main-01" }
        };

    [Theory]
    [MemberData(nameof(InstanceSamplesWithIdentity))]
    public void OpponentSampleParsesIntoModel(string fixtureFileName, string platform, string id)
    {
        var json = SpecTestHost.Loader.ReadFixtureText(fixtureFileName);

        var spec = SpecTestHost.Loader.ParseInstance(json);

        Assert.Equal(platform, spec.Platform);
        Assert.Equal(id, spec.Id);
        Assert.False(string.IsNullOrWhiteSpace(spec.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(spec.ImageRef));
        Assert.True(spec.Resources.MemoryMB > 0);
        Assert.True(spec.Resources.CpuCores > 0);
        Assert.True(spec.Resources.DiskGB > 0);
    }

    [Theory]
    [MemberData(nameof(InstanceSampleFiles))]
    public void PlatformConfigIsPreservedVerbatim(string fixtureFileName)
    {
        var json = SpecTestHost.Loader.ReadFixtureText(fixtureFileName);

        var spec = SpecTestHost.Loader.ParseInstance(json);

        Assert.NotNull(spec.PlatformConfig);

        var original = JsonNode.Parse(json)!["platformConfig"]!;
        var reparsed = JsonSerializer.SerializeToNode(spec.PlatformConfig);

        Assert.True(JsonNode.DeepEquals(original, reparsed), "platformConfig 未能原样保留");
    }

    [Theory]
    [MemberData(nameof(InstanceSampleFiles))]
    public void RoundTripKeepsEveryField(string fixtureFileName)
    {
        var json = SpecTestHost.Loader.ReadFixtureText(fixtureFileName);

        var first = SpecTestHost.Loader.ParseInstance(json);
        var reserialized = JsonSerializer.Serialize(first, SpecLoader.SerializerOptions);

        var originalPaths = CollectLeafPaths(JsonNode.Parse(json)!);
        var roundTripPaths = CollectLeafPaths(JsonNode.Parse(reserialized)!);

        Assert.Equal(originalPaths, roundTripPaths);
    }

    [Theory]
    [MemberData(nameof(InstanceSampleFiles))]
    public void RoundTripIsSemanticallyEquivalent(string fixtureFileName)
    {
        var json = SpecTestHost.Loader.ReadFixtureText(fixtureFileName);

        var first = SpecTestHost.Loader.ParseInstance(json);
        var reserialized = JsonSerializer.Serialize(first, SpecLoader.SerializerOptions);
        var second = SpecTestHost.Loader.ParseInstance(reserialized);

        var secondPass = JsonSerializer.Serialize(second, SpecLoader.SerializerOptions);

        Assert.Equal(reserialized, secondPass);
        Assert.Equal(first.Platform, second.Platform);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.DisplayName, second.DisplayName);
        Assert.Equal(first.ImageRef, second.ImageRef);
        Assert.Equal(first.SchemaVersion, second.SchemaVersion);
        Assert.Equal(first.Resources.MemoryMB, second.Resources.MemoryMB);
        Assert.Equal(first.Resources.CpuCores, second.Resources.CpuCores);
        Assert.Equal(first.Resources.DiskGB, second.Resources.DiskGB);
    }

    [Theory]
    [MemberData(nameof(InstanceSampleFiles))]
    public void RoundTrippedSampleStillPassesSchema(string fixtureFileName)
    {
        var json = SpecTestHost.Loader.ReadFixtureText(fixtureFileName);

        var spec = SpecTestHost.Loader.ParseInstance(json);
        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        var result = SpecTestHost.Validator.ValidateInstance(reserialized);

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Theory]
    [MemberData(nameof(InstanceSampleFiles))]
    public void DeviceIdentitySurvivesRoundTrip(string fixtureFileName)
    {
        var json = SpecTestHost.Loader.ReadFixtureText(fixtureFileName);

        var spec = SpecTestHost.Loader.ParseInstance(json);

        Assert.NotNull(spec.DeviceIdentity);
        Assert.NotNull(spec.DeviceIdentity!.SerialNo);
        Assert.NotNull(spec.DeviceIdentity.AndroidId);
        Assert.NotNull(spec.DeviceIdentity.Imei);
    }

    [Theory]
    [MemberData(nameof(InstanceSampleFiles))]
    public void NetworkSettingsSurviveRoundTrip(string fixtureFileName)
    {
        var json = SpecTestHost.Loader.ReadFixtureText(fixtureFileName);

        var spec = SpecTestHost.Loader.ParseInstance(json);

        Assert.NotNull(spec.Network);
        Assert.NotEmpty(spec.Network!.PortForwards);
        Assert.NotNull(spec.Network.Proxy);
        Assert.All(spec.Network.PortForwards, f => Assert.InRange(f.HostPort, 1, 65535));
        Assert.All(spec.Network.PortForwards, f => Assert.InRange(f.GuestPort, 1, 65535));
        Assert.All(spec.Network.PortForwards, f => Assert.Contains(f.Protocol, new[] { "tcp", "udp" }));
    }

    [Fact]
    public void WindowsSampleProxyIsPreserved()
    {
        var spec = SpecTestHost.Loader.ParseInstance(SpecTestHost.WindowsInstanceJson());

        Assert.NotNull(spec.Network?.Proxy);
        Assert.Equal("http", spec.Network!.Proxy!.Type);
        Assert.Equal("127.0.0.1", spec.Network.Proxy.Host);
        Assert.Equal(7890, spec.Network.Proxy.Port);
        Assert.Equal("loopback", spec.Network.Exposure);
        Assert.Equal("10.0.2.15", spec.Network.FixedAddress);
    }

    [Fact]
    public void AndroidSampleProxyIsParsedIndependently()
    {
        var spec = SpecTestHost.Loader.ParseInstance(SpecTestHost.AndroidInstanceJson());

        Assert.NotNull(spec.Network?.Proxy);
        Assert.Equal("socks5", spec.Network!.Proxy!.Type);
    }

    [Fact]
    public void CpuModelSurvivesRoundTrip()
    {
        var sample = JsonNode.Parse(SpecTestHost.WindowsInstanceJson())!.AsObject();
        sample["resources"]!["cpuModel"] = "qemu64";

        var first = SpecTestHost.Loader.ParseInstance(sample.ToJsonString());
        var reserialized = JsonSerializer.Serialize(first, SpecLoader.SerializerOptions);
        var second = SpecTestHost.Loader.ParseInstance(reserialized);

        Assert.Equal("qemu64", first.Resources.CpuModel);
        Assert.Equal("qemu64", second.Resources.CpuModel);
        Assert.Contains("\"cpuModel\": \"qemu64\"", reserialized, StringComparison.Ordinal);
    }

    /// <summary>
    /// 向后兼容：未声明 cpuModel 的存量实例文件必须解析为缺省值，
    /// 且再次写出时不得凭空补出该键，否则会把可选字段固化进实例文件。
    /// </summary>
    [Theory]
    [MemberData(nameof(InstanceSampleFiles))]
    public void InstanceFileWithoutCpuModelParsesToNullAndIsNotWrittenBack(string fixtureFileName)
    {
        var json = SpecTestHost.Loader.ReadFixtureText(fixtureFileName);

        var spec = SpecTestHost.Loader.ParseInstance(json);

        Assert.Null(spec.Resources.CpuModel);

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);
        Assert.DoesNotContain("cpuModel", reserialized, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTrippedCpuModelStillPassesSchema()
    {
        var sample = JsonNode.Parse(SpecTestHost.WindowsInstanceJson())!.AsObject();
        sample["resources"]!["cpuModel"] = "qemu64";

        var spec = SpecTestHost.Loader.ParseInstance(sample.ToJsonString());
        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        var result = SpecTestHost.Validator.ValidateInstance(reserialized);

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    /// <summary>
    /// 契约把 cpuModel 声明为可空字符串，缺省由平台按镜像指令集要求选型，
    /// 因此显式 null 必须同样合法，写入侧不做臆测。
    /// </summary>
    [Fact]
    public void SchemaAcceptsExplicitNullCpuModel()
    {
        var sample = JsonNode.Parse(SpecTestHost.WindowsInstanceJson())!.AsObject();
        sample["resources"]!["cpuModel"] = null;

        var result = SpecTestHost.Validator.ValidateInstance(sample.ToJsonString());

        Assert.True(result.IsValid, result.DescribeErrors());
        Assert.Null(SpecTestHost.Loader.ParseInstance(sample.ToJsonString()).Resources.CpuModel);
    }

    [Fact]
    public void InitrdImageAndKernelAppendSurviveRoundTrip()
    {
        var sample = JsonNode.Parse(SpecTestHost.WindowsInstanceJson())!.AsObject();
        sample["platformConfig"]!["initrdImage"] = @"D:\xbear\boot\initrd.img";
        sample["platformConfig"]!["kernelAppend"] = "root=/dev/ram0 quiet nomodeset custom=1";

        var first = SpecTestHost.Loader.ParseInstance(sample.ToJsonString());
        Assert.NotNull(first.PlatformConfig);
        Assert.Equal(@"D:\xbear\boot\initrd.img", first.PlatformConfig!.InitrdImage);
        Assert.Equal("root=/dev/ram0 quiet nomodeset custom=1", first.PlatformConfig.KernelAppend);

        var reserialized = JsonSerializer.Serialize(first, SpecLoader.SerializerOptions);
        var second = SpecTestHost.Loader.ParseInstance(reserialized);

        Assert.NotNull(second.PlatformConfig);
        Assert.Equal(@"D:\xbear\boot\initrd.img", second.PlatformConfig!.InitrdImage);
        Assert.Equal("root=/dev/ram0 quiet nomodeset custom=1", second.PlatformConfig.KernelAppend);
        Assert.Contains("\"initrdImage\":", reserialized, StringComparison.Ordinal);
        Assert.Contains("\"kernelAppend\":", reserialized, StringComparison.Ordinal);
    }

    /// <summary>
    /// 向后兼容：未声明 initrdImage 与 kernelAppend 的存量实例文件必须解析为 null，
    /// 且再次写出时不得凭空补出该键，确保旧版本契约文件双向兼容。
    /// </summary>
    [Fact]
    public void InstanceFileWithoutNewFieldsParsesToNullAndIsNotWrittenBack()
    {
        var sample = JsonNode.Parse(SpecTestHost.WindowsInstanceJson())!.AsObject();
        sample["platformConfig"]!.AsObject().Remove("initrdImage");
        sample["platformConfig"]!.AsObject().Remove("kernelAppend");

        var spec = SpecTestHost.Loader.ParseInstance(sample.ToJsonString());

        Assert.NotNull(spec.PlatformConfig);
        Assert.Null(spec.PlatformConfig!.InitrdImage);
        Assert.Null(spec.PlatformConfig.KernelAppend);

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);
        Assert.DoesNotContain("initrdImage", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("kernelAppend", reserialized, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsFixtureContainsInitrdImageAndKernelAppend()
    {
        var spec = SpecTestHost.Loader.ParseInstance(SpecTestHost.WindowsInstanceJson());

        Assert.NotNull(spec.PlatformConfig);
        Assert.Equal("initrd.img", spec.PlatformConfig!.InitrdImage);
        Assert.Equal("root=/dev/ram0 quiet nomodeset", spec.PlatformConfig.KernelAppend);
    }

    [Fact]
    public void WindowsFixtureContainsDisplaySettings()
    {
        var spec = SpecTestHost.Loader.ParseInstance(SpecTestHost.WindowsInstanceJson());

        Assert.NotNull(spec.Display);
        Assert.Equal(1920, spec.Display!.Width);
        Assert.Equal(1080, spec.Display.Height);
        Assert.Equal(240, spec.Display.Dpi);
        Assert.Equal("landscape", spec.Display.Orientation);
        Assert.True(spec.Display.IsLandscape());
    }

    [Fact]
    public void DisplaySurvivesRoundTrip()
    {
        var first = SpecTestHost.Loader.ParseInstance(SpecTestHost.WindowsInstanceJson());

        var reserialized = JsonSerializer.Serialize(first, SpecLoader.SerializerOptions);
        var second = SpecTestHost.Loader.ParseInstance(reserialized);

        Assert.NotNull(second.Display);
        Assert.Equal(first.Display!.Width, second.Display!.Width);
        Assert.Equal(first.Display.Height, second.Display.Height);
        Assert.Equal(first.Display.Dpi, second.Display.Dpi);
        Assert.Equal(first.Display.Orientation, second.Display.Orientation);
        Assert.Contains("\"dpi\": 240", reserialized, StringComparison.Ordinal);
    }

    /// <summary>
    /// 契约新增可选字段后，未声明该字段的存量实例文件必须解析为缺省，
    /// 且再次写出时不得凭空补出该键，否则旧版本实例文件会被静默改写。
    /// </summary>
    [Fact]
    public void InstanceFileWithoutDisplayParsesToNullAndIsNotWrittenBack()
    {
        var spec = SpecTestHost.Loader.ParseInstance(SpecTestHost.AndroidInstanceJson());

        Assert.Null(spec.Display);

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.DoesNotContain("\"display\"", reserialized, StringComparison.Ordinal);
    }

    /// <summary>
    /// 显示设置未封闭，两端各自的专有显示字段必须继续放得下；
    /// 未映射的键既不能在解析时丢掉，也不能在写出时变形。
    /// </summary>
    [Fact]
    public void DisplayKeepsUnmappedFieldsAsEscapeHatch()
    {
        var sample = JsonNode.Parse(SpecTestHost.WindowsInstanceJson())!.AsObject();
        sample["display"]!["colorMode"] = "hdr";

        var spec = SpecTestHost.Loader.ParseInstance(sample.ToJsonString());

        Assert.NotNull(spec.Display!.ExtensionData);
        Assert.True(spec.Display.ExtensionData!.ContainsKey("colorMode"));

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.Contains("\"colorMode\": \"hdr\"", reserialized, StringComparison.Ordinal);
        Assert.True(
            SpecTestHost.Validator.ValidateInstance(reserialized).IsValid,
            "显示设置的逃生舱字段必须能原样通过契约校验");
    }

    /// <summary>
    /// 显示设置的可选字段缺省时不得被补成显式 null，
    /// 否则会把「由平台按镜像的显示能力选型」这一缺省状态固化成契约内容。
    /// </summary>
    [Fact]
    public void DisplayWithoutOptionalFieldsIsNotWrittenBack()
    {
        var sample = JsonNode.Parse(SpecTestHost.AndroidInstanceJson())!.AsObject();
        sample["display"] = new JsonObject
        {
            ["width"] = 1280,
            ["height"] = 720
        };

        var spec = SpecTestHost.Loader.ParseInstance(sample.ToJsonString());

        Assert.Null(spec.Display!.Dpi);
        Assert.Null(spec.Display.Orientation);

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.DoesNotContain("dpi", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("orientation", reserialized, StringComparison.Ordinal);
        Assert.True(SpecTestHost.Validator.ValidateInstance(reserialized).IsValid);
    }

    /// <summary>
    /// 递归收集 JSON 文档中全部叶子节点路径与取值，用于比对往返前后的字段集合。
    /// </summary>
    private static List<string> CollectLeafPaths(JsonNode? node, string prefix = "")    {
        var paths = new List<string>();

        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    paths.AddRange(CollectLeafPaths(property.Value, $"{prefix}/{property.Key}"));
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    paths.AddRange(CollectLeafPaths(array[i], $"{prefix}/{i}"));
                }

                break;

            default:
                paths.Add($"{prefix}={node?.ToJsonString() ?? "null"}");
                break;
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }
}