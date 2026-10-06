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