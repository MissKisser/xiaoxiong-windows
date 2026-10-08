using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.Schema;
using XBear.Core.Spec;

namespace XBear.Core.Tests.Spec;

/// <summary>共享规格自身的自检，确认契约文件与强类型模型同源且一致。</summary>
public class ContractSelfCheckTests
{
    /// <summary>共享规格声明的全部 Schema 文件名。</summary>
    public static TheoryData<string> SchemaFileNames =>
        new()
        {
            SpecLoader.InstanceSchemaFileName,
            SpecLoader.ImageSchemaFileName,
            SpecLoader.SnapshotSchemaFileName,
            SpecLoader.TerminologySchemaFileName
        };

    /// <summary>术语条目必须齐备的字段。</summary>
    public static TheoryData<string> TermFieldNames =>
        new()
        {
            "id",
            "zh",
            "en",
            "meaning",
            "forbidden"
        };

    [Theory]
    [MemberData(nameof(SchemaFileNames))]
    public void SchemaFileIsWellFormedJson(string schemaFileName)
    {
        var node = JsonNode.Parse(SpecTestHost.Loader.ReadSchemaText(schemaFileName));

        Assert.NotNull(node);
        Assert.IsType<JsonObject>(node);
    }

    [Theory]
    [MemberData(nameof(SchemaFileNames))]
    public void SchemaFileIsAcceptedByTheSchemaLibrary(string schemaFileName)
    {
        var text = SpecTestHost.Loader.ReadSchemaText(schemaFileName);

        var exception = Record.Exception(() => JsonSchema.FromText(text));

        Assert.Null(exception);
    }

    [Fact]
    public void InstanceSchemaRejectsUnknownTopLevelFields()
    {
        var schema = JsonNode
            .Parse(SpecTestHost.Loader.ReadSchemaText(SpecLoader.InstanceSchemaFileName))!
            .AsObject();

        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void ImageSchemaRejectsUnknownTopLevelFields()
    {
        var schema = JsonNode
            .Parse(SpecTestHost.Loader.ReadSchemaText(SpecLoader.ImageSchemaFileName))!
            .AsObject();

        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void SnapshotSchemaRejectsUnknownTopLevelFields()
    {
        var schema = JsonNode
            .Parse(SpecTestHost.Loader.ReadSchemaText(SpecLoader.SnapshotSchemaFileName))!
            .AsObject();

        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
    }

    /// <summary>快照契约声明的必填字段。</summary>
    public static TheoryData<string> SnapshotRequiredFields =>
        new()
        {
            "schemaVersion",
            "id",
            "instanceRef",
            "imageRef",
            "createdAt",
            "state"
        };

    /// <summary>快照模型必须绑定的全部契约字段。</summary>
    public static TheoryData<string> SnapshotFieldNames =>
        new()
        {
            "schemaVersion",
            "id",
            "displayName",
            "instanceRef",
            "imageRef",
            "createdAt",
            "state",
            "parentRef",
            "note",
            "platformConfig"
        };

    [Theory]
    [MemberData(nameof(SnapshotRequiredFields))]
    public void SnapshotModelBindsEveryRequiredField(string fieldName)
    {
        var schema = JsonNode
            .Parse(SpecTestHost.Loader.ReadSchemaText(SpecLoader.SnapshotSchemaFileName))!
            .AsObject();

        Assert.True(
            schema["required"]!.AsArray().Any(node => node!.GetValue<string>() == fieldName),
            $"快照契约未把 {fieldName} 列为必填");

        Assert.NotNull(typeof(SnapshotSpec).GetProperty(PropertyName(fieldName)));
    }

    [Theory]
    [MemberData(nameof(SnapshotFieldNames))]
    public void SnapshotModelBindsEveryDeclaredField(string fieldName)
    {
        var schema = JsonNode
            .Parse(SpecTestHost.Loader.ReadSchemaText(SpecLoader.SnapshotSchemaFileName))!
            .AsObject();

        Assert.True(
            schema["properties"]!.AsObject().ContainsKey(fieldName),
            $"快照契约声明了 {fieldName} 但强类型模型未覆盖");

        var property = typeof(SnapshotSpec).GetProperty(PropertyName(fieldName));

        Assert.NotNull(property);

        var attribute = property!.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false)
            .Cast<JsonPropertyNameAttribute>()
            .Single();

        Assert.Equal(fieldName, attribute.Name);
    }

    [Fact]
    public void SnapshotFixturesPassSchema()
    {
        foreach (var fixture in new[]
                 {
                     SpecLoader.SnapshotMinimalFixtureName,
                     SpecLoader.SnapshotFullFixtureName
                 })
        {
            var result = SpecTestHost.Validator.ValidateSnapshot(
                SpecTestHost.Loader.ReadFixtureText(fixture));

            Assert.True(result.IsValid, $"{fixture}：{result.DescribeErrors()}");
        }
    }

    [Fact]
    public void MinimalSnapshotFixtureDeclaresOnlyRequiredFields()
    {
        var sample = JsonNode.Parse(SpecTestHost.SnapshotMinimalJson())!.AsObject();

        var schema = JsonNode
            .Parse(SpecTestHost.Loader.ReadSchemaText(SpecLoader.SnapshotSchemaFileName))!
            .AsObject();
        var required = schema["required"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();

        foreach (var fieldName in required)
        {
            Assert.True(sample.ContainsKey(fieldName), $"最小快照样例缺少必填字段 {fieldName}");
        }

        Assert.Equal(required.Length, sample.Count);
    }

    [Fact]
    public void SnapshotSchemaVersionIsComparableSemver()
    {
        var spec = SpecTestHost.Loader.ParseSnapshot(SpecTestHost.SnapshotFullJson());

        Assert.True(SemanticVersion.TryParse(spec.SchemaVersion, out _), spec.SchemaVersion);
    }

    [Fact]
    public void VersionFileExposesBothVersionSequences()
    {
        var document = SpecTestHost.Loader.LoadVersion();

        Assert.True(SemanticVersion.TryParse(document.Product.Version, out _));
        Assert.True(SemanticVersion.TryParse(document.Spec.Version, out _));
        Assert.NotEqual(VersionSequence.Product, VersionSequence.Spec);
        Assert.True(
            VersionSequence.Product != VersionSequence.Spec,
            "产品版本与规格版本必须是两个互不推导的序列");
        Assert.NotEmpty(document.Rules);
    }

    [Fact]
    public void VersionFileKeepsProductAndSpecVersionsIndependent()
    {
        var document = SpecTestHost.Loader.LoadVersion();

        var spec = document.ParseSpecVersion();
        var product = document.ParseProductVersion();

        var specNext = spec.Next(VersionBumpLevel.Minor);

        Assert.NotEqual(spec, specNext);
        Assert.True(
            product == document.ParseProductVersion(),
            "递增规格层版本不得带动产品版本");
    }

    [Fact]
    public void BaselineSeparatesTargetsFromMeasuredValues()
    {
        var baseline = SpecTestHost.Loader.LoadBaseline();

        Assert.NotEmpty(baseline.Metrics);
        Assert.All(baseline.Metrics, m =>
        {
            Assert.True(m.Target.HasKnownOperator());
            Assert.Null(m.Measured);
        });
        Assert.NotEmpty(baseline.FillPolicy);
    }

    [Fact]
    public void BaselineMetricUnitsComeFromTheDeclaredUnitTable()
    {
        var baseline = SpecTestHost.Loader.LoadBaseline();

        foreach (var metric in baseline.Metrics)
        {
            Assert.NotNull(metric.Target.Unit);
            Assert.True(baseline.Units.ContainsSymbol(metric.Target.Unit!));
        }
    }

    private static string PropertyName(string fieldName) =>
        char.ToUpperInvariant(fieldName[0]) + fieldName[1..];

    [Fact]
    public void WindowsInstanceFixturePassesSchema()
    {
        var result = SpecTestHost.Validator.ValidateInstance(SpecTestHost.WindowsInstanceJson());

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void AndroidInstanceFixturePassesSchema()
    {
        var result = SpecTestHost.Validator.ValidateInstance(SpecTestHost.AndroidInstanceJson());

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void ImageFixturePassesSchema()
    {
        var result = SpecTestHost.Validator.ValidateImage(SpecTestHost.ImageJson());

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void TerminologyFilePassesSchema()
    {
        var json = SpecTestHost.Loader.ReadText(
            Path.Combine(SpecTestHost.Loader.SpecRoot, SpecLoader.TerminologyFileName));

        var result = SpecTestHost.Validator.ValidateTerminology(json);

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void DesignTokensFileIsWellFormedJson()
    {
        var json = SpecTestHost.Loader.ReadText(Path.Combine(
            SpecTestHost.Loader.SpecRoot,
            SpecLoader.TokensDirectoryName,
            SpecLoader.DesignTokensFileName));

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public void TerminologyIsNonEmptyAndIdsAreUnique()
    {
        var document = SpecTestHost.Terminology();
        var declared = JsonNode.Parse(SpecTestHost.Loader.ReadText(
            Path.Combine(SpecTestHost.Loader.SpecRoot, SpecLoader.TerminologyFileName)))!;

        var ids = document.Terms.Select(t => t.Id).ToArray();

        Assert.NotEmpty(ids);
        Assert.True(
            declared["terms"]!.AsArray().Count == ids.Length,
            "加载后的术语条目数应与术语表文件声明的条目数一致");
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryTermDeclaresAllRequiredFields()
    {
        foreach (var term in SpecTestHost.Terminology().Terms)
        {
            Assert.False(string.IsNullOrWhiteSpace(term.Id), "术语缺少 id");
            Assert.False(string.IsNullOrWhiteSpace(term.Zh), $"术语 {term.Id} 缺少 zh");
            Assert.False(string.IsNullOrWhiteSpace(term.En), $"术语 {term.Id} 缺少 en");
            Assert.False(string.IsNullOrWhiteSpace(term.Meaning), $"术语 {term.Id} 缺少 meaning");
            Assert.NotEmpty(term.Forbidden);
        }
    }

    [Theory]
    [MemberData(nameof(TermFieldNames))]
    public void TermModelBindsEveryDeclaredField(string fieldName)
    {
        var json = JsonNode.Parse(SpecTestHost.Loader.ReadText(
            Path.Combine(SpecTestHost.Loader.SpecRoot, SpecLoader.TerminologyFileName)))!;
        var declared = json["terms"]!.AsArray()[0]!.AsObject();

        Assert.True(declared.ContainsKey(fieldName), $"术语表首条缺少字段 {fieldName}");

        var property = typeof(Term).GetProperty(
            char.ToUpperInvariant(fieldName[0]) + fieldName[1..]);

        Assert.NotNull(property);

        var attribute = property.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false)
            .Cast<JsonPropertyNameAttribute>()
            .Single();

        Assert.Equal(fieldName, attribute.Name);
    }
}