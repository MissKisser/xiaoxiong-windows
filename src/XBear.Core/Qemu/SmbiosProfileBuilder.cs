using System.Text;
using System.Text.RegularExpressions;
using XBear.Core.Diagnostics;
using XBear.Core.Identity;
using XBear.Core.Spec;

namespace XBear.Core.Qemu;

/// <summary>
/// 机型族画像到 DMI/SMBIOS 条目的映射。全部取值由画像与设备身份派生，禁止手写常量。
/// </summary>
/// <remarks>
/// SMBIOS 按条目分隔符切分取值，因此逗号是 QEMU 的选项分隔符、双引号会破坏命令行结构，
/// 这两个字符必须被拒绝；空格则是真实机型串的常态，必须放行——
/// 白名单若按标识通道统一收窄到无空格，机型族画像里的销售名就会被整段丢弃，
/// 反而退回宿主仿真器的默认机型串。
/// </remarks>
public static partial class SmbiosProfileBuilder
{
    /// <summary>BIOS 信息条目类型号。</summary>
    public const string BiosInformationEntryType = "0";

    /// <summary>系统信息条目类型号。</summary>
    public const string SystemInformationEntryType = "1";

    /// <summary>主板条目类型号。</summary>
    public const string BaseboardEntryType = "2";

    /// <summary>机箱条目类型号。</summary>
    public const string ChassisEntryType = "3";

    /// <summary>处理器条目类型号。</summary>
    public const string ProcessorEntryType = "4";

    /// <summary>SMBIOS 取值允许的字符集：字母、数字、空格与常见符号，禁止逗号与双引号。</summary>
    private const string AllowedValuePattern = "^[A-Za-z0-9][A-Za-z0-9 ._+/()#:-]*$";

    /// <summary>SMBIOS 条目中的键值分隔符。</summary>
    private const string EntrySeparator = ",";

    /// <summary>SMBIOS 条目中的键与值分隔符。</summary>
    private const string KeyValueSeparator = "=";

    [GeneratedRegex(AllowedValuePattern, RegexOptions.CultureInvariant)]
    private static partial Regex AllowedValueRegex();

    /// <summary>
    /// 按机型族画像与设备身份组装全部 SMBIOS 条目。
    /// </summary>
    /// <param name="profile">机型族画像，提供全部机型串。</param>
    /// <param name="identity">设备身份，提供序列号与资产号。</param>
    /// <returns>逐条下发的 SMBIOS 条目序列，顺序为 BIOS、系统、主板、机箱、处理器。</returns>
    /// <exception cref="XBearException">任一取值含白名单外字符时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public static IReadOnlyList<string> BuildEntries(DeviceProfile profile, DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(identity);

        string serialNo = Require(identity.SerialNo, "设备序列号");

        return new[]
        {
            // 固件条目只接受 vendor 与 version：宿主实测确认日期键被拒绝，
            // 因此固件日期属已知不可改写项，不在本条目内伪造。
            Compose(
                BiosInformationEntryType,
                ("vendor", profile.Manufacturer + " Firmware"),
                ("version", profile.ProductVersion + ".0")),

            Compose(
                SystemInformationEntryType,
                ("manufacturer", profile.Manufacturer),
                ("product", profile.ProductName),
                ("version", profile.ProductVersion),
                ("family", profile.ProductFamily),
                ("sku", profile.ProductSku),
                ("serial", serialNo)),

            Compose(
                BaseboardEntryType,
                ("manufacturer", profile.BoardManufacturer),
                ("product", profile.BoardProduct),
                ("version", profile.ProductVersion),
                ("serial", serialNo),
                ("asset", profile.ChassisAsset)),

            Compose(
                ChassisEntryType,
                ("manufacturer", profile.ChassisManufacturer),
                ("version", profile.ProductVersion),
                ("serial", serialNo),
                ("asset", profile.ChassisAsset),
                ("sku", profile.ProductSku)),

            Compose(
                ProcessorEntryType,
                ("manufacturer", profile.CpuManufacturer),
                ("version", profile.CpuVersion),
                ("serial", serialNo),
                ("asset", profile.ChassisAsset)),
        };
    }

    /// <summary>
    /// 判断取值是否可安全下发到 SMBIOS 通道。
    /// </summary>
    /// <param name="value">待校验的取值。</param>
    /// <returns>非空且仅含白名单字符时返回 true。</returns>
    public static bool IsAllowedValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) && AllowedValueRegex().IsMatch(value.Trim());

    /// <summary>
    /// 组装单条 SMBIOS 条目。
    /// </summary>
    /// <param name="entryType">条目类型号。</param>
    /// <param name="fields">键值对序列，顺序即条目内的取值顺序。</param>
    /// <returns>形如 type=N,key=value 的条目文本。</returns>
    /// <exception cref="XBearException">任一取值含白名单外字符时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private static string Compose(string entryType, params (string Key, string Value)[] fields)
    {
        var builder = new StringBuilder();
        builder.Append("type=").Append(entryType);

        foreach (var (key, value) in fields)
        {
            string trimmed = value?.Trim() ?? string.Empty;

            if (!IsAllowedValue(trimmed))
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"SMBIOS 条目键 {key} 的取值 {value} 含非法字符。",
                    "SMBIOS 取值允许字母、数字、空格与常见符号，但不得含逗号与双引号：逗号是 QEMU 的选项分隔符，双引号会破坏命令行结构。");
            }

            builder.Append(EntrySeparator).Append(key).Append(KeyValueSeparator).Append(trimmed);
        }

        return builder.ToString();
    }

    private static string Require(string? value, string fieldName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new XBearException(
                ErrorCategory.Spec,
                $"{fieldName} 缺失，无法组装 SMBIOS 条目。",
                "设备身份必须在实例创建时生成并持久化；请重新创建该实例后再启动。")
            : value.Trim();
}