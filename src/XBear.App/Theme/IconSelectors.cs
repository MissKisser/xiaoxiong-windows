using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using XBear.Core.Abstractions;

namespace XBear.App.Theme;

/// <summary>
/// 图标资源键的集中定义。XAML 侧一律通过这些常量访问图标，
/// 避免在标记里散落字符串字面量，也让测试可以直接断言键名。
/// </summary>
public static class IconKeys
{
    /// <summary>实例列表行的图标键。</summary>
    public const string Instance = "Icon.Instance";

    /// <summary>镜像的图标键。</summary>
    public const string Image = "Icon.Image";

    /// <summary>启动动作的图标键。</summary>
    public const string Start = "Icon.Start";

    /// <summary>停止动作的图标键。</summary>
    public const string Stop = "Icon.Stop";

    /// <summary>删除动作的图标键。</summary>
    public const string Delete = "Icon.Delete";

    /// <summary>创建快照的图标键。</summary>
    public const string Snapshot = "Icon.Snapshot";

    /// <summary>恢复的图标键。</summary>
    public const string Restore = "Icon.Restore";

    /// <summary>多开的图标键。</summary>
    public const string MultiInstance = "Icon.MultiInstance";

    /// <summary>投屏的图标键。</summary>
    public const string Projection = "Icon.Projection";

    /// <summary>输入通道的图标键。</summary>
    public const string InputChannel = "Icon.InputChannel";

    /// <summary>文件传输的图标键。</summary>
    public const string FileTransfer = "Icon.FileTransfer";

    /// <summary>保真度的图标键。</summary>
    public const string Fidelity = "Icon.Fidelity";

    /// <summary>设备标识的图标键。</summary>
    public const string DeviceIdentity = "Icon.DeviceIdentity";

    /// <summary>诊断包的图标键。</summary>
    public const string Diagnostics = "Icon.Diagnostics";

    /// <summary>暴露级别的图标键。</summary>
    public const string Exposure = "Icon.Exposure";

    /// <summary>宿主环境错误的图标键。</summary>
    public const string ErrorEnvironment = "Icon.ErrorEnvironment";

    /// <summary>运行态的图标键。</summary>
    public const string StateRunning = "Icon.StateRunning";

    /// <summary>停止态的图标键。</summary>
    public const string StateStopped = "Icon.StateStopped";

    /// <summary>失败态的图标键。</summary>
    public const string StateFailed = "Icon.StateFailed";

    /// <summary>
    /// 图标集覆盖的全部键。资源字典必须逐一提供这些键，缺一即为接入不完整。
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Instance, Image, Start, Stop, Delete,
        Snapshot, Restore, MultiInstance,
        Projection, InputChannel, FileTransfer,
        Fidelity, DeviceIdentity, Diagnostics, Exposure, ErrorEnvironment,
        StateRunning, StateStopped, StateFailed
    ];

    /// <summary>
    /// 从应用资源中按键取出图标绘制。
    /// </summary>
    /// <param name="key">图标资源键。</param>
    /// <returns>图标绘制；键为空或资源缺失时返回 null。</returns>
    public static ImageSource? Find(string? key) =>
        !string.IsNullOrEmpty(key) &&
        System.Windows.Application.Current?.TryFindResource(key) is ImageSource source
            ? source
            : null;
}

/// <summary>
/// 实例运行态到状态图标的映射。状态图标只表示状态，不表示原因：
/// 启动中与停止中这类过渡态归入运行态图形，失败态单独用失败图形。
/// 映射只依赖 <see cref="InstanceState"/>，因此列表项无需新增视图模型属性。
/// </summary>
public sealed class StateIconConverter : IValueConverter
{
    /// <summary>
    /// 按运行态取状态图标。
    /// </summary>
    /// <param name="value">实例运行态。</param>
    /// <param name="targetType">目标类型。</param>
    /// <param name="parameter">未使用。</param>
    /// <param name="culture">未使用。</param>
    /// <returns>对应的状态图标绘制。</returns>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        IconKeys.Find(KeyOf(value as InstanceState?));

    /// <summary>
    /// 状态图标是单向映射，不支持回写。
    /// </summary>
    /// <param name="value">未使用。</param>
    /// <param name="targetType">目标类型。</param>
    /// <param name="parameter">未使用。</param>
    /// <param name="culture">未使用。</param>
    /// <returns>始终抛出。</returns>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("状态图标是单向映射，不可回写。");

    /// <summary>
    /// 取运行态对应的图标键。未知取值回落到停止态，
    /// 保证界面总能画出状态图标，不会出现空白格。
    /// </summary>
    /// <param name="state">实例运行态，null 表示尚未知状态。</param>
    /// <returns>图标键。</returns>
    public static string KeyOf(InstanceState? state) =>
        state switch
        {
            InstanceState.Running or InstanceState.Starting or InstanceState.Stopping =>
                IconKeys.StateRunning,
            InstanceState.Faulted => IconKeys.StateFailed,
            _ => IconKeys.StateStopped
        };
}