using XBear.Core.Identity;

namespace XBear.Core.Tests.Identity;

/// <summary>Luhn 校验位与 IMEI 生成测试。</summary>
public sealed class LuhnTests
{
    [Theory]
    [InlineData("490154203237518")]
    [InlineData("861234567890127")]
    [InlineData("356938035643809")]
    public void IsValid_已知有效Imei_返回真(string imei)
    {
        Assert.True(Luhn.IsValid(imei));
    }

    [Theory]
    [InlineData("490154203237518", "490154203237519")]
    [InlineData("356938035643809", "356938035643800")]
    [InlineData("861234567890127", "861234567890124")]
    public void IsValid_篡改一位后_返回假(string valid, string tampered)
    {
        Assert.True(Luhn.IsValid(valid));
        Assert.False(Luhn.IsValid(tampered));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("49015420323751a")]
    [InlineData("4901542032375 8")]
    public void IsValid_非数字或长度不足_返回假(string? candidate)
    {
        Assert.False(Luhn.IsValid(candidate));
    }

    [Theory]
    [InlineData("49015420323751", 8)]
    [InlineData("35693803564380", 9)]
    [InlineData("86123456789012", 7)]
    public void ComputeCheckDigit_与已知校验位一致(string payload, int expected)
    {
        Assert.Equal(expected, Luhn.ComputeCheckDigit(payload));
    }

    [Fact]
    public void AppendCheckDigit_拼接后可被自身校验通过()
    {
        const string payload = "86123456789012";

        string full = Luhn.AppendCheckDigit(payload);

        Assert.Equal(payload + "7", full);
        Assert.True(Luhn.IsValid(full));
    }

    [Fact]
    public void ComputeCheckDigit_载荷含非数字_抛出参数异常()
    {
        Assert.Throws<ArgumentException>(() => Luhn.ComputeCheckDigit("86123a789012"));
        Assert.Throws<ArgumentException>(() => Luhn.ComputeCheckDigit(string.Empty));
    }

    [Fact]
    public void 生成Imei_始终通过Luhn校验且长度为15位()
    {
        for (int i = 0; i < 200; i++)
        {
            string imei = CryptoDeviceIdentityGenerator.GenerateImei();

            Assert.Equal(15, imei.Length);
            Assert.True(Luhn.IsValid(imei), $"生成的 IMEI {imei} 未通过 Luhn 校验。");
            Assert.StartsWith(CryptoDeviceIdentityGenerator.ImeiCountryCode, imei, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 生成Imei_Tac非全零()
    {
        for (int i = 0; i < 100; i++)
        {
            string imei = CryptoDeviceIdentityGenerator.GenerateImei();
            string tac = imei[..8];

            Assert.False(tac == "00000000", "TAC 不得为全零。");
        }
    }
}