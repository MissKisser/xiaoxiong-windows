using XBear.Core.Diagnostics;

namespace XBear.Core.Tests.Diagnostics;

/// <summary>
/// 构建标识装配与解析单元测试。
/// </summary>
public sealed class BuildInfoTests
{
    [Fact]
    public void BuildIdPropertyIsNotEmptyAndHasValidFormat()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.BuildId));
        Assert.Contains("+", BuildInfo.BuildId);
        Assert.DoesNotContain("{", BuildInfo.BuildId);
        Assert.DoesNotContain("}", BuildInfo.BuildId);
    }

    [Fact]
    public void CommitShortIsSevenCharactersOrFallback()
    {
        string commit = BuildInfo.CommitShort;
        Assert.False(string.IsNullOrWhiteSpace(commit));
        Assert.True(commit.Length <= 7 || commit == BuildInfo.UnknownCommitShort);
    }

    [Theory]
    [InlineData("0.1.0+abcdef0123456789", "abcdef0")]
    [InlineData("0.1.0+1234567", "1234567")]
    [InlineData("0.1.0+abc", "abc")]
    [InlineData("0.1.0+unknown", "unknown")]
    [InlineData("0.1.0+", "unknown")]
    [InlineData("0.1.0", "unknown")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    public void ResolveCommitShortHandlesVariousInputs(string? input, string expected)
    {
        string actual = BuildInfo.ResolveCommitShort(input);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("0.1.0+abcdef0", "0.1.0")]
    [InlineData("1.2.3+1234567", "1.2.3")]
    [InlineData("9.9.9", "9.9.9")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    public void ResolveProductVersionHandlesVariousInputs(string? input, string expected)
    {
        string actual = BuildInfo.ResolveProductVersion(input);
        Assert.Equal(expected, actual);
    }
}