using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using XBear.Core.Spec;

namespace XBear.Core.Identity;

/// <summary>已删除实例的标识墓碑，记录其曾占用的标识以阻止复用。</summary>
public sealed class TombstoneRecord
{
    /// <summary>已删除实例的标识。</summary>
    [JsonPropertyName("instanceId")]
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>该实例曾用的序列号。</summary>
    [JsonPropertyName("serialNo")]
    public string? SerialNo { get; set; }

    /// <summary>该实例曾用的 Android ID。</summary>
    [JsonPropertyName("androidId")]
    public string? AndroidId { get; set; }

    /// <summary>该实例曾用的 IMEI。</summary>
    [JsonPropertyName("imei")]
    public string? Imei { get; set; }
}

/// <summary>
/// 已删除实例的标识墓碑仓库。实例删除后其标识进入墓碑，
/// 新建实例时不得撞上任何墓碑记录，避免宿主应用把新实例与历史实例视为同一台物理设备。
/// </summary>
public sealed class TombstoneStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    /// <summary>
    /// 初始化墓碑仓库。
    /// </summary>
    /// <param name="filePath">墓碑文件的存放路径，目录不存在时按需创建。</param>
    public TombstoneStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>墓碑文件路径。</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// 列出全部墓碑记录。
    /// </summary>
    /// <returns>墓碑记录列表，文件不存在时为空列表。</returns>
    public async Task<IReadOnlyList<TombstoneRecord>> ListAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await LoadUnsafeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 查询实例标识是否已被墓碑占用。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>存在墓碑记录时返回 true。</returns>
    public async Task<bool> IsTombstonedAsync(string instanceId)
    {
        IReadOnlyList<TombstoneRecord> records = await ListAsync().ConfigureAwait(false);
        return records.Any(r => string.Equals(r.InstanceId, instanceId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 查询任一标识是否已被墓碑占用。
    /// </summary>
    /// <param name="serialNo">待查序列号，为 null 时跳过该项。</param>
    /// <param name="androidId">待查 Android ID，为 null 时跳过该项。</param>
    /// <param name="imei">待查 IMEI，为 null 时跳过该项。</param>
    /// <returns>任一非空标识命中墓碑即返回 true。</returns>
    public async Task<bool> IsIdentityTakenAsync(
        string? serialNo,
        string? androidId,
        string? imei)
    {
        IReadOnlyList<TombstoneRecord> records = await ListAsync().ConfigureAwait(false);
        return records.Any(r =>
            Matches(serialNo, r.SerialNo) ||
            Matches(androidId, r.AndroidId) ||
            Matches(imei, r.Imei));
    }

    /// <summary>
    /// 写入一条墓碑记录，实例标识已存在时合并其标识字段。
    /// </summary>
    /// <param name="instanceId">已删除实例的标识。</param>
    /// <param name="identity">该实例曾用的标识，可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否为新增的墓碑记录，重复写入返回 false。</returns>
    public async Task<bool> AddAsync(
        string instanceId,
        DeviceIdentity? identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<TombstoneRecord> records = (await LoadUnsafeAsync().ConfigureAwait(false)).ToList();
            TombstoneRecord? existing = records.Find(
                r => string.Equals(r.InstanceId, instanceId, StringComparison.Ordinal));

            if (existing is null)
            {
                records.Add(new TombstoneRecord
                {
                    InstanceId = instanceId,
                    SerialNo = identity?.SerialNo,
                    AndroidId = identity?.AndroidId,
                    Imei = identity?.Imei,
                });
            }
            else
            {
                existing.SerialNo ??= identity?.SerialNo;
                existing.AndroidId ??= identity?.AndroidId;
                existing.Imei ??= identity?.Imei;
            }

            await SaveUnsafeAsync(records, cancellationToken).ConfigureAwait(false);
            return existing is null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool Matches(string? candidate, string? recorded) =>
        !string.IsNullOrEmpty(candidate) &&
        !string.IsNullOrEmpty(recorded) &&
        string.Equals(candidate, recorded, StringComparison.OrdinalIgnoreCase);

    private async Task<List<TombstoneRecord>> LoadUnsafeAsync()
    {
        if (!File.Exists(_filePath))
        {
            return new List<TombstoneRecord>();
        }

        string json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<TombstoneRecord>();
        }

        return JsonSerializer.Deserialize<List<TombstoneRecord>>(json, SerializerOptions)
            ?? new List<TombstoneRecord>();
    }

    private async Task SaveUnsafeAsync(List<TombstoneRecord> records, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(records, SerializerOptions);
        string tempPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, _filePath, overwrite: true);
    }
}