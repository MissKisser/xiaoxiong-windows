using System.Text.RegularExpressions;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.Core.Qemu;

/// <summary>
/// 设备标识在各下发通道上的承载形式。
/// </summary>
/// <remarks>
/// 磁盘引导（只挂载启动盘、不传 -kernel）时 QEMU 拒绝 -append，
/// 因此该模式下标识没有内核命令行通道：序列号改由 DMI/SMBIOS 系统信息条目承载，
/// Android-x86 系镜像可从 DMI 读取该值。Android ID 与 IMEI 在磁盘引导模式下
/// 仍无标准下发通道，由 <see cref="KernelCommandLine"/> 保留为内核引导模式的实现。
/// </remarks>
public static class DeviceIdentityChannels
{
    /// <summary>DMI/SMBIOS 系统信息条目的类型号，序列号由该条目承载。</summary>
    public const string SystemInformationEntryType = "1";

    /// <summary>
    /// 标识取值允许的字符集。
    /// 内核按空白切分命令行、SMBIOS 按条目分隔符切分取值，
    /// 标识一旦掺入空白、引号或逗号就会被拆开或破坏条目结构，
    /// 导致实例以错误的标识启动或 QEMU 直接拒绝启动，因此按白名单校验并在越界时拒绝生成。
    /// </summary>
    private static readonly Regex AllowedValuePattern =
        new(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);

    /// <summary>标识取值是否可安全下发到任一通道。</summary>
    /// <param name="value">待校验的标识取值。</param>
    /// <returns>非空且仅含白名单字符时返回 true。</returns>
    public static bool IsAllowedValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) && AllowedValuePattern.IsMatch(value.Trim());

    /// <summary>
    /// 校验实例的全部标识字段。任一字段越界都拒绝生成：
    /// 越界字段在当前引导模式下可能根本没有通道，
    /// 但静默丢弃会让实例带着与配置不一致的标识启动，必须当场报错。
    /// </summary>
    /// <param name="identity">实例设备标识，可为空。</param>
    /// <exception cref="XBearException">任一标识字段含白名单外字符时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public static void ValidateIdentity(DeviceIdentity? identity)
    {
        if (identity is null)
        {
            return;
        }

        Validate(identity.SerialNo, "设备序列号");
        Validate(identity.AndroidId, "Android ID");
        Validate(identity.Imei, "IMEI");
    }

    /// <summary>
    /// 把实例序列号映射为 -smbios 的条目取值。
    /// 序列号为空时返回 null，调用方据此省略整条 -smbios 参数。
    /// </summary>
    /// <param name="identity">实例设备标识，可为空。</param>
    /// <returns>形如 type=1,serial=取值 的条目文本，序列号缺失时返回 null。</returns>
    /// <exception cref="XBearException">序列号含白名单外字符时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public static string? BuildSmbiosSerialEntry(DeviceIdentity? identity)
    {
        ValidateIdentity(identity);

        string? serialNo = identity?.SerialNo?.Trim();
        return string.IsNullOrEmpty(serialNo)
            ? null
            : $"type={SystemInformationEntryType},serial={serialNo}";
    }

    private static void Validate(string? value, string fieldName)
    {
        if (value is null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!AllowedValuePattern.IsMatch(value.Trim()))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"{fieldName} {value} 含非法字符。",
                "设备标识只能使用字母、数字、点、下划线与连字符；请修正实例配置中的该标识后重试。");
        }
    }
}