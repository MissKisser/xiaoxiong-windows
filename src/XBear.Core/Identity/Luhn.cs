namespace XBear.Core.Identity;

/// <summary>
/// Luhn 校验位算法。IMEI 的末位校验位由前 14 位按此算法算出，
/// 单独抽出为可测试的纯函数，供生成与校验共用，避免两处实现漂移。
/// </summary>
public static class Luhn
{
    /// <summary>
    /// 校验一个候选串是否为合法的 Luhn 串（含末位校验位）。
    /// </summary>
    /// <param name="value">待校验的数字串，允许为 null。</param>
    /// <returns>全部为数字且末位校验位与计算值一致时返回 true。</returns>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 2)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        string payload = value[..^1];
        int actual = value[^1] - '0';
        return ComputeCheckDigit(payload) == actual;
    }

    /// <summary>
    /// 计算载荷的 Luhn 校验位。
    /// </summary>
    /// <param name="payload">不含校验位的纯数字载荷。</param>
    /// <returns>0 到 9 之间的校验位。</returns>
    /// <exception cref="ArgumentException">载荷为空或含非数字字符时抛出。</exception>
    public static int ComputeCheckDigit(string payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            throw new ArgumentException("载荷不能为空。", nameof(payload));
        }

        foreach (char c in payload)
        {
            if (c is < '0' or > '9')
            {
                throw new ArgumentException("载荷必须为纯数字。", nameof(payload));
            }
        }

        int sum = 0;
        bool doubleDigit = true;
        for (int i = payload.Length - 1; i >= 0; i--)
        {
            int digit = payload[i] - '0';
            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubleDigit = !doubleDigit;
        }

        return (10 - (sum % 10)) % 10;
    }

    /// <summary>
    /// 为载荷补上末位校验位，返回完整数字串。
    /// </summary>
    /// <param name="payload">不含校验位的纯数字载荷。</param>
    /// <returns>载荷与校验位拼接后的完整数字串。</returns>
    public static string AppendCheckDigit(string payload) =>
        payload + ComputeCheckDigit(payload).ToString(System.Globalization.CultureInfo.InvariantCulture);
}