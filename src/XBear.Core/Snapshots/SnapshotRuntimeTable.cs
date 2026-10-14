using System.Globalization;
using System.Text.RegularExpressions;

namespace XBear.Core.Snapshots;

/// <summary>
/// QEMU 侧快照表中的一行，对应一次 savevm 留下的内部快照。
/// </summary>
/// <param name="Id">QEMU 侧序号。</param>
/// <param name="Tag">快照标签，本产品以快照标识作为标签。</param>
/// <param name="VmSize">QEMU 报告的虚拟机内存体积文本。</param>
/// <param name="CreatedAt">QEMU 报告的创建时刻，其墙上钟点即 QEMU 输出文本，读数本身不带时区信息。</param>
/// <param name="VmClock">QEMU 记录的虚拟机时钟。</param>
public sealed record SnapshotRuntimeEntry(
    string Id,
    string Tag,
    string VmSize,
    DateTimeOffset CreatedAt,
    string VmClock)
{
    /// <summary>
    /// 判断该条目是否对应给定的快照标识。
    /// </summary>
    /// <param name="snapshotId">快照标识。</param>
    /// <returns>标签与快照标识一致时返回 true。</returns>
    public bool Matches(string snapshotId) =>
        string.Equals(Tag, snapshotId, StringComparison.Ordinal);
}

/// <summary>
/// 解析 <c>info snapshots</c> 的文本输出。输出为对齐的定宽表格，
/// 体积列可能占一个或两个词，因此以末尾的日期、时刻与时钟三段作为锚点切分。
/// </summary>
public static partial class SnapshotRuntimeTable
{
    /// <summary>快照表为空时 QEMU 返回的提示文本。</summary>
    public const string EmptyTableText = "No snapshots available.";

    /// <summary>快照表为空时 QEMU 返回的另一常见提示文本。</summary>
    public const string AlternativeEmptyTableText = "There is no snapshot available.";

    /// <summary>快照表内日期与时刻的固定文本格式，与 QEMU 的输出格式一致。</summary>
    private const string MomentFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// QEMU 报告的创建时刻默认附带的固定时区偏移。
    ///
    /// 依据：QEMU 在 <c>bdrv_snapshot_dump</c> 中以 <c>g_date_time_new_from_unix_local</c>
    /// 取快照创建时刻，再按 <c>%Y-%m-%d %H:%M:%S</c> 打印，文本只含墙上钟点、不含任何偏移量。
    /// 因此该文本无法反推出唯一时刻：按宿主时区理解会让同一段 QEMU 输出在不同机器上得到不同结果，
    /// 这正是历史缺陷的根源。此处固定以零偏移承载该读数，解析结果只由文本本身决定，
    /// 不随宿主时区与区域变化；需要按墙上钟点读数时应取 <see cref="DateTimeOffset.DateTime"/>，
    /// 不应对该时刻再做时区换算。
    /// </summary>
    public static readonly TimeSpan DefaultReportedTimeZoneOffset = TimeSpan.Zero;

    /// <summary>
    /// 解析快照表文本。
    /// </summary>
    /// <param name="output"><c>info snapshots</c> 的原始输出。</param>
    /// <param name="reportedTimeZoneOffset">
    /// 承载读数的时区偏移，为 null 时使用 <see cref="DefaultReportedTimeZoneOffset"/>。
    /// 调用方可在明确知道 QEMU 所在宿主时区时传入该时区；此处不读取本机时区。
    /// </param>
    /// <returns>解析出的条目，输出为空表或无法识别时返回空集合。</returns>
    public static IReadOnlyList<SnapshotRuntimeEntry> Parse(
        string? output,
        TimeSpan? reportedTimeZoneOffset = null)
    {
        var carriedOffset = reportedTimeZoneOffset ?? DefaultReportedTimeZoneOffset;

        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<SnapshotRuntimeEntry>();
        }

        var entries = new List<SnapshotRuntimeEntry>();
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0 ||
                string.Equals(line, EmptyTableText, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(line, AlternativeEmptyTableText, StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("List of snapshots", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Snapshot list:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("ID ", StringComparison.Ordinal) ||
                line.StartsWith("ID\t", StringComparison.Ordinal))
            {
                continue;
            }

            Match match = RowPattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            // QEMU 的日期时刻是固定形状的墙上钟点：按精确格式与固定文化解析，
            // 不用宿主区域下的宽松解析，避免不同区域把同一段数字读成不同时刻。
            if (!DateTime.TryParseExact(
                    match.Groups["date"].Value + " " + match.Groups["time"].Value,
                    MomentFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime reportedWallClock))
            {
                continue;
            }

            entries.Add(new SnapshotRuntimeEntry(
                match.Groups["id"].Value,
                match.Groups["tag"].Value,
                match.Groups["size"].Value.Trim(),
                new DateTimeOffset(reportedWallClock, carriedOffset),
                match.Groups["clock"].Value));
        }

        return entries;
    }

    /// <summary>
    /// 在快照表中查找指定标签的条目。
    /// </summary>
    /// <param name="entries">已解析的快照表。</param>
    /// <param name="snapshotId">快照标识。</param>
    /// <returns>命中的条目，未命中时返回 null。</returns>
    public static SnapshotRuntimeEntry? Find(
        IReadOnlyList<SnapshotRuntimeEntry> entries,
        string snapshotId) =>
        entries.FirstOrDefault(entry => entry.Matches(snapshotId));

    [GeneratedRegex(
        @"^(?<id>\S+)\s+(?<tag>\S+)\s+(?<size>.+?)\s+(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}:\d{2})\s+(?<clock>\S+)(?:\s+.*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RowPattern();
}