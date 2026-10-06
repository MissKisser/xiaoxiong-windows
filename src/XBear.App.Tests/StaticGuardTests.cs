using System.IO;
using System.Text.RegularExpressions;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>测试用的共享规格读取器，以输出目录下的 spec/ 为根。</summary>
public static class XBeeSpec
{
    /// <summary>
    /// 返回以输出目录 spec/ 为根的规格读取器。
    /// </summary>
    /// <returns>规格读取器。</returns>
    public static SpecLoader TestSpec() =>
        new(Path.Combine(AppContext.BaseDirectory, SpecLoader.SpecDirectoryName));
}

/// <summary>界面源码与 XAML 的定位，供静态扫描使用。</summary>
public static class UiSources
{
    /// <summary>输出目录下界面源码的根目录。</summary>
    public static string Root { get; } =
        Path.Combine(AppContext.BaseDirectory, "ui");

    /// <summary>
    /// 枚举界面源码目录下全部待扫描文件。
    /// </summary>
    /// <returns>待扫描文件路径序列。</returns>
    public static IEnumerable<string> Enumerate() =>
        Directory.Exists(Root)
            ? Directory.EnumerateFiles(Root, "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            : Array.Empty<string>();
}

/// <summary>
/// 规则一自证：界面源码与 XAML 中不得出现字面量十六进制颜色。
/// 所有视觉值必须来自 DesignTokens，由 Theme/TokenResources.cs 写入资源字典后经 DynamicResource 消费。
/// </summary>
public class NoLiteralColorTests
{
    /// <summary>匹配 #RGB、#RRGGBB、#AARRGGBB 形式的字面量颜色。</summary>
    private static readonly Regex HexColor = new(
        @"#[0-9a-fA-F]{3,8}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void UiSourcesAreLocatable()
    {
        Assert.True(
            Directory.Exists(UiSources.Root),
            $"界面源码目录不存在：{UiSources.Root}");

        Assert.NotEmpty(UiSources.Enumerate());
    }

    [Fact]
    public void NoXamlOrCsharpContainsHexColorLiteral()
    {
        var offenders = new List<string>();

        foreach (string path in UiSources.Enumerate())
        {
            string text = File.ReadAllText(path);
            foreach (Match match in HexColor.Matches(text))
            {
                // 设计令牌访问器自身不写死颜色，界面上任何 #RRGGBB 都属违规。
                offenders.Add($"{Relative(path)} 第 {LineOf(text, match.Index)} 行出现字面量颜色 {match.Value}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "界面不得出现字面量颜色，所有视觉值须来自设计令牌：" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    private static string LineOf(string text, int index) =>
        (text[..index].Count(c => c == '\n') + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Relative(string path) =>
        Path.GetRelativePath(UiSources.Root, path);
}

/// <summary>
/// 规则二自证：界面 XAML 文案不得命中术语表中的禁用近义词。
/// 校验直接复用 Core 的 TerminologyValidator 与 terminology.json，避免测试与契约各写一套词表。
/// </summary>
public class TerminologyComplianceTests
{
    [Fact]
    public void NoXamlTextContainsForbiddenTerm()
    {
        TerminologyDocument terminology = XBeeSpec.TestSpec().LoadTerminology();

        var offenders = new List<string>();

        foreach (string path in UiSources.Enumerate().Where(p => p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
        {
            string text = File.ReadAllText(path);
            TerminologyCheckResult result = TerminologyValidator.CheckText(text, terminology);

            foreach (TerminologyViolation violation in result.Violations)
            {
                offenders.Add(
                    $"{Path.GetRelativePath(UiSources.Root, path)} 命中术语 {violation.TermId} " +
                    $"的禁用词「{violation.ForbiddenWord}」：{violation.Context}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "界面文案不得使用术语表禁用近义词：" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TerminologyFileIsActuallyLoadedAndChecked()
    {
        // 防止扫描因术语表为空而空转，必须确认词表真的读到了禁用词。
        TerminologyDocument terminology = XBeeSpec.TestSpec().LoadTerminology();

        Assert.NotEmpty(terminology.Terms);
        Assert.Contains(terminology.Terms, t => t.Forbidden.Count > 0);

        TerminologyCheckResult result =
            TerminologyValidator.CheckText("这是一个虚拟机实例的分身", terminology);

        Assert.False(result.IsValid);
    }
}

/// <summary>
/// 规则二自证：XAML 引用的每一个设计令牌键，都必须由 Theme/TokenResources.cs 真实写入资源字典。
///
/// 这类错误只在窗口构造、样式被应用时才暴露，单元测试默认不构造真实窗口，
/// 因此若无此静态交叉核对，一个拼错的键或缺失的形态会一路绿到程序启动即崩。
/// WPF 的资源名区分大小写，写错一个字母即解析失败。
/// </summary>
public class TokenKeyReferentialIntegrityTests
{
    /// <summary>匹配 XAML 中 DynamicResource 与 StaticResource 引用的令牌键。</summary>
    private static readonly Regex TokenReference = new(
        @"Token\.[A-Za-z0-9.]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>匹配 TokenResources 中写入资源字典的键，写入时会补上统一前缀。</summary>
    private static readonly Regex ProducedKey = new(
        @"Put\(resources,\s*""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>TokenResources 写入键时使用的统一前缀。</summary>
    private const string TokenPrefix = "Token.";

    private static IReadOnlyCollection<string> ReadReferencedKeys()
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (string path in UiSources.Enumerate()
                     .Where(p => p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (Match match in TokenReference.Matches(File.ReadAllText(path)))
            {
                referenced.Add(match.Value);
            }
        }

        return referenced;
    }

    private static IReadOnlyCollection<string> ReadProducedKeys()
    {
        string source = File.ReadAllText(
            UiSources.Enumerate().First(p =>
                p.EndsWith("TokenResources.cs", StringComparison.OrdinalIgnoreCase)));

        var produced = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in ProducedKey.Matches(source))
        {
            produced.Add(TokenPrefix + match.Groups[1].Value);
        }

        return produced;
    }

    [Fact]
    public void EveryTokenKeyReferencedByXamlIsActuallyWrittenToResourceDictionary()
    {
        IReadOnlyCollection<string> referenced = ReadReferencedKeys();
        IReadOnlyCollection<string> produced = ReadProducedKeys();

        // 防止两侧都为空而空转：界面确实消费了令牌，桥接层也确实产出了键。
        Assert.NotEmpty(referenced);
        Assert.NotEmpty(produced);

        var missing = referenced.Where(k => !produced.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            "XAML 引用了未由 TokenResources 写入的资源键，运行期会解析失败：" + Environment.NewLine +
            string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void CompositeTypedTokenFormsArePresentForSpacingAndCornerRadius()
    {
        // Margin 与 Padding 的类型是 Thickness，CornerRadius 属性要 CornerRadius，
        // 都不能直接消费 double 形态的令牌。缺少对应形态时窗口构造会抛异常。
        IReadOnlyCollection<string> produced = ReadProducedKeys();

        var required = new[]
        {
            TokenPrefix + "Thickness.Xs",
            TokenPrefix + "Thickness.Sm",
            TokenPrefix + "Thickness.Md",
            TokenPrefix + "Thickness.Lg",
        };

        var absent = required.Where(k => !produced.Contains(k)).ToList();

        Assert.True(
            absent.Count == 0,
            "间距令牌必须提供 Thickness 形态供 Margin 与 Padding 消费：" + Environment.NewLine +
            string.Join(Environment.NewLine, absent));
    }
}