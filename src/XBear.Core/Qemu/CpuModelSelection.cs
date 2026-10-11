using XBear.Core.Diagnostics;

namespace XBear.Core.Qemu;

/// <summary>
/// 处理器型号选型。按机型族画像逐族选型，而不是全局固定单一型号。
/// </summary>
/// <remarks>
/// 型号集合只收「已实测可引导目标镜像」的取值。把未验证的选型交给用户，
/// 表现是启动失败后自行排查，既损失可用性也没有拿到任何保真度收益；
/// 因此集合的扩充必须以实测引导成功为前提，不能凭型号名看起来更贴近真机就放行。
/// </remarks>
public static class CpuModelSelection
{
    /// <summary>机型族 <c>xb-vibe12-11</c> 的处理器型号。</summary>
    public const string Vibe12CpuModel = "Skylake-Client";

    /// <summary>机型族 <c>xb-nova8-11</c> 的处理器型号。</summary>
    public const string Nova8CpuModel = "Skylake-Client";

    /// <summary>机型族 <c>xb-edge7-11</c> 的处理器型号。</summary>
    public const string Edge7CpuModel = "Skylake-Client";

    /// <summary>关闭 CPUID 中 hypervisor 位与虚拟化专属指令组合的特性后缀。</summary>
    /// <remarks>
    /// 这些特性只在虚拟化下暴露：guest 内 /proc/cpuinfo 的 flags 末段与 invpcid 组合
    /// 都会成为可直接命中的暴露面。宿主实测确认逐项均被 QEMU 接受。
    /// 但「参数被接受」不等于「guest 内该位消失」：加速器层是否仍置位需实机回读确认，
    /// 单元测试只守住参数拼装形态，不据此宣称已生效。
    /// </remarks>
    public static readonly IReadOnlyList<string> SuppressedVirtualizationFeatures = new[]
    {
        "hypervisor=off",
        "invpcid=off",
        "fsgsbase=off",
        "kvmclock=off",
        "kvm-pv-eoi=off",
    };

    /// <summary>
    /// 取机型族画像对应的处理器型号。
    /// </summary>
    /// <param name="profileId">机型族标识。</param>
    /// <returns>该机型族登记的处理器型号。</returns>
    /// <exception cref="XBearException">机型族未登记处理器型号时抛出 <see cref="ErrorCategory.Identity"/>。</exception>
    public static string ForProfile(string profileId) => profileId switch
    {
        Identity.DeviceProfileCatalog.Vibe12ProfileId => Vibe12CpuModel,
        Identity.DeviceProfileCatalog.Nova8ProfileId => Nova8CpuModel,
        Identity.DeviceProfileCatalog.Edge7ProfileId => Edge7CpuModel,
        _ => throw new XBearException(
            ErrorCategory.Identity,
            $"机型族 {profileId} 未登记处理器型号。",
            "处理器型号必须与机型族画像同源；请在机型族表与处理器型号选型表中一并登记后重试。"),
    };

    /// <summary>
    /// 在处理器型号后附加关闭虚拟化特征的特性后缀。
    /// </summary>
    /// <param name="cpuModel">处理器型号，可已带特性后缀。</param>
    /// <returns>带虚拟化特征关闭后缀的完整 -cpu 取值。</returns>
    public static string WithSuppressedVirtualization(string cpuModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cpuModel);

        var builder = new System.Text.StringBuilder(cpuModel.Trim());
        foreach (var feature in SuppressedVirtualizationFeatures)
        {
            builder.Append(',').Append(feature);
        }

        return builder.ToString();
    }
}