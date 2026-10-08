using System.Text.Json;
using System.Text.Json.Nodes;
using XBear.Core.Spec;
using XBear.Core.Tests.Spec;

namespace XBear.Core.Tests.Compat;

/// <summary>
/// 快照元数据的往返测试：宿主与两端产出的快照样例都必须能被本端强类型模型完整解析，
/// 且解析后重新序列化再解析，字段集合与语义均不丢失。
/// </summary>
public class SnapshotModelRoundTripTests
{
    /// <summary>两份快照样例文件名。</summary>
    public static TheoryData<string> SnapshotFixtureFiles =>
        new()
        {
            { SpecLoader.SnapshotMinimalFixtureName },
            { SpecLoader.SnapshotFullFixtureName }
        };

    [Theory]
    [MemberData(nameof(SnapshotFixtureFiles))]
    public void SnapshotSampleParsesIntoModel(string fixtureFileName)
    {
        var spec = SpecTestHost.Loader.LoadSnapshotFixture(fixtureFileName);

        Assert.Matches(@"^\d+\.\d+\.\d+$", spec.SchemaVersion);
        Assert.Matches("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$", spec.Id);
        Assert.Matches("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$", spec.InstanceRef);
        Assert.False(string.IsNullOrWhiteSpace(spec.ImageRef));
        Assert.False(string.IsNullOrWhiteSpace(spec.CreatedAt));
    }

    [Theory]
    [MemberData(nameof(SnapshotFixtureFiles))]
    public void RoundTripKeepsEveryField(string fixtureFileName)
    {
        var json = SpecTestHost.Loader.ReadFixtureText(fixtureFileName);

        var first = SpecTestHost.Loader.ParseSnapshot(json);
        var reserialized = JsonSerializer.Serialize(first, SpecLoader.SerializerOptions);

        var originalPaths = CollectLeafPaths(JsonNode.Parse(json)!);
        var roundTripPaths = CollectLeafPaths(JsonNode.Parse(reserialized)!);

        Assert.Equal(originalPaths, roundTripPaths);
    }

    [Theory]
    [MemberData(nameof(SnapshotFixtureFiles))]
    public void RoundTripIsSemanticallyEquivalent(string fixtureFileName)
    {
        var json = SpecTestHost.Loader.ReadFixtureText(fixtureFileName);

        var first = SpecTestHost.Loader.ParseSnapshot(json);
        var reserialized = JsonSerializer.Serialize(first, SpecLoader.SerializerOptions);
        var second = SpecTestHost.Loader.ParseSnapshot(reserialized);

        var secondPass = JsonSerializer.Serialize(second, SpecLoader.SerializerOptions);

        Assert.Equal(reserialized, secondPass);
        Assert.Equal(first.SchemaVersion, second.SchemaVersion);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.DisplayName, second.DisplayName);
        Assert.Equal(first.InstanceRef, second.InstanceRef);
        Assert.Equal(first.ImageRef, second.ImageRef);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal(first.State, second.State);
        Assert.Equal(first.ParentRef, second.ParentRef);
        Assert.Equal(first.Note, second.Note);
    }

    [Theory]
    [MemberData(nameof(SnapshotFixtureFiles))]
    public void RoundTrippedSnapshotStillPassesSchema(string fixtureFileName)
    {
        var json = SpecTestHost.Loader.ReadFixtureText(fixtureFileName);

        var spec = SpecTestHost.Loader.ParseSnapshot(json);
        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        var result = SpecTestHost.Validator.ValidateSnapshot(reserialized);

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    /// <summary>
    /// 最小样例不含可选字段，写出时不得凭空补出它们，
    /// 否则会把宿主持有的缺省状态固化成契约内容。
    /// </summary>
    [Fact]
    public void MinimalSnapshotDoesNotGainOptionalFields()
    {
        var spec = SpecTestHost.Loader.LoadSnapshotFixture(SpecLoader.SnapshotMinimalFixtureName);

        Assert.Null(spec.DisplayName);
        Assert.Null(spec.ParentRef);
        Assert.Null(spec.Note);
        Assert.Null(spec.PlatformConfig);

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.DoesNotContain("displayName", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("parentRef", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("note", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("platformConfig", reserialized, StringComparison.Ordinal);
    }

    [Fact]
    public void FullSnapshotKeepsEveryOptionalField()
    {
        var spec = SpecTestHost.Loader.LoadSnapshotFixture(SpecLoader.SnapshotFullFixtureName);

        Assert.False(string.IsNullOrWhiteSpace(spec.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(spec.Note));
        Assert.False(string.IsNullOrWhiteSpace(spec.ParentRef));
        Assert.NotNull(spec.PlatformConfig);
        Assert.False(spec.IsChainRoot(), "带父快照引用的快照不是链首");
    }

    [Fact]
    public void MinimalSnapshotIsChainRoot()
    {
        var spec = SpecTestHost.Loader.LoadSnapshotFixture(SpecLoader.SnapshotMinimalFixtureName);

        Assert.True(spec.IsChainRoot());
    }

    [Fact]
    public void PlatformConfigIsPreservedVerbatim()
    {
        var json = SpecTestHost.SnapshotFullJson();

        var spec = SpecTestHost.Loader.ParseSnapshot(json);

        Assert.NotNull(spec.PlatformConfig);

        var original = JsonNode.Parse(json)!["platformConfig"]!;
        var reparsed = JsonSerializer.SerializeToNode(spec.PlatformConfig!.Value);

        Assert.True(JsonNode.DeepEquals(original, reparsed), "platformConfig 未能原样保留");
    }

    [Theory]
    [MemberData(nameof(SnapshotFixtureFiles))]
    public void CreatedAtKeepsItsTimeZoneOffset(string fixtureFileName)
    {
        var json = SpecTestHost.Loader.ReadFixtureText(fixtureFileName);

        var spec = SpecTestHost.Loader.ParseSnapshot(json);

        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$", spec.CreatedAt);
        Assert.Contains("+08:00", spec.CreatedAt, StringComparison.Ordinal);
    }

    /// <summary>
    /// 状态以小写字面量对齐契约，写出时不得退化为枚举成员名。
    /// </summary>
    [Theory]
    [MemberData(nameof(SnapshotFixtureFiles))]
    public void StateIsWrittenAsLowerCaseLiteral(string fixtureFileName)
    {
        var spec = SpecTestHost.Loader.LoadSnapshotFixture(fixtureFileName);

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.Contains($"\"state\": \"{spec.State.ToString().ToLowerInvariant()}\"", reserialized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SnapshotState.Creating, false)]
    [InlineData(SnapshotState.Ready, true)]
    [InlineData(SnapshotState.Failed, false)]
    public void OnlyReadySnapshotIsRestorable(SnapshotState state, bool restorable)
    {
        var spec = new SnapshotSpec
        {
            Id = "snap-probe-01",
            InstanceRef = "win-main-01",
            ImageRef = "bliss-os-17-x86_64",
            CreatedAt = "2026-10-07T11:20:00+08:00",
            State = state
        };

        Assert.Equal(restorable, spec.IsRestorable());

        var result = SpecTestHost.Validator.ValidateSnapshot(
            JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions));

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    /// <summary>
    /// 递归收集 JSON 文档中全部叶子节点路径与取值，用于比对往返前后的字段集合。
    /// </summary>
    private static List<string> CollectLeafPaths(JsonNode? node, string prefix = "")
    {
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
