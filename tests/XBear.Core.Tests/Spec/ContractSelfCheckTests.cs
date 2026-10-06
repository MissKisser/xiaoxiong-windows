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
    public void TerminologyContainsEightTerms()
    {
        Assert.Equal(8, SpecTestHost.Terminology().Terms.Count);
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