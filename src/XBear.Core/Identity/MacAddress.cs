using System.Text.RegularExpressions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Identity;

/// <summary>
/// 由机型族画像与设备序列号确定性派生网卡 MAC。
/// </summary>
/// <remarks>
/// MAC 的末三字节不用独立随机源，而是由序列号的后段确定性映射：
/// 这样一来探针若发现「同一实例重启后 MAC 变了」或「不同实例 MAC 段重复」会直接命中，
/// 而确定性派生让回读比对不需要额外存储——序列号已在册，MAC 可随时重算复核。
/// </remarks>
public static partial class MacAddress
{
    /// <summary>MAC 字符集，六字节小写十六冒号分隔。</summary>
    private const string MacPattern = "^([0-9a-f]{2}:){5}[0-9a-f]{2}$";

    /// <summary>组播判定位取自首字节最低位，为 0 才是单播；契约要求末字节取偶数位。</summary>
    private const int MulticastBit = 0x01;

    /// <summary>末字节的最低位必须为 0，即单播。</summary>
    private const int LowBitMask = 0x01;

    /// <summary>全零 MAC 被契约显式拒绝：QEMU 会照单全收全零值，格式合法性由本侧兜底。</summary>
    public const string AllZeroMac = "00:00:00:00:00:00";

    [GeneratedRegex(MacPattern, RegexOptions.CultureInvariant)]
    private static partial Regex MacRegex();

    /// <summary>
    /// 由机型族画像与序列号派生 MAC。OUI 取机型族表的内置固定段，
    /// 末三字节由序列号后六位十六确定性映射。
    /// </summary>
    /// <param name="profile">机型族画像，提供 OUI 段与选型盐。</param>
    /// <param name="serialNo">设备序列号，取其后六位十六参与派生。</param>
    /// <returns>形如 xx:xx:xx:xx:xx:xx 的小写 MAC。</returns>
    /// <exception cref="XBearException">序列号长度不足或画像缺失时抛出 <see cref="ErrorCategory.Identity"/>。</exception>
    public static string Derive(DeviceProfile profile, string serialNo)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNo);

        int payload = DerivePayload(profile, serialNo);
        return Format(profile.MacOui, payload);
    }

    /// <summary>
    /// 判断 MAC 是否为契约允许的取值：格式合法、非全零、且为单播方向。
    /// </summary>
    /// <param name="mac">待校验的 MAC。</param>
    /// <returns>三项约束全部成立时返回 true。</returns>
    public static bool IsValid(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
        {
            return false;
        }

        string trimmed = mac.Trim();
        if (!MacRegex().IsMatch(trimmed))
        {
            return false;
        }

        if (string.Equals(trimmed, AllZeroMac, StringComparison.Ordinal))
        {
            return false;
        }

        var octets = trimmed.Split(':');
        int first = Convert.ToInt32(octets[0], 16);
        int last = Convert.ToInt32(octets[5], 16);

        // 单播方向以首字节最低位判定，与内核网络栈的地址分类一致。
        return (first & MulticastBit) == 0 && (last & LowBitMask) == 0;
    }

    /// <summary>
    /// 校验 MAC 是否为契约允许的取值，越界时给出可操作的处置建议。
    /// </summary>
    /// <param name="mac">待校验的 MAC。</param>
    /// <exception cref="XBearException">MAC 不合法时抛出 <see cref="ErrorCategory.Identity"/>。</exception>
    public static void EnsureValid(string? mac)
    {
        if (!IsValid(mac))
        {
            throw new XBearException(
                ErrorCategory.Identity,
                $"网卡 MAC {mac ?? "（空）"} 不满足契约。",
                "MAC 必须为六字节小写十六冒号分隔、非全零、且末字节最低位为 0（单播方向）。");
        }
    }

    private static int DerivePayload(DeviceProfile profile, string serialNo)
    {
        // 序列号恒为 XBSN 加 12 位大写十六，后六位十六提供 24 位派生空间。
        const int HexPayloadLength = 6;

        if (serialNo.Length < HexPayloadLength)
        {
            throw new XBearException(
                ErrorCategory.Identity,
                $"序列号 {serialNo} 长度不足，无法派生网卡 MAC。",
                "请重新生成该实例的设备标识。");
        }

        string payload = serialNo[^HexPayloadLength..];
        return int.Parse(payload, System.Globalization.NumberStyles.HexNumber)
            ^ profile.ProfileSeed;
    }

    private static string Format(string oui, int payload)
    {
        // 末三字节取自派生载荷，末字节最低位清零以保证单播方向。
        int b3 = (payload >> 16) & 0xFF;
        int b4 = (payload >> 8) & 0xFF;
        int b5 = payload & 0xFE;

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{oui}:{b3:x2}:{b4:x2}:{b5:x2}");
    }
}