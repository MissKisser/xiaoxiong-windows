using XBear.App.Presentation;
using XBear.Core.Diagnostics;

namespace XBear.App.Tests;

/// <summary>
/// 技术错误分类到标准四层分类的映射测试。
/// 技术分类覆盖必须完整，层级归属必须落在宿主环境、虚拟化实现、实例内与配置四层之内。
/// </summary>
public class ErrorLayerMapTests
{
    /// <summary>契约定义的技术分类数量。</summary>
    private const int TechnicalCategoryCount = 11;

    [Theory]
    [InlineData(ErrorCategory.State, ErrorLayer.User)]
    [InlineData(ErrorCategory.Dependency, ErrorLayer.Environment)]
    [InlineData(ErrorCategory.Process, ErrorLayer.Provider)]
    [InlineData(ErrorCategory.Protocol, ErrorLayer.Guest)]
    [InlineData(ErrorCategory.Port, ErrorLayer.User)]
    [InlineData(ErrorCategory.Spec, ErrorLayer.User)]
    [InlineData(ErrorCategory.Storage, ErrorLayer.User)]
    [InlineData(ErrorCategory.Identity, ErrorLayer.User)]
    [InlineData(ErrorCategory.Canceled, ErrorLayer.User)]
    [InlineData(ErrorCategory.Timeout, ErrorLayer.Guest)]
    [InlineData(ErrorCategory.Internal, ErrorLayer.Provider)]
    public void EveryTechnicalCategoryMapsToItsStandardLayer(
        ErrorCategory category,
        ErrorLayer expected)
    {
        Assert.Equal(expected, ErrorLayerMap.Resolve(category));
    }

    [Fact]
    public void MappingCoversEveryDeclaredTechnicalCategory()
    {
        IReadOnlyList<ErrorCategory> categories = ErrorLayerMap.Categories;

        Assert.Equal(TechnicalCategoryCount, categories.Count);
        Assert.Equal(
            Enum.GetValues<ErrorCategory>().Length,
            categories.Count);

        // 逐项核对，不允许存在没有归属的技术分类。
        foreach (ErrorCategory category in categories)
        {
            Assert.Contains(
                ErrorLayerMap.Resolve(category),
                Enum.GetValues<ErrorLayer>());
        }
    }

    [Fact]
    public void AllFourStandardLayersAreReachable()
    {
        var reached = ErrorLayerMap.Categories
            .Select(ErrorLayerMap.Resolve)
            .Distinct()
            .ToHashSet();

        Assert.Equal(Enum.GetValues<ErrorLayer>().Length, reached.Count);
    }

    [Fact]
    public void UnknownCategoryFallsBackToConfigurationError()
    {
        // 未声明取值一律归入配置错误：立即返回且不改变实例状态，最保守。
        Assert.Equal(ErrorLayer.User, ErrorLayerMap.Resolve((ErrorCategory)777));
    }
}

/// <summary>
/// 错误文案呈现测试。标题使用术语表中的标准分类名称，
/// 技术分类与详情一并保留，供诊断展开时定位具体失败步骤。
/// </summary>
public class ErrorLayerPresentationTests
{
    private static TerminologyCatalog Terms() =>
        new(XBeeSpec.TestSpec().LoadTerminology());

    private static string LayerName(ErrorLayer layer) =>
        layer switch
        {
            ErrorLayer.Environment => "宿主环境错误",
            ErrorLayer.Provider => "虚拟化实现错误",
            ErrorLayer.Guest => "实例内错误",
            _ => "配置错误",
        };

    [Fact]
    public void EveryTechnicalCategoryIsPresentedWithItsStandardLayerName()
    {
        TerminologyCatalog terms = Terms();

        foreach (ErrorCategory category in ErrorLayerMap.Categories)
        {
            ErrorCategoryText text = ErrorPresenter.Describe(category, terms);

            Assert.Equal(ErrorLayerMap.Resolve(category), text.Layer);
            Assert.StartsWith(LayerName(text.Layer), text.Title, StringComparison.Ordinal);
            Assert.Contains(text.TechnicalName, text.Title, StringComparison.Ordinal);
            Assert.NotEmpty(text.TechnicalDetail);
        }
    }

    [Fact]
    public void PresentedLayerNameAlwaysMatchesTheTerminologyCatalog()
    {
        TerminologyCatalog terms = Terms();

        foreach (ErrorCategory category in ErrorLayerMap.Categories)
        {
            ErrorCategoryText text = ErrorPresenter.Describe(category, terms);

            // 分类名称必须逐字取自术语表，不允许在呈现层另写一套说法。
            Assert.Equal(terms.Zh(text.LayerTermId), LayerName(text.Layer));
            Assert.StartsWith(terms.Zh(text.LayerTermId), text.Title, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TechnicalCategoryAndDetailAreKeptForDiagnostics()
    {
        TerminologyCatalog terms = Terms();

        ErrorCategoryText text = ErrorPresenter.Describe(ErrorCategory.Port, terms);

        Assert.Equal(ErrorLayer.User, text.Layer);
        Assert.Equal("端口不可用", text.TechnicalName);
        Assert.Contains("端口", text.TechnicalDetail, StringComparison.Ordinal);
        Assert.Equal($"{terms.ErrorUser}：端口不可用", text.Title);
    }

    [Fact]
    public void EachStandardLayerResolvesToItsContractTermId()
    {
        Assert.Equal(
            TerminologyCatalog.ErrorEnvironmentTermId,
            ErrorPresenter.TermIdOf(ErrorLayer.Environment));
        Assert.Equal(
            TerminologyCatalog.ErrorProviderTermId,
            ErrorPresenter.TermIdOf(ErrorLayer.Provider));
        Assert.Equal(
            TerminologyCatalog.ErrorGuestTermId,
            ErrorPresenter.TermIdOf(ErrorLayer.Guest));
        Assert.Equal(
            TerminologyCatalog.ErrorUserTermId,
            ErrorPresenter.TermIdOf(ErrorLayer.User));
    }

    [Fact]
    public void ProjectExceptionKeepsRemediationAndLayeredTitle()
    {
        TerminologyCatalog terms = Terms();
        var ex = new XBearException(
            ErrorCategory.Dependency,
            "虚拟化组件缺失",
            "在系统功能中启用硬件加速后重试。");

        (ErrorCategoryText text, string? remediation) = ErrorPresenter.Describe(ex, terms);

        Assert.Equal($"{terms.ErrorEnvironment}：缺少外部依赖", text.Title);
        Assert.Equal("在系统功能中启用硬件加速后重试。", remediation);
    }

    [Fact]
    public void NonProjectExceptionIsPresentedAsInternalWithoutStack()
    {
        var boom = new InvalidOperationException("内部堆栈细节");

        (ErrorCategoryText text, string? remediation) = ErrorPresenter.Describe(boom, Terms());

        Assert.Equal(ErrorLayer.Provider, text.Layer);
        Assert.DoesNotContain("内部堆栈细节", text.Title, StringComparison.Ordinal);
        Assert.Null(remediation);
    }

    [Fact]
    public void PresenterWithoutTerminologyFallsBackToTechnicalName()
    {
        // 术语表尚未读到时仍要能给出可读的标题，且层级判定不受影响。
        ErrorCategoryText text = ErrorPresenter.Describe(ErrorCategory.Port);

        Assert.Equal("端口不可用", text.Title);
        Assert.Equal(ErrorLayer.User, text.Layer);
    }
}