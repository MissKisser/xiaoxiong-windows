using XBear.Core.Abstractions;
using XBear.Core.Spec;

namespace XBear.App.Presentation;

/// <summary>实例状态在详情区的展示文案与可用操作。</summary>
/// <param name="Label">状态中文名。</param>
/// <param name="CanStart">是否允许启动。</param>
/// <param name="CanStop">是否允许停止。</param>
/// <param name="IsBusy">是否处于过渡态，过渡态下两个按钮都禁用。</param>
public sealed record InstanceStatePresentation(string Label, bool CanStart, bool CanStop, bool IsBusy);

/// <summary>把 <see cref="InstanceState"/> 映射为按钮可用性，供单测直接断言。</summary>
public static class InstanceStateMapper
{
    /// <summary>
    /// 取实例状态的界面呈现。
    /// </summary>
    /// <param name="state">实例运行态。</param>
    /// <returns>状态文案与按钮可用性。</returns>
    public static InstanceStatePresentation Describe(InstanceState state) =>
        state switch
        {
            InstanceState.Stopped => new InstanceStatePresentation("已停止", CanStart: true, CanStop: false, IsBusy: false),
            InstanceState.Starting => new InstanceStatePresentation("启动中", CanStart: false, CanStop: false, IsBusy: true),
            InstanceState.Running => new InstanceStatePresentation("已运行", CanStart: false, CanStop: true, IsBusy: false),
            InstanceState.Stopping => new InstanceStatePresentation("停止中", CanStart: false, CanStop: false, IsBusy: true),
            InstanceState.Faulted => new InstanceStatePresentation("启动失败", CanStart: true, CanStop: false, IsBusy: false),
            _ => new InstanceStatePresentation("未知状态", CanStart: false, CanStop: false, IsBusy: true)
        };
}

/// <summary>输入通道的展示文案。</summary>
/// <param name="Label">通道中文名。</param>
/// <param name="Detail">补充说明，两条通路都不可用时给出具体原因。</param>
/// <param name="IsUnavailable">两条通路是否均不可用。</param>
public sealed record InputChannelPresentation(string Label, string Detail, bool IsUnavailable);

/// <summary>把输入通道探测结论映射为界面文案，两条通路都不可用时必须说明原因。</summary>
public static class InputChannelMapper
{
    /// <summary>
    /// 取输入通道的界面呈现。通路名称由术语表拼出，
    /// 界面不得自行书写投屏等概念名。
    /// </summary>
    /// <param name="result">输入通路探测结论。</param>
    /// <param name="terms">界面文案术语来源。</param>
    /// <returns>通道文案与不可用原因。</returns>
    public static InputChannelPresentation Describe(InputProbeResult result, TerminologyCatalog terms)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(terms);

        string nativeInput = $"原生{terms.InputChannel}";
        string projectionInput = $"{terms.Projection}{terms.InputChannel}";

        return result.Channel switch
        {
            InputChannelKind.Native => new InputChannelPresentation(
                nativeInput,
                "通过 QMP 直接投递触摸与按键。",
                IsUnavailable: false),
            InputChannelKind.Projection => new InputChannelPresentation(
                projectionInput,
                $"原生通路不可用，已降级为{terms.Projection}，{terms.InputChannel}随之改由{terms.ProjectionSession}承载。",
                IsUnavailable: false),
            InputChannelKind.Unavailable => new InputChannelPresentation(
                $"{terms.InputChannel}不可用",
                BuildUnavailableDetail(result, nativeInput, projectionInput),
                IsUnavailable: true),
            _ => new InputChannelPresentation(
                "尚未探测",
                $"实例尚未启动，{terms.InputChannel}状态未知。",
                IsUnavailable: true)
        };
    }

    private static string BuildUnavailableDetail(
        InputProbeResult result,
        string nativeInput,
        string projectionInput)
    {
        List<string> reasons = new();

        if (!string.IsNullOrWhiteSpace(result.NativeFailure))
        {
            reasons.Add($"{nativeInput}：{result.NativeFailure}");
        }

        if (!string.IsNullOrWhiteSpace(result.ProjectionFailure))
        {
            reasons.Add($"{projectionInput}：{result.ProjectionFailure}");
        }

        // 两条通路都没有给出原因时也要说明不可用，不能让用户对着「就绪」一样的界面等待。
        return reasons.Count == 0
            ? $"{nativeInput}与{projectionInput}两条通路均不可用，该镜像无法交互。"
            : $"两条通路均不可用。{string.Join("；", reasons)}";
    }
}

/// <summary>单条保真度的展示文案。</summary>
/// <param name="Label">条目名称。</param>
/// <param name="Value">实测结论的中文表述。</param>
/// <param name="IsPass">是否实测通过。</param>
public sealed record FidelityItemPresentation(string Label, string Value, bool IsPass);

/// <summary>把保真度六项实测结论映射为界面文案，未实测如实显示为「未实测」。</summary>
public static class FidelityMapper
{
    /// <summary>
    /// 取保真度六项的界面呈现，顺序固定为 P1 至 P6。
    /// </summary>
    /// <param name="fidelity">保真度逐项实测结论。</param>
    /// <returns>六条展示文案。</returns>
    public static IReadOnlyList<FidelityItemPresentation> Describe(FidelitySet fidelity)
    {
        ArgumentNullException.ThrowIfNull(fidelity);

        return new[]
        {
            Item("P1 可获取 Root 权限", fidelity.P1Root),
            Item("P2 系统分区可写", fidelity.P2SystemWrite),
            Item("P3 可安装模块", fidelity.P3ModuleFlash),
            Item("P4 可刷镜像", fidelity.P4ImageSwap),
            Item("P5 Root 权限持久", fidelity.P5RootPersist),
            Item("P6 可运行 ARM 应用", fidelity.P6ArmApp)
        };
    }

    private static FidelityItemPresentation Item(string label, VerificationState state) =>
        state switch
        {
            VerificationState.Pass => new FidelityItemPresentation(label, "已实测通过", IsPass: true),
            VerificationState.Fail => new FidelityItemPresentation(label, "实测未通过", IsPass: false),
            // 未实测绝不能显示成通过，宁可显示为未实测。
            _ => new FidelityItemPresentation(label, "未实测", IsPass: false)
        };
}