using System.Text;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.Core.Qemu;

/// <summary>
/// 设备标识与镜像保真度到内核引导命令行的映射。
/// 全部为纯函数，不接触文件系统与进程，便于单元测试与逐项核对。
/// </summary>
/// <remarks>
/// AOSP 系（含 Bliss OS 与 Android-x86 系的 androidboot 派生内核）在 init 阶段
/// 解析 /proc/cmdline 中以 <see cref="AndroidBootPropertyPrefix"/> 开头的键值对，
/// 并映射为只读的 ro.boot.* 属性，随后由 guest 侧框架消费。
/// 该通道只覆盖「引导期属性」，因此并非每一项设备标识都能经此生效。
/// QEMU 只在传了 -kernel 的内核引导模式下接受 -append，
/// 因此本合成器的调用方必须在磁盘引导模式下跳过 -append，
/// 序列号改由 <see cref="DeviceIdentityChannels"/> 的 SMBIOS 通道下发。
/// </remarks>
public static class KernelCommandLine
{
    /// <summary>内核引导属性的统一前缀。</summary>
    public const string AndroidBootPropertyPrefix = "androidboot.";

    /// <summary>
    /// 序列号对应的引导键。该键是标识通道中唯一具备标准 guest 侧消费方的取值，
    /// AOSP init 会据此设置 ro.serialno 与 ro.boot.serialno。
    /// </summary>
    public const string SerialNoKey = AndroidBootPropertyPrefix + "serialno";

    /// <summary>
    /// Android ID 对应的引导键。AOSP 未为该标识定义内核引导期消费方，
    /// 系统内的实际取值由 secure 设置库持有，此处按下发约定输出作为扩展点。
    /// </summary>
    public const string AndroidIdKey = AndroidBootPropertyPrefix + "android_id";

    /// <summary>
    /// IMEI 对应的引导键。AOSP 未为该标识定义内核引导期消费方，
    /// 系统内的实际取值由基带侧持有，此处按下发约定输出作为扩展点。
    /// </summary>
    public const string ImeiKey = AndroidBootPropertyPrefix + "imei";

    /// <summary>
    /// 系统分区可写对应的引导键。仅在镜像保真度实测通过时下发，
    /// 未实测不得臆测为可写。
    /// </summary>
    public const string WritableSystemKey = AndroidBootPropertyPrefix + "writable_system";

    /// <summary>系统分区可写开关的取值。</summary>
    public const string WritableSystemEnabledValue = "1";

    /// <summary>命令行片段之间的分隔符，内核按空白切分命令行。</summary>
    public const string FragmentSeparator = " ";

    /// <summary>设备标识各字段到引导键的固定映射顺序。</summary>
    private static readonly (string Key, Func<DeviceIdentity, string?> Selector)[] IdentityBindings =
    {
        (SerialNoKey, identity => identity.SerialNo),
        (AndroidIdKey, identity => identity.AndroidId),
        (ImeiKey, identity => identity.Imei),
    };

    /// <summary>
    /// 把设备标识映射为内核引导命令行片段，空字段不产出片段。
    /// </summary>
    /// <param name="identity">实例设备标识，可为空，空值不产出任何片段。</param>
    /// <returns>按固定顺序排列的引导命令行片段集合。</returns>
    /// <exception cref="XBearException">标识取值含白名单外字符时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public static IReadOnlyList<string> BuildIdentityFragments(DeviceIdentity? identity)
    {
        if (identity is null)
        {
            return Array.Empty<string>();
        }

        var fragments = new List<string>(IdentityBindings.Length);
        foreach (var (key, selector) in IdentityBindings)
        {
            var value = selector(identity);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            fragments.Add(ComposeFragment(key, value.Trim()));
        }

        return fragments;
    }

    /// <summary>
    /// 把系统可写保真度映射为内核引导命令行片段。
    /// 仅实测通过时产出片段，未实测与实测失败均视为不可写。
    /// </summary>
    /// <param name="systemWrite">镜像保真度中 P2 系统可写的实测结论。</param>
    /// <returns>实测通过时返回可写开关片段，否则返回 null。</returns>
    public static string? BuildWritableSystemFragment(VerificationState systemWrite)
        => systemWrite == VerificationState.Pass
            ? ComposeFragment(WritableSystemKey, WritableSystemEnabledValue)
            : null;

    /// <summary>
    /// 合成完整的内核引导命令行。仅内核引导模式（已配置 -kernel）可使用。
    /// </summary>
    /// <param name="identity">实例设备标识，可为空。</param>
    /// <param name="systemWrite">镜像保真度中 P2 系统可写的实测结论。</param>
    /// <returns>以空白分隔的引导命令行，无任何片段时返回空串。</returns>
    /// <exception cref="XBearException">标识取值含白名单外字符时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public static string Build(DeviceIdentity? identity, VerificationState systemWrite)
    {
        var fragments = new List<string>(BuildIdentityFragments(identity));
        var writable = BuildWritableSystemFragment(systemWrite);
        if (writable is not null)
        {
            fragments.Add(writable);
        }

        return Compose(fragments);
    }

    /// <summary>
    /// 按固定分隔符拼接命令行片段，空集合与全空白片段均不参与拼接，空集合得到空串。
    /// </summary>
    /// <param name="fragments">待拼接的片段集合，可为空。</param>
    /// <returns>拼接后的命令行文本。</returns>
    public static string Compose(IEnumerable<string> fragments)
    {
        ArgumentNullException.ThrowIfNull(fragments);

        var builder = new StringBuilder();
        foreach (var fragment in fragments)
        {
            if (string.IsNullOrWhiteSpace(fragment))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(FragmentSeparator);
            }

            builder.Append(fragment);
        }

        return builder.ToString();
    }

    /// <summary>拼接单个键值对形式的引导命令行片段。</summary>
    /// <param name="key">引导键。</param>
    /// <param name="value">引导取值。</param>
    /// <returns>形如 键=值 的片段文本。</returns>
    /// <exception cref="XBearException">取值含白名单外字符时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private static string ComposeFragment(string key, string value)
    {
        if (!DeviceIdentityChannels.IsAllowedValue(value))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"引导命令行取值 {value} 含非法字符。",
                "设备标识只能使用字母、数字、点、下划线与连字符；请修正实例配置中的该标识后重试。");
        }

        return $"{key}={value}";
    }
}