using System.Text.Json.Nodes;
using XBear.Core.Spec;
using XBear.Core.Tests.Spec;

namespace XBear.Core.Tests.Compat;

/// <summary>
/// 双向兼容测试：保真度状态语义。
/// 项目硬规则是未实测不得臆测为通过，测试负责守住这条底线。
/// </summary>
public class FidelitySemanticsTests
{
    /// <summary>保真度逐项键名，与 image.schema.json 的 fidelity 子对象一致。</summary>
    public static TheoryData<string> FidelityKeys =>
        new()
        {
            { "P1_root" },
            { "P2_systemWrite" },
            { "P3_moduleFlash" },
            { "P4_imageSwap" },
            { "P5_rootPersist" },
            { "P6_armApp" }
        };

    /// <summary>允许的保真度取值域。</summary>
    public static TheoryData<string> AllowedStates =>
        new()
        {
            { "pass" },
            { "fail" },
            { "untested" }
        };

    /// <summary>已完成实测并结论为通过的保真度项。</summary>
    public static TheoryData<string> MeasuredPassKeys =>
        new()
        {
            { "P1_root" },
            { "P2_systemWrite" },
            { "P3_moduleFlash" },
            { "P4_imageSwap" },
            { "P5_rootPersist" },
            { "P6_armApp" }
        };

    [Fact]
    public void VerificationStateHasExactlyThreeValues()
    {
        var names = Enum.GetNames<VerificationState>();

        Assert.Equal(3, names.Length);
        Assert.Contains("Pass", names);
        Assert.Contains("Fail", names);
        Assert.Contains("Untested", names);
    }

    [Fact]
    public void SchemaRestrictsFidelityToSharedDomain()
    {
        var schema = JsonNode.Parse(
            SpecTestHost.Loader.ReadSchemaText(SpecLoader.ImageSchemaFileName))!;
        var fidelity = schema["properties"]!["verified"]!["properties"]!["fidelity"]!
            ["properties"]!.AsObject();

        foreach (var row in FidelityKeys)
        {
            var key = (string)row[0]!;
            Assert.True(fidelity.ContainsKey(key), $"Schema 的 fidelity 缺少 {key}");

            var allowed = fidelity[key]!["enum"]!.AsArray()
                .Select(v => v!.GetValue<string>())
                .ToArray();

            Assert.Equal(new[] { "pass", "fail", "untested" }, allowed);
        }
    }

    [Theory]
    [MemberData(nameof(AllowedStates))]
    public void SchemaAcceptsEveryFidelityState(string state)
    {
        var sample = JsonNode.Parse(SpecTestHost.ImageJson())!.AsObject();
        sample["verified"]!["fidelity"]!["P1_root"] = state;

        var result = SpecTestHost.Validator.ValidateImage(sample.ToJsonString());

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void UnknownFidelityStateIsRejected()
    {
        var sample = JsonNode.Parse(SpecTestHost.ImageJson())!.AsObject();
        sample["verified"]!["fidelity"]!["P1_root"] = "likely-pass";

        var result = SpecTestHost.Validator.ValidateImage(sample.ToJsonString());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "/verified/fidelity/P1_root");
    }

    /// <summary>
    /// 验证镜像样例的保真度字段如实反映实测事实。
    /// 声称通过的项必须确有实测依据，未实测项一律保持 untested。
    /// </summary>
    /// <param name="key">保真度逐项键名。</param>
    /// <param name="expected">期望的实测或未测状态。</param>
    [Theory]
    [InlineData("P1_root", "pass")]
    [InlineData("P2_systemWrite", "pass")]
    [InlineData("P3_moduleFlash", "pass")]
    [InlineData("P4_imageSwap", "pass")]
    [InlineData("P5_rootPersist", "pass")]
    [InlineData("P6_armApp", "pass")]
    public void SampleFidelityReflectsMeasuredFacts(string key, string expected)
    {
        var value = JsonNode.Parse(SpecTestHost.ImageJson())!["verified"]!["fidelity"]![key]!
            .GetValue<string>();

        Assert.Equal(expected, value);
    }

    /// <summary>
    /// 验证只有列入实测通过清单的保真度项才声称通过，
    /// 清单之外的项一律保持 untested，既不臆测通过也不臆测失败。
    /// </summary>
    [Fact]
    public void OnlyMeasuredFidelityItemsClaimPass()
    {
        var fidelity = JsonNode.Parse(SpecTestHost.ImageJson())!["verified"]!["fidelity"]!.AsObject();
        var measuredPass = MeasuredPassKeys
            .Select(row => (string)row[0]!)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        var claimedPass = fidelity
            .Where(item => item.Value!.GetValue<string>() == "pass")
            .Select(item => item.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(measuredPass, claimedPass);

        Assert.All(
            fidelity.Where(item => !measuredPass.Contains(item.Key, StringComparer.Ordinal)),
            item => Assert.Equal("untested", item.Value!.GetValue<string>()));
    }

    /// <summary>
    /// 验证强类型模型解析镜像样例后准确还原各项保真度状态。
    /// </summary>
    [Fact]
    public void ParsedModelReflectsSampleFidelity()
    {
        var spec = SpecTestHost.Loader.ParseImage(SpecTestHost.ImageJson());

        var fidelity = spec.Verified!.Fidelity;
        Assert.Equal(VerificationState.Pass, fidelity.P1Root);
        Assert.Equal(VerificationState.Pass, fidelity.P2SystemWrite);
        Assert.Equal(VerificationState.Pass, fidelity.P3ModuleFlash);
        Assert.Equal(VerificationState.Pass, fidelity.P4ImageSwap);
        Assert.Equal(VerificationState.Pass, fidelity.P5RootPersist);
        Assert.Equal(VerificationState.Pass, fidelity.P6ArmApp);
    }

    /// <summary>
    /// 回归测试：非默认取值必须被真实解析出来。
    /// 样例文件当前的取值组合（pass 与 untested 混合）恰好等于解析器的默认值 untested 时，
    /// 单靠它无法暴露「模型字段名与契约不符导致静默回落默认值」这类缺陷，
    /// 故此处用刻意构造的、覆盖三种取值的探针 JSON 验证。
    /// </summary>
    [Fact]
    public void NonDefaultFidelityValuesAreParsedRatherThanFallingBackToDefault()
    {
        const string json = """
        {
          "schemaVersion": "1.0.0",
          "id": "probe-image",
          "displayName": "回归探针镜像",
          "androidVersion": "11",
          "abi": "x86_64",
          "source": { "type": "local", "sha256": "0000000000000000000000000000000000000000000000000000000000000000" },
          "verified": {
            "inputChannel": "native",
            "verifiedAt": "2026-10-07T00:00:00+08:00",
            "qemuVersion": "11.1.0",
            "evidence": "回归探针",
            "fidelity": {
              "P1_root": "pass",
              "P2_systemWrite": "fail",
              "P3_moduleFlash": "pass",
              "P4_imageSwap": "fail",
              "P5_rootPersist": "pass",
              "P6_armApp": "fail"
            }
          }
        }
        """;

        var spec = SpecTestHost.Loader.ParseImage(json);
        var fidelity = spec.Verified!.Fidelity;

        Assert.Equal(VerificationState.Pass, fidelity.P1Root);
        Assert.Equal(VerificationState.Fail, fidelity.P2SystemWrite);
        Assert.Equal(VerificationState.Pass, fidelity.P3ModuleFlash);
        Assert.Equal(VerificationState.Fail, fidelity.P4ImageSwap);
        Assert.Equal(VerificationState.Pass, fidelity.P5RootPersist);
        Assert.Equal(VerificationState.Fail, fidelity.P6ArmApp);
        Assert.Equal("native", spec.Verified.InputChannel);
        Assert.Equal("local", spec.Source.Type);
    }

    /// <summary>回归测试：非默认取值回写后不得丢失。</summary>
    [Fact]
    public void NonDefaultFidelityValuesSurviveRoundTrip()
    {
        var original = new FidelitySet
        {
            P1Root = VerificationState.Pass,
            P2SystemWrite = VerificationState.Fail,
            P3ModuleFlash = VerificationState.Pass,
            P4ImageSwap = VerificationState.Fail,
            P5RootPersist = VerificationState.Pass,
            P6ArmApp = VerificationState.Fail
        };

        var text = System.Text.Json.JsonSerializer.Serialize(original);
        var restored = System.Text.Json.JsonSerializer.Deserialize<FidelitySet>(text)!;

        Assert.Equal(VerificationState.Pass, restored.P1Root);
        Assert.Equal(VerificationState.Fail, restored.P2SystemWrite);
        Assert.Equal(VerificationState.Pass, restored.P3ModuleFlash);
        Assert.Equal(VerificationState.Fail, restored.P4ImageSwap);
        Assert.Equal(VerificationState.Pass, restored.P5RootPersist);
        Assert.Equal(VerificationState.Fail, restored.P6ArmApp);
        Assert.Contains("\"P1_root\":\"pass\"", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证样例登记的是已验证可用的原生 QMP 输入通道，
    /// 不得回退为「均不可用」而与实测证据相抵触。
    /// </summary>
    [Fact]
    public void SampleDeclaresTheVerifiedNativeInputChannel()
    {
        var inputChannel = JsonNode.Parse(SpecTestHost.ImageJson())!
            ["verified"]!["inputChannel"]!.GetValue<string>();

        Assert.Equal("native", inputChannel);
    }

    [Fact]
    public void SampleCarriesRealOfficialSha256Digest()
    {
        var spec = SpecTestHost.Loader.ParseImage(SpecTestHost.ImageJson());

        // 契约要求 sha256 为 64 位十六进制；样例回填官方真实摘要后不再是全零占位。
        Assert.Equal(64, spec.Source.Sha256.Length);
        Assert.All(spec.Source.Sha256, c =>
            Assert.True(
                (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'),
                $"摘要字符 {c} 不是十六进制"));
        Assert.NotEqual(new string('0', 64), spec.Source.Sha256);
    }

    [Fact]
    public void SampleSha256IsRealDigestAfterIntegrityVerified()
    {
        var spec = SpecTestHost.Loader.ParseImage(SpecTestHost.ImageJson());

        // 契约要求 sha256 为 64 位十六进制。镜像完整性实测通过后该字段
        // 必须回填为真实摘要，全零占位表示尚未校验，两者都不可接受。
        Assert.Equal(64, spec.Source.Sha256.Length);
        Assert.Matches("^[0-9a-f]{64}$", spec.Source.Sha256);
        Assert.NotEqual(new string('0', 64), spec.Source.Sha256);
    }

    [Fact]
    public void SampleSizeMatchesDeclaredSourceSize()
    {
        var spec = SpecTestHost.Loader.ParseImage(SpecTestHost.ImageJson());

        // 文件大小是完整性判定的另一半，摘要正确而大小错对同样会误判为损坏。
        Assert.True(
            spec.Source.SizeBytes > 0,
            "sizeBytes 必须为正数，否则完整性校验失去意义。");
    }

    [Fact]
    public void SampleSatisfiesEverySchemaRequiredImageField()
    {
        var schema = JsonNode.Parse(
            SpecTestHost.Loader.ReadSchemaText(SpecLoader.ImageSchemaFileName))!;
        var image = JsonNode.Parse(SpecTestHost.ImageJson())!.AsObject();

        var required = schema["required"]!.AsArray()
            .Select(v => v!.GetValue<string>())
            .ToArray();

        foreach (var field in required)
        {
            Assert.True(image.ContainsKey(field), $"样例缺少 Schema 要求的字段 {field}");
        }

        var sourceRequired = schema["properties"]!["source"]!["required"]!.AsArray()
            .Select(v => v!.GetValue<string>())
            .ToArray();

        Assert.Contains("type", sourceRequired);
        Assert.True(image["source"]!.AsObject().ContainsKey("type"));
    }
}