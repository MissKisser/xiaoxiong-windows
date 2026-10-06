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