using XBear.Core.Snapshots;

namespace XBear.Core.Tests.Snapshots;

/// <summary>
/// <c>info snapshots</c> 文本解析的单元测试。QEMU 输出为对齐的定宽表格，
/// 体积列宽度随数值变化，因此解析必须以末尾的日期与时刻为锚点。
/// </summary>
public class SnapshotRuntimeTableTests
{
    private const string TwoRowTable =
        """
        ID       TAG                        VM SIZE                DATE        VM CLOCK
        0        snap-win-main-01-0001      0 B                    2026-10-07  11:20:00   00:00:01.000
        1        snap-win-main-01-0002      1.5 GiB                2026-10-07  14:05:30   00:12:34.567
        """;

    [Fact]
    public void ParsesEveryRowAndSkipsTheHeader()
    {
        IReadOnlyList<SnapshotRuntimeEntry> entries = SnapshotRuntimeTable.Parse(TwoRowTable);

        Assert.Equal(2, entries.Count);
        Assert.Equal("0", entries[0].Id);
        Assert.Equal("snap-win-main-01-0001", entries[0].Tag);
        Assert.Equal("0 B", entries[0].VmSize);
        Assert.Equal("00:00:01.000", entries[0].VmClock);
        Assert.Equal("snap-win-main-01-0002", entries[1].Tag);
        Assert.Equal("1.5 GiB", entries[1].VmSize);
    }

    [Fact]
    public void ParsedCreationMomentKeepsTheReportedWallClock()
    {
        IReadOnlyList<SnapshotRuntimeEntry> entries = SnapshotRuntimeTable.Parse(TwoRowTable);

        Assert.Equal(new DateTimeOffset(2026, 10, 7, 11, 20, 0, TimeSpan.FromHours(8)), entries[0].CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 14, 5, 30, TimeSpan.FromHours(8)), entries[1].CreatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(SnapshotRuntimeTable.EmptyTableText)]
    public void EmptyOrAbsentTableYieldsNoEntries(string? output)
    {
        Assert.Empty(SnapshotRuntimeTable.Parse(output));
    }

    [Fact]
    public void UnrecognisedLinesAreSkipped()
    {
        const string mixed =
            """
            List of QEMU snapshots
            ID       TAG     VM SIZE     DATE        VM CLOCK
            this line carries no timestamp at all
            2        snap-x  12 MiB      2026-10-07  14:05:30   00:00:09.000
            """;

        SnapshotRuntimeEntry entry = Assert.Single(SnapshotRuntimeTable.Parse(mixed));

        Assert.Equal("snap-x", entry.Tag);
        Assert.Equal("12 MiB", entry.VmSize);
    }

    [Fact]
    public void FindLocatesTheEntryBySnapshotIdentifier()
    {
        IReadOnlyList<SnapshotRuntimeEntry> entries = SnapshotRuntimeTable.Parse(TwoRowTable);

        Assert.NotNull(SnapshotRuntimeTable.Find(entries, "snap-win-main-01-0002"));
        Assert.Null(SnapshotRuntimeTable.Find(entries, "snap-win-main-01-9999"));
    }
}