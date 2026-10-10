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
                     SpecLoader.ImageFixtureName,
                     SpecLoader.SnapshotMinimalFixtureName,
                     SpecLoader.SnapshotFullFixtureName,
                     SpecLoader.ProjectionMinimalFixtureName,
                     SpecLoader.ProjectionFullFixtureName,
                     SpecLoader.FileTransferMinimalFixtureName,
                     SpecLoader.FileTransferFullFixtureName,
                     SpecLoader.ModuleMinimalFixtureName,
                     SpecLoader.ModuleFullFixtureName,
                     SpecLoader.ApplicationMinimalFixtureName,
                     SpecLoader.ApplicationFullFixtureName
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
    public void SchemaRejectsNonStringInitrdImage()
    {
        var sample = JsonNode.Parse(SpecTestHost.WindowsInstanceJson())!.AsObject();
        sample["platformConfig"]!.AsObject()["initrdImage"] = 12345;

        var result = SpecTestHost.Validator.ValidateInstance(sample.ToJsonString());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "/platformConfig/initrdImage");
    }

    [Fact]
    public void SchemaRejectsNonStringKernelAppend()
    {
        var sample = JsonNode.Parse(SpecTestHost.WindowsInstanceJson())!.AsObject();
        sample["platformConfig"]!.AsObject()["kernelAppend"] = true;

        var result = SpecTestHost.Validator.ValidateInstance(sample.ToJsonString());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "/platformConfig/kernelAppend");
    }

    [Fact]
    public void PlatformConfigAcceptsUnknownFieldsAsEscapeHatch()
    {
        // 与 NestedSchemasDoNotDeclareAdditionalPropertiesFalse 一致：
        // platformConfig 逃生舱未声明 additionalProperties: false，各端专有字段或扩展配置正常通过。
        var sample = JsonNode.Parse(SpecTestHost.WindowsInstanceJson())!.AsObject();
        sample["platformConfig"]!.AsObject()["customDebugOption"] = "active";

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

    /// <summary>两份快照样例文件名。</summary>
    public static TheoryData<string> SnapshotFixtureFiles =>
        new()
        {
            { SpecLoader.SnapshotMinimalFixtureName },
            { SpecLoader.SnapshotFullFixtureName }
        };

    [Theory]
    [MemberData(nameof(SnapshotFixtureFiles))]
    public void SnapshotFixturePassesSnapshotSchema(string fixtureFileName)
    {
        var result = SpecTestHost.Validator.ValidateSnapshot(
            SpecTestHost.Loader.ReadFixtureText(fixtureFileName));

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void UnknownSnapshotFieldIsRejected()
    {
        var sample = JsonNode.Parse(SpecTestHost.SnapshotMinimalJson())!.AsObject();
        sample["restorable"] = true;

        var result = SpecTestHost.Validator.ValidateSnapshot(sample.ToJsonString());

        Assert.False(result.IsValid, "快照契约顶层封闭，未知字段必须被拒绝");
        Assert.Contains(result.Errors, e => e.Path == "/restorable");
    }

    [Theory]
    [InlineData("creating")]
    [InlineData("ready")]
    [InlineData("failed")]
    public void SchemaAcceptsEveryDeclaredSnapshotState(string state)
    {
        var sample = JsonNode.Parse(SpecTestHost.SnapshotMinimalJson())!.AsObject();
        sample["state"] = state;

        var result = SpecTestHost.Validator.ValidateSnapshot(sample.ToJsonString());

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void UndeclaredSnapshotStateIsRejected()
    {
        var sample = JsonNode.Parse(SpecTestHost.SnapshotMinimalJson())!.AsObject();
        sample["state"] = "restoring";

        var result = SpecTestHost.Validator.ValidateSnapshot(sample.ToJsonString());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "/state");
    }

    [Fact]
    public void SnapshotCreatedAtWithoutZoneOffsetIsRejected()
    {
        var sample = JsonNode.Parse(SpecTestHost.SnapshotMinimalJson())!.AsObject();
        sample["createdAt"] = "2026-10-07T11:20:00";

        var result = SpecTestHost.Validator.ValidateSnapshot(sample.ToJsonString());

        Assert.False(result.IsValid, "创建时刻必须带时区偏移");
        Assert.Contains(result.Errors, e => e.Path == "/createdAt");
    }

    [Fact]
    public void SnapshotInstanceRefMustFallInSharedIdDomain()
    {
        var sample = JsonNode.Parse(SpecTestHost.SnapshotMinimalJson())!.AsObject();
        sample["instanceRef"] = "Win_Main_01";

        var result = SpecTestHost.Validator.ValidateSnapshot(sample.ToJsonString());

        Assert.False(result.IsValid, "实例标识的取值域两端共用，大写下划线不在域内");
        Assert.Contains(result.Errors, e => e.Path == "/instanceRef");
    }

    [Fact]
    public void BothSnapshotSamplesShareTheSameSchemaVersion()
    {
        var minimal = SpecTestHost.Loader.ParseSnapshot(SpecTestHost.SnapshotMinimalJson());
        var full = SpecTestHost.Loader.ParseSnapshot(SpecTestHost.SnapshotFullJson());

        Assert.Equal(minimal.SchemaVersion, full.SchemaVersion);
        Assert.Equal(minimal.InstanceRef, full.InstanceRef);
    }

    /// <summary>新契约样例与其所属 Schema 的对应关系，防止样例被挂到错误的契约上校验。</summary>
    public static TheoryData<string, string> NewContractFixtureSchemaMapping =>
        new()
        {
            { SpecLoader.ProjectionMinimalFixtureName, SpecLoader.ProjectionSchemaFileName },
            { SpecLoader.ProjectionFullFixtureName, SpecLoader.ProjectionSchemaFileName },
            { SpecLoader.FileTransferMinimalFixtureName, SpecLoader.FileTransferSchemaFileName },
            { SpecLoader.FileTransferFullFixtureName, SpecLoader.FileTransferSchemaFileName },
            { SpecLoader.ModuleMinimalFixtureName, SpecLoader.ModuleSchemaFileName },
            { SpecLoader.ModuleFullFixtureName, SpecLoader.ModuleSchemaFileName },
            { SpecLoader.ApplicationMinimalFixtureName, SpecLoader.ApplicationSchemaFileName },
            { SpecLoader.ApplicationFullFixtureName, SpecLoader.ApplicationSchemaFileName }
        };

    [Theory]
    [MemberData(nameof(NewContractFixtureSchemaMapping))]
    public void NewContractFixtureIsValidatedAgainstItsOwnSchema(string fixtureFileName, string schemaFileName)
    {
        var result = SpecTestHost.Validator.ValidateFixture(fixtureFileName);

        Assert.True(result.IsValid, $"{fixtureFileName}：{result.DescribeErrors()}");
        Assert.Equal(schemaFileName, result.SchemaFileName);
    }

    /// <summary>实例契约新增可选字段后，两端样例不必同步升到同一版本。</summary>
    [Fact]
    public void InstanceSampleWithoutDisplayStillPassesSchema()
    {
        var result = SpecTestHost.Validator.ValidateInstance(SpecTestHost.AndroidInstanceJson());

        Assert.True(result.IsValid, result.DescribeErrors());
        Assert.Null(SpecTestHost.Loader.ParseInstance(SpecTestHost.AndroidInstanceJson()).Display);
    }

    [Fact]
    public void DisplayRequiresBothWidthAndHeight()
    {
        var sample = JsonNode.Parse(SpecTestHost.AndroidInstanceJson())!.AsObject();
        sample["display"] = new JsonObject
        {
            ["width"] = 1920
        };

        var result = SpecTestHost.Validator.ValidateInstance(sample.ToJsonString());

        Assert.False(result.IsValid, "显示设置缺少必填的高度时不得通过校验");
    }

    [Theory]
    [InlineData("landscape")]
    [InlineData("portrait")]
    [InlineData("auto")]
    public void SchemaAcceptsEveryDeclaredScreenOrientation(string orientation)
    {
        var sample = JsonNode.Parse(SpecTestHost.WindowsInstanceJson())!.AsObject();
        sample["display"]!["orientation"] = orientation;

        var result = SpecTestHost.Validator.ValidateInstance(sample.ToJsonString());

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void UndeclaredScreenOrientationIsRejected()
    {
        var sample = JsonNode.Parse(SpecTestHost.WindowsInstanceJson())!.AsObject();
        sample["display"]!["orientation"] = "upside-down";

        var result = SpecTestHost.Validator.ValidateInstance(sample.ToJsonString());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "/display/orientation");
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

    /// <summary>实例契约新增可选字段后，两端样例不必同步升到同一版本。</summary>
    [Fact]
    public void NewerInstanceSampleIsNotBehindTheOlderOne()
    {
        var windows = SpecTestHost.Loader.ParseInstance(SpecTestHost.WindowsInstanceJson());
        var android = SpecTestHost.Loader.ParseInstance(SpecTestHost.AndroidInstanceJson());

        // 规格层向后兼容地新增可选字段时，两端样例允许停在不同版本：
        // 契约只要求两端都能解析上一版本，不要求样例同步升版。
        Assert.True(
            IsNotOlder(windows.SchemaVersion, android.SchemaVersion),
            $"带新增可选字段的样例版本 {windows.SchemaVersion} 不得落后于 {android.SchemaVersion}");
    }

    /// <summary>
    /// 判断候选版本是否不低于基线版本，逐段比较三段式版本号。
    /// </summary>
    private static bool IsNotOlder(string candidate, string baseline)
    {
        Assert.True(SemanticVersion.TryParse(candidate, out var candidateVersion), candidate);
        Assert.True(SemanticVersion.TryParse(baseline, out var baselineVersion), baseline);

        return candidateVersion.Major > baselineVersion.Major ||
               (candidateVersion.Major == baselineVersion.Major &&
                (candidateVersion.Minor > baselineVersion.Minor ||
                 (candidateVersion.Minor == baselineVersion.Minor &&
                  candidateVersion.Patch >= baselineVersion.Patch)));
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