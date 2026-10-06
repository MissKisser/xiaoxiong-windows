using System.Text.Json.Nodes;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;
using XBear.Core.Tests.Spec;

namespace XBear.Core.Tests.Compat;

/// <summary>
/// 双向兼容测试：双方样例都必须通过本端 Schema 校验，
/// 且未知字段不会被静默吞掉。
/// </summary>
public class SchemaCompatibilityTests
{
    [Fact]
    public void AllSharedFixturesPassOwnSchema()
    {
        foreach (var fixture in new[]
                 {
                     SpecLoader.WindowsInstanceFixtureName,
                     SpecLoader.AndroidInstanceFixtureName,
                     SpecLoader.ImageFixtureName
                 })
        {
            var result = SpecTestHost.Validator.ValidateFixture(fixture);

            Assert.True(result.IsValid, $"{fixture} 未通过校验：{result.DescribeErrors()}");
        }
    }

    [Fact]
    public void AndroidSamplePassesWindowsSideSchema()
    {
        var result = SpecTestHost.Validator.ValidateInstance(SpecTestHost.AndroidInstanceJson());

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void WindowsSamplePassesAndroidSideSchema()
    {
        var result = SpecTestHost.Validator.ValidateInstance(SpecTestHost.WindowsInstanceJson());

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void UnknownTopLevelFieldIsRejected()
    {
        var sample = JsonNode.Parse(SpecTestHost.AndroidInstanceJson())!.AsObject();
        sample["androidOnlyExtension"] = "该字段不在共享 Schema 内";

        var result = SpecTestHost.Validator.ValidateInstance(sample.ToJsonString());

        Assert.False(result.IsValid, "未知字段未被拒绝，两端会静默吞掉对方扩展字段");
        Assert.Contains(result.Errors, e => e.Path == "/androidOnlyExtension");
    }

    [Fact]
    public void NestedSchemasDoNotDeclareAdditionalPropertiesFalse()
    {
        // 已知契约缺口：只有顶层封闭，嵌套对象未声明 additionalProperties: false。
        // 该行为由本测试固定下来，一旦补齐契约即刻转红，提醒同步更新判定。
        var schema = JsonNode
            .Parse(SpecTestHost.Loader.ReadSchemaText(SpecLoader.InstanceSchemaFileName))!
            .AsObject();

        var nestedObjects = new[] { "resources", "network", "deviceIdentity", "platformConfig" };

        foreach (var name in nestedObjects)
        {
            var closed = schema["properties"]![name]!["additionalProperties"];
            Assert.True(closed is null, $"{name} 已声明 additionalProperties，请更新本测试与契约说明");
        }
    }

    [Fact]
    public void NestedUnknownFieldIsCurrentlyAccepted()
    {
        // 与 NestedSchemasDoNotDeclareAdditionalPropertiesFalse 对应，记录当前实际行为。
        var sample = JsonNode.Parse(SpecTestHost.WindowsInstanceJson())!.AsObject();
        sample["resources"]!.AsObject()["gpuModel"] = "unknown";

        var result = SpecTestHost.Validator.ValidateInstance(sample.ToJsonString());

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void UnknownImageFieldIsRejected()
    {
        var sample = JsonNode.Parse(SpecTestHost.ImageJson())!.AsObject();
        sample["mirrorSites"] = "android-only";

        var result = SpecTestHost.Validator.ValidateImage(sample.ToJsonString());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "/mirrorSites");
    }

    [Fact]
    public void ViolationReportsPathAndMessage()
    {
        var sample = JsonNode.Parse(SpecTestHost.WindowsInstanceJson())!.AsObject();
        sample["network"]!.AsObject()["exposure"] = "wan";

        var result = SpecTestHost.Validator.ValidateInstance(sample.ToJsonString());

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("/network/exposure", error.Path);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.False(string.IsNullOrWhiteSpace(error.KeywordLocation));
    }

    [Fact]
    public void MultipleViolationsAreAllReported()
    {
        var sample = JsonNode.Parse(SpecTestHost.WindowsInstanceJson())!.AsObject();
        sample["network"]!.AsObject()["exposure"] = "wan";
        sample["resources"]!.AsObject()["memoryMB"] = 999999;

        var result = SpecTestHost.Validator.ValidateInstance(sample.ToJsonString());

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 2, $"应报告多条错误，实际 {result.Errors.Count} 条");
    }

    [Fact]
    public void InvalidSampleCanBeReportedAsException()
    {
        var sample = JsonNode.Parse(SpecTestHost.WindowsInstanceJson())!.AsObject();
        sample["platform"] = "linux";

        var result = SpecTestHost.Validator.ValidateInstance(sample.ToJsonString());

        var ex = Assert.Throws<XBearException>(() => { result.EnsureValid(); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void ValidSamplePassesEnsureValid()
    {
        var result = SpecTestHost.Validator.ValidateInstance(SpecTestHost.WindowsInstanceJson());

        Assert.Same(result, result.EnsureValid());
    }
}

/// <summary>
/// 双向兼容测试：跨端语义一致性，两端对同一概念的取值必须落在同一取值域内。
/// </summary>
public class SemanticConsistencyTests
{
    /// <summary>端口对外可达级别的合法取值域。</summary>
    public static TheoryData<string> AllowedExposures =>
        new()
        {
            { "loopback" },
            { "lan" },
            { "public" }
        };

    /// <summary>两端实例样例文件名。</summary>
    public static TheoryData<string> InstanceSampleFiles =>
        new()
        {
            { SpecLoader.WindowsInstanceFixtureName },
            { SpecLoader.AndroidInstanceFixtureName }
        };

    [Theory]
    [MemberData(nameof(InstanceSampleFiles))]
    public void SchemaVersionIsComparableSemver(string fixtureFileName)
    {
        var spec = SpecTestHost.Loader.ParseInstance(
            SpecTestHost.Loader.ReadFixtureText(fixtureFileName));

        var parts = spec.SchemaVersion.Split('.');

        Assert.Equal(3, parts.Length);
        Assert.All(parts, part => Assert.True(int.TryParse(part, out _), $"版本片段 {part} 非数字"));
    }

    [Fact]
    public void BothSamplesShareTheSameSchemaVersion()
    {
        var windows = SpecTestHost.Loader.ParseInstance(SpecTestHost.WindowsInstanceJson());
        var android = SpecTestHost.Loader.ParseInstance(SpecTestHost.AndroidInstanceJson());

        Assert.Equal(windows.SchemaVersion, android.SchemaVersion);
    }

    [Theory]
    [MemberData(nameof(InstanceSampleFiles))]
    public void ExposureFallsInSharedDomain(string fixtureFileName)
    {
        var spec = SpecTestHost.Loader.ParseInstance(
            SpecTestHost.Loader.ReadFixtureText(fixtureFileName));

        Assert.NotNull(spec.Network);
        Assert.Contains(spec.Network!.Exposure, new[] { "loopback", "lan", "public" });
    }

    [Theory]
    [MemberData(nameof(InstanceSampleFiles))]
    public void EveryPortForwardBindFallsInSharedDomain(string fixtureFileName)
    {
        var spec = SpecTestHost.Loader.ParseInstance(
            SpecTestHost.Loader.ReadFixtureText(fixtureFileName));

        Assert.NotNull(spec.Network);
        foreach (var forward in spec.Network!.PortForwards)
        {
            if (forward.Bind is not null)
            {
                Assert.Contains(forward.Bind, new[] { "loopback", "lan", "public" });
            }
        }
    }

    [Theory]
    [MemberData(nameof(InstanceSampleFiles))]
    public void DeviceIdentityLengthsFallInSchemaConstraints(string fixtureFileName)
    {
        var identity = SpecTestHost.Loader
            .ParseInstance(SpecTestHost.Loader.ReadFixtureText(fixtureFileName))
            .DeviceIdentity;

        Assert.NotNull(identity);

        if (identity!.SerialNo is { } serialNo)
        {
            Assert.InRange(serialNo.Length, 8, 32);
        }

        if (identity.AndroidId is { } androidId)
        {
            Assert.InRange(androidId.Length, 16, 32);
        }

        if (identity.Imei is { } imei)
        {
            Assert.InRange(imei.Length, 14, 16);
        }
    }

    [Theory]
    [MemberData(nameof(InstanceSampleFiles))]
    public void FixedAddressKeepsStableGuestIp(string fixtureFileName)
    {
        var spec = SpecTestHost.Loader.ParseInstance(
            SpecTestHost.Loader.ReadFixtureText(fixtureFileName));

        if (spec.Network?.FixedAddress is { } address)
        {
            Assert.Matches(@"^10\.0\.2\.[0-9]{1,3}$", address);
        }
    }

    [Theory]
    [MemberData(nameof(AllowedExposures))]
    public void SchemaAcceptsEveryDocumentedExposure(string exposure)
    {
        var sample = JsonNode.Parse(SpecTestHost.WindowsInstanceJson())!.AsObject();
        sample["network"]!.AsObject()["exposure"] = exposure;

        var result = SpecTestHost.Validator.ValidateInstance(sample.ToJsonString());

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Theory]
    [MemberData(nameof(InstanceSampleFiles))]
    public void SampleInstanceIdFallsInSchemaConstraints(string fixtureFileName)
    {
        var spec = SpecTestHost.Loader.ParseInstance(
            SpecTestHost.Loader.ReadFixtureText(fixtureFileName));

        Assert.Matches("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$", spec.Id);
        Assert.InRange(spec.DisplayName.Length, 1, 64);
        Assert.Contains(spec.Platform, new[] { "windows", "android" });
    }
}