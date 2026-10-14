using XBear.Core.Snapshots;

namespace XBear.Core.Tests.Snapshots;

/// <summary>
/// <c>info snapshots</c> 文本解析的单元测试。QEMU 输出为对齐的定宽表格，
/// 体积列宽度随数值变化，因此解析必须以末尾的日期与时刻为锚点。
///
/// 本工程以 invariant 全球化模式运行，只能覆盖与宿主时区相关的口径；
/// 跨区域的一致性由界面层测试工程的同名用例覆盖，那里的运行时与正式程序一致。
/// </summary>
public class SnapshotRuntimeTableTests
{
    private const string TwoRowTable =
        """
        ID       TAG                        VM SIZE                DATE        VM CLOCK
        0        snap-win-main-01-0001      0 B                    2026-10-07  11:20:00   00:00:01.000
        1        snap-win-main-01-0002      1.5 GiB                2026-10-07  14:05:30   00:12:34.567
        """;

    private const string SingleRowTable =
        """
        ID       TAG                        VM SIZE                DATE        VM CLOCK
        0        snap-win-main-01-0001      0 B                    2026-10-07  11:20:00   00:00:01.000
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

        // QEMU 的 DATE 列是墙上钟点读数，解析必须原样保留读数本身。
        Assert.Equal(new DateTime(2026, 10, 7, 11, 20, 0), entries[0].CreatedAt.DateTime);
        Assert.Equal(new DateTime(2026, 10, 7, 14, 5, 30), entries[1].CreatedAt.DateTime);
    }

    [Fact]
    public void ParsedCreationMomentCarriesTheDocumentedFixedOffset()
    {
        IReadOnlyList<SnapshotRuntimeEntry> entries = SnapshotRuntimeTable.Parse(TwoRowTable);

        // 读数本身不带时区信息，偏移量固定，解析结果不随宿主时区漂移。
        Assert.All(entries, entry => Assert.Equal(SnapshotRuntimeTable.DefaultReportedTimeZoneOffset, entry.CreatedAt.Offset));
    }

    [Fact]
    public void ParsedCreationMomentIgnoresTheHostTimeZone()
    {
        IReadOnlyList<SnapshotRuntimeEntry> entries = SnapshotRuntimeTable.Parse(TwoRowTable);

        // 无论宿主处于哪个时区，同一段 QEMU 输出都解析为同一读数。
        Assert.Equal(
            new DateTimeOffset(2026, 10, 7, 11, 20, 0, SnapshotRuntimeTable.DefaultReportedTimeZoneOffset),
            entries[0].CreatedAt);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 7, 14, 5, 30, SnapshotRuntimeTable.DefaultReportedTimeZoneOffset),
            entries[1].CreatedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(-5)]
    public void ExplicitOffsetChangesOnlyTheCarriedOffsetNeverTheReading(int offsetHours)
    {
        var offset = TimeSpan.FromHours(offsetHours);

        SnapshotRuntimeEntry entry = Assert.Single(SnapshotRuntimeTable.Parse(SingleRowTable, offset));

        // 承载读数的偏移由调用方显式指定，读数本身在任意偏移下都保持 QEMU 报告的墙上钟点。
        Assert.Equal(offset, entry.CreatedAt.Offset);
        Assert.Equal(new DateTime(2026, 10, 7, 11, 20, 0), entry.CreatedAt.DateTime);
    }

    [Fact]
    public void DefaultOffsetAppliesWhenNoOffsetIsSupplied()
    {
        SnapshotRuntimeEntry entry = Assert.Single(SnapshotRuntimeTable.Parse(SingleRowTable));

        Assert.Equal(SnapshotRuntimeTable.DefaultReportedTimeZoneOffset, entry.CreatedAt.Offset);
    }

    [Fact]
    public void MalformedMomentsAreSkippedInsteadOfGuessed()
    {
        const string malformed =
            """
            ID       TAG                 VM SIZE       DATE        VM CLOCK
            0        snap-bad-date       0 B           2026-13-45  11:20:00   00:00:01.000
            1        snap-good           0 B           2026-10-07  11:20:00   00:00:02.000
            """;

        SnapshotRuntimeEntry entry = Assert.Single(SnapshotRuntimeTable.Parse(malformed));

        Assert.Equal("snap-good", entry.Tag);
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