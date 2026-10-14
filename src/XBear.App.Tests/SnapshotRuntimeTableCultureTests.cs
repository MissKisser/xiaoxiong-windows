using System.Globalization;
using XBear.Core.Snapshots;

namespace XBear.App.Tests;

/// <summary>
/// <c>info snapshots</c> 时刻解析的跨区域一致性测试。
///
/// QEMU 的 DATE 列只输出墙上钟点、不带时区标识，若解析依赖宿主区域或时区，
/// 同一段 QEMU 输出就会在开发机与构建机上得到不同结果。
/// 本工程的运行时与正式程序一致（关闭 invariant 全球化），因此可真实切换具体文化来守住这条边界。
/// </summary>
public class SnapshotRuntimeTableCultureTests
{
    private const string TwoRowTable =
        """
        ID       TAG                        VM SIZE                DATE        VM CLOCK
        0        snap-win-main-01-0001      0 B                    2026-10-07  11:20:00   00:00:01.000
        1        snap-win-main-01-0002      1.5 GiB                2026-10-07  14:05:30   00:12:34.567
        """;

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public void ParsedMomentIsIdenticalUnderEveryCulture(string cultureName)
    {
        using CultureScope scope = CultureScope.Enter(cultureName);

        IReadOnlyList<SnapshotRuntimeEntry> entries = SnapshotRuntimeTable.Parse(TwoRowTable);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new DateTime(2026, 10, 7, 11, 20, 0), entries[0].CreatedAt.DateTime);
        Assert.Equal(new DateTime(2026, 10, 7, 14, 5, 30), entries[1].CreatedAt.DateTime);
        Assert.All(entries, entry => Assert.Equal(SnapshotRuntimeTable.DefaultReportedTimeZoneOffset, entry.CreatedAt.Offset));
    }

    [Fact]
    public void ParsedMomentDoesNotFollowTheHostTimeZone()
    {
        IReadOnlyList<SnapshotRuntimeEntry> entries = SnapshotRuntimeTable.Parse(TwoRowTable);

        // 宿主本地时区不参与解析：偏移量恒为文档约定的固定值，与 TimeZoneInfo.Local 无关。
        Assert.All(entries, entry => Assert.Equal(TimeSpan.Zero, entry.CreatedAt.Offset));
        Assert.Equal(
            new DateTimeOffset(2026, 10, 7, 11, 20, 0, SnapshotRuntimeTable.DefaultReportedTimeZoneOffset),
            entries[0].CreatedAt);
    }

    [Fact]
    public void ParsedMomentIsIdenticalAcrossRepeatedRunsUnderTheHostCulture()
    {
        // 反复解析必须稳定得到同一读数，避免任何按当前时刻漂移的取值。
        DateTimeOffset first = SnapshotRuntimeTable.Parse(TwoRowTable)[0].CreatedAt;
        DateTimeOffset second = SnapshotRuntimeTable.Parse(TwoRowTable)[0].CreatedAt;

        Assert.Equal(first, second);
    }

    /// <summary>临时切换当前文化，退出时还原，保证用例之间互不污染。</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _originalCulture;
        private readonly CultureInfo _originalUiCulture;

        private CultureScope(CultureInfo originalCulture, CultureInfo originalUiCulture)
        {
            _originalCulture = originalCulture;
            _originalUiCulture = originalUiCulture;
        }

        /// <summary>切换到指定文化并返回还原句柄。</summary>
        /// <param name="cultureName">目标文化名称。</param>
        /// <returns>退出作用域时还原文化的句柄。</returns>
        public static CultureScope Enter(string cultureName)
        {
            var scope = new CultureScope(CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            CultureInfo.CurrentUICulture = new CultureInfo(cultureName);
            return scope;
        }

        /// <summary>还原进入作用域前的文化。</summary>
        public void Dispose()
        {
            CultureInfo.CurrentCulture = _originalCulture;
            CultureInfo.CurrentUICulture = _originalUiCulture;
        }
    }
}
