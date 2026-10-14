using System.Reflection;

namespace XBear.Core.Diagnostics;

/// <summary>
/// 构建标识的统一入口。
/// 产品版本与提交短哈希在构建期由 Directory.Build.props 从版本契约与版本控制信息装配进
/// <see cref="AssemblyInformationalVersionAttribute"/>，格式与版本契约的 build.idFormat 一致；
/// 关于页、诊断包与运行期日志一律引用本类型，不自行拼接版本字符串。
/// </summary>
public static class BuildInfo
{
    /// <summary>无版本控制信息时提交短哈希的占位值，取值与版本契约的约定一致。</summary>
    public const string UnknownCommitShort = "unknown";

    /// <summary>提交短哈希的字符数。</summary>
    public const int CommitShortLength = 7;

    private const char CommitSeparator = '+';

    /// <summary>程序集信息版本全文，取值形如 <c>{productVersion}+{commitShort}</c>。</summary>
    public static string InformationalVersion { get; } = ReadInformationalVersion();

    /// <summary>
    /// 构建标识，与版本契约的 build.idFormat 格式一致。
    /// 关于页展示、诊断包记录与运行期日志共用这一个值。
    /// </summary>
    public static string BuildId => InformationalVersion;

    /// <summary>提交短哈希，无版本控制信息时为 <see cref="UnknownCommitShort"/>。</summary>
    public static string CommitShort => ResolveCommitShort(InformationalVersion);

    /// <summary>产品版本，取自构建标识中提交短哈希之前的部分。</summary>
    public static string ProductVersion => ResolveProductVersion(InformationalVersion);

    /// <summary>
    /// 从信息版本字符串解析提交短哈希。
    /// </summary>
    /// <param name="informationalVersion">程序集信息版本字符串，可为空。</param>
    /// <returns>提交短哈希；字符串缺位或分隔符后为空时为 <see cref="UnknownCommitShort"/>。</returns>
    public static string ResolveCommitShort(string? informationalVersion)
    {
        int separator = informationalVersion?.IndexOf(CommitSeparator) ?? -1;
        if (separator < 0 || separator >= informationalVersion!.Length - 1)
        {
            return UnknownCommitShort;
        }

        string rawCommit = informationalVersion[(separator + 1)..].Trim();
        if (rawCommit.Length == 0)
        {
            return UnknownCommitShort;
        }

        return rawCommit.Length > CommitShortLength ? rawCommit[..CommitShortLength] : rawCommit;
    }

    /// <summary>
    /// 从信息版本字符串解析产品版本。
    /// </summary>
    /// <param name="informationalVersion">程序集信息版本字符串，可为空。</param>
    /// <returns>提交短哈希之前的部分；字符串缺位时为 <see cref="UnknownCommitShort"/>。</returns>
    public static string ResolveProductVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return UnknownCommitShort;
        }

        int separator = informationalVersion.IndexOf(CommitSeparator);
        string product = separator < 0 ? informationalVersion : informationalVersion[..separator];
        return product.Trim().Length == 0 ? UnknownCommitShort : product.Trim();
    }

    private static string ReadInformationalVersion() =>
        typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? UnknownCommitShort;
}