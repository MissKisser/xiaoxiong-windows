using XBear.App.Presentation;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 暴露级别切换的状态机测试。覆盖默认档位、局域网提示与公网阻断式确认。
/// </summary>
public class ExposureSelectionTests
{
    private static ExposureSelection Create() => new(new[]
    {
        new ExposureLevelOption(ExposureSelection.Loopback, "仅本机（loopback）", "仅本机可连接。"),
        new ExposureLevelOption(ExposureSelection.Lan, "局域网（lan）", "同网段设备可连接。"),
        new ExposureLevelOption(ExposureSelection.Public, "公网（public）", "任何可达网络的主机均可连接。")
    });

    [Fact]
    public void DefaultsToLoopbackAndNeverExposesOutward()
    {
        ExposureSelection selection = Create();

        Assert.Equal(ExposureSelection.Loopback, selection.Current);
        Assert.False(selection.IsConfirmationPending);
        Assert.False(ExposureSelection.RequiresRiskBanner(selection.Current));
    }

    [Fact]
    public void RequestingLoopbackAppliesImmediately()
    {
        ExposureSelection selection = Create();

        ExposureChangeOutcome outcome = selection.Request(ExposureSelection.Loopback);

        Assert.Equal(ExposureChangeOutcome.Applied, outcome);
        Assert.Equal(ExposureSelection.Loopback, selection.Current);
        Assert.False(selection.IsConfirmationPending);
    }

    [Fact]
    public void RequestingLanRequiresAcknowledgementAndWarnsSameNetwork()
    {
        ExposureSelection selection = Create();

        ExposureChangeOutcome outcome = selection.Request(ExposureSelection.Lan);

        Assert.Equal(ExposureChangeOutcome.RequiresAcknowledgement, outcome);
        Assert.True(selection.IsConfirmationPending);

        // 提示必须点明同网段设备可连接。
        Assert.Contains("同网段", selection.PendingWarning, StringComparison.Ordinal);

        // 未勾选时不得放行。
        Assert.Equal(ExposureChangeOutcome.Blocked, selection.Confirm(acknowledged: false));
        Assert.Equal(ExposureSelection.Loopback, selection.Current);

        Assert.Equal(ExposureChangeOutcome.Applied, selection.Confirm(acknowledged: true));
        Assert.Equal(ExposureSelection.Lan, selection.Current);
    }

    [Fact]
    public void RequestingPublicIsBlockedUntilExplicitlyAcknowledged()
    {
        ExposureSelection selection = Create();

        ExposureChangeOutcome outcome = selection.Request(ExposureSelection.Public);

        Assert.Equal(ExposureChangeOutcome.RequiresAcknowledgement, outcome);
        Assert.True(selection.IsConfirmationPending);
        Assert.Equal(ExposureSelection.Loopback, selection.Current);

        // 未勾选「我已理解风险」时不允许继续。
        Assert.Equal(ExposureChangeOutcome.Blocked, selection.Confirm(acknowledged: false));
        Assert.Equal(ExposureSelection.Loopback, selection.Current);
        Assert.True(selection.IsConfirmationPending);
    }

    [Fact]
    public void RequestingPublicAppliesOnlyAfterAcknowledgement()
    {
        ExposureSelection selection = Create();
        selection.Request(ExposureSelection.Public);

        ExposureChangeOutcome outcome = selection.Confirm(acknowledged: true);

        Assert.Equal(ExposureChangeOutcome.Applied, outcome);
        Assert.Equal(ExposureSelection.Public, selection.Current);
        Assert.False(selection.IsConfirmationPending);
        Assert.True(ExposureSelection.RequiresRiskBanner(selection.Current));
    }

    [Fact]
    public void DowngradingFromPublicBackToLoopbackAppliesWithoutConfirmation()
    {
        ExposureSelection selection = Create();
        selection.Request(ExposureSelection.Public);
        selection.Confirm(acknowledged: true);

        // 降低风险的变更不需要阻断式确认。
        Assert.Equal(ExposureChangeOutcome.Applied, selection.Request(ExposureSelection.Loopback));
        Assert.Equal(ExposureSelection.Loopback, selection.Current);
    }

    [Fact]
    public void CancelPendingRevertsToCurrentLevel()
    {
        ExposureSelection selection = Create();
        selection.Request(ExposureSelection.Public);

        selection.CancelPending();

        Assert.False(selection.IsConfirmationPending);
        Assert.Equal(ExposureSelection.Loopback, selection.Current);
    }

    [Fact]
    public void RiskBannerAppliesOnlyToNonLoopbackExposure()
    {
        Assert.False(ExposureSelection.RequiresRiskBanner("loopback"));
        Assert.False(ExposureSelection.RequiresRiskBanner(null));
        Assert.True(ExposureSelection.RequiresRiskBanner("lan"));
        Assert.True(ExposureSelection.RequiresRiskBanner("public"));
    }

    [Fact]
    public void AdbPortIsExternallyBoundWhenExposureIsNotLoopback()
    {
        var forwards = new List<PortForward>
        {
            new() { HostPort = 5555, GuestPort = 5555, Protocol = "tcp" }
        };

        Assert.False(ExposureSelection.HasExternallyBoundPort("loopback", forwards));
        Assert.True(ExposureSelection.HasExternallyBoundPort("lan", forwards));
        Assert.True(ExposureSelection.HasExternallyBoundPort("public", forwards));
    }
}

/// <summary>实例状态到按钮可用性的映射测试。</summary>
public class InstanceStateMapperTests
{
    [Theory]
    [InlineData(InstanceState.Stopped, true, false, false)]
    [InlineData(InstanceState.Starting, false, false, true)]
    [InlineData(InstanceState.Running, false, true, false)]
    [InlineData(InstanceState.Stopping, false, false, true)]
    [InlineData(InstanceState.Faulted, true, false, false)]
    public void StateMapsToCommandAvailability(
        InstanceState state,
        bool canStart,
        bool canStop,
        bool isBusy)
    {
        InstanceStatePresentation presentation = InstanceStateMapper.Describe(state);

        Assert.Equal(canStart, presentation.CanStart);
        Assert.Equal(canStop, presentation.CanStop);
        Assert.Equal(isBusy, presentation.IsBusy);
        Assert.NotEmpty(presentation.Label);
    }

    [Fact]
    public void TransitionalStatesDisableBothCommands()
    {
        Assert.False(InstanceStateMapper.Describe(InstanceState.Starting).CanStart);
        Assert.False(InstanceStateMapper.Describe(InstanceState.Starting).CanStop);
        Assert.False(InstanceStateMapper.Describe(InstanceState.Stopping).CanStart);
        Assert.False(InstanceStateMapper.Describe(InstanceState.Stopping).CanStop);
    }
}

/// <summary>错误分类到中文文案的映射测试。</summary>
public class ErrorPresenterTests
{
    [Theory]
    [InlineData(ErrorCategory.State)]
    [InlineData(ErrorCategory.Dependency)]
    [InlineData(ErrorCategory.Process)]
    [InlineData(ErrorCategory.Protocol)]
    [InlineData(ErrorCategory.Port)]
    [InlineData(ErrorCategory.Spec)]
    [InlineData(ErrorCategory.Storage)]
    [InlineData(ErrorCategory.Identity)]
    [InlineData(ErrorCategory.Canceled)]
    [InlineData(ErrorCategory.Timeout)]
    [InlineData(ErrorCategory.Internal)]
    public void EveryCategoryHasChineseText(ErrorCategory category)
    {
        ErrorCategoryText text = ErrorPresenter.Describe(category);

        Assert.NotEmpty(text.Title);
        Assert.NotEmpty(text.Description);
    }

    [Fact]
    public void NonProjectExceptionIsNotLeakedAsStackTrace()
    {
        var boom = new InvalidOperationException("内部堆栈细节");

        (ErrorCategoryText text, string? remediation) = ErrorPresenter.Describe(boom);

        Assert.Equal("内部错误", text.Title);
        Assert.Null(remediation);
    }

    [Fact]
    public void RemediationIsSurfacedWhenPresent()
    {
        var ex = new XBearException(
            ErrorCategory.Port,
            "端口被占用",
            "请释放被占用的端口后重试。");

        (ErrorCategoryText text, string? remediation) = ErrorPresenter.Describe(ex);

        Assert.Equal("端口不可用", text.Title);
        Assert.Equal("请释放被占用的端口后重试。", remediation);
    }
}

/// <summary>输入通道与保真度的如实呈现测试。</summary>
public class PresentationTests
{
    [Fact]
    public void UnavailableInputChannelShowsExplicitReason()
    {
        var result = new InputProbeResult(
            InputChannelKind.Unavailable,
            NativeFailure: "QMP 未连接",
            ProjectionFailure: "scrcpy 缺失");

        InputChannelPresentation presentation = InputChannelMapper.Describe(result);

        Assert.True(presentation.IsUnavailable);
        Assert.Contains("QMP 未连接", presentation.Detail, StringComparison.Ordinal);
        Assert.Contains("scrcpy 缺失", presentation.Detail, StringComparison.Ordinal);

        // 不得显示成就绪。
        Assert.DoesNotContain("就绪", presentation.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void UnavailableInputChannelWithoutReasonsStillExplains()
    {
        InputChannelPresentation presentation =
            InputChannelMapper.Describe(new InputProbeResult(InputChannelKind.Unavailable));

        Assert.True(presentation.IsUnavailable);
        Assert.NotEmpty(presentation.Detail);
    }

    [Fact]
    public void UntestedFidelityIsNeverShownAsPass()
    {
        var fidelity = new FidelitySet
        {
            P1Root = VerificationState.Pass,
            P2SystemWrite = VerificationState.Untested
        };

        IReadOnlyList<FidelityItemPresentation> items = FidelityMapper.Describe(fidelity);

        Assert.Equal("已实测通过", items[0].Value);
        Assert.True(items[0].IsPass);

        // 未实测必须如实显示为未实测。
        Assert.Equal("未实测", items[1].Value);
        Assert.False(items[1].IsPass);
    }

    [Fact]
    public void AllSixFidelityItemsAreAlwaysShown()
    {
        IReadOnlyList<FidelityItemPresentation> items =
            FidelityMapper.Describe(new FidelitySet());

        Assert.Equal(6, items.Count);
        Assert.All(items, i => Assert.Equal("未实测", i.Value));
    }
}