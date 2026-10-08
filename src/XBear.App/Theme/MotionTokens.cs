using System.Windows;
using System.Windows.Media.Animation;
using XBear.Core.Spec;

namespace XBear.App.Theme;

/// <summary>
/// 动效时长令牌到 WPF <see cref="Duration"/> 的桥接。
/// 令牌以毫秒为单位，XAML 侧无法直接把数值赋给 <see cref="Duration"/>，
/// 因此在应用资源中同时登记一份可直接绑定到动画的 <see cref="Duration"/>。
/// </summary>
public static class MotionTokens
{
    /// <summary>可直接绑定的时长资源键前缀。</summary>
    private const string Prefix = "Motion.Duration.";

    /// <summary>参与桥接的时长档位。</summary>
    private static readonly string[] Tiers = { "fast", "normal", "slow" };

    /// <summary>
    /// 把动效时长令牌写入资源字典，形成 <c>Motion.Duration.&lt;档位&gt;</c>。
    /// </summary>
    /// <param name="tokens">设计令牌访问器。</param>
    /// <param name="resources">目标资源字典。</param>
    public static void Apply(DesignTokens tokens, ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(resources);

        foreach (string tier in Tiers)
        {
            resources[Key(tier)] = DurationOf(tokens.MotionDuration(tier));
        }
    }

    /// <summary>
    /// 按档位构造资源键，供 XAML 与测试引用。
    /// </summary>
    /// <param name="tier">时长档位，取值范围为 fast、normal、slow。</param>
    /// <returns>完整资源键。</returns>
    public static string Key(string tier) => Prefix + tier;

    /// <summary>
    /// 把毫秒数转换为 WPF 时长。
    /// </summary>
    /// <param name="milliseconds">时长毫秒数。</param>
    /// <returns>对应时长。</returns>
    public static Duration DurationOf(int milliseconds) =>
        new(TimeSpan.FromMilliseconds(milliseconds));
}