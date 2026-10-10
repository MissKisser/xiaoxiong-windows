using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using XBear.Core.Diagnostics;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.Core.Snapshots;

/// <summary>快照状态迁移规则。creating 是唯一的中间态，其余两个状态为终态，只能被删除。</summary>
public static class SnapshotStateTransition
{
    /// <summary>
    /// 判断一次状态迁移是否合法。
    /// </summary>
    /// <param name="from">迁移前状态。</param>
    /// <param name="to">迁移后状态。</param>
    /// <returns>迁移合法时返回 true。</returns>
    public static bool IsAllowed(SnapshotState from, SnapshotState to) =>
        (from, to) switch
        {
            (SnapshotState.Creating, SnapshotState.Ready) => true,
            (SnapshotState.Creating, SnapshotState.Failed) => true,
            _ => false,
        };

    /// <summary>
    /// 断言一次状态迁移合法，非法时抛出带处置建议的异常。
    /// </summary>
    /// <param name="snapshotId">快照标识，用于错误定位。</param>
    /// <param name="from">迁移前状态。</param>
    /// <param name="to">迁移后状态。</param>
    /// <exception cref="XBearException">迁移不属于允许集合时抛出 <see cref="ErrorCategory.State"/>。</exception>
    public static void EnsureAllowed(string snapshotId, SnapshotState from, SnapshotState to)
    {
        if (IsAllowed(from, to))
        {
            return;
        }

        throw new XBearException(
            ErrorCategory.State,
            $"快照 {snapshotId} 不能由状态 {Describe(from)} 迁移为 {Describe(to)}。",
            "快照状态只能由创建中转为就绪或创建失败；就绪与创建失败都是终态，只能删除该快照。");
    }

    /// <summary>
    /// 返回状态的中文描述，供异常文案与界面提示使用。
    /// </summary>
    /// <param name="state">待描述的状态。</param>
    /// <returns>状态描述文本。</returns>
    public static string Describe(SnapshotState state) => state switch
    {
        SnapshotState.Creating => "创建中",
        SnapshotState.Ready => "就绪",
        SnapshotState.Failed => "创建失败",
        _ => "未知",
    };
}

/// <summary>
/// 快照元数据仓库。每个实例在自己的数据目录下持有一份快照目录，
/// 目录内每个快照一份 JSON 文件，文件名即快照标识；删除时把标识写入墓碑，后续不得复用。
/// </summary>
public sealed partial class SnapshotStore
{
    /// <summary>快照目录名，位于实例数据目录之下。</summary>
    public const string DirectoryName = "snapshots";

    /// <summary>已删除快照标识的墓碑文件名，位于快照目录之下。</summary>
    public const string TombstoneFileName = "tombstones.json";

    /// <summary>快照显示名的长度上限，与契约一致。</summary>
    public const int DisplayNameMaxLength = 64;

    /// <summary>快照备注的长度上限，与契约一致。</summary>
    public const int NoteMaxLength = 256;

    /// <summary>快照标识的长度上限，与契约一致。</summary>
    public const int IdMaxLength = 64;

    private const string FileExtension = ".json";
    private const string IdPrefix = "snap-";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SpecValidator? _validator;

    /// <summary>
    /// 构造快照元数据仓库。
    /// </summary>
    /// <param name="instanceDataDirectory">实例数据目录，快照目录建在其下。</param>
    /// <param name="validator">共享契约校验器，为 null 时只做内置字段校验。</param>
    public SnapshotStore(string instanceDataDirectory, SpecValidator? validator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceDataDirectory);
        _validator = validator;
        InstanceDataDirectory = Path.GetFullPath(instanceDataDirectory);
    }

    /// <summary>实例数据目录绝对路径。</summary>
    public string InstanceDataDirectory { get; }

    /// <summary>该实例的快照目录绝对路径。</summary>
    public string Directory => Path.Combine(InstanceDataDirectory, DirectoryName);

    /// <summary>墓碑文件绝对路径。</summary>
    public string TombstoneFilePath => Path.Combine(Directory, TombstoneFileName);

    /// <summary>
    /// 按创建时刻升序列出全部快照元数据。损坏的文件跳过不抛异常，避免单份坏数据挡住整张列表。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该实例的全部快照元数据。</returns>
    public async Task<IReadOnlyList<SnapshotSpec>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return Array.Empty<SnapshotSpec>();
            }

            var results = new List<SnapshotSpec>();
            foreach (string path in System.IO.Directory
                         .EnumerateFiles(Directory, "*" + FileExtension, SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();

                SnapshotSpec? spec = await TryReadAsync(path, cancellationToken).ConfigureAwait(false);
                if (spec is not null)
                {
                    results.Add(spec);
                }
            }

            return results
                .OrderBy(spec => spec.CreatedAt, StringComparer.Ordinal)
                .ThenBy(spec => spec.Id, StringComparer.Ordinal)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 按标识读取单份快照元数据。
    /// </summary>
    /// <param name="snapshotId">快照标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>快照不存在时返回 null。</returns>
    public async Task<SnapshotSpec?> GetAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        EnsureIdShape(snapshotId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = PathFor(snapshotId);
            return File.Exists(path) ? await ReadAsync(path, cancellationToken).ConfigureAwait(false) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 写入快照元数据，先落临时文件再原子替换，并按共享契约校验字段与取值。
    /// </summary>
    /// <param name="spec">待写入的快照元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="XBearException">字段不满足契约时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public async Task SaveAsync(SnapshotSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        EnsureShape(spec);

        string json = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        if (_validator is not null)
        {
            _validator.ValidateSnapshot(json).EnsureValid();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            await AtomicFile
                .WriteAllTextAsync(PathFor(spec.Id), json, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 删除快照元数据文件，并把其标识写入墓碑以阻止复用。
    /// </summary>
    /// <param name="snapshotId">快照标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文件原本存在并已删除时返回 true。</returns>
    public async Task<bool> DeleteAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        EnsureIdShape(snapshotId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool existed = false;
            string path = PathFor(snapshotId);
            if (File.Exists(path))
            {
                File.Delete(path);
                existed = true;
            }

            await AddTombstoneUnsafeAsync(snapshotId, cancellationToken).ConfigureAwait(false);
            return existed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 分配一个未被占用也未被墓碑记录的快照标识。同一实例内序号单调递增。
    /// </summary>
    /// <param name="instanceId">实例标识，用于生成长度合规的唯一标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新分配的快照标识。</returns>
    public async Task<string> AllocateIdAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            if (System.IO.Directory.Exists(Directory))
            {
                foreach (string path in System.IO.Directory.EnumerateFiles(
                             Directory,
                             "*" + FileExtension,
                             SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (!name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        used.Add(name);
                    }
                }
            }

            IReadOnlyList<string> tombstones = await ReadTombstonesUnsafeAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (string tombstone in tombstones)
            {
                used.Add(tombstone);
            }

            for (int sequence = 1; sequence <= int.MaxValue; sequence++)
            {
                string candidate = BuildId(instanceId, sequence);
                if (IsIdShape(candidate) && !used.Contains(candidate))
                {
                    return candidate;
                }
            }

            throw new XBearException(
                ErrorCategory.Spec,
                $"实例 {instanceId} 的快照标识已用尽，无法继续分配。",
                "请删除不再需要的快照元数据后重试。");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 查询快照标识是否已被墓碑占用。
    /// </summary>
    /// <param name="snapshotId">快照标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>存在墓碑记录时返回 true。</returns>
    public async Task<bool> IsTombstonedAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<string> tombstones = await ReadTombstonesUnsafeAsync(cancellationToken)
                .ConfigureAwait(false);
            return tombstones.Contains(snapshotId, StringComparer.Ordinal);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 校验一份快照元数据的字段取值是否符合契约。
    /// </summary>
    /// <param name="spec">待校验的快照元数据。</param>
    /// <exception cref="XBearException">字段不满足契约时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public static void EnsureShape(SnapshotSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (string.IsNullOrWhiteSpace(spec.SchemaVersion) ||
            !SemanticVersionPattern().IsMatch(spec.SchemaVersion))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"快照契约版本 {spec.SchemaVersion} 不符合契约要求的语义化版本格式。",
                "请使用三段式语义化版本，例如 1.0.0。");
        }

        EnsureIdShape(spec.Id);
        EnsureIdShape(spec.InstanceRef);

        if (string.IsNullOrWhiteSpace(spec.ImageRef))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"快照 {spec.Id} 的来源镜像标识为空。",
                "请在实例配置中填写镜像引用后再创建快照。");
        }

        if (spec.DisplayName is not null)
        {
            if (spec.DisplayName.Length is 0 or > DisplayNameMaxLength)
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"快照 {spec.Id} 的显示名长度超出契约允许的 1 到 {DisplayNameMaxLength} 个字符。",
                    "请改用更短的名称，或留空由两端从创建时刻渲染。");
            }
        }

        if (spec.Note is not null && spec.Note.Length > NoteMaxLength)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"快照 {spec.Id} 的备注长度超出契约允许的 {NoteMaxLength} 个字符。",
                "请精简备注内容后重试。");
        }

        if (!DateTimeOffset.TryParse(
                spec.CreatedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _)
            || !TimestampPattern().IsMatch(spec.CreatedAt))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"快照 {spec.Id} 的创建时刻 {spec.CreatedAt} 不符合契约要求的带时区偏移文本。",
                "请使用形如 2026-10-07T14:05:30+08:00 的 ISO 8601 文本。");
        }

        if (spec.ParentRef is not null)
        {
            EnsureIdShape(spec.ParentRef);
        }

        if (spec.PlatformConfig is not null && spec.PlatformConfig.Value.ValueKind != JsonValueKind.Object)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"快照 {spec.Id} 的平台特有字段不是对象。",
                "platformConfig 必须是对象，内部结构由各端自行约定。");
        }
    }

    /// <summary>
    /// 校验快照标识的形状，不满足契约格式时抛出异常。
    /// </summary>
    /// <param name="snapshotId">快照标识。</param>
    /// <exception cref="XBearException">标识不满足契约格式时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public static void EnsureIdShape(string snapshotId)
    {
        if (!IsIdShape(snapshotId))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"快照标识 {snapshotId} 不满足契约要求的小写字母、数字与连字符组合。",
                "请使用 3 到 64 位的小写字母、数字或连字符作为快照标识，且以字母数字开头结尾。");
        }
    }

    /// <summary>
    /// 判断一个标识是否满足契约格式。
    /// </summary>
    /// <param name="value">待判断的标识文本。</param>
    /// <returns>满足契约格式时返回 true。</returns>
    public static bool IsIdShape(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length is >= 3 and <= IdMaxLength
        && IdPattern().IsMatch(value);

    /// <summary>
    /// 生成时间戳文本，格式与契约的 createdAt 约束一致，且带时区偏移。
    /// </summary>
    /// <param name="moment">待格式化的时刻。</param>
    /// <returns>ISO 8601 文本。</returns>
    public static string FormatTimestamp(DateTimeOffset moment) =>
        moment.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>
    /// 由实例标识与序号生成快照标识。标识长度超出契约上限时截断实例部分并追加稳定摘要，
    /// 使长实例名下的不同快照仍互不相同。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="sequence">序号。</param>
    /// <returns>候选快照标识。</returns>
    public static string BuildId(string instanceId, int sequence)
    {
        string suffix = "-" + sequence.ToString("D4", CultureInfo.InvariantCulture);
        string stem = IdPrefix + instanceId;

        int budget = IdMaxLength - suffix.Length - IdPrefix.Length - 1;
        if (stem.Length > IdMaxLength - suffix.Length)
        {
            string digest = StableSuffix(instanceId + "|" + sequence.ToString(CultureInfo.InvariantCulture));
            stem = IdPrefix + instanceId[..Math.Max(1, budget - digest.Length)] + "-" + digest;
        }

        return stem + suffix;
    }

    /// <summary>
    /// 由快照标识派生稳定的长度固定摘要，用于标识超长时的区分后缀。
    /// </summary>
    /// <param name="text">待摘要的文本。</param>
    /// <returns>由十六进制字符组成的摘要。</returns>
    private static string StableSuffix(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in text)
            {
                hash = (hash ^ c) * 16777619;
            }

            return hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    private string PathFor(string snapshotId) => Path.Combine(Directory, snapshotId + FileExtension);

    private static async Task<SnapshotSpec?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (XBearException)
        {
            return null;
        }
    }

    private static async Task<SnapshotSpec> ReadAsync(string path, CancellationToken cancellationToken)
    {
        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<SnapshotSpec>(json, SpecLoader.SerializerOptions)
                   ?? throw new XBearException(
                       ErrorCategory.Spec,
                       $"快照元数据文件 {Path.GetFileName(path)} 内容为空。",
                       "请删除该损坏的文件后重新创建快照。");
        }
        catch (JsonException ex)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"快照元数据文件 {Path.GetFileName(path)} 无法解析：{ex.Message}",
                "请删除该损坏的文件后重新创建快照。",
                ex);
        }
    }

    private async Task<IReadOnlyList<string>> ReadTombstonesUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(TombstoneFilePath))
        {
            return Array.Empty<string>();
        }

        string json = await File.ReadAllTextAsync(TombstoneFilePath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, SpecLoader.SerializerOptions)
                   ?? new List<string>();
        }
        catch (JsonException)
        {
            // 墓碑文件损坏时不静默放行复用：按全部已用处理，交由用户清理后重试。
            throw new XBearException(
                ErrorCategory.Spec,
                $"快照标识墓碑文件无法解析：{TombstoneFilePath}",
                "请删除该文件后重新创建快照，届时标识将从当前未被占用的序号重新分配。");
        }
    }

    private async Task AddTombstoneUnsafeAsync(string snapshotId, CancellationToken cancellationToken)
    {
        List<string> records = (await ReadTombstonesUnsafeAsync(cancellationToken).ConfigureAwait(false)).ToList();
        if (records.Contains(snapshotId, StringComparer.Ordinal))
        {
            return;
        }

        records.Add(snapshotId);
        await AtomicFile
            .WriteAllTextAsync(
                TombstoneFilePath,
                JsonSerializer.Serialize(records, SpecLoader.SerializerOptions),
                cancellationToken)
            .ConfigureAwait(false);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();
}