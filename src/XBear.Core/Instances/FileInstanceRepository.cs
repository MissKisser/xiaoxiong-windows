using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Identity;
using XBear.Core.Spec;

namespace XBear.Core.Instances;

/// <summary>
/// 文件系统实例仓库。每个实例一个 JSON 文件，文件名即实例标识。
/// 写入走临时文件再替换，避免进程崩溃留下半个文件。
/// 删除时把实例标识写入墓碑，阻止标识被后续实例复用。
/// </summary>
public sealed partial class FileInstanceRepository : IInstanceRepository
{
    private const string FileExtension = ".json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _directory;
    private readonly TombstoneStore _tombstones;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 初始化实例仓库。
    /// </summary>
    /// <param name="directory">实例配置文件所在目录，不存在时自动创建。</param>
    /// <param name="tombstones">墓碑仓库，删除实例时写入其标识。</param>
    public FileInstanceRepository(string directory, TombstoneStore tombstones)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(tombstones);

        _directory = directory;
        _tombstones = tombstones;
        System.IO.Directory.CreateDirectory(_directory);
    }

    /// <summary>实例配置目录路径。</summary>
    public string Directory => _directory;

    /// <summary>
    /// 列出全部实例配置，损坏或无法解析的文件跳过不抛异常。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部可解析的实例配置。</returns>
    public async Task<IReadOnlyList<InstanceSpec>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<InstanceSpec>();
            foreach (string path in EnumerateSpecFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                InstanceSpec? spec = await TryReadAsync(path, cancellationToken).ConfigureAwait(false);
                if (spec is not null)
                {
                    results.Add(spec);
                }
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 按标识读取单个实例配置。
    /// </summary>
    /// <param name="id">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实例不存在时返回 null，配置损坏时抛出 <see cref="ErrorCategory.Spec"/>。</returns>
    public async Task<InstanceSpec?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = PathFor(id);
            if (!File.Exists(path))
            {
                return null;
            }

            return await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 写入实例配置，先写临时文件再原子替换。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="XBearException">实例标识不满足契约时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public async Task SaveAsync(InstanceSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (!InstanceIdPattern().IsMatch(spec.Id))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"实例标识 {spec.Id} 不满足契约要求的小写字母、数字与连字符组合。",
                "请使用 3 到 64 位的小写字母、数字或连字符作为实例标识，且以字母数字开头结尾。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            string json = JsonSerializer.Serialize(spec, SerializerOptions);
            await AtomicFile.WriteAllTextAsync(PathFor(spec.Id), json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 删除实例配置，并把其曾用的标识写入墓碑以阻止复用。
    /// </summary>
    /// <param name="id">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = PathFor(id);
            DeviceIdentity? identity = null;

            if (File.Exists(path))
            {
                InstanceSpec? spec = await TryReadAsync(path, cancellationToken).ConfigureAwait(false);
                identity = spec?.DeviceIdentity;

                File.Delete(path);
            }

            await _tombstones.AddAsync(id, identity, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 查询实例标识是否已被墓碑占用。
    /// </summary>
    /// <param name="id">实例标识。</param>
    /// <returns>存在墓碑记录时返回 true。</returns>
    public Task<bool> IsTombstonedAsync(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _tombstones.IsTombstonedAsync(id);
    }

    private IEnumerable<string> EnumerateSpecFiles()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return Array.Empty<string>();
        }

        return System.IO.Directory
            .EnumerateFiles(_directory, "*" + FileExtension, SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    private string PathFor(string id) => Path.Combine(_directory, id + FileExtension);

    private static async Task<InstanceSpec?> TryReadAsync(string path, CancellationToken cancellationToken)
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

    private static async Task<InstanceSpec> ReadAsync(string path, CancellationToken cancellationToken)
    {
        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<InstanceSpec>(json, SerializerOptions)
                ?? throw new XBearException(
                    ErrorCategory.Spec,
                    $"实例配置文件 {Path.GetFileName(path)} 内容为空。",
                    "请删除该损坏的配置文件后重新创建实例。");
        }
        catch (JsonException ex)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"实例配置文件 {Path.GetFileName(path)} 无法解析：{ex.Message}",
                "请删除该损坏的配置文件后重新创建实例。",
                ex);
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex InstanceIdPattern();
}