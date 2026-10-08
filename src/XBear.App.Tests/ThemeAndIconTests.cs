using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using XBear.App.Theme;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 品牌资产、图标集、调色板映射与令牌桥接的验证。
/// 契约与资源字典之间必须双向一致，缺一或取值不符均视为破坏设计契约。
/// </summary>
public class ThemeAndIconTests
{
    private readonly SpecLoader _spec = XBeeSpec.TestSpec();

    /// <summary>
    /// 共享规格定义的 19 个图标必须在图标资源字典中逐一登记，
    /// 且均为合法可渲染的矢量绘制，尺寸档位均不低于 16 像素。
    /// </summary>
    [Fact]
    public void AllNineteenIconsAreRegisteredInResourceDictionary()
    {
        Assert.Equal(19, IconKeys.All.Count);
        Assert.Equal(19, IconKeys.All.Distinct(StringComparer.Ordinal).Count());

        var resources = LoadDictionary("Theme/IconResources.xaml");

        foreach (string key in IconKeys.All)
        {
            Assert.True(
                resources.Contains(key),
                $"图标资源字典缺少键 {key}。");

            object value = resources[key];
            Assert.IsAssignableFrom<DrawingImage>(value);
        }

        Assert.True((double)resources["Icon.Size.Sm"] >= 16d);
        Assert.True((double)resources["Icon.Size.Md"] >= 16d);
        Assert.True((double)resources["Icon.Size.Lg"] >= 16d);
        Assert.True((double)resources["Icon.Size.Xl"] >= 16d);
    }

    /// <summary>
    /// 品牌调色板映射必须正确读取 brand/palette.json 并映射到设计令牌，
    /// 登记的所有语义色必须与设计令牌完全一致。
    /// </summary>
    [Fact]
    public void BrandPaletteLoadsAndBindsToDesignTokens()
    {
        IReadOnlyList<BrandRole> roles = BrandPalette.Load(_spec);
        DesignTokens tokens = _spec.LoadDesignTokens();

        Assert.NotEmpty(roles);
        Assert.Contains(roles, r => r.Name == "bear-brown" && r.Token == "color.brand.primary");
        Assert.Contains(roles, r => r.Name == "forest-green" && r.Token == "color.brand.secondary");
        Assert.Contains(roles, r => r.Name == "honey-yellow" && r.Token == "color.brand.accent");
        Assert.Contains(roles, r => r.Name == "fur-light" && r.Token == "color.surface.raised");
        Assert.Contains(roles, r => r.Name == "ink" && r.Token == "color.text.primary");
        Assert.Contains(roles, r => r.Name == "ink-soft" && r.Token == "color.text.secondary");
        Assert.Contains(roles, r => r.Name == "on-dark" && r.Token == "color.text.inverse");
        Assert.Contains(roles, r => r.Name == "canvas" && r.Token == "color.surface.base");

        var resources = new ResourceDictionary();
        BrandPalette.Apply(roles, tokens, resources);

        foreach (BrandRole role in roles)
        {
            string key = BrandPalette.Key(role.Name);
            Assert.True(resources.Contains(key), $"缺少品牌画刷资源 {key}。");
            object brush = resources[key];
            Assert.IsType<SolidColorBrush>(brush);
            Assert.True(((SolidColorBrush)brush).IsFrozen);
        }
    }

    /// <summary>
    /// 品牌语义色与令牌取值不一致时必须拒绝载入，不可静默兜底。
    /// </summary>
    [Fact]
    public void BrandPaletteRejectsMismatchedValues()
    {
        DesignTokens tokens = _spec.LoadDesignTokens();
        var forged = new List<BrandRole>
        {
            new("bear-brown", "color.brand.primary", "rgb(0,0,0)", "伪造取值")
        };

        var resources = new ResourceDictionary();
        Assert.Throws<XBearException>(() => BrandPalette.Apply(forged, tokens, resources));
    }

    /// <summary>
    /// 投影令牌必须被正确解析为 WPF 阴影效果，卡片与弹窗两档的模糊半径与偏移量必须递增。
    /// </summary>
    [Fact]
    public void ElevationTokensParseIntoDropShadowEffects()
    {
        DesignTokens tokens = _spec.LoadDesignTokens();
        var resources = new ResourceDictionary();

        ElevationTokens.Apply(tokens, resources);

        Assert.True(resources.Contains(ElevationTokens.Key("Card")));
        Assert.True(resources.Contains(ElevationTokens.Key("Dialog")));

        var card = Assert.IsType<DropShadowEffect>(resources[ElevationTokens.Key("Card")]);
        var dialog = Assert.IsType<DropShadowEffect>(resources[ElevationTokens.Key("Dialog")]);

        Assert.True(card.BlurRadius > 0);
        Assert.True(dialog.BlurRadius > card.BlurRadius);
        Assert.True(dialog.ShadowDepth >= card.ShadowDepth);
    }

    /// <summary>
    /// 动效时长令牌必须被桥接为 WPF Duration 资源，毫秒数与令牌定义一致。
    /// </summary>
    [Fact]
    public void MotionTokensBridgeIntoDurations()
    {
        DesignTokens tokens = _spec.LoadDesignTokens();
        var resources = new ResourceDictionary();

        MotionTokens.Apply(tokens, resources);

        Assert.True(resources.Contains(MotionTokens.Key("fast")));
        Assert.True(resources.Contains(MotionTokens.Key("normal")));
        Assert.True(resources.Contains(MotionTokens.Key("slow")));

        var fast = (Duration)resources[MotionTokens.Key("fast")];
        var normal = (Duration)resources[MotionTokens.Key("normal")];
        var slow = (Duration)resources[MotionTokens.Key("slow")];

        Assert.Equal(TimeSpan.FromMilliseconds(tokens.MotionDuration("fast")), fast.TimeSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(tokens.MotionDuration("normal")), normal.TimeSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(tokens.MotionDuration("slow")), slow.TimeSpan);
    }

    /// <summary>
    /// 品牌图形资源字典必须完整提供纯图形正反色版、应用图标与尺寸。
    /// </summary>
    [Fact]
    public void BrandResourcesAreRegistered()
    {
        DesignTokens tokens = _spec.LoadDesignTokens();
        IReadOnlyList<BrandRole> roles = BrandPalette.Load(_spec);
        var baseResources = new ResourceDictionary();
        TokenResources.Apply(tokens, baseResources, roles);

        var brandResources = LoadDictionary("Theme/BrandResources.xaml");
        brandResources.MergedDictionaries.Add(baseResources);

        Assert.True(brandResources.Contains("Brand.Mark"));
        Assert.True(brandResources.Contains("Brand.MarkInverse"));
        Assert.True(brandResources.Contains("Brand.AppIcon"));
        Assert.True(brandResources.Contains("Brand.HorizontalMark"));

        var appIcon = Assert.IsAssignableFrom<DrawingImage>(brandResources["Brand.AppIcon"]);
        BitmapSource bitmap = WindowIcon.Render(appIcon, 32);

        Assert.NotNull(bitmap);
        Assert.Equal(32, bitmap.PixelWidth);
        Assert.Equal(32, bitmap.PixelHeight);
    }

    /// <summary>
    /// 实例运行态到状态图标的转换必须覆盖运行态、停止态与失败态。
    /// </summary>
    [Theory]
    [InlineData(InstanceState.Running, IconKeys.StateRunning)]
    [InlineData(InstanceState.Starting, IconKeys.StateRunning)]
    [InlineData(InstanceState.Stopping, IconKeys.StateRunning)]
    [InlineData(InstanceState.Stopped, IconKeys.StateStopped)]
    [InlineData(InstanceState.Faulted, IconKeys.StateFailed)]
    [InlineData(null, IconKeys.StateStopped)]
    public void StateIconConverterMapsAllStates(InstanceState? state, string expectedKey)
    {
        string actualKey = StateIconConverter.KeyOf(state);
        Assert.Equal(expectedKey, actualKey);
    }

    /// <summary>
    /// 可执行文件图标必须在 Assets/app.ico 存在且包含有效的 ICO 多尺寸数据。
    /// </summary>
    [Fact]
    public void ExecutableIconExistsAndContainsMultiSizeData()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "ui", "Assets", "app.ico");
        if (!File.Exists(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "XBear.App", "Assets", "app.ico");
        }

        Assert.True(File.Exists(path), $"可执行文件图标文件不存在：{path}");

        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 100, "ICO 文件体积异常");

        // ICO 头部：reserved(0), type(1 for ICO), count(>=3)
        ushort reserved = BitConverter.ToUInt16(bytes, 0);
        ushort type = BitConverter.ToUInt16(bytes, 2);
        ushort count = BitConverter.ToUInt16(bytes, 4);

        Assert.Equal(0, reserved);
        Assert.Equal(1, type);
        Assert.True(count >= 3, $"ICO 必须包含多尺寸位图，实际仅 {count} 项。");
    }

    private static ResourceDictionary LoadDictionary(string relativePath)
    {
        string fullPath = Path.Combine(AppContext.BaseDirectory, "ui", relativePath);
        if (!File.Exists(fullPath))
        {
            fullPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "XBear.App", relativePath);
        }

        using FileStream stream = File.OpenRead(Path.GetFullPath(fullPath));
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
    }
}