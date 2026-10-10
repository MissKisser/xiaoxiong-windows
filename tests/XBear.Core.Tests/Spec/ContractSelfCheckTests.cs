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
            SpecLoader.TerminologySchemaFileName,
            SpecLoader.ProjectionSchemaFileName,
            SpecLoader.FileTransferSchemaFileName,
            SpecLoader.ModuleSchemaFileName,
            SpecLoader.ApplicationSchemaFileName
        };

    /// <summary>
    /// W-M4 新增的投屏、传输、模块与应用四份契约的 Schema 文件名。
    /// </summary>
    public static TheoryData<string> NewContractSchemaFileNames =>
        new()
        {
            SpecLoader.ProjectionSchemaFileName,
            SpecLoader.FileTransferSchemaFileName,
            SpecLoader.ModuleSchemaFileName,
            SpecLoader.ApplicationSchemaFileName
        };

    /// <summary>四份新契约的八份样例文件名。</summary>
    public static TheoryData<string> NewContractFixtureNames =>
        new()
        {
            SpecLoader.ProjectionMinimalFixtureName,
            SpecLoader.ProjectionFullFixtureName,
            SpecLoader.FileTransferMinimalFixtureName,
            SpecLoader.FileTransferFullFixtureName,
            SpecLoader.ModuleMinimalFixtureName,
            SpecLoader.ModuleFullFixtureName,
            SpecLoader.ApplicationMinimalFixtureName,
            SpecLoader.ApplicationFullFixtureName
        };

    /// <summary>四份新契约的最小样例文件名，只含必填字段。</summary>
    public static TheoryData<string> NewContractMinimalFixtureNames =>
        new()
        {
            SpecLoader.ProjectionMinimalFixtureName,
            SpecLoader.FileTransferMinimalFixtureName,
            SpecLoader.ModuleMinimalFixtureName,
            SpecLoader.ApplicationMinimalFixtureName
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

    /// <summary>platformConfig 契约声明的可选字段名。</summary>
    public static TheoryData<string> PlatformConfigFieldNames =>
        new()
        {
            "initrdImage",
            "kernelAppend"
        };

    /// <summary>
    /// platformConfig 内的字段一旦在契约中声明，模型必须绑定同名属性，
    /// 否则跨端写入的取值会被静默丢弃，实例按缺省值启动。
    /// </summary>
    [Theory]
    [MemberData(nameof(PlatformConfigFieldNames))]
    public void PlatformConfigModelBindsEveryDeclaredField(string fieldName)
    {
        var schema = JsonNode
            .Parse(SpecTestHost.Loader.ReadSchemaText(SpecLoader.InstanceSchemaFileName))!
            .AsObject();

        var declared = schema["properties"]!["platformConfig"]!["properties"]!.AsObject();
        Assert.True(declared.ContainsKey(fieldName), $"platformConfig 契约未声明 {fieldName}");
        Assert.Equal("string", declared[fieldName]!["type"]!.GetValue<string>());

        var property = typeof(PlatformConfig).GetProperty(PropertyName(fieldName));
        Assert.NotNull(property);

        var attribute = property!.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false)
            .Cast<JsonPropertyNameAttribute>()
            .Single();

        Assert.Equal(fieldName, attribute.Name);
    }

    /// <summary>
    /// platformConfig 是逃生舱，声明可选字段不得顺带把它封闭：
    /// 两端各自的专有字段必须继续放得下，否则会立刻打破契约的逃生舱语义。
    /// </summary>
    [Fact]
    public void PlatformConfigStaysOpenAfterDeclaringFields()
    {
        var schema = JsonNode
            .Parse(SpecTestHost.Loader.ReadSchemaText(SpecLoader.InstanceSchemaFileName))!
            .AsObject();

        Assert.Null(schema["properties"]!["platformConfig"]!["additionalProperties"]);
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

    /// <summary>
    /// 四份新契约的顶层必须封闭：任一端擅自写入未声明字段时，
    /// 另一端会静默吞掉该字段，跨端事实就此丢失。
    /// </summary>
    [Theory]
    [MemberData(nameof(NewContractSchemaFileNames))]
    public void NewContractSchemaRejectsUnknownTopLevelFields(string schemaFileName)
    {
        var schema = JsonNode
            .Parse(SpecTestHost.Loader.ReadSchemaText(schemaFileName))!
            .AsObject();

        Assert.False(schema["additionalProperties"]!.GetValue<bool>(), $"{schemaFileName} 未封闭顶层");
    }

    /// <summary>
    /// 契约新声明的字段一旦漏绑强类型属性，另一端写入的取值就会被静默丢弃，
    /// 解析后回落到缺省值而无任何告警。
    /// </summary>
    [Theory]
    [MemberData(nameof(NewContractSchemaFileNames))]
    public void NewContractModelBindsEveryDeclaredField(string schemaFileName)
    {
        var schema = JsonNode
            .Parse(SpecTestHost.Loader.ReadSchemaText(schemaFileName))!
            .AsObject();
        var modelType = NewContractModelType(schemaFileName);

        foreach (var declared in schema["properties"]!.AsObject())
        {
            var property = modelType.GetProperty(PropertyName(declared.Key));

            Assert.True(
                property is not null,
                $"{schemaFileName} 声明了 {declared.Key} 但 {modelType.Name} 未覆盖");

            var attribute = property!.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false)
                .Cast<JsonPropertyNameAttribute>()
                .Single();

            Assert.Equal(declared.Key, attribute.Name);
        }
    }

    /// <summary>
    /// 必填字段漏绑比可选字段漏绑更隐蔽：解析照样成功，缺的是契约明文要求的那个值。
    /// </summary>
    [Theory]
    [MemberData(nameof(NewContractSchemaFileNames))]
    public void NewContractModelBindsEveryRequiredField(string schemaFileName)
    {
        var schema = JsonNode
            .Parse(SpecTestHost.Loader.ReadSchemaText(schemaFileName))!
            .AsObject();
        var modelType = NewContractModelType(schemaFileName);

        foreach (var required in schema["required"]!.AsArray())
        {
            var fieldName = required!.GetValue<string>();

            Assert.True(
                modelType.GetProperty(PropertyName(fieldName)) is not null,
                $"{schemaFileName} 把 {fieldName} 列为必填但 {modelType.Name} 未覆盖");
        }
    }

    /// <summary>
    /// 应用契约的操作记录是文件内的嵌套契约，与应用记录共用同一份文件，
    /// 拆成两个文件会形成双向引用，因此嵌套部分同样要求模型逐字段绑定。
    /// </summary>
    [Fact]
    public void ApplicationOperationModelBindsEveryDeclaredField()
    {
        var schema = JsonNode
            .Parse(SpecTestHost.Loader.ReadSchemaText(SpecLoader.ApplicationSchemaFileName))!
            .AsObject();
        var operation = schema["$defs"]!["operation"]!.AsObject();

        foreach (var declared in operation["properties"]!.AsObject())
        {
            var property = typeof(ApplicationOperation).GetProperty(PropertyName(declared.Key));

            Assert.True(
                property is not null,
                $"操作契约声明了 {declared.Key} 但 ApplicationOperation 未覆盖");

            var attribute = property!.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false)
                .Cast<JsonPropertyNameAttribute>()
                .Single();

            Assert.Equal(declared.Key, attribute.Name);
        }

        foreach (var required in operation["required"]!.AsArray())
        {
            var fieldName = required!.GetValue<string>();

            Assert.True(
                typeof(ApplicationOperation).GetProperty(PropertyName(fieldName)) is not null,
                $"操作契约把 {fieldName} 列为必填但 ApplicationOperation 未覆盖");
        }
    }

    /// <summary>
    /// 八份新样例必须既能通过各自契约的 Schema 校验，也能被强类型模型接受，
    /// 后者才是两端真正能读懂对方的保证。
    /// </summary>
    [Theory]
    [MemberData(nameof(NewContractFixtureNames))]
    public void NewContractFixturePassesSchema(string fixtureFileName)
    {
        var result = SpecTestHost.Validator.ValidateFixture(fixtureFileName);

        Assert.True(result.IsValid, $"{fixtureFileName}：{result.DescribeErrors()}");
    }

    /// <summary>
    /// 最小样例只列必填字段。一旦把可选字段也写进去，
    /// 「最小样例」就不再代表另一端可能收到的最简输入。
    /// </summary>
    [Theory]
    [MemberData(nameof(NewContractMinimalFixtureNames))]
    public void MinimalNewContractFixtureDeclaresOnlyRequiredFields(string fixtureFileName)
    {
        var sample = JsonNode.Parse(SpecTestHost.Loader.ReadFixtureText(fixtureFileName))!.AsObject();
        var result = SpecTestHost.Validator.ValidateFixture(fixtureFileName);
        var schema = JsonNode
            .Parse(SpecTestHost.Loader.ReadSchemaText(result.SchemaFileName))!
            .AsObject();
        var required = schema["required"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();

        foreach (var fieldName in required)
        {
            Assert.True(sample.ContainsKey(fieldName), $"最小样例 {fixtureFileName} 缺少必填字段 {fieldName}");
        }

        Assert.Equal(required.Length, sample.Count);
    }

    /// <summary>取新契约对应的强类型模型类型。</summary>
    private static Type NewContractModelType(string schemaFileName) => schemaFileName switch
    {
        SpecLoader.ProjectionSchemaFileName => typeof(ProjectionSpec),
        SpecLoader.FileTransferSchemaFileName => typeof(FileTransferSpec),
        SpecLoader.ModuleSchemaFileName => typeof(ModuleSpec),
        SpecLoader.ApplicationSchemaFileName => typeof(ApplicationSpec),
        _ => throw new InvalidOperationException($"未登记的契约 {schemaFileName}")
    };

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

            // 实测列只接受真实测量：声明了受阻原因的指标没有测量依据，measured 必须保持 null；
            // 反过来已填实测值的指标不得再声明受阻原因，否则等于用受阻话术为占位数字背书。
            Assert.True(
                m.IsMeasured() != m.IsBlocked(),
                $"指标 {m.Id} 的 measured 与 blockedBy 必须互斥：受阻指标实测值必须为 null，已测指标不得再声明受阻");
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