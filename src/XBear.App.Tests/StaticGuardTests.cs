using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
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
/// 规则一自证：界面源码与 XAML 中不得出现字面量十六进制颜色与 WPF 命名颜色。
/// 所有视觉值必须来自 DesignTokens，由 Theme/TokenResources.cs 写入资源字典后经 DynamicResource 消费。
/// </summary>
public class NoLiteralColorTests
{
    /// <summary>匹配 #RGB、#RRGGBB、#AARRGGBB 形式的字面量颜色。</summary>
    private static readonly Regex HexColor = new(
        @"#[0-9a-fA-F]{3,8}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>WPF 全部预定义命名颜色合集。</summary>
    private static readonly HashSet<string> WpfNamedColors = typeof(Colors)
        .GetProperties(BindingFlags.Public | BindingFlags.Static)
        .Select(p => p.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>XAML 中代表画刷或颜色设置的属性名称。</summary>
    private static readonly HashSet<string> ColorAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Background", "Foreground", "BorderBrush", "Fill", "Stroke", "Color", "Brush"
    };

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

    [Fact]
    public void NoXamlContainsNamedColorLiteral()
    {
        var offenders = new List<string>();

        foreach (string path in UiSources.Enumerate().Where(p => p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
        {
            string text = File.ReadAllText(path);
            List<string> violations = FindNamedColorViolations(text, Relative(path));
            offenders.AddRange(violations);
        }

        Assert.True(
            offenders.Count == 0,
            "XAML 画刷或颜色位置不得使用 WPF 命名颜色字面量，所有色彩须来自设计令牌：" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void NamedColorGuardSelfTestCatchesViolations()
    {
        const string badXaml = """
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Button Background="Red" />
                <TextBlock Foreground="Black" />
                <Border BorderBrush="White" />
                <Rectangle Fill="Blue" />
                <Path Stroke="DarkGray" />
                <SolidColorBrush Color="Transparent" />
                <Style TargetType="Button">
                    <Setter Property="Background" Value="White" />
                    <Setter Property="Foreground" Value="Red" />
                </Style>
            </StackPanel>
            """;

        List<string> violations = FindNamedColorViolations(badXaml, "BadSnippet.xaml");

        Assert.Equal(8, violations.Count);
        Assert.Contains(violations, v => v.Contains("Red"));
        Assert.Contains(violations, v => v.Contains("Black"));
        Assert.Contains(violations, v => v.Contains("White"));
        Assert.Contains(violations, v => v.Contains("Blue"));
        Assert.Contains(violations, v => v.Contains("DarkGray"));
        Assert.Contains(violations, v => v.Contains("Transparent"));
    }

    [Fact]
    public void NamedColorGuardSelfTestAllowsValidTokenBindings()
    {
        const string goodXaml = """
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Button Background="{DynamicResource Token.Color.Brand.Primary}" />
                <TextBlock Foreground="{DynamicResource Token.Color.Text.Primary}" Text="Red" />
                <Border BorderBrush="{DynamicResource Token.Color.Surface.Overlay}" />
                <Style TargetType="Button">
                    <Setter Property="Background" Value="{DynamicResource Token.Color.Surface.Raised}" />
                </Style>
            </StackPanel>
            """;

        List<string> violations = FindNamedColorViolations(goodXaml, "GoodSnippet.xaml");

        Assert.Empty(violations);
    }

    /// <summary>
    /// 扫描 XAML 文本中画刷与颜色位置是否存在 WPF 命名颜色字面量。
    /// </summary>
    /// <param name="xamlText">XAML 源码文本。</param>
    /// <param name="path">文件相对路径。</param>
    /// <returns>违规描述列表。</returns>
    public static List<string> FindNamedColorViolations(string xamlText, string path)
    {
        var violations = new List<string>();
        XDocument doc = LoadXamlDocument(xamlText);

        foreach (XElement element in doc.Descendants())
        {
            string tag = element.Name.LocalName;

            if (string.Equals(tag, "Setter", StringComparison.OrdinalIgnoreCase))
            {
                string? property = element.Attribute("Property")?.Value;
                string? value = element.Attribute("Value")?.Value;

                if (!string.IsNullOrWhiteSpace(property) &&
                    !string.IsNullOrWhiteSpace(value) &&
                    ColorAttributes.Contains(property) &&
                    !value.TrimStart().StartsWith('{') &&
                    WpfNamedColors.Contains(value.Trim()))
                {
                    int line = (element.Attribute("Value") as IXmlLineInfo)?.LineNumber
                        ?? (element as IXmlLineInfo)?.LineNumber ?? 1;
                    violations.Add($"{path} 第 {line} 行 Setter 属性 {property} 使用了命名颜色字面量「{value}」");
                }
            }
            else
            {
                foreach (XAttribute attr in element.Attributes())
                {
                    string attrName = attr.Name.LocalName;
                    string value = attr.Value;

                    if (ColorAttributes.Contains(attrName) &&
                        !value.TrimStart().StartsWith('{') &&
                        WpfNamedColors.Contains(value.Trim()))
                    {
                        int line = (attr as IXmlLineInfo)?.LineNumber
                            ?? (element as IXmlLineInfo)?.LineNumber ?? 1;
                        violations.Add($"{path} 第 {line} 行元素 <{tag}> 属性 {attrName} 使用了命名颜色字面量「{value}」");
                    }
                }
            }
        }

        return violations;
    }

    private static XDocument LoadXamlDocument(string xamlText)
    {
        try
        {
            return XDocument.Parse(xamlText, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            // 片段没有根命名空间或外层根节点时，用包装根节点兜底解析。
            string wrapped = $"<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">{xamlText}</Grid>";
            return XDocument.Parse(wrapped, LoadOptions.SetLineInfo);
        }
    }

    private static string LineOf(string text, int index) =>
        (text[..index].Count(c => c == '\n') + 1).ToString(CultureInfo.InvariantCulture);

    private static string Relative(string path) =>
        Path.GetRelativePath(UiSources.Root, path);
}

/// <summary>
/// 规则二自证：排版类元素与用户界面代码中不得出现字号、间距与排版尺寸的裸数字字面量。
/// 所有排版属性（FontSize、Margin、Padding、Width、Height 等）必须一律来自设计令牌。
/// </summary>
public class NoLiteralLayoutMetricsTests
{
    private static readonly HashSet<string> MetricsAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "FontSize", "Width", "Height", "Margin", "Padding"
    };

    private static readonly HashSet<string> GeometryElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "RectangleGeometry", "EllipseGeometry", "LineGeometry", "PathGeometry",
        "GeometryGroup", "DrawingGroup", "GeometryDrawing", "DrawingImage",
        "Pen", "DashStyle", "ScaleTransform", "TranslateTransform", "Thickness"
    };

    private static readonly Regex CsharpMetricsRegex = new(
        @"\b(?<prop>FontSize|Width|Height|Margin|Padding)\s*=\s*(?:new\s+Thickness\s*\(\s*)?(?<val>[1-9]\d*(?:\.\d+)?)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CsharpSetValueMetricsRegex = new(
        @"\bSetValue\s*\(\s*\w*\.(?<prop>FontSize|Width|Height|Margin|Padding)Property\s*,\s*(?:new\s+Thickness\s*\(\s*)?(?<val>[1-9]\d*(?:\.\d+)?)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void NoXamlContainsLiteralTypographyOrSpacingMetrics()
    {
        var offenders = new List<string>();

        foreach (string path in UiSources.Enumerate().Where(p => p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
        {
            string text = File.ReadAllText(path);
            List<string> violations = FindXamlMetricsViolations(text, Relative(path));
            offenders.AddRange(violations);
        }

        Assert.True(
            offenders.Count == 0,
            "XAML 中排版类元素不得使用字号、间距或尺寸的裸数字字面量，须一律引用设计令牌：" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void NoCsharpUiCodeContainsLiteralTypographyOrSpacingMetrics()
    {
        var offenders = new List<string>();

        foreach (string path in UiSources.Enumerate().Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            string text = File.ReadAllText(path);
            List<string> violations = FindCsharpMetricsViolations(text, Relative(path));
            offenders.AddRange(violations);
        }

        Assert.True(
            offenders.Count == 0,
            "C# 界面代码中不得硬编码排版字号、间距或控件尺寸裸数字：" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void MetricsGuardSelfTestCatchesXamlViolations()
    {
        const string badXaml = """
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <TextBlock FontSize="14" />
                <Button Width="120" />
                <Border Height="50" />
                <StackPanel Margin="10" />
                <TextBox Padding="8" />
                <StackPanel Margin="0,8,0,0" />
                <Style TargetType="Button">
                    <Setter Property="FontSize" Value="16" />
                    <Setter Property="Margin" Value="10,20" />
                </Style>
            </StackPanel>
            """;

        List<string> violations = FindXamlMetricsViolations(badXaml, "BadMetrics.xaml");

        Assert.Equal(8, violations.Count);
        Assert.Contains(violations, v => v.Contains("FontSize") && v.Contains("14"));
        Assert.Contains(violations, v => v.Contains("Width") && v.Contains("120"));
        Assert.Contains(violations, v => v.Contains("Height") && v.Contains("50"));
        Assert.Contains(violations, v => v.Contains("Margin") && v.Contains("10"));
        Assert.Contains(violations, v => v.Contains("Padding") && v.Contains("8"));
        Assert.Contains(violations, v => v.Contains("Margin") && v.Contains("0,8,0,0"));
        Assert.Contains(violations, v => v.Contains("Setter") && v.Contains("FontSize") && v.Contains("16"));
        Assert.Contains(violations, v => v.Contains("Setter") && v.Contains("Margin") && v.Contains("10,20"));
    }

    [Fact]
    public void MetricsGuardSelfTestCatchesCsharpViolations()
    {
        const string badCsharp = """
            public class TestView
            {
                public void Setup()
                {
                    var textBlock = new TextBlock();
                    textBlock.FontSize = 14;
                    var button = new Button();
                    button.Width = 100;
                    button.Height = 40;
                    var panel = new StackPanel();
                    panel.Margin = new Thickness(10);
                    var box = new TextBox();
                    box.Padding = new Thickness(8);
                }
            }
            """;

        List<string> violations = FindCsharpMetricsViolations(badCsharp, "Views/BadView.cs");

        Assert.Equal(5, violations.Count);
        Assert.Contains(violations, v => v.Contains("FontSize") && v.Contains("14"));
        Assert.Contains(violations, v => v.Contains("Width") && v.Contains("100"));
        Assert.Contains(violations, v => v.Contains("Height") && v.Contains("40"));
        Assert.Contains(violations, v => v.Contains("Margin") && v.Contains("10"));
        Assert.Contains(violations, v => v.Contains("Padding") && v.Contains("8"));
    }

    [Fact]
    public void MetricsGuardSelfTestAllowsValidTokensAndResets()
    {
        const string goodXaml = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    Title="测试窗口" Height="720" Width="1180">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="380" />
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto" />
                        <RowDefinition Height="*" />
                    </Grid.RowDefinitions>
                    <TextBlock FontSize="{DynamicResource Token.Font.Body}"
                               Margin="0" />
                    <Button Width="{DynamicResource Icon.Size.Md}"
                            Height="{DynamicResource Icon.Size.Md}"
                            Padding="{DynamicResource Token.Space.Sm}" />
                    <Border Margin="0,0,0,0"
                            Padding="{DynamicResource Token.Space.Md}" />
                </Grid>
            </Window>
            """;

        List<string> violations = FindXamlMetricsViolations(goodXaml, "Views/GoodWindow.xaml");

        Assert.Empty(violations);
    }

    /// <summary>
    /// 扫描 XAML 文本中排版类元素是否违规使用裸数字尺寸或字号。
    /// </summary>
    /// <param name="xamlText">XAML 源码文本。</param>
    /// <param name="path">文件相对路径。</param>
    /// <returns>违规描述列表。</returns>
    public static List<string> FindXamlMetricsViolations(string xamlText, string path)
    {
        // 白名单说明：
        // Theme/ 目录下的 IconResources.xaml 与 BrandResources.xaml 为矢量资源字典，
        // 其内部的数字均为 SVG 坐标、几何形状尺寸、笔刷粗细或图标/品牌尺寸定义，
        // 不属于排版/界面组件的字号或间距硬编码。
        string normalizedPath = path.Replace('\\', '/');
        if (normalizedPath.StartsWith("Theme/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/Theme/", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string>();
        }

        var violations = new List<string>();
        XDocument doc = LoadXamlDocument(xamlText);

        foreach (XElement element in doc.Descendants())
        {
            string tag = element.Name.LocalName;

            // 白名单说明：
            // 1. Window 根节点的 Width 与 Height 为桌面宿主窗口初始外框宽高，非界面排版组件属性；
            // 2. ColumnDefinition 与 RowDefinition 的 Width/Height 为网格行列轨道定义规则；
            // 3. 几何绘图元素为矢量图形自身坐标。
            if (string.Equals(tag, "Window", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "ColumnDefinition", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "RowDefinition", StringComparison.OrdinalIgnoreCase) ||
                GeometryElements.Contains(tag))
            {
                continue;
            }

            if (string.Equals(tag, "Setter", StringComparison.OrdinalIgnoreCase))
            {
                string? property = element.Attribute("Property")?.Value;
                string? value = element.Attribute("Value")?.Value;

                if (!string.IsNullOrWhiteSpace(property) &&
                    !string.IsNullOrWhiteSpace(value) &&
                    MetricsAttributes.Contains(property) &&
                    IsMetricViolation(property, value))
                {
                    int line = (element.Attribute("Value") as IXmlLineInfo)?.LineNumber
                        ?? (element as IXmlLineInfo)?.LineNumber ?? 1;
                    violations.Add($"{path} 第 {line} 行 Setter 属性 {property} 使用了裸数字字面量「{value}」");
                }
            }
            else
            {
                foreach (XAttribute attr in element.Attributes())
                {
                    string attrName = attr.Name.LocalName;
                    string value = attr.Value;

                    if (MetricsAttributes.Contains(attrName) && IsMetricViolation(attrName, value))
                    {
                        int line = (attr as IXmlLineInfo)?.LineNumber
                            ?? (element as IXmlLineInfo)?.LineNumber ?? 1;
                        violations.Add($"{path} 第 {line} 行元素 <{tag}> 属性 {attrName} 使用了裸数字字面量「{value}」");
                    }
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// 扫描 C# 代码中是否包含直接给排版类属性赋数值字面量的语句。
    /// </summary>
    /// <param name="csharpText">C# 源码文本。</param>
    /// <param name="path">文件相对路径。</param>
    /// <returns>违规描述列表。</returns>
    public static List<string> FindCsharpMetricsViolations(string csharpText, string path)
    {
        // 白名单说明：
        // 1. Theme/WindowIcon.cs: 品牌图标转窗口位图的设备无关像素渲染与包围盒缩放计算；
        // 2. Theme/TokenResources.cs: 负责从 design-tokens.json 将令牌数值写入 WPF 资源；
        // 3. Theme/ElevationTokens.cs: 阴影令牌字符串的像素偏移与模糊半径解析；
        // 4. Theme/MotionTokens.cs: 毫秒动效时长换算为 WPF Duration；
        // 5. Theme/IconSelectors.cs: 图标常量键定义；
        // 6. Services/DiagnosticsExporter.cs: 系统诊断材料打包导出；
        // 7. Spec/AppSchemaEvaluator.cs, AppComposition.cs, AssemblyInfo.cs: 基础设施与契约求值。
        string normalized = path.Replace('\\', '/');
        if (normalized.Contains("Theme/WindowIcon", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Theme/TokenResources", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Theme/ElevationTokens", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Theme/MotionTokens", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Theme/IconSelectors", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Services/DiagnosticsExporter", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Spec/AppSchemaEvaluator", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("AppComposition.cs", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string>();
        }

        var violations = new List<string>();
        string clean = StripCsharpComments(csharpText);

        foreach (Match match in CsharpMetricsRegex.Matches(clean))
        {
            int line = LineOf(csharpText, match.Index);
            violations.Add($"{path} 第 {line} 行 C# 代码为属性 {match.Groups["prop"].Value} 赋予裸数字字面量 {match.Groups["val"].Value}");
        }

        foreach (Match match in CsharpSetValueMetricsRegex.Matches(clean))
        {
            int line = LineOf(csharpText, match.Index);
            violations.Add($"{path} 第 {line} 行 C# 代码通过 SetValue 为属性 {match.Groups["prop"].Value} 赋予裸数字字面量 {match.Groups["val"].Value}");
        }

        return violations;
    }

    private static bool IsMetricViolation(string property, string value)
    {
        string trimmed = value.Trim();

        // 动态资源、静态资源或数据绑定一律放行。
        if (trimmed.StartsWith('{'))
        {
            return false;
        }

        // Grid Auto 与比例星号尺寸放行。
        if (string.Equals(trimmed, "Auto", StringComparison.OrdinalIgnoreCase) || trimmed.Contains('*'))
        {
            return false;
        }

        if (string.Equals(property, "Margin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(property, "Padding", StringComparison.OrdinalIgnoreCase))
        {
            // 白名单说明：
            // 零间距（如 Margin="0" 或 "0,0,0,0"）用于显式清除控件默认外边距（如重置 TextBlock 默认边距），
            // 共享设计令牌中未定义 0 间距档位（令牌仅有 xs 起的档位），WPF 亦无 null Thickness，
            // 因此全零间距属于合法重置，其余非零数字均须来自设计令牌。
            return !IsAllZeroThickness(trimmed);
        }

        // FontSize、Width、Height 为裸数字时违规。
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private static bool IsAllZeroThickness(string value)
    {
        string[] parts = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                if (Math.Abs(d) > 0.0001)
                {
                    return false;
                }
            }
            else
            {
                return true;
            }
        }

        return true;
    }

    private static string StripCsharpComments(string code)
    {
        var sb = new StringBuilder();
        int i = 0;
        int n = code.Length;
        while (i < n)
        {
            if (code[i] == '/' && i + 1 < n && code[i + 1] == '/')
            {
                i += 2;
                while (i < n && code[i] != '\n')
                {
                    i++;
                }
                continue;
            }
            if (code[i] == '/' && i + 1 < n && code[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(code[i] == '*' && code[i + 1] == '/'))
                {
                    if (code[i] == '\n') sb.Append('\n');
                    i++;
                }
                i += 2;
                continue;
            }
            sb.Append(code[i]);
            i++;
        }
        return sb.ToString();
    }

    private static XDocument LoadXamlDocument(string xamlText)
    {
        try
        {
            return XDocument.Parse(xamlText, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            string wrapped = $"<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">{xamlText}</Grid>";
            return XDocument.Parse(wrapped, LoadOptions.SetLineInfo);
        }
    }

    private static int LineOf(string text, int index) =>
        text[..index].Count(c => c == '\n') + 1;

    private static string Relative(string path) =>
        Path.GetRelativePath(UiSources.Root, path);
}

/// <summary>
/// 规则三自证：界面 XAML 文案与 C# 用户可见字符串不得命中术语表中的禁用近义词。
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
            offenders.AddRange(FindXamlTerminologyViolations(text, Relative(path), terminology));
        }

        Assert.True(
            offenders.Count == 0,
            "界面 XAML 文案不得使用术语表禁用近义词：" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void NoCsharpUserFacingTextContainsForbiddenTerm()
    {
        TerminologyDocument terminology = XBeeSpec.TestSpec().LoadTerminology();

        var offenders = new List<string>();

        foreach (string path in UiSources.Enumerate().Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            string text = File.ReadAllText(path);
            List<string> violations = FindCsharpTerminologyViolations(text, Relative(path), terminology);
            offenders.AddRange(violations);
        }

        Assert.True(
            offenders.Count == 0,
            "界面层 C# 用户可见字符串不得使用术语表禁用近义词：" + Environment.NewLine +
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

    [Fact]
    public void CsharpTerminologyGuardSelfTestCatchesViolations()
    {
        TerminologyDocument terminology = XBeeSpec.TestSpec().LoadTerminology();

        const string badCsharp = """
            namespace XBear.App.ViewModels;

            public class BadViewModel
            {
                public string Title => "这是一个虚拟机实例的分身";
                public string ImageError => "系统镜像加载失败";
                public string ButtonText => "点击开机";
                public string UserError(string field) => $"当前属于{field}，出现用户错误";
                public string TransferText => @"多开与文件共享功能";
            }
            """;

        List<string> violations = FindCsharpTerminologyViolations(badCsharp, "ViewModels/BadViewModel.cs", terminology);

        Assert.True(violations.Count >= 5);
        Assert.Contains(violations, v => v.Contains("虚拟机实例"));
        Assert.Contains(violations, v => v.Contains("分身"));
        Assert.Contains(violations, v => v.Contains("系统镜像"));
        Assert.Contains(violations, v => v.Contains("开机"));
        Assert.Contains(violations, v => v.Contains("用户错误"));
        Assert.Contains(violations, v => v.Contains("文件共享"));
    }

    [Fact]
    public void CsharpTerminologyGuardSelfTestAllowsCompliantCodeAndComments()
    {
        TerminologyDocument terminology = XBeeSpec.TestSpec().LoadTerminology();

        const string goodCsharp = """
            namespace XBear.App.ViewModels;

            /// <summary>
            /// 这是一个合规的视图模型注释，说明为何不使用虚拟机实例或分身。
            /// </summary>
            public class GoodViewModel
            {
                // 单行注释：避免系统镜像与开机词汇
                public const string ResourceKey = "Icon.Instance";
                public string Title => "这是一个实例";
                public string State => "已启动";
            }
            """;

        List<string> violations = FindCsharpTerminologyViolations(goodCsharp, "ViewModels/GoodViewModel.cs", terminology);

        Assert.Empty(violations);
    }

    [Fact]
    public void CsharpTerminologyGuardSelfTestAllowsTermIdentifiers()
    {
        TerminologyDocument terminology = XBeeSpec.TestSpec().LoadTerminology();

        // 术语标识是术语表自身的键，其取值域与禁用词表同源，不参与禁用词比对。
        const string catalogCode = """
            namespace XBear.App.Presentation;

            public sealed class TerminologyCatalog
            {
                public const string ApplicationTermId = "application";
                public const string AppLaunchTermId = "app-launch";
            }
            """;

        List<string> violations = FindCsharpTerminologyViolations(catalogCode, "Presentation/TerminologyCatalog.cs", terminology);

        Assert.Empty(violations);
    }

    [Fact]
    public void XamlTerminologyGuardSelfTestCatchesUserVisibleViolations()
    {
        TerminologyDocument terminology = XBeeSpec.TestSpec().LoadTerminology();

        const string badXaml = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="XBear.App.Views.BadWindow"
                    Title="虚拟机实例">
              <TextBlock TextWrapping="Wrap" Text="这是实例的分身" />
              <WrapPanel>
                <TextBlock Text="系统镜像加载失败" />
              </WrapPanel>
              <Button Content="点击开机" />
            </Window>
            """;

        List<string> violations = FindXamlTerminologyViolations(badXaml, "Views/BadWindow.xaml", terminology);

        Assert.Contains(violations, v => v.Contains("虚拟机实例"));
        Assert.Contains(violations, v => v.Contains("分身"));
        Assert.Contains(violations, v => v.Contains("系统镜像"));
        Assert.Contains(violations, v => v.Contains("开机"));
    }

    [Fact]
    public void XamlTerminologyGuardSelfTestAllowsMarkupIdentifiersAndComments()
    {
        TerminologyDocument terminology = XBeeSpec.TestSpec().LoadTerminology();

        // 元素名、属性名、命名空间与资源键都是标记标识，不属于用户界面文案；
        // 注释与 C# 侧保持一致地放行。
        const string goodXaml = """
            <!-- 注释里可以出现开机与虚拟机实例等说明性措辞 -->
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="XBear.App.Views.GoodWindow"
                    Title="关于小熊模拟器">
              <TextBlock TextWrapping="Wrap" Text="这是一个实例" />
              <WrapPanel>
                <Image Source="{StaticResource Brand.AppIcon}" />
                <TextBlock Text="{DynamicResource Token.Color.Text.Primary}" />
                <TextBlock Text="{Binding StatusText}" />
              </WrapPanel>
              <Style TargetType="Button">
                <Setter Property="Foreground" Value="White" />
              </Style>
            </Window>
            """;

        List<string> violations = FindXamlTerminologyViolations(goodXaml, "Views/GoodWindow.xaml", terminology);

        Assert.Empty(violations);
    }

    /// <summary>
    /// 防止扫描因提取规则收紧而空转：界面真实文案必须仍被取到，
    /// 否则上面的守卫会在什么都没扫到的情况下一直保持绿色。
    /// </summary>
    [Fact]
    public void XamlTerminologyScanStillReadsRealUserFacingText()
    {
        string path = UiSources.Enumerate()
            .First(p => p.EndsWith("MainWindow.xaml", StringComparison.OrdinalIgnoreCase));

        List<string> extracted = ExtractXamlPresentationText(File.ReadAllText(path))
            .Select(item => item.Text)
            .ToList();

        Assert.NotEmpty(extracted);
        Assert.Contains(extracted, text => text.Contains("小熊模拟器", StringComparison.Ordinal));
        Assert.Contains(extracted, text => text.Contains("投屏", StringComparison.Ordinal));
        Assert.Contains(extracted, text => text.Contains("暴露级别", StringComparison.Ordinal));
        Assert.DoesNotContain(extracted, text => text.Contains("WrapPanel", StringComparison.Ordinal));
        Assert.DoesNotContain(extracted, text => text.Contains("XBear.App", StringComparison.Ordinal));
    }

    /// <summary>
    /// 扫描 XAML 中的用户可见文案是否命中术语表中的禁用近义词。
    /// 只取元素文本与承载文案的属性值；标记标识与资源键不是文案，按白名单放行。
    /// </summary>
    /// <param name="xamlText">XAML 源码文本。</param>
    /// <param name="path">文件相对路径。</param>
    /// <param name="terminology">术语表文档。</param>
    /// <returns>违规描述列表。</returns>
    public static List<string> FindXamlTerminologyViolations(
        string xamlText,
        string path,
        TerminologyDocument terminology)
    {
        var violations = new List<string>();

        foreach ((string text, int line) in ExtractXamlPresentationText(xamlText))
        {
            // 白名单说明：
            // 标记扩展（绑定、静态资源、动态资源、类型引用）是表达式而非文案，
            // 资源键本身也不是文案，两者都不参与禁用词比对。
            string literal = StripMarkupExtensions(text);
            if (literal.Length == 0 || IsResourceKey(literal))
            {
                continue;
            }

            TerminologyCheckResult result = TerminologyValidator.CheckText(literal, terminology);
            foreach (TerminologyViolation violation in result.Violations)
            {
                violations.Add(
                    $"{path} 第 {line} 行 XAML 文案命中术语 {violation.TermId} " +
                    $"的禁用词「{violation.ForbiddenWord}」：{violation.Context}");
            }
        }

        return violations;
    }

    /// <summary>XAML 中承载的是标记标识而非用户文案的属性名。</summary>
    private static readonly HashSet<string> StructuralAttributeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Class", "Name", "Key", "Source", "Target", "Command", "CommandParameter",
        "Property", "TargetProperty", "Setter", "Ignorable", "Requires", "ProcessContent"
    };

    /// <summary>
    /// 逐段提取 XAML 中的用户可见文案：元素文本内容与非结构属性的取值。
    /// 注释、元素名与属性名一律跳过，与 C# 侧跳过注释的口径保持一致。
    /// </summary>
    /// <param name="xamlText">XAML 源码文本。</param>
    /// <returns>文案文本及其起始行号。</returns>
    public static IEnumerable<(string Text, int Line)> ExtractXamlPresentationText(string xamlText)
    {
        int i = 0;
        int n = xamlText.Length;
        int line = 1;

        while (i < n)
        {
            char c = xamlText[i];

            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            // 注释不是文案，注释里可以自由解释被禁用的措辞为何被禁用。
            if (c == '<' && i + 3 < n && xamlText.AsSpan(i, 4).SequenceEqual("<!--"))
            {
                i += 4;
                while (i + 2 < n && !xamlText.AsSpan(i, 3).SequenceEqual("-->"))
                {
                    if (xamlText[i] == '\n')
                    {
                        line++;
                    }

                    i++;
                }

                i += 3;
                continue;
            }

            if (c == '<')
            {
                int startLine = line;
                while (i < n && xamlText[i] != '>')
                {
                    if (xamlText[i] == '"' || xamlText[i] == '\'')
                    {
                        char quote = xamlText[i];
                        int valueStart = ++i;
                        while (i < n && xamlText[i] != quote)
                        {
                            if (xamlText[i] == '\n')
                            {
                                line++;
                            }

                            i++;
                        }

                        string value = xamlText[valueStart..Math.Min(i, n)];
                        if (!IsStructuralAttribute(xamlText, valueStart) && value.Length > 0)
                        {
                            yield return (value, startLine);
                        }
                    }

                    if (xamlText[i] == '\n')
                    {
                        line++;
                    }

                    i++;
                }

                i++;
                continue;
            }

            // 标签之间的文本内容直接展示给用户。
            int textStart = i;
            while (i < n && xamlText[i] != '<')
            {
                if (xamlText[i] == '\n')
                {
                    line++;
                }

                i++;
            }

            string content = xamlText[textStart..i];
            if (content.Trim().Length > 0)
            {
                yield return (content.Trim(), line);
            }
        }
    }

    /// <summary>
    /// 判断一个属性值是否来自结构属性，即仅承载标记标识而不承载文案。
    /// </summary>
    /// <param name="xamlText">XAML 源码文本。</param>
    /// <param name="valueStart">属性值起始下标。</param>
    /// <returns>该属性为结构属性时为 true。</returns>
    private static bool IsStructuralAttribute(string xamlText, int valueStart)
    {
        // 属性写法为 Name="value"，取值左侧可能隔着空白、引号与等号，先一并跨过再回读属性名。
        int end = valueStart;
        while (end > 0 &&
               (char.IsWhiteSpace(xamlText[end - 1]) ||
                xamlText[end - 1] == '"' ||
                xamlText[end - 1] == '\'' ||
                xamlText[end - 1] == '='))
        {
            end--;
        }

        int nameEnd = end;
        while (nameEnd > 0 && (char.IsLetterOrDigit(xamlText[nameEnd - 1]) ||
                               xamlText[nameEnd - 1] == '.' ||
                               xamlText[nameEnd - 1] == '_' ||
                               xamlText[nameEnd - 1] == ':'))
        {
            nameEnd--;
        }

        if (nameEnd == end)
        {
            return false;
        }

        string name = xamlText[nameEnd..end];

        if (name.StartsWith("xmlns", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("d:", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("mc:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 属性可能带前缀（如 x:Class），去掉前缀后比对裸名。
        int prefixEnd = name.LastIndexOf(':');
        string bareName = prefixEnd >= 0 ? name[(prefixEnd + 1)..] : name;

        return StructuralAttributeNames.Contains(bareName);
    }

    /// <summary>
    /// 去掉标记扩展表达式，只留下真正展示给用户的文字部分。
    /// </summary>
    /// <param name="value">XAML 属性值或文本内容。</param>
    /// <returns>去掉标记扩展后的文本。</returns>
    private static string StripMarkupExtensions(string value)
    {
        var sb = new StringBuilder();
        int depth = 0;

        foreach (char c in value)
        {
            if (c == '{')
            {
                depth++;
                continue;
            }

            if (c == '}')
            {
                if (depth > 0)
                {
                    depth--;
                }

                continue;
            }

            if (depth == 0)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// 判断一段文本是否为资源键。资源键与令牌键是程序符号，不是界面文案。
    /// </summary>
    /// <param name="text">待判断文本。</param>
    /// <returns>为资源键时为 true。</returns>
    private static bool IsResourceKey(string text) =>
        text.StartsWith("Icon.", StringComparison.Ordinal) ||
        text.StartsWith("Token.", StringComparison.Ordinal) ||
        text.StartsWith("Palette.", StringComparison.Ordinal) ||
        text.StartsWith("Brand.", StringComparison.Ordinal);

    /// <summary>
    /// 扫描 C# 代码中的用户可见字符串字面量是否命中术语表中的禁用近义词。
    /// </summary>
    /// <param name="csharpText">C# 源码文本。</param>
    /// <param name="path">文件相对路径。</param>
    /// <param name="terminology">术语表文档。</param>
    /// <returns>违规描述列表。</returns>
    public static List<string> FindCsharpTerminologyViolations(
        string csharpText,
        string path,
        TerminologyDocument terminology)
    {
        string normalized = path.Replace('\\', '/');

        // 白名单说明：
        // 仅扫描用户可见的界面层：ViewModels/、Presentation/、Views/ 以及主窗口代码后置；
        // 排除非界面层代码（Theme 样式资源、Services 导出实现、Spec 契约求值、AppComposition 组装根等基础设施）。
        bool isUiLayer = normalized.Contains("ViewModels/", StringComparison.OrdinalIgnoreCase) ||
                         normalized.Contains("Presentation/", StringComparison.OrdinalIgnoreCase) ||
                         normalized.Contains("Views/", StringComparison.OrdinalIgnoreCase) ||
                         normalized.EndsWith("MainWindow.xaml.cs", StringComparison.OrdinalIgnoreCase);

        if (!isUiLayer)
        {
            return new List<string>();
        }

        var violations = new List<string>();
        var termIds = terminology.Terms.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

        foreach ((string literal, int line) in ExtractStringLiterals(csharpText))
        {
            // 白名单说明：
            // 1. 资源键（如 Icon.*, Token.*, Palette.*, Brand.*）、协议标识符（如 tcp, udp）等内部程序符号
            //    不属于用户界面展示文案；
            // 2. 术语标识是术语表自身的键，与禁用词表同源，拿禁用词去比对术语标识必然自相矛盾，
            //    因此按值整体放行，界面仍然只能通过术语目录取词，不得自行拼写概念名。
            if (literal.StartsWith("Icon.", StringComparison.Ordinal) ||
                literal.StartsWith("Token.", StringComparison.Ordinal) ||
                literal.StartsWith("Palette.", StringComparison.Ordinal) ||
                literal.StartsWith("Brand.", StringComparison.Ordinal) ||
                termIds.Contains(literal) ||
                string.Equals(literal, "tcp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(literal, "udp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TerminologyCheckResult result = TerminologyValidator.CheckText(literal, terminology);
            foreach (TerminologyViolation violation in result.Violations)
            {
                violations.Add(
                    $"{path} 第 {line} 行字符串命中术语 {violation.TermId} " +
                    $"的禁用词「{violation.ForbiddenWord}」：{violation.Context}");
            }
        }

        return violations;
    }

    /// <summary>
    /// 逐词法提取 C# 源码中的全部字符串字面量（自动跳过单行与多行注释）。
    /// </summary>
    /// <param name="csharpText">C# 源码文本。</param>
    /// <returns>提取出的字符串字面量文本及起始行号。</returns>
    public static IEnumerable<(string Literal, int Line)> ExtractStringLiterals(string csharpText)
    {
        int i = 0;
        int n = csharpText.Length;
        int line = 1;

        while (i < n)
        {
            char c = csharpText[i];
            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            // 单行注释
            if (c == '/' && i + 1 < n && csharpText[i + 1] == '/')
            {
                i += 2;
                while (i < n && csharpText[i] != '\n')
                {
                    i++;
                }
                continue;
            }

            // 多行注释
            if (c == '/' && i + 1 < n && csharpText[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(csharpText[i] == '*' && csharpText[i + 1] == '/'))
                {
                    if (csharpText[i] == '\n')
                    {
                        line++;
                    }
                    i++;
                }
                i += 2;
                continue;
            }

            // 插值逐字字符串：$@"..." 或 @$"..."
            if ((c == '$' && i + 2 < n && csharpText[i + 1] == '@' && csharpText[i + 2] == '"') ||
                (c == '@' && i + 2 < n && csharpText[i + 1] == '$' && csharpText[i + 2] == '"'))
            {
                int startLine = line;
                i += 3;
                var sb = new StringBuilder();
                while (i < n)
                {
                    if (csharpText[i] == '\n')
                    {
                        line++;
                    }
                    if (csharpText[i] == '"')
                    {
                        if (i + 1 < n && csharpText[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    sb.Append(csharpText[i]);
                    i++;
                }
                yield return (sb.ToString(), startLine);
                continue;
            }

            // 逐字字符串：@"..."
            if (c == '@' && i + 1 < n && csharpText[i + 1] == '"')
            {
                int startLine = line;
                i += 2;
                var sb = new StringBuilder();
                while (i < n)
                {
                    if (csharpText[i] == '\n')
                    {
                        line++;
                    }
                    if (csharpText[i] == '"')
                    {
                        if (i + 1 < n && csharpText[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    sb.Append(csharpText[i]);
                    i++;
                }
                yield return (sb.ToString(), startLine);
                continue;
            }

            // 普通字符串或插值字符串：$"..." 或 "..."
            if ((c == '$' && i + 1 < n && csharpText[i + 1] == '"') || c == '"')
            {
                int startLine = line;
                i += (c == '$' ? 2 : 1);
                var sb = new StringBuilder();
                while (i < n)
                {
                    if (csharpText[i] == '\\' && i + 1 < n)
                    {
                        sb.Append(csharpText[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (csharpText[i] == '"')
                    {
                        i++;
                        break;
                    }
                    sb.Append(csharpText[i]);
                    i++;
                }
                yield return (sb.ToString(), startLine);
                continue;
            }

            i++;
        }
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(UiSources.Root, path);
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

/// <summary>
/// 规则三自证：WPF 界面层不得在 invariant 全球化模式下构建。
///
/// invariant 模式下进程内所有区域设置均为空，WPF 数据绑定在解析语言时
/// 会抛出「找不到对应的非中性文化」而中断，程序在启动阶段直接失败。
/// 该缺陷对所有单元测试不可见，因此必须在构建配置层面守住。
/// </summary>
public class GlobalizedUiTests
{
    /// <summary>
    /// 取界面层程序集自身的运行时配置文件路径。
    /// 测试进程本身的入口是测试宿主，检查它没有意义，必须检查被测程序集。
    /// </summary>
    private static string RuntimeConfigPath
    {
        get
        {
            string assemblyPath = typeof(XBear.App.App).Assembly.Location;
            string configPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");

            if (!File.Exists(configPath))
            {
                throw new InvalidOperationException($"找不到运行时配置文件，测试结论不成立：{configPath}");
            }

            return configPath;
        }
    }

    [Fact]
    public void UiAssemblyIsNotBuiltWithInvariantGlobalization()
    {
        string path = RuntimeConfigPath;
        string config = File.ReadAllText(path);

        // 只在显式声明为 true 时才算启用；键缺失即默认关闭。
        Assert.False(
            config.Contains("\"System.Globalization.Invariant\": true", StringComparison.Ordinal),
            "WPF 界面层不得以 invariant 全球化模式构建，否则数据绑定会因无法解析区域设置而中断：" +
            Environment.NewLine + path);
    }

    [Fact]
    public void RuntimeExposesAtLeastOneConcreteCulture()
    {
        // invariant 模式下不会有任何具体文化，绑定引擎必然失败。
        var concrete = CultureInfo.GetCultures(CultureTypes.AllCultures)
            .Where(c => !c.IsNeutralCulture)
            .ToList();

        Assert.NotEmpty(concrete);
        Assert.False(
            CultureInfo.CurrentUICulture.IsNeutralCulture,
            "当前 UI 文化不应是中性的，否则 WPF 绑定无法解析。");
    }
}
