using System.Reflection;
using XBear.App.Presentation;
using XBear.App.Services;
using XBear.App.ViewModels;
using XBear.Core.Abstractions;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 术语目录完整性与取词路径测试。界面文案只能来自术语表，
/// 词表新增条目必须同步暴露强类型访问，界面不得自行拼写概念名。
/// </summary>
public class TerminologyCatalogTests
{
    /// <summary>共享术语表的条目数，与 v1.2.0 契约一致。</summary>
    private const int ContractTermCount = 36;

    private static TerminologyCatalog Create() =>
        new(XBeeSpec.TestSpec().LoadTerminology());

    [Fact]
    public void EveryContractTermHasAStronglyTypedAccessor()
    {
        TerminologyDocument document = XBeeSpec.TestSpec().LoadTerminology();
        TerminologyCatalog catalog = Create();

        Assert.Equal(ContractTermCount, document.Terms.Count);

        // 中文名属性与术语表条目一一对应：既不缺项，也不多出契约之外的属性。
        var accessors = typeof(TerminologyCatalog)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(ContractTermCount, accessors.Count);
        Assert.Equal(ContractTermCount, catalog.TermIds.Count);
        Assert.Equal(ContractTermCount, catalog.TermIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryCatalogTermResolvesToContractText()
    {
        TerminologyDocument document = XBeeSpec.TestSpec().LoadTerminology();
        TerminologyCatalog catalog = Create();

        Assert.Equal(
            document.Terms.Select(t => t.Id).Order(StringComparer.Ordinal),
            catalog.TermIds.Order(StringComparer.Ordinal));

        foreach (Term term in document.Terms)
        {
            Assert.Equal(term.Zh, catalog.Zh(term.Id));
            Assert.Equal(term.Meaning, catalog.Meaning(term.Id));
        }
    }

    [Fact]
    public void EachNamedPropertyMatchesItsContractEntry()
    {
        TerminologyDocument document = XBeeSpec.TestSpec().LoadTerminology();
        TerminologyCatalog catalog = Create();

        Assert.Equal(document.Require(TerminologyCatalog.InstanceTermId).Zh, catalog.Instance);
        Assert.Equal(document.Require(TerminologyCatalog.ImageTermId).Zh, catalog.Image);
        Assert.Equal(document.Require(TerminologyCatalog.RootTermId).Zh, catalog.Root);
        Assert.Equal(document.Require(TerminologyCatalog.ExposureTermId).Zh, catalog.Exposure);
        Assert.Equal(document.Require(TerminologyCatalog.InputChannelTermId).Zh, catalog.InputChannel);
        Assert.Equal(document.Require(TerminologyCatalog.FidelityTermId).Zh, catalog.Fidelity);
        Assert.Equal(document.Require(TerminologyCatalog.StartTermId).Zh, catalog.Start);
        Assert.Equal(document.Require(TerminologyCatalog.StopTermId).Zh, catalog.Stop);
        Assert.Equal(document.Require(TerminologyCatalog.ErrorEnvironmentTermId).Zh, catalog.ErrorEnvironment);
        Assert.Equal(document.Require(TerminologyCatalog.ErrorProviderTermId).Zh, catalog.ErrorProvider);
        Assert.Equal(document.Require(TerminologyCatalog.ErrorGuestTermId).Zh, catalog.ErrorGuest);
        Assert.Equal(document.Require(TerminologyCatalog.ErrorUserTermId).Zh, catalog.ErrorUser);
        Assert.Equal(document.Require(TerminologyCatalog.SnapshotTermId).Zh, catalog.Snapshot);
        Assert.Equal(document.Require(TerminologyCatalog.ProjectionTermId).Zh, catalog.Projection);
        Assert.Equal(document.Require(TerminologyCatalog.FileTransferTermId).Zh, catalog.FileTransfer);
        Assert.Equal(document.Require(TerminologyCatalog.DiagnosticsTermId).Zh, catalog.Diagnostics);
        Assert.Equal(document.Require(TerminologyCatalog.ResolutionTermId).Zh, catalog.Resolution);
        Assert.Equal(document.Require(TerminologyCatalog.DensityTermId).Zh, catalog.Density);
        Assert.Equal(document.Require(TerminologyCatalog.OrientationTermId).Zh, catalog.Orientation);
        Assert.Equal(document.Require(TerminologyCatalog.ProjectionSessionTermId).Zh, catalog.ProjectionSession);
        Assert.Equal(document.Require(TerminologyCatalog.FrameRateTermId).Zh, catalog.FrameRate);
        Assert.Equal(document.Require(TerminologyCatalog.PointerCoordinateDomainTermId).Zh, catalog.PointerCoordinateDomain);
        Assert.Equal(document.Require(TerminologyCatalog.InputDeviceKindTermId).Zh, catalog.InputDeviceKind);
        Assert.Equal(document.Require(TerminologyCatalog.ModuleTermId).Zh, catalog.Module);
        Assert.Equal(document.Require(TerminologyCatalog.ModuleInstallTermId).Zh, catalog.ModuleInstall);
        Assert.Equal(document.Require(TerminologyCatalog.ModuleUninstallTermId).Zh, catalog.ModuleUninstall);
        Assert.Equal(document.Require(TerminologyCatalog.ModuleManifestTermId).Zh, catalog.ModuleManifest);
        Assert.Equal(document.Require(TerminologyCatalog.StageScriptTermId).Zh, catalog.StageScript);
        Assert.Equal(document.Require(TerminologyCatalog.SystemOverlayTermId).Zh, catalog.SystemOverlay);
        Assert.Equal(document.Require(TerminologyCatalog.DisableMarkerTermId).Zh, catalog.DisableMarker);
        Assert.Equal(document.Require(TerminologyCatalog.TransferTaskTermId).Zh, catalog.TransferTask);
        Assert.Equal(document.Require(TerminologyCatalog.ConflictPolicyTermId).Zh, catalog.ConflictPolicy);
        Assert.Equal(document.Require(TerminologyCatalog.ApplicationTermId).Zh, catalog.Application);
        Assert.Equal(document.Require(TerminologyCatalog.AppLaunchTermId).Zh, catalog.AppLaunch);
        Assert.Equal(document.Require(TerminologyCatalog.ApkFileTermId).Zh, catalog.ApkFile);
        Assert.Equal(document.Require(TerminologyCatalog.InstallStateTermId).Zh, catalog.InstallState);
    }
}

/// <summary>
/// 界面文案取词路径测试。主窗口上与术语相关的按钮、标题与输入通道文案
/// 必须由术语目录拼出，不得在视图模型或呈现层里硬写。
/// </summary>
public class TerminologyRoutingTests
{
    private static MainViewModel CreateMainViewModel(TerminologyCatalog terms) =>
        new(
            new StubInstanceRepository(),
            manager: null,
            new Dictionary<string, ImageSpec>(StringComparer.Ordinal),
            new DiagnosticsExporter(XBeeSpec.TestSpec()),
            terms);

    [Fact]
    public void ExportDiagnosticsButtonTextIsBuiltFromTerminology()
    {
        TerminologyCatalog terms = new(XBeeSpec.TestSpec().LoadTerminology());
        MainViewModel viewModel = CreateMainViewModel(terms);

        Assert.Equal($"导出{terms.Diagnostics}", viewModel.ExportDiagnosticsText);
    }

    [Fact]
    public void InstanceDetailTitleIsBuiltFromTerminology()
    {
        TerminologyCatalog terms = new(XBeeSpec.TestSpec().LoadTerminology());
        MainViewModel viewModel = CreateMainViewModel(terms);

        Assert.Equal($"{terms.Instance}详情", viewModel.DetailTitle);
    }

    [Fact]
    public void ProjectionChannelIsNamedEntirelyFromTerminology()
    {
        TerminologyCatalog terms = new(XBeeSpec.TestSpec().LoadTerminology());

        InputChannelPresentation presentation = InputChannelMapper.Describe(
            new InputProbeResult(InputChannelKind.Projection),
            terms);

        Assert.Equal($"{terms.Projection}{terms.InputChannel}", presentation.Label);
        Assert.Contains(terms.Projection, presentation.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InputChannelKind.Native)]
    [InlineData(InputChannelKind.Projection)]
    [InlineData(InputChannelKind.Unavailable)]
    public void InputChannelLabelsAreNamedFromTerminology(InputChannelKind channel)
    {
        TerminologyCatalog terms = new(XBeeSpec.TestSpec().LoadTerminology());

        InputChannelPresentation presentation = InputChannelMapper.Describe(
            new InputProbeResult(channel),
            terms);

        // 通道名与不可用结论都由术语表拼出，呈现层不得自行书写概念名。
        Assert.Contains(terms.InputChannel, presentation.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void UnavailableChannelNamesBothPathsFromTerminology()
    {
        TerminologyCatalog terms = new(XBeeSpec.TestSpec().LoadTerminology());

        InputChannelPresentation presentation = InputChannelMapper.Describe(
            new InputProbeResult(
                InputChannelKind.Unavailable,
                NativeFailure: "QMP 未连接",
                ProjectionFailure: "scrcpy 缺失"),
            terms);

        Assert.Equal($"{terms.InputChannel}不可用", presentation.Label);
        Assert.Contains($"{terms.Projection}{terms.InputChannel}", presentation.Detail, StringComparison.Ordinal);
        Assert.Contains($"{terms.Projection}{terms.InputChannel}：scrcpy 缺失", presentation.Detail, StringComparison.Ordinal);
    }
}