using System.Security.Cryptography;
using System.Text;
using XBear.Core.Spec;

namespace XBear.Core.Identity;

/// <summary>
/// 标识原始值生成器。只负责产出格式合法的标识串，不负责查重，
/// 便于测试时注入确定性序列验证冲突重生成路径。
/// </summary>
public interface IDeviceIdentityGenerator
{
    /// <summary>
    /// 产出一组全新标识。此处不做查重，冲突处置由工厂负责。
    /// </summary>
    /// <returns>新生成的标识组合。</returns>
    DeviceIdentity Next();
}

/// <summary>
/// 基于密码学随机源的标识生成器。全部取值走 <see cref="RandomNumberGenerator"/>，
/// 不用 <see cref="Random"/>：标识碰撞会让宿主应用把多个实例判定为同一台物理设备，
/// 表现为登录互踢、设备数超限与风控触发，多开价值直接归零。
/// </summary>
public sealed class CryptoDeviceIdentityGenerator : IDeviceIdentityGenerator
{
    /// <summary>序列号前缀。</summary>
    public const string SerialNoPrefix = "XBSN";

    /// <summary>序列号中随机十六进制位的长度。</summary>
    public const int SerialNoHexLength = 12;

    /// <summary>Android ID 的十六进制位数，Android ID 本身即 16 位十六进制定长。</summary>
    public const int AndroidIdHexLength = 16;

    /// <summary>IMEI 的 GSMA 国家码，中国大陆为 86。</summary>
    public const string ImeiCountryCode = "86";

    /// <summary>TAC 中国家码之后的随机位数，TAC 合计 8 位。</summary>
    public const int ImeiTacRandomLength = 6;

    /// <summary>IMEI 序列号位数，合计 8 位 TAC 加 6 位序列号加 1 位校验位。</summary>
    public const int ImeiSerialLength = 6;

    private static readonly char[] HexDigitsUpper = "0123456789ABCDEF".ToCharArray();

    /// <summary>
    /// 产出一组全新标识，三个字段各自独立取随机值。
    /// </summary>
    /// <returns>新生成的标识组合。</returns>
    public DeviceIdentity Next() => new()
    {
        SerialNo = GenerateSerialNo(),
        AndroidId = GenerateAndroidId(),
        Imei = GenerateImei(),
    };

    /// <summary>
    /// 生成序列号，形如 XBSN 加 12 位大写十六进制。
    /// </summary>
    /// <returns>长度为 16 的序列号。</returns>
    public static string GenerateSerialNo() =>
        SerialNoPrefix + GenerateHex(SerialNoHexLength, upper: true);

    /// <summary>
    /// 生成 Android ID，为 16 位小写十六进制。
    /// </summary>
    /// <returns>长度为 16 的 Android ID。</returns>
    public static string GenerateAndroidId() => GenerateHex(AndroidIdHexLength, upper: false);

    /// <summary>
    /// 生成 IMEI，为 15 位数字，末位为 Luhn 校验位。
    /// TAC 以国家码 86 开头，避免全零这类一眼即假的取值。
    /// </summary>
    /// <returns>通过 Luhn 校验的 15 位 IMEI。</returns>
    public static string GenerateImei()
    {
        var builder = new StringBuilder(ImeiCountryCode, ImeiCountryCode.Length + ImeiTacRandomLength + ImeiSerialLength + 1);
        builder.Append(GenerateDecimal(ImeiTacRandomLength));
        builder.Append(GenerateDecimal(ImeiSerialLength));
        return Luhn.AppendCheckDigit(builder.ToString());
    }

    private static string GenerateHex(int length, bool upper)
    {
        char[] buffer = new char[length];
        for (int i = 0; i < length; i++)
        {
            int value = RandomNumberGenerator.GetInt32(16);
            buffer[i] = upper ? HexDigitsUpper[value] : "0123456789abcdef"[value];
        }

        return new string(buffer);
    }

    private static string GenerateDecimal(int length)
    {
        char[] buffer = new char[length];
        for (int i = 0; i < length; i++)
        {
            buffer[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        }

        return new string(buffer);
    }
}