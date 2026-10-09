using System.Text.RegularExpressions;

namespace XBear.Core.Qemu;

/// <summary>
/// 宿主级硬件加速器参数。默认面向 Windows 宿主选择 WHPX，
/// 并默认关闭内核 irqchip 直通：部分宿主上开启直通会让客户机 CPU 完全不执行，
/// 关闭后中断由内核模拟，代价只是多一次陷入，在支持直通的宿主上同样安全。
/// </summary>
public sealed class AcceleratorOptions
{
    /// <summary>Windows 宿主默认使用的加速器名称。</summary>
    public const string WhpxAccelerator = "whpx";

    /// <summary>WHPX 的默认附加选项，关闭内核 irqchip 直通。</summary>
    public const string DefaultWhpxOptions = "kernel-irqchip=off";

    private static readonly Regex AcceleratorNamePattern =
        new(@"^[A-Za-z0-9_.\-]+$", RegexOptions.CultureInvariant);

    private static readonly Regex AcceleratorOptionPattern =
        new(@"^[A-Za-z0-9_.,=+:\-]+$", RegexOptions.CultureInvariant);

    /// <summary>默认参数：WHPX 且关闭内核 irqchip 直通。</summary>
    public static AcceleratorOptions Default { get; } = new();

    /// <summary>加速器名称，对应 -accel 的第一个取值。</summary>
    public string Accelerator { get; init; } = WhpxAccelerator;

    /// <summary>加速器附加选项，形如逗号分隔的 key=value 序列，可为空。</summary>
    public string? Options { get; init; } = DefaultWhpxOptions;

    /// <summary>加速器名称是否可安全下发。</summary>
    /// <param name="name">待校验的加速器名称。</param>
    /// <returns>名称非空且仅含允许字符时返回 true。</returns>
    public static bool IsValidAcceleratorName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && AcceleratorNamePattern.IsMatch(name.Trim());

    /// <summary>加速器附加选项是否可安全下发。</summary>
    /// <param name="options">待校验的附加选项，空白视为不下发任何选项。</param>
    /// <returns>为空或仅含允许字符时返回 true。</returns>
    public static bool IsValidOptions(string? options) =>
        string.IsNullOrWhiteSpace(options) || AcceleratorOptionPattern.IsMatch(options.Trim());
}