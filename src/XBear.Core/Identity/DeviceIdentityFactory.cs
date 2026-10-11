using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.Core.Identity;

/// <summary>
/// 设备身份生成器。产出后必须在在册实例与墓碑记录中查重，
/// 命中任一冲突即重新生成，避免多开实例被宿主应用判定为同一台物理设备。
/// </summary>
public sealed class DeviceIdentityFactory : IDeviceIdentityFactory
{
    /// <summary>默认的最大重生成次数。</summary>
    public const int DefaultMaxAttempts = 16;

    private readonly IInstanceRepository _repository;
    private readonly TombstoneStore _tombstones;
    private readonly IDeviceIdentityGenerator _generator;
    private readonly int _maxAttempts;

    /// <summary>
    /// 初始化标识工厂。
    /// </summary>
    /// <param name="repository">在册实例仓库，用于查重。</param>
    /// <param name="tombstones">墓碑仓库，用于查重已删除实例的标识。</param>
    /// <param name="generator">标识原始值生成器，为 null 时使用密码学随机实现。</param>
    /// <param name="maxAttempts">单次生成的最大重试次数。</param>
    public DeviceIdentityFactory(
        IInstanceRepository repository,
        TombstoneStore tombstones,
        IDeviceIdentityGenerator? generator = null,
        int maxAttempts = DefaultMaxAttempts)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(tombstones);

        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "重试次数至少为 1。");
        }

        _repository = repository;
        _tombstones = tombstones;
        _generator = generator ?? new CryptoDeviceIdentityGenerator();
        _maxAttempts = maxAttempts;
    }

    /// <summary>
    /// 生成一组全新标识，逐个校验格式、查重在册实例与墓碑记录。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>未与任何在册或已删除实例冲突的标识组合。</returns>
    /// <exception cref="XBearException">连续多次重生成仍无法避开冲突时抛出 <see cref="ErrorCategory.Identity"/>。</exception>
    public async Task<DeviceIdentity> GenerateAsync(CancellationToken cancellationToken = default)
        => await GenerateAsync(null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 生成一组全新标识并绑定机型族画像。机型族画像、序列号、Android ID 与 MAC 同源一次产出，
    /// 派生链路上不存在「画像一套、标识另一套」的拼装环节。
    /// </summary>
    /// <param name="profile">要绑定的机型族画像，为空时按实例序号确定性选取。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>未与任何在册或已删除实例冲突的标识组合。</returns>
    /// <exception cref="XBearException">连续多次重生成仍无法避开冲突时抛出 <see cref="ErrorCategory.Identity"/>。</exception>
    public async Task<DeviceIdentity> GenerateAsync(
        DeviceProfile? profile,
        CancellationToken cancellationToken = default)
        => await GenerateForOrdinalAsync(profile, 0, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 生成一组全新标识并绑定机型族画像。实例序号只参与未显式指定机型族时的确定性选取。
    /// </summary>
    /// <param name="profile">要绑定的机型族画像，为空时按实例序号确定性选取。</param>
    /// <param name="instanceOrdinal">实例在在册列表中的序号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>未与任何在册或已删除实例冲突的标识组合。</returns>
    /// <exception cref="XBearException">连续多次重生成仍无法避开冲突时抛出 <see cref="ErrorCategory.Identity"/>。</exception>
    public async Task<DeviceIdentity> GenerateForOrdinalAsync(
        DeviceProfile? profile,
        int instanceOrdinal,
        CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; attempt < _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DeviceIdentity candidate = _generator.Next(profile, instanceOrdinal);
            ValidateFormat(candidate);

            if (await IsIdentityInUseAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            return candidate;
        }

        throw new XBearException(
            ErrorCategory.Identity,
            $"连续 {_maxAttempts} 次生成的设备标识均与在册实例或墓碑记录冲突。",
            "请检查实例仓库与墓碑文件中是否存在异常多的重复标识，重试创建实例。");
    }

    /// <summary>
    /// 校验标识各字段是否落在契约允许的取值区间内。
    /// 序列号、Android ID 与 IMEI 必填；MAC 为条件必填：
    /// 缺 MAC 时由序列号与机型族画像确定性补齐，因此放行而不拒绝。
    /// </summary>
    /// <param name="identity">待校验的标识组合。</param>
    /// <exception cref="XBearException">字段格式越界时抛出 <see cref="ErrorCategory.Identity"/>。</exception>
    public static void ValidateFormat(DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        RequireLength(identity.SerialNo, 8, 32, "序列号");
        RequireLength(identity.AndroidId, 16, 32, "Android ID");
        RequireLength(identity.Imei, 14, 16, "IMEI");

        if (identity.Imei is not null && !Luhn.IsValid(identity.Imei))
        {
            throw new XBearException(
                ErrorCategory.Identity,
                $"IMEI {identity.Imei} 未通过 Luhn 校验。",
                "请重新生成该实例的设备标识。");
        }

        if (identity.Mac is not null && !string.IsNullOrWhiteSpace(identity.Mac))
        {
            MacAddress.EnsureValid(identity.Mac);
        }
    }

    private static void RequireLength(string? value, int min, int max, string fieldName)
    {
        if (value is null || value.Length < min || value.Length > max)
        {
            throw new XBearException(
                ErrorCategory.Identity,
                $"{fieldName} 长度 {value?.Length ?? 0} 超出契约区间 {min} 到 {max}。",
                "请重新生成该实例的设备标识。");
        }
    }

    /// <summary>
    /// 判断标识是否已被在册实例或墓碑记录占用。
    /// </summary>
    /// <param name="identity">待查标识组合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任一处命中即返回 true。</returns>
    public async Task<bool> IsIdentityInUseAsync(DeviceIdentity identity, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<InstanceSpec> instances = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);

        bool inUse = instances.Any(spec =>
            spec.DeviceIdentity is { } existing && Conflicts(existing, identity));

        return inUse || await _tombstones
            .IsIdentityTakenAsync(identity.SerialNo, identity.AndroidId, identity.Imei)
            .ConfigureAwait(false);
    }

    private static bool Conflicts(DeviceIdentity left, DeviceIdentity right) =>
        Same(left.SerialNo, right.SerialNo) ||
        Same(left.AndroidId, right.AndroidId) ||
        Same(left.Imei, right.Imei) ||
        Same(left.Mac, right.Mac);

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrEmpty(left) &&
        !string.IsNullOrEmpty(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}