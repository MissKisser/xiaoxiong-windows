using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.Core.Tests.Spec;

/// <summary>规格读取器的定位与失败语义。</summary>
public class SpecLoaderTests
{
    [Fact]
    public void DefaultLoaderResolvesSpecRoot()
    {
        var root = SpecLoader.Default.SpecRoot;

        Assert.True(Directory.Exists(root), $"输出目录下未找到 spec/ 目录：{root}");
    }

    [Fact]
    public void ReadFixtureTextReturnsWindowsSample()
    {
        var json = SpecTestHost.WindowsInstanceJson();

        Assert.Contains("win-main-01", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFixtureTextReturnsAndroidSample()
    {
        var json = SpecTestHost.AndroidInstanceJson();

        Assert.Contains("and-main-01", json, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFileThrowsSpecException()
    {
        var missing = Path.Combine(SpecTestHost.Loader.SpecRoot, "schema", "absent.schema.json");

        var ex = Assert.Throws<XBearException>(() => { SpecTestHost.Loader.ReadText(missing); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void InvalidJsonThrowsSpecException()
    {
        var ex = Assert.Throws<XBearException>(
            () => { SpecTestHost.Loader.ParseInstance("{ 这不是 JSON "); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void NullDocumentThrowsSpecException()
    {
        var ex = Assert.Throws<XBearException>(() => { SpecTestHost.Loader.ParseInstance("null"); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void LoaderAcceptsExplicitRoot()
    {
        var loader = new SpecLoader(SpecTestHost.Loader.SpecRoot);

        Assert.Equal(SpecTestHost.Loader.SpecRoot, loader.SpecRoot);
    }

    [Fact]
    public void NullRootThrowsSpecException()
    {
        var ex = Assert.Throws<XBearException>(() => { _ = new SpecLoader(null!); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void TerminologyCanBeLookedUpById()
    {
        var term = SpecTestHost.Terminology().Require("fidelity");

        Assert.Equal("保真度", term.Zh);
        Assert.Contains("拟真度", term.Forbidden);
    }

    [Fact]
    public void UnknownTermIdThrowsSpecException()
    {
        var terminology = SpecTestHost.Terminology();

        Assert.Null(terminology.Find("not-a-term"));

        var ex = Assert.Throws<XBearException>(() => { terminology.Require("not-a-term"); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void SnapshotFixturesAreLocatable()
    {
        var minimal = SpecTestHost.Loader.FixturePath(SpecLoader.SnapshotMinimalFixtureName);
        var full = SpecTestHost.Loader.FixturePath(SpecLoader.SnapshotFullFixtureName);

        Assert.True(File.Exists(minimal), $"缺少最小快照样例：{minimal}");
        Assert.True(File.Exists(full), $"缺少完整快照样例：{full}");
    }

    [Fact]
    public void SnapshotSchemaAndContractFilesAreLocatable()
    {
        Assert.True(File.Exists(SpecTestHost.Loader.SchemaPath(SpecLoader.SnapshotSchemaFileName)));
        Assert.True(File.Exists(Path.Combine(
            SpecTestHost.Loader.SpecRoot,
            SpecLoader.VersionFileName)));
        Assert.True(File.Exists(Path.Combine(
            SpecTestHost.Loader.SpecRoot,
            SpecLoader.BaselineFileName)));
    }

    [Fact]
    public void SnapshotFixtureFileLoadsIntoModel()
    {
        var spec = SpecTestHost.Loader.LoadSnapshotFixture(SpecLoader.SnapshotFullFixtureName);

        Assert.False(string.IsNullOrWhiteSpace(spec.Id));
        Assert.False(string.IsNullOrWhiteSpace(spec.InstanceRef));
    }

    [Fact]
    public void SnapshotFileLoadsFromAbsolutePath()
    {
        var path = SpecTestHost.Loader.FixturePath(SpecLoader.SnapshotMinimalFixtureName);

        var spec = SpecTestHost.Loader.ParseSnapshot(SpecTestHost.Loader.ReadText(path));

        Assert.Equal(SnapshotState.Ready, spec.State);
    }

    [Fact]
    public void InvalidSnapshotJsonThrowsSpecException()
    {
        var ex = Assert.Throws<XBearException>(
            () => { SpecTestHost.Loader.ParseSnapshot("{ 不是 JSON"); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void MissingSnapshotFixtureThrowsSpecException()
    {
        var ex = Assert.Throws<XBearException>(
            () => { SpecTestHost.Loader.LoadSnapshotFixture("absent-snapshot.json"); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    /// <summary>新契约的样例文件名与其强类型模型加载入口的对应关系。</summary>
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

    [Theory]
    [MemberData(nameof(NewContractFixtureNames))]
    public void NewContractFixturesAreLocatable(string fixtureFileName)
    {
        var path = SpecTestHost.Loader.FixturePath(fixtureFileName);

        Assert.True(File.Exists(path), $"缺少样例文件：{path}");
    }

    [Fact]
    public void ProjectionFixturesLoadIntoModel()
    {
        var minimal = SpecTestHost.Loader.LoadProjectionFixture(SpecLoader.ProjectionMinimalFixtureName);
        var full = SpecTestHost.Loader.LoadProjectionFixture(SpecLoader.ProjectionFullFixtureName);

        Assert.Equal(ProjectionState.Pending, minimal.State);
        Assert.Equal(ProjectionState.Active, full.State);
        Assert.False(string.IsNullOrWhiteSpace(minimal.InstanceRef));
    }

    [Fact]
    public void FileTransferFixturesLoadIntoModel()
    {
        var minimal = SpecTestHost.Loader.LoadFileTransferFixture(SpecLoader.FileTransferMinimalFixtureName);
        var full = SpecTestHost.Loader.LoadFileTransferFixture(SpecLoader.FileTransferFullFixtureName);

        Assert.Equal(FileTransferState.Queued, minimal.State);
        Assert.Equal(ConflictPolicy.Overwrite, minimal.Overwrite);
        Assert.Equal(FileTransferState.Running, full.State);
        Assert.Equal(ConflictPolicy.Rename, full.Overwrite);
    }

    [Fact]
    public void ModuleFixturesLoadIntoModel()
    {
        var minimal = SpecTestHost.Loader.LoadModuleFixture(SpecLoader.ModuleMinimalFixtureName);
        var full = SpecTestHost.Loader.LoadModuleFixture(SpecLoader.ModuleFullFixtureName);

        Assert.Equal(ModuleInstallState.Installed, minimal.Install.State);
        Assert.Equal(ModuleInstallState.Enabled, full.Install.State);
        Assert.Equal("zygisk-next.zip", minimal.Install.SourceZip);
    }

    [Fact]
    public void ApplicationFixturesLoadIntoModel()
    {
        var minimal = SpecTestHost.Loader.LoadApplicationFixture(SpecLoader.ApplicationMinimalFixtureName);
        var full = SpecTestHost.Loader.LoadApplicationFixture(SpecLoader.ApplicationFullFixtureName);

        Assert.Equal(ApplicationInstallState.NotInstalled, minimal.InstallState);
        Assert.Equal(ApplicationInstallState.Installed, full.InstallState);
        Assert.Null(minimal.LastOperation);
        Assert.NotNull(full.LastOperation);
    }

    /// <summary>新契约的 Schema 文件名与其解析入口的对应关系。</summary>
    public static TheoryData<string> NewContractSchemaFileNames =>
        new()
        {
            SpecLoader.ProjectionSchemaFileName,
            SpecLoader.FileTransferSchemaFileName,
            SpecLoader.ModuleSchemaFileName,
            SpecLoader.ApplicationSchemaFileName
        };

    [Theory]
    [MemberData(nameof(NewContractSchemaFileNames))]
    public void NewContractSchemasAreLocatable(string schemaFileName)
    {
        var path = SpecTestHost.Loader.SchemaPath(schemaFileName);

        Assert.True(File.Exists(path), $"缺少契约文件：{path}");
    }

    [Theory]
    [MemberData(nameof(NewContractSchemaFileNames))]
    public void InvalidNewContractJsonThrowsSpecException(string schemaFileName)
    {
        var ex = Assert.Throws<XBearException>(() =>
        {
            switch (schemaFileName)
            {
                case SpecLoader.ProjectionSchemaFileName:
                    SpecTestHost.Loader.ParseProjection("{ 不是 JSON");
                    break;
                case SpecLoader.FileTransferSchemaFileName:
                    SpecTestHost.Loader.ParseFileTransfer("{ 不是 JSON");
                    break;
                case SpecLoader.ModuleSchemaFileName:
                    SpecTestHost.Loader.ParseModule("{ 不是 JSON");
                    break;
                default:
                    SpecTestHost.Loader.ParseApplication("{ 不是 JSON");
                    break;
            }
        });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void NewContractFilesLoadFromAbsolutePath()
    {
        var path = SpecTestHost.Loader.FixturePath(SpecLoader.ModuleMinimalFixtureName);

        var spec = SpecTestHost.Loader.LoadModuleFile(path);

        Assert.Equal("zygisk_next", spec.Id);
        Assert.Equal("win-main-01", spec.InstanceRef);
    }
}

/// <summary>版本契约读取与两个独立递增序列的暴露方式。</summary>
public class VersionContractTests
{
    [Fact]
    public void VersionFileLoads()
    {
        var document = SpecTestHost.Loader.LoadVersion();

        Assert.False(string.IsNullOrWhiteSpace(document.Product.Id));
        Assert.False(string.IsNullOrWhiteSpace(document.Product.NameZh));
        Assert.False(string.IsNullOrWhiteSpace(document.Product.NameEn));
        Assert.False(string.IsNullOrWhiteSpace(document.Spec.Description));
    }

    [Fact]
    public void ProductAndSpecVersionsAreTwoSeparateSequences()
    {
        var document = SpecTestHost.Loader.LoadVersion();

        var product = document.ParseProductVersion();
        var spec = document.ParseSpecVersion();

        Assert.Matches(@"^\d+\.\d+\.\d+$", document.Product.Version);
        Assert.Matches(@"^\d+\.\d+\.\d+$", document.Spec.Version);
        Assert.Equal(document.Product.Version, product.ToString());
        Assert.Equal(document.Spec.Version, spec.ToString());
    }

    [Fact]
    public void EachRuleBumpsOnlyItsOwnSequence()
    {
        var document = SpecTestHost.Loader.LoadVersion();

        Assert.NotEmpty(document.Rules);

        foreach (var rule in document.Rules)
        {
            var sequence = rule.AffectedSequence();

            Assert.True(rule.TryResolveLevel(out _), $"递增级别 {rule.Level} 未定义");
            Assert.NotNull(sequence);
            Assert.Equal(
                rule.Level.StartsWith("product-", StringComparison.Ordinal)
                    ? VersionSequence.Product
                    : VersionSequence.Spec,
                sequence!.Value);
        }
    }

    [Fact]
    public void ContractDeclaresEveryKnownRuleLevel()
    {
        var document = SpecTestHost.Loader.LoadVersion();
        var declared = document.Rules.Select(r => r.Level).ToArray();

        Assert.All(VersionBumpRules.AllLevels, level => Assert.Contains(level, declared));
    }

    [Fact]
    public void TerminologyVersioningIsDescribedIndependently()
    {
        var document = SpecTestHost.Loader.LoadVersion();

        Assert.False(string.IsNullOrWhiteSpace(document.Terminology.VersionField));
        Assert.False(string.IsNullOrWhiteSpace(document.Terminology.Rule));
        Assert.False(string.IsNullOrWhiteSpace(document.Terminology.Relationship));
    }

    [Fact]
    public void BuildIdIsComposedFromDeclaredFields()
    {
        var document = SpecTestHost.Loader.LoadVersion();

        var buildId = document.FormatBuildId("abc1234");

        Assert.Equal($"{document.Product.Version}+abc1234", buildId);
        Assert.DoesNotContain("{", buildId, StringComparison.Ordinal);
    }

    [Fact]
    public void RulesAreLookupableByLevel()
    {
        var document = SpecTestHost.Loader.LoadVersion();

        var rule = document.RequireRule(document.Rules[0].Level);

        Assert.False(string.IsNullOrWhiteSpace(rule.When));
        Assert.NotEmpty(rule.Effects);
    }

    [Fact]
    public void UnknownRuleLevelThrowsSpecException()
    {
        var document = SpecTestHost.Loader.LoadVersion();

        Assert.Null(document.FindRule("no-such-level"));

        var ex = Assert.Throws<XBearException>(() => { document.RequireRule("no-such-level"); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    /// <summary>递增规则名与期望的作用序列。</summary>
    public static TheoryData<string, VersionSequence> RuleLevels =>
        new()
        {
            { "major", VersionSequence.Spec },
            { "minor", VersionSequence.Spec },
            { "patch", VersionSequence.Spec },
            { "product-major", VersionSequence.Product },
            { "product-minor", VersionSequence.Product },
            { "product-patch", VersionSequence.Product }
        };

    [Theory]
    [MemberData(nameof(RuleLevels))]
    public void KnownRuleLevelsMapToSequences(string level, VersionSequence expected)
    {
        Assert.True(VersionBumpRules.TryParseLevel(level, out var parsed));
        Assert.True(VersionBumpRules.TryGetSequence(parsed, out var sequence));
        Assert.Equal(expected, sequence);
    }

    [Theory]
    [InlineData("1.2.0", VersionBumpLevel.Minor, "1.3.0")]
    [InlineData("1.2.0", VersionBumpLevel.ProductMajor, "2.0.0")]
    [InlineData("1.2.3", VersionBumpLevel.Patch, "1.2.4")]
    [InlineData("1.2.3", VersionBumpLevel.ProductMinor, "1.3.0")]
    public void SemanticVersionAdvancesExpectedSegment(string text, VersionBumpLevel level, string expected)
    {
        var version = SemanticVersion.Parse(text);

        Assert.Equal(expected, version.Next(level).ToString());
    }

    /// <summary>不是三段式语义版本的文本。</summary>
    public static TheoryData<string?> MalformedVersions =>
        new()
        {
            { null },
            { string.Empty },
            { "1.2" },
            { "1.2.3.4" },
            { "v1.2.3" },
            { "1.2.x" },
            { "-1.2.3" }
        };

    [Theory]
    [MemberData(nameof(MalformedVersions))]
    public void MalformedVersionTextIsRejected(string? text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));

        var ex = Assert.Throws<XBearException>(() => { SemanticVersion.Parse(text); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void SemanticVersionsCompareBySegment()
    {
        Assert.Equal(SemanticVersion.Parse("1.2.3"), SemanticVersion.Parse("1.2.3"));
        Assert.NotEqual(SemanticVersion.Parse("1.2.3"), SemanticVersion.Parse("1.2.4"));
        Assert.Equal(new[] { 1, 2, 3 }, SegmentsOf(SemanticVersion.Parse("1.2.3")));
        Assert.Equal(new[] { 2, 0, 0 }, SegmentsOf(SemanticVersion.Parse("1.9.9").Next(VersionBumpLevel.Major)));
    }

    private static int[] SegmentsOf(SemanticVersion version) =>
        new[] { version.Major, version.Minor, version.Patch };

    [Fact]
    public void VersionFilePassesStructuralValidation()
    {
        var result = SpecTestHost.Validator.ValidateVersion(SpecTestHost.VersionJson());

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void VersionDocumentWithBrokenRuleLevelIsReported()
    {
        var sample = System.Text.Json.Nodes.JsonNode.Parse(SpecTestHost.VersionJson())!.AsObject();
        sample["rules"]!.AsArray()[0]!["level"] = "epoch";

        var result = SpecTestHost.Validator.ValidateVersion(sample.ToJsonString());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "/rules/0/level");
    }

    [Fact]
    public void VersionDocumentWithNonSemanticVersionIsReported()
    {
        var sample = System.Text.Json.Nodes.JsonNode.Parse(SpecTestHost.VersionJson())!.AsObject();
        sample["product"]!["version"] = "最新版";

        var result = SpecTestHost.Validator.ValidateVersion(sample.ToJsonString());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "/product/version");
    }

    [Fact]
    public void InvalidVersionJsonThrowsSpecException()
    {
        var ex = Assert.Throws<XBearException>(
            () => { SpecTestHost.Loader.ParseVersion("{ 坏的版本契约"); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }
}

/// <summary>性能基线读取、目标门限与实测值的分列语义。</summary>
public class BaselineContractTests
{
    [Fact]
    public void BaselineFileLoads()
    {
        var baseline = SpecTestHost.Loader.LoadBaseline();

        Assert.Matches(@"^\d+\.\d+\.\d+$", baseline.Version);
        Assert.False(string.IsNullOrWhiteSpace(baseline.StatusNote));
        Assert.NotEmpty(baseline.FillPolicy);
    }

    [Fact]
    public void EveryMetricDeclaresATarget()
    {
        var baseline = SpecTestHost.Loader.LoadBaseline();

        Assert.NotEmpty(baseline.Metrics);

        foreach (var metric in baseline.Metrics)
        {
            Assert.False(string.IsNullOrWhiteSpace(metric.Id), "指标缺少 id");
            Assert.False(string.IsNullOrWhiteSpace(metric.Name), $"指标 {metric.Id} 缺少 name");
            Assert.False(string.IsNullOrWhiteSpace(metric.Measure), $"指标 {metric.Id} 缺少 measure");
            Assert.False(string.IsNullOrWhiteSpace(metric.Owner), $"指标 {metric.Id} 缺少 owner");
            Assert.True(metric.Target.HasKnownOperator(), $"指标 {metric.Id} 的运算符未定义");
        }
    }

    [Fact]
    public void MetricIdentifiersAreUnique()
    {
        var ids = SpecTestHost.Loader.LoadBaseline().Metrics.Select(m => m.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryTargetUnitIsDeclaredInTheUnitTable()
    {
        var baseline = SpecTestHost.Loader.LoadBaseline();

        foreach (var metric in baseline.Metrics)
        {
            Assert.True(
                baseline.Units.ContainsSymbol(metric.Target.Unit ?? string.Empty),
                $"指标 {metric.Id} 的单位 {metric.Target.Unit} 未在单位表中声明");
        }
    }

    /// <summary>
    /// 实测列只装真实测量：没有测量依据的指标必须保持 null 并写明 blockedBy，
    /// 已回填实测值的指标不得用 0 或默认值占位，也不得同时挂受阻话术。
    /// </summary>
    [Fact]
    public void MeasuredColumnOnlyHoldsRealMeasurements()
    {
        var baseline = SpecTestHost.Loader.LoadBaseline();

        Assert.All(baseline.Metrics, m => Assert.True(
            m.IsMeasured() != m.IsBlocked(),
            $"指标 {m.Id} 的 measured 与 blockedBy 必须互斥"));
        Assert.All(
            baseline.BlockedMetrics(),
            m => Assert.Null(m.MeasuredMeetsTarget()));
        Assert.Equal(
            baseline.Metrics.Count(m => !m.IsMeasured()),
            baseline.PendingMetrics().Count);
    }

    /// <summary>
    /// 受阻口径随实测定向收敛：没有实测值的指标必须逐条声明受阻原因，
    /// 已回填实测值的指标则不得再声明受阻。两集合恒等，因此受阻集合允许为空——
    /// 全部指标都有实测值时不应凭空造出一条受阻理由；
    /// 但只要将来新增指标或某项退回未测，受阻集合就必须与待测集合逐条对应。
    /// </summary>
    [Fact]
    public void BlockedMetricsCarryAReason()
    {
        var baseline = SpecTestHost.Loader.LoadBaseline();
        var blocked = baseline.BlockedMetrics();

        Assert.Equal(
            baseline.PendingMetrics().Select(m => m.Id).OrderBy(id => id, StringComparer.Ordinal),
            blocked.Select(m => m.Id).OrderBy(id => id, StringComparer.Ordinal));

        Assert.All(blocked, m =>
        {
            Assert.True(m.IsBlocked());
            Assert.False(string.IsNullOrWhiteSpace(m.BlockedBy));
        });
    }

    [Fact]
    public void MetricsAreLookupableById()
    {
        var baseline = SpecTestHost.Loader.LoadBaseline();
        var first = baseline.Metrics[0];

        Assert.Same(first, baseline.Find(first.Id));
        Assert.Same(first, baseline.Require(first.Id));
        Assert.Null(baseline.Find("no-such-metric"));
    }

    [Fact]
    public void UnknownMetricIdThrowsSpecException()
    {
        var baseline = SpecTestHost.Loader.LoadBaseline();

        var ex = Assert.Throws<XBearException>(() => { baseline.Require("no-such-metric"); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    /// <summary>门限运算符、阈值、实测取值与达标结论。</summary>
    public static TheoryData<string, double, double, bool> ThresholdCases =>
        new()
        {
            { ComparisonOperators.LessOrEqual, 60, 60, true },
            { ComparisonOperators.LessOrEqual, 60, 61, false },
            { ComparisonOperators.LessOrEqual, 60, 59, true },
            { ComparisonOperators.GreaterOrEqual, 30, 30, true },
            { ComparisonOperators.GreaterOrEqual, 30, 29, false },
            { ComparisonOperators.GreaterOrEqual, 30, 31, true }
        };

    [Theory]
    [MemberData(nameof(ThresholdCases))]
    public void MeasuredValueIsComparedAgainstTarget(string op, double threshold, double measured, bool expected)
    {
        var bound = new BaselineThreshold { Op = op, Value = threshold };

        Assert.Equal(expected, bound.Matches(measured));
    }

    [Fact]
    public void UnknownComparisonOperatorThrowsSpecException()
    {
        var threshold = new BaselineThreshold { Op = "≈", Value = 1 };

        Assert.False(threshold.HasKnownOperator());
        Assert.Throws<XBearException>(() => { threshold.Matches(1); });
    }

    [Fact]
    public void MeasuredValueDrivesTargetComparison()
    {
        var metric = SpecTestHost.Loader.LoadBaseline().Metrics[0];
        var baselineValue = metric.Target.Value;

        metric.Measured = baselineValue;
        Assert.True(metric.MeasuredMeetsTarget());

        metric.Measured = metric.Target.Op == ComparisonOperators.LessOrEqual
            ? baselineValue + 1
            : baselineValue - 1;
        Assert.False(metric.MeasuredMeetsTarget());
    }

    [Fact]
    public void DensityProfilesBindToHostMemory()
    {
        var density = SpecTestHost.Loader.LoadBaseline().Density;

        Assert.NotEmpty(density.Profiles);

        foreach (var profile in density.Profiles)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.Tier));
            Assert.True(profile.HostMemoryGB > 0, $"档位 {profile.Tier} 未绑定宿主内存");
            Assert.True(profile.TargetInstances.HasKnownOperator());
            Assert.False(profile.IsMeasured());
        }
    }

    [Fact]
    public void DensityProfilesAreLookupableByTier()
    {
        var density = SpecTestHost.Loader.LoadBaseline().Density;
        var first = density.Profiles[0];

        Assert.Same(first, density.RequireTier(first.Tier));
        Assert.Throws<XBearException>(() => { density.RequireTier("no-such-tier"); });
    }

    [Fact]
    public void DensityRegressionCapsDegradation()
    {
        var regression = SpecTestHost.Loader.LoadBaseline().Density.Regression;

        Assert.True(regression.MaxDegradationPercent > 0);
        Assert.True(regression.AcceptsDegradation(regression.MaxDegradationPercent));
        Assert.False(regression.AcceptsDegradation(regression.MaxDegradationPercent + 1));
    }

    [Fact]
    public void UnitsAreLookupableBySymbol()
    {
        var units = SpecTestHost.Loader.LoadBaseline().Units;
        var declared = units.Kinds().Where(k => !string.IsNullOrWhiteSpace(k.Symbol)).ToArray();

        Assert.NotEmpty(declared);
        Assert.All(declared, kind =>
        {
            Assert.Equal(kind.Name, units.FindBySymbol(kind.Symbol));
            Assert.True(units.ContainsSymbol(kind.Symbol));
        });
        Assert.Null(units.FindBySymbol("PB"));
    }

    [Fact]
    public void BaselinePassesStructuralValidation()
    {
        var result = SpecTestHost.Validator.ValidateBaseline(SpecTestHost.BaselineJson());

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void BaselineWithUndeclaredUnitIsReported()
    {
        var sample = System.Text.Json.Nodes.JsonNode.Parse(SpecTestHost.BaselineJson())!.AsObject();
        sample["metrics"]!.AsArray()[0]!["target"]!["unit"] = "PB";

        var result = SpecTestHost.Validator.ValidateBaseline(sample.ToJsonString());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "/metrics/0/target/unit");
    }

    [Fact]
    public void BaselineWithDuplicateMetricIdIsReported()
    {
        var sample = System.Text.Json.Nodes.JsonNode.Parse(SpecTestHost.BaselineJson())!.AsObject();
        var metrics = sample["metrics"]!.AsArray();
        metrics[1]!.AsObject()["id"] = metrics[0]!["id"]!.GetValue<string>();

        var result = SpecTestHost.Validator.ValidateBaseline(sample.ToJsonString());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "/metrics/1/id");
    }

    [Fact]
    public void InvalidBaselineJsonThrowsSpecException()
    {
        var ex = Assert.Throws<XBearException>(
            () => { SpecTestHost.Loader.ParseBaseline("{ 坏的基线"); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }
}

/// <summary>设计令牌缺失即失败，不允许静默兜底。</summary>
public class DesignTokensTests
{
    [Fact]
    public void BrandColorsAreReadable()
    {
        var tokens = SpecTestHost.Tokens();

        Assert.Equal("#C87533", tokens.BrandPrimary);
        Assert.Equal("#2E5C4A", tokens.BrandSecondary);
        Assert.Equal("#D9A441", tokens.BrandAccent);
    }

    [Fact]
    public void SurfaceAndTextColorsAreReadable()
    {
        var tokens = SpecTestHost.Tokens();

        Assert.StartsWith("#", tokens.SurfaceBase, StringComparison.Ordinal);
        Assert.StartsWith("#", tokens.SurfaceRaised, StringComparison.Ordinal);
        Assert.StartsWith("#", tokens.SurfaceOverlay, StringComparison.Ordinal);
        Assert.StartsWith("#", tokens.TextPrimary, StringComparison.Ordinal);
        Assert.StartsWith("#", tokens.TextSecondary, StringComparison.Ordinal);
        Assert.StartsWith("#", tokens.TextInverse, StringComparison.Ordinal);
    }

    [Fact]
    public void AllStateColorsAreReadable()
    {
        var tokens = SpecTestHost.Tokens();

        Assert.StartsWith("#", tokens.StateSuccess, StringComparison.Ordinal);
        Assert.StartsWith("#", tokens.StateWarning, StringComparison.Ordinal);
        Assert.StartsWith("#", tokens.StateDanger, StringComparison.Ordinal);
        Assert.StartsWith("#", tokens.StateInfo, StringComparison.Ordinal);
    }

    [Fact]
    public void TokenSetMetadataIsReadable()
    {
        var tokens = SpecTestHost.Tokens();

        Assert.False(string.IsNullOrWhiteSpace(tokens.Name));
        Assert.False(string.IsNullOrWhiteSpace(tokens.Version));
        Assert.Equal("dp", tokens.SpacingUnit);
        Assert.Equal(4, tokens.SpacingBase);
    }

    /// <summary>间距刻度名与期望值。</summary>
    public static TheoryData<string, double> SpacingScales =>
        new()
        {
            { "xs", 4 },
            { "sm", 8 },
            { "md", 16 },
            { "lg", 24 },
            { "xl", 32 },
            { "xxl", 48 }
        };

    /// <summary>字号名与期望值。</summary>
    public static TheoryData<string, int> FontSizes =>
        new()
        {
            { "caption", 12 },
            { "body", 14 },
            { "subtitle", 16 },
            { "title", 20 },
            { "display", 28 }
        };

    /// <summary>字重名与期望值。</summary>
    public static TheoryData<string, int> FontWeights =>
        new()
        {
            { "regular", 400 },
            { "medium", 500 },
            { "bold", 700 }
        };

    /// <summary>圆角名与期望值。</summary>
    public static TheoryData<string, int> Radii =>
        new()
        {
            { "sm", 4 },
            { "md", 8 },
            { "lg", 16 },
            { "full", 9999 }
        };

    /// <summary>动效时长名与期望值。</summary>
    public static TheoryData<string, int> Durations =>
        new()
        {
            { "fast", 120 },
            { "normal", 220 },
            { "slow", 400 }
        };

    /// <summary>投影名。</summary>
    public static TheoryData<string> Elevations =>
        new()
        {
            { "card" },
            { "dialog" }
        };

    [Theory]
    [MemberData(nameof(SpacingScales))]
    public void SpacingScaleIsReadable(string key, double expected)
    {
        Assert.Equal(expected, SpecTestHost.Tokens().Spacing(key));
    }

    [Theory]
    [MemberData(nameof(FontSizes))]
    public void FontSizesAreReadable(string key, int expected)
    {
        Assert.Equal(expected, SpecTestHost.Tokens().FontSize(key));
    }

    [Theory]
    [MemberData(nameof(FontWeights))]
    public void FontWeightsAreReadable(string key, int expected)
    {
        Assert.Equal(expected, SpecTestHost.Tokens().FontWeight(key));
    }

    [Theory]
    [MemberData(nameof(Radii))]
    public void RadiusIsReadable(string key, int expected)
    {
        Assert.Equal(expected, SpecTestHost.Tokens().Radius(key));
    }

    [Theory]
    [MemberData(nameof(Durations))]
    public void MotionDurationIsReadable(string key, int expected)
    {
        Assert.Equal(expected, SpecTestHost.Tokens().MotionDuration(key));
    }

    [Theory]
    [MemberData(nameof(Elevations))]
    public void ElevationIsReadable(string key)
    {
        Assert.False(string.IsNullOrWhiteSpace(SpecTestHost.Tokens().Elevation(key)));
    }

    [Fact]
    public void FontFamiliesAreReadable()
    {
        var tokens = SpecTestHost.Tokens();

        Assert.Contains("Microsoft YaHei", tokens.FontFamilyPrimary, StringComparison.Ordinal);
        Assert.Contains("Consolas", tokens.FontFamilyMonospace, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTokenKeyThrowsInsteadOfReturningDefault()
    {
        var ex = Assert.Throws<XBearException>(
            () => { SpecTestHost.Tokens().Spacing("no-such-step"); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
        Assert.Contains("no-such-step", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingNestedTokenGroupThrows()
    {
        var ex = Assert.Throws<XBearException>(() => { SpecTestHost.Tokens().Radius("huge"); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void EmptyTokenKeyThrows()
    {
        var ex = Assert.Throws<XBearException>(() => { SpecTestHost.Tokens().FontSize("  "); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void UnknownTokenKeyThrows()
    {
        var ex = Assert.Throws<XBearException>(() => { SpecTestHost.Tokens().FontWeight("heavy"); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void InvalidTokensJsonThrows()
    {
        var ex = Assert.Throws<XBearException>(() => { DesignTokens.Parse("{ 坏的 JSON"); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void NonObjectTokensDocumentThrows()
    {
        var ex = Assert.Throws<XBearException>(() => { DesignTokens.Parse("[1,2,3]"); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void TokensWithoutRequiredGroupThrowOnAccess()
    {
        var tokens = DesignTokens.Parse("""{ "name": "空令牌集", "version": "1.0.0" }""");

        var ex = Assert.Throws<XBearException>(() => { _ = tokens.BrandPrimary; });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }
}

/// <summary>术语表禁用近义词校验。</summary>
public class TerminologyValidatorTests
{
    [Fact]
    public void CanonicalWordingIsAccepted()
    {
        var terminology = SpecTestHost.Terminology();

        var result = TerminologyValidator.CheckText(
            "本实例使用保真度实测结论，启动实例后输入通道走原生输入。",
            terminology);

        Assert.True(result.IsValid, result.DescribeViolations());
    }

    [Fact]
    public void ForbiddenChineseSynonymIsRejected()
    {
        var result = TerminologyValidator.CheckText(
            "这个虚拟机实例卡住了",
            SpecTestHost.Terminology());

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.ForbiddenWord == "虚拟机实例");
    }

    [Fact]
    public void AllForbiddenWordsOfATermAreDetected()
    {
        var result = TerminologyValidator.CheckText("仿真度 拟真度 还原度", SpecTestHost.Terminology());

        Assert.Equal(3, result.Violations.Count);
        Assert.All(result.Violations, v => Assert.Equal("fidelity", v.TermId));
    }

    [Fact]
    public void RepeatedForbiddenWordIsReportedEveryTime()
    {
        var result = TerminologyValidator.CheckText("分身 分身 分身", SpecTestHost.Terminology());

        Assert.Equal(3, result.Violations.Count);
    }

    [Fact]
    public void EmptyTextIsAccepted()
    {
        var result = TerminologyValidator.CheckText(string.Empty, SpecTestHost.Terminology());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void EnsureValidThrowsOnViolation()
    {
        var result = TerminologyValidator.CheckText("请先开机再操作", SpecTestHost.Terminology());

        var ex = Assert.Throws<XBearException>(() => { result.EnsureValid(); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void EnsureValidPassesOnCleanText()
    {
        var result = TerminologyValidator.CheckText("创建一个实例", SpecTestHost.Terminology());

        Assert.Same(result, result.EnsureValid());
    }

    [Fact]
    public void NullTerminologyThrows()
    {
        var ex = Assert.Throws<XBearException>(
            () => { TerminologyValidator.CheckText("任意文本", null!); });

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }
}

/// <summary>品牌调色板契约加载与语义色映射校验。</summary>
public class PaletteContractTests
{
    [Fact]
    public void PaletteFileLoads()
    {
        var palette = SpecTestHost.Loader.LoadPalette();

        Assert.False(string.IsNullOrWhiteSpace(palette.Name));
        Assert.False(string.IsNullOrWhiteSpace(palette.Version));
        Assert.False(string.IsNullOrWhiteSpace(palette.Description));
        Assert.False(string.IsNullOrWhiteSpace(palette.Source));
        Assert.NotEmpty(palette.Roles);
    }

    [Fact]
    public void AllRegisteredRolesMapToTokens()
    {
        var palette = SpecTestHost.Loader.LoadPalette();

        Assert.Equal(8, palette.Roles.Count);
        Assert.Equal(palette.Roles.Count, palette.RoleToTokenMap.Count);

        Assert.Equal("color.brand.primary", palette.GetToken("bear-brown"));
        Assert.Equal("color.brand.secondary", palette.GetToken("forest-green"));
        Assert.Equal("color.brand.accent", palette.GetToken("honey-yellow"));
        Assert.Equal("color.surface.raised", palette.GetToken("fur-light"));
        Assert.Equal("color.text.primary", palette.GetToken("ink"));
        Assert.Equal("color.text.secondary", palette.GetToken("ink-soft"));
        Assert.Equal("color.text.inverse", palette.GetToken("on-dark"));
        Assert.Equal("color.surface.base", palette.GetToken("canvas"));
    }

    [Fact]
    public void RoleLookupSucceedsAndThrowsOnUnknown()
    {
        var palette = SpecTestHost.Loader.LoadPalette();

        var role = palette.RequireRole("bear-brown");
        Assert.Equal("bear-brown", role.Name);
        Assert.Equal("color.brand.primary", role.Token);
        Assert.False(string.IsNullOrWhiteSpace(role.Value));
        Assert.False(string.IsNullOrWhiteSpace(role.Description));

        Assert.Null(palette.FindRole("unknown-role"));

        var ex = Assert.Throws<XBearException>(() => palette.RequireRole("unknown-role"));
        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void PaletteRoleValuesMatchDesignTokens()
    {
        var palette = SpecTestHost.Loader.LoadPalette();
        var tokens = SpecTestHost.Tokens();

        foreach (var role in palette.Roles)
        {
            string tokenValue = role.Token switch
            {
                "color.brand.primary" => tokens.BrandPrimary,
                "color.brand.secondary" => tokens.BrandSecondary,
                "color.brand.accent" => tokens.BrandAccent,
                "color.surface.base" => tokens.SurfaceBase,
                "color.surface.raised" => tokens.SurfaceRaised,
                "color.text.primary" => tokens.TextPrimary,
                "color.text.secondary" => tokens.TextSecondary,
                "color.text.inverse" => tokens.TextInverse,
                _ => throw new InvalidOperationException($"未知的令牌键 {role.Token}")
            };

            Assert.Equal(tokenValue, role.Value);
        }
    }

    [Fact]
    public void InvalidJsonThrowsSpecException()
    {
        var ex = Assert.Throws<XBearException>(
            () => SpecTestHost.Loader.ParsePalette("{ 不是合法的 JSON"));

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void MissingRolesNodeThrowsSpecException()
    {
        var ex = Assert.Throws<XBearException>(
            () => SpecTestHost.Loader.ParsePalette("""{ "name": "无roles" }"""));

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void EmptyRolesNodeThrowsSpecException()
    {
        var ex = Assert.Throws<XBearException>(
            () => SpecTestHost.Loader.ParsePalette("""{ "name": "空roles", "roles": {} }"""));

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void MissingTokenFieldThrowsSpecException()
    {
        var ex = Assert.Throws<XBearException>(
            () => SpecTestHost.Loader.ParsePalette("""
            {
                "name": "缺token",
                "roles": {
                    "test": { "value": "#000000" }
                }
            }
            """));

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }
}