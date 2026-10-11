using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.Core.Identity;

/// <summary>
/// 设备身份迁移器。为缺失身份字段的既有实例配置自动补齐生成并回写。
/// </summary>
/// <remarks>
/// 实例配置的 deviceIdentity 段由「有则下发、无则省略」收紧为「创建时必然生成并写入」，
/// 但历史实例配置里已经存在没有该段或字段不全的配置。迁移必须自动完成：
/// 要求用户手工修配置等于把契约收紧的成本转嫁给用户，而半截身份在运行期
/// 只表现为「序列号为空」这类难以定位的暴露，不会报错，因此必须在加载期就地补齐。
/// 迁移本身是幂等的：已完整的身份原样保留，绝不重新生成。
/// </remarks>
public sealed class DeviceIdentityMigration
{
    private readonly IInstanceRepository _repository;
    private readonly IDeviceIdentityFactory _factory;

    /// <summary>
    /// 初始化身份迁移器。
    /// </summary>
    /// <param name="repository">实例配置仓库，用于读出待迁移的实例与回写补齐结果。</param>
    /// <param name="factory">设备身份工厂，用于补齐缺失的标识。</param>
    public DeviceIdentityMigration(IInstanceRepository repository, IDeviceIdentityFactory factory)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(factory);
        _repository = repository;
        _factory = factory;
    }

    /// <summary>
    /// 扫描在册实例，为缺失身份字段的配置补齐生成并回写。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次补齐并回写的实例配置数量，无缺失时返回 0。</returns>
    /// <exception cref="XBearException">回写失败时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public async Task<int> MigrateAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<InstanceSpec> instances = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);

        var migrated = 0;
        foreach (var spec in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!NeedsMigration(spec.DeviceIdentity))
            {
                continue;
            }

            var completed = await CompleteAsync(spec, cancellationToken).ConfigureAwait(false);
            if (!completed)
            {
                continue;
            }

            await _repository.SaveAsync(spec, cancellationToken).ConfigureAwait(false);
            migrated++;
        }

        return migrated;
    }

    /// <summary>
    /// 判断实例身份是否需要迁移补齐。完整身份一律原样保留：
    /// 身份在实例生命周期内不得变更，重新生成会让已下发的通道取值与配置脱节。
    /// </summary>
    /// <param name="identity">实例配置中的身份段，可为空。</param>
    /// <returns>需要补齐时返回 true。</returns>
    public static bool NeedsMigration(DeviceIdentity? identity)
        => identity is null || !identity.HasRequiredFields();

    /// <summary>
    /// 判断某台实例是否需要迁移补齐身份。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <returns>需要补齐时返回 true。</returns>
    public static bool NeedsMigration(InstanceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return NeedsMigration(spec.DeviceIdentity);
    }

    /// <summary>
    /// 就地为单台实例补齐身份。已完整的身份不做任何改动。
    /// </summary>
    /// <param name="spec">待补齐的实例配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次确实补齐了内容时返回 true。</returns>
    /// <exception cref="XBearException">补齐过程中标识生成失败时抛出。</exception>
    private async Task<bool> CompleteAsync(InstanceSpec spec, CancellationToken cancellationToken)
    {
        var existing = spec.DeviceIdentity;

        // 只补缺失字段，已有的序列号与 Android ID 原样保留。
        // 历史配置里可能已经带了部分标识，把它们一并丢弃会让该实例在运行期与既有下发取值脱节。
        DeviceIdentity? generated = existing is null
            ? await _factory.GenerateAsync(cancellationToken).ConfigureAwait(false)
            : await GenerateMissingFieldsAsync(existing, spec, cancellationToken).ConfigureAwait(false);

        if (generated is null)
        {
            return false;
        }

        spec.DeviceIdentity = generated;
        return true;
    }

    private async Task<DeviceIdentity?> GenerateMissingFieldsAsync(
        DeviceIdentity existing,
        InstanceSpec spec,
        CancellationToken cancellationToken)
    {
        if (existing.HasRequiredFields() &&
            !string.IsNullOrWhiteSpace(existing.Mac) &&
            !string.IsNullOrWhiteSpace(existing.DeviceProfileId))
        {
            return null;
        }

        DeviceIdentity fresh = await _factory.GenerateAsync(cancellationToken).ConfigureAwait(false);

        var completed = new DeviceIdentity
        {
            SerialNo = Blank(existing.SerialNo) ? fresh.SerialNo : existing.SerialNo,
            AndroidId = Blank(existing.AndroidId) ? fresh.AndroidId : existing.AndroidId,
            Imei = string.IsNullOrWhiteSpace(existing.Imei) ? fresh.Imei : existing.Imei,
            Mac = Blank(existing.Mac) ? DeriveMac(fresh, spec) : existing.Mac,
            DeviceProfileId = Blank(existing.DeviceProfileId)
                ? fresh.DeviceProfileId
                : existing.DeviceProfileId,
            IdentityVersion = existing.IdentityVersion ?? fresh.IdentityVersion,
        };

        DeviceIdentityFactory.ValidateFormat(completed);
        return completed;
    }

    private static string? DeriveMac(DeviceIdentity fresh, InstanceSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(fresh.Mac) || string.IsNullOrWhiteSpace(fresh.SerialNo))
        {
            return fresh.Mac;
        }

        var profile = DeviceProfileCatalog.Find(fresh.DeviceProfileId)
            ?? DeviceProfileCatalog.Select(
                DeviceProfileCatalog.OrdinalForInstanceId(spec.Id));

        return MacAddress.Derive(profile, fresh.SerialNo);
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}