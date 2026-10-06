using CommunityToolkit.Mvvm.ComponentModel;
using XBear.Core.Abstractions;
using XBear.Core.Spec;
using XBear.App.Presentation;

namespace XBear.App.ViewModels;

/// <summary>实例列表中的一项，展示名称、状态、镜像、Android 版本与暴露级别。</summary>
public sealed partial class InstanceListItemViewModel : ObservableObject
{
    private readonly InstanceSpec _spec;

    [ObservableProperty]
    private InstanceState _state;

    [ObservableProperty]
    private string _stateLabel;

    /// <summary>
    /// 构造列表项。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="state">实例当前运行态。</param>
    /// <param name="imageDisplayName">镜像显示名，缺失时回退为镜像引用。</param>
    public InstanceListItemViewModel(InstanceSpec spec, InstanceState state, string imageDisplayName)
    {
        ArgumentNullException.ThrowIfNull(spec);

        _spec = spec;
        _state = state;
        _stateLabel = InstanceStateMapper.Describe(state).Label;
        ImageDisplayName = string.IsNullOrWhiteSpace(imageDisplayName) ? spec.ImageRef : imageDisplayName;
    }

    /// <summary>实例标识。</summary>
    public string Id => _spec.Id;

    /// <summary>实例显示名。</summary>
    public string DisplayName => _spec.DisplayName;

    /// <summary>镜像显示名。</summary>
    public string ImageDisplayName { get; }

    /// <summary>实例引用的镜像标识。</summary>
    public string ImageRef => _spec.ImageRef;

    /// <summary>Android 版本，来自镜像清单，缺失时显示为未标注。</summary>
    public string AndroidVersion { get; set; } = "未标注";

    /// <summary>暴露级别的中文名，缺省为回环。</summary>
    public string ExposureLabel =>
        ExposureSelection.RequiresRiskBanner(_spec.Network?.Exposure) ? "对外可达" : "仅本机";

    /// <summary>实例级暴露级别原始取值。</summary>
    public string Exposure => _spec.Network?.Exposure ?? ExposureSelection.Loopback;

    /// <summary>实例配置原文，供详情区与诊断包使用。</summary>
    public InstanceSpec Spec => _spec;

    /// <summary>adb 端口是否实际监听在对外地址。</summary>
    public bool IsAdbExternallyBound =>
        ExposureSelection.HasExternallyBoundPort(_spec.Network?.Exposure, _spec.Network?.PortForwards);

    /// <summary>
    /// 同步实例状态，保持按钮可用性与列表文案一致。
    /// </summary>
    /// <param name="newState">新的实例状态。</param>
    public void ApplyState(InstanceState newState)
    {
        State = newState;
        StateLabel = InstanceStateMapper.Describe(newState).Label;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateLabel));
    }
}