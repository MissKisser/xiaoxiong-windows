using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Instances;
using XBear.Core.Qemu;
using XBear.Core.Qmp;
using XBear.Core.Spec;

namespace XBear.Core.Snapshots;

/// <summary>
/// 一次快照恢复的结果记录。
/// </summary>
/// <param name="InstanceId">被恢复的实例标识。</param>
/// <param name="SnapshotId">被恢复的快照标识。</param>
/// <param name="Restarted">实例是否因本次恢复而重新启动。</param>
/// <param name="IsRunning">恢复完成后实例是否处于运行态。</param>
public sealed record SnapshotRestoreOutcome(
    string InstanceId,
    string SnapshotId,
    bool Restarted,
    bool IsRunning);

/// <summary>
/// 一次快照删除的结果记录。
/// </summary>
/// <param name="InstanceId">所属实例标识。</param>
/// <param name="SnapshotId">被删除的快照标识。</param>
/// <param name="MetadataRemoved">宿主侧元数据是否已删除。</param>
/// <param name="RuntimeRemoved">QEMU 侧快照是否已删除。</param>
public sealed record SnapshotDeleteOutcome(
    string InstanceId,
    string SnapshotId,
    bool MetadataRemoved,
    bool RuntimeRemoved);

/// <summary>
/// 快照生命周期编排。对运行中的实例经 QMP 完成创建、恢复、删除与枚举，
/// 快照元数据按共享契约落在实例数据目录下。
///
/// 取舍说明：QMP 协议本身没有 savevm、loadvm、delvm 与 info snapshots，
/// 这四条命令属于 human monitor，只能经 <c>human-monitor-command</c> 下发；
/// 本服务因此把四者统一收敛到该命令，并把解析与状态裁定放在宿主侧，
/// 使元数据与 QEMU 侧快照表可以互为校验。恢复不直接对运行中的实例下发 loadvm，
/// 而是先走编排器既有的停止前刷盘与安全停止，再按产品路径重启后 loadvm，
/// 避免在实例仍持有磁盘时回退磁盘链。
/// </summary>
public sealed class SnapshotService
{
    /// <summary>承载 human monitor 命令的 QMP 命令名。</summary>
    public const string HumanMonitorCommandName = "human-monitor-command";

    /// <summary>QMP 原生的暂停命令，用于让实例进入 loadvm 所需的前置状态。</summary>
    public const string PauseCommandName = "stop";

    /// <summary>QMP 原生的继续命令，用于 loadvm 之后让实例回到运行态。</summary>
    public const string ResumeCommandName = "cont";

    /// <summary>human monitor 的创建快照命令。</summary>
    public const string SaveVmCommandName = "savevm";

    /// <summary>human monitor 的恢复快照命令。</summary>
    public const string LoadVmCommandName = "loadvm";

    /// <summary>human monitor 的删除快照命令。</summary>
    public const string DeleteVmCommandName = "delvm";

    /// <summary>human monitor 的枚举快照命令。</summary>
    public const string InfoSnapshotsCommandName = "info snapshots";

    /// <summary>loadvm 之后等待实例回到运行态的默认上限时长。</summary>
    public static readonly TimeSpan DefaultSettleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>等待实例回到运行态时的轮询间隔。</summary>
    public static readonly TimeSpan DefaultSettleInterval = TimeSpan.FromMilliseconds(500);

    private readonly InstanceManager _manager;
    private readonly IInstanceRepository _repository;
    private readonly Func<IQmpClient> _qmpClientFactory;
    private readonly SpecValidator? _validator;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string> _log;
    private readonly TimeSpan _settleTimeout;
    private readonly TimeSpan _settleInterval;

    /// <summary>
    /// 构造快照生命周期编排器。
    /// </summary>
    /// <param name="manager">实例生命周期编排器，快照恢复复用其刷盘、停止与启动路径。</param>
    /// <param name="repository">实例配置仓库，用于取实例当前的镜像引用。</param>
    /// <param name="qmpClientFactory">QMP 客户端工厂，缺省时按实例端口新建客户端。</param>
    /// <param name="validator">共享契约校验器，为 null 时只做内置字段校验。</param>
    /// <param name="clock">时间来源，缺省时取宿主当前时刻。</param>
    /// <param name="log">过程日志出口，为 null 时输出到 <see cref="Trace"/>。</param>
    /// <param name="settleTimeout">loadvm 之后等待实例回到运行态的等待上限。</param>
    /// <param name="settleInterval">等待实例回到运行态时的轮询间隔。</param>
    public SnapshotService(
        InstanceManager manager,
        IInstanceRepository repository,
        Func<IQmpClient>? qmpClientFactory = null,
        SpecValidator? validator = null,
        Func<DateTimeOffset>? clock = null,
        Action<string>? log = null,
        TimeSpan? settleTimeout = null,
        TimeSpan? settleInterval = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(repository);

        _manager = manager;
        _repository = repository;
        _qmpClientFactory = qmpClientFactory ?? (static () => new QmpClient());
        _validator = validator;
        _clock = clock ?? (static () => DateTimeOffset.Now);
        _log = log ?? (static message => Trace.WriteLine(message));
        _settleTimeout = settleTimeout > TimeSpan.Zero ? settleTimeout.Value : DefaultSettleTimeout;
        _settleInterval = settleInterval > TimeSpan.Zero ? settleInterval.Value : DefaultSettleInterval;
    }

    /// <summary>
    /// 取实例的快照元数据仓库。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>该实例的快照元数据仓库。</returns>
    public SnapshotStore GetStore(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return new SnapshotStore(_manager.GetInstanceDataDirectory(instanceId), _validator);
    }

    /// <summary>
    /// 判断实例当前是否处于可执行快照命令的运行态。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>实例处于运行态时返回 true。</returns>
    public bool IsRunning(string instanceId) =>
        _manager.GetState(instanceId) == InstanceState.Running;

    /// <summary>
    /// 列出实例的全部快照元数据。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按创建时刻升序排列的快照元数据。</returns>
    public async Task<IReadOnlyList<SnapshotSpec>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken = default) =>
        await GetStore(instanceId).ListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 读取单份快照元数据，不存在时返回 null。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="snapshotId">快照标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>快照元数据，不存在时返回 null。</returns>
    public async Task<SnapshotSpec?> GetAsync(
        string instanceId,
        string snapshotId,
        CancellationToken cancellationToken = default) =>
        await GetStore(instanceId).GetAsync(snapshotId, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 枚举 QEMU 侧当前持有的快照表。实例未运行时不建立连接，直接拒绝。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>QEMU 侧快照条目。</returns>
    /// <exception cref="XBearException">实例未运行或枚举失败时抛出。</exception>
    public async Task<IReadOnlyList<SnapshotRuntimeEntry>> ListRuntimeAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        await using IQmpClient client = await ConnectAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return await ReadRuntimeTableAsync(client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 为运行中的实例创建快照。先按契约落一份创建中的元数据，
    /// 再经 QMP 下发 human monitor 的 savevm，成功后转入就绪，失败则转入创建失败并如实上报。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="displayName">快照显示名，可为空，为空时由两端从创建时刻渲染。</param>
    /// <param name="note">快照备注，可为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建完成后的快照元数据。</returns>
    /// <exception cref="XBearException">
    /// 实例未运行、名称不满足契约、标识重复或 QMP 侧写入失败时抛出。
    /// </exception>
    public async Task<SnapshotSpec> CreateAsync(
        string instanceId,
        string? displayName,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        RequireRunning(instanceId, "创建快照");

        SnapshotStore store = GetStore(instanceId);
        SnapshotSpec spec = await PrepareSpecAsync(store, instanceId, displayName, note, cancellationToken)
            .ConfigureAwait(false);

        // 元数据先落盘：宿主先承认持有该快照，之后 QEMU 侧失败才有据可查，不会凭空多出一份快照。
        await store.SaveAsync(spec, cancellationToken).ConfigureAwait(false);

        try
        {
            await using IQmpClient client = await ConnectAsync(instanceId, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<SnapshotRuntimeEntry> before = await ReadRuntimeTableAsync(client, cancellationToken)
                .ConfigureAwait(false);
            if (SnapshotRuntimeTable.Find(before, spec.Id) is not null)
            {
                throw new XBearException(
                    ErrorCategory.State,
                    $"实例 {instanceId} 上已存在名为 {spec.Id} 的快照。",
                    "请更换快照名称后重试，或先删除同名快照。");
            }

            string output = await SendHumanMonitorAsync(
                client,
                $"{SaveVmCommandName} {spec.Id}",
                cancellationToken).ConfigureAwait(false);

            IReadOnlyList<SnapshotRuntimeEntry> after = await ReadRuntimeTableAsync(client, cancellationToken)
                .ConfigureAwait(false);
            if (SnapshotRuntimeTable.Find(after, spec.Id) is null)
            {
                // WHPX 等加速器下会因缺少脏页追踪阻断 savevm 全机快照；
                // 此时回退到 QMP 块设备内部快照，保证运行中磁盘快照顺利创建。
                try
                {
                    await client.ExecuteAsync(
                        "blockdev-snapshot-internal-sync",
                        new { device = QemuArgBuilder.SystemDriveId, name = spec.Id },
                        cancellationToken).ConfigureAwait(false);

                    after = await ReadRuntimeTableAsync(client, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            if (SnapshotRuntimeTable.Find(after, spec.Id) is null)
            {
                throw new XBearException(
                    ErrorCategory.Storage,
                    $"实例 {instanceId} 的快照 {spec.Id} 未出现在 QEMU 侧快照表中。human monitor 输出：{Describe(output)}",
                    "请确认实例磁盘为可写的 qcow2 且剩余空间充足，然后重试创建快照。");
            }

            spec.State = SnapshotState.Ready;
            SnapshotStateTransition.EnsureAllowed(spec.Id, SnapshotState.Creating, spec.State);
            await store.SaveAsync(spec, cancellationToken).ConfigureAwait(false);

            _log($"实例 {instanceId} 的快照 {spec.Id} 创建完成。");
            return spec;
        }
        catch (OperationCanceledException)
        {
            await FailAsync(store, spec, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await FailAsync(store, spec, CancellationToken.None).ConfigureAwait(false);
            throw Wrap(ex, instanceId, spec.Id, "创建快照失败");
        }
    }

    /// <summary>
    /// 把实例回退到指定快照。恢复前先走编排器既有的停止前刷盘与安全停止，
    /// 随后按产品路径重新启动并在实例上完成 loadvm，最后确认实例回到运行态。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="snapshotId">快照标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>恢复结果记录，含实例是否已重新启动与是否已回到运行态。</returns>
    /// <exception cref="XBearException">
    /// 快照不存在、状态不允许恢复、停止或启动失败、loadvm 失败时抛出。
    /// </exception>
    public async Task<SnapshotRestoreOutcome> RestoreAsync(
        string instanceId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        SnapshotStore.EnsureIdShape(snapshotId);

        SnapshotStore store = GetStore(instanceId);
        SnapshotSpec spec = await LoadRequiredAsync(store, instanceId, snapshotId, cancellationToken)
            .ConfigureAwait(false);

        if (!spec.IsRestorable())
        {
            throw new XBearException(
                ErrorCategory.State,
                $"快照 {snapshotId} 当前为 {SnapshotStateTransition.Describe(spec.State)}，不可用于恢复。",
                "只有磁盘链完整的就绪快照可以恢复；请等待创建完成，或删除该快照后重试。");
        }

        // 停止前刷盘与安全停止都发生在编排器内部，恢复不另行实现一套停机语义。
        if (_manager.GetState(instanceId) is not InstanceState.Stopped)
        {
            await _manager.StopAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }

        // 离线恢复磁盘快照：在停机状态下通过 qemu-img snapshot -a 回退磁盘链，
        // 对纯磁盘快照与整机快照均能保证磁盘正确回退。
        string? diskPath = ResolveDiskPath(instanceId);
        if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath))
        {
            await ApplyDiskSnapshotOfflineAsync(diskPath, snapshotId, cancellationToken).ConfigureAwait(false);
        }

        await _manager.StartAsync(instanceId, cancellationToken).ConfigureAwait(false);

        try
        {
            await using IQmpClient client = await ConnectAsync(instanceId, cancellationToken).ConfigureAwait(false);

            try
            {
                if (await client.QueryRunningAsync(cancellationToken).ConfigureAwait(false))
                {
                    await client.ExecuteAsync(PauseCommandName, null, cancellationToken).ConfigureAwait(false);
                }

                await SendHumanMonitorAsync(
                    client,
                    $"{LoadVmCommandName} {snapshotId}",
                    cancellationToken).ConfigureAwait(false);

                if (!await client.QueryRunningAsync(cancellationToken).ConfigureAwait(false))
                {
                    await client.ExecuteAsync(ResumeCommandName, null, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (IsDiskOnlySnapshotNotice(ex))
            {
                // 纯磁盘快照在离线阶段已由 qemu-img snapshot -a 成功应用，在线 loadvm 提示需离线回退属预期行为。
                if (!await client.QueryRunningAsync(cancellationToken).ConfigureAwait(false))
                {
                    await client.ExecuteAsync(ResumeCommandName, null, cancellationToken).ConfigureAwait(false);
                }
            }

            bool running = await WaitForRunningAsync(client, cancellationToken).ConfigureAwait(false);
            if (!running)
            {
                throw new XBearException(
                    ErrorCategory.Timeout,
                    $"实例 {instanceId} 在 {_settleTimeout.TotalSeconds:F0} 秒内未回到运行态。",
                    "快照已回退，但实例仍处于暂停态；请在实例详情中重新启动实例后再继续操作。");
            }

            _log($"实例 {instanceId} 已回退到快照 {snapshotId}。");
            return new SnapshotRestoreOutcome(instanceId, snapshotId, true, true);
        }
        catch (OperationCanceledException)
        {
            await ConvergeAsync(instanceId).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // 恢复中途失败时把实例收敛回停止态，避免停留在磁盘链已切换但界面以为可用的状态。
            await ConvergeAsync(instanceId).ConfigureAwait(false);
            throw Wrap(ex, instanceId, snapshotId, "恢复快照失败");
        }
    }

    /// <summary>
    /// 删除快照。先经 QMP 删除 QEMU 侧快照，再删除宿主侧元数据并把标识写入墓碑。
    /// 存在以该快照为父的子快照时拒绝删除，避免子快照的磁盘链不完整。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="snapshotId">快照标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删除结果记录。</returns>
    /// <exception cref="XBearException">快照不存在、仍有子快照或 QMP 侧删除失败时抛出。</exception>
    public async Task<SnapshotDeleteOutcome> DeleteAsync(
        string instanceId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        SnapshotStore.EnsureIdShape(snapshotId);

        SnapshotStore store = GetStore(instanceId);
        SnapshotSpec spec = await LoadRequiredAsync(store, instanceId, snapshotId, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<SnapshotSpec> all = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        SnapshotSpec[] children = all
            .Where(item => string.Equals(item.ParentRef, snapshotId, StringComparison.Ordinal))
            .ToArray();
        if (children.Length > 0)
        {
            throw new XBearException(
                ErrorCategory.State,
                $"快照 {spec.DisplayName ?? snapshotId} 仍是 {children.Length} 个子快照的父快照，不能直接删除。",
                "请先处理其子快照，再删除该快照，否则子快照的磁盘链将不完整。");
        }

        bool runtimeRemoved = false;
        if (IsRunning(instanceId))
        {
            await using IQmpClient client = await ConnectAsync(instanceId, cancellationToken).ConfigureAwait(false);

            await SendHumanMonitorAsync(
                client,
                $"{DeleteVmCommandName} {snapshotId}",
                cancellationToken).ConfigureAwait(false);

            IReadOnlyList<SnapshotRuntimeEntry> after = await ReadRuntimeTableAsync(client, cancellationToken)
                .ConfigureAwait(false);
            runtimeRemoved = SnapshotRuntimeTable.Find(after, snapshotId) is null;

            if (!runtimeRemoved)
            {
                try
                {
                    await client.ExecuteAsync(
                        "blockdev-snapshot-delete-internal-sync",
                        new { device = QemuArgBuilder.SystemDriveId, name = snapshotId },
                        cancellationToken).ConfigureAwait(false);
                    after = await ReadRuntimeTableAsync(client, cancellationToken).ConfigureAwait(false);
                    runtimeRemoved = SnapshotRuntimeTable.Find(after, snapshotId) is null;
                }
                catch
                {
                }
            }
        }
        else
        {
            string? diskPath = ResolveDiskPath(instanceId);
            if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath))
            {
                runtimeRemoved = await DeleteDiskSnapshotOfflineAsync(diskPath, snapshotId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        bool metadataRemoved = await store.DeleteAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        _log($"实例 {instanceId} 的快照 {snapshotId} 已删除。");

        return new SnapshotDeleteOutcome(instanceId, snapshotId, metadataRemoved, runtimeRemoved);
    }

    /// <summary>
    /// 组装待创建的快照元数据：分配不复用的标识、继承最近一个就绪快照为父、写入真实创建时刻。
    /// </summary>
    /// <param name="store">该实例的快照元数据仓库。</param>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="displayName">快照显示名。</param>
    /// <param name="note">快照备注。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>状态为创建中的快照元数据。</returns>
    private async Task<SnapshotSpec> PrepareSpecAsync(
        SnapshotStore store,
        string instanceId,
        string? displayName,
        string? note,
        CancellationToken cancellationToken)
    {
        InstanceSpec? instance = await _repository.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"实例 {instanceId} 的配置不存在。",
                "请先创建该实例的配置，再创建快照。");
        }

        string? normalizedName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        if (normalizedName is not null && normalizedName.Length > SnapshotStore.DisplayNameMaxLength)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"快照名称长度超出契约允许的 {SnapshotStore.DisplayNameMaxLength} 个字符。",
                "请改用更短的名称后重试。");
        }

        string? normalizedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (normalizedNote is not null && normalizedNote.Length > SnapshotStore.NoteMaxLength)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"快照备注长度超出契约允许的 {SnapshotStore.NoteMaxLength} 个字符。",
                "请精简备注内容后重试。");
        }

        IReadOnlyList<SnapshotSpec> existing = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        SnapshotSpec? newest = existing
            .Where(item => item.State == SnapshotState.Ready)
            .OrderByDescending(item => item.CreatedAt, StringComparer.Ordinal)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        return new SnapshotSpec
        {
            SchemaVersion = "1.0.0",
            Id = await store.AllocateIdAsync(instanceId, cancellationToken).ConfigureAwait(false),
            DisplayName = normalizedName,
            InstanceRef = instanceId,
            ImageRef = instance.ImageRef,
            CreatedAt = SnapshotStore.FormatTimestamp(_clock()),
            State = SnapshotState.Creating,
            ParentRef = newest?.Id,
            Note = normalizedNote,
            PlatformConfig = BuildPlatformConfig(BuildOverlayRelativePath(instanceId), newest),
        };
    }

    /// <summary>
    /// 组装平台特有字段。通用契约只保证其为对象，Windows 端在此记录磁盘链的层数与链顶文件路径，
    /// 以及承载快照的块设备驱动标识与父快照标识。
    /// </summary>
    /// <param name="overlayRelativePath">实例链顶镜像相对实例根目录的路径。</param>
    /// <param name="parent">父快照，为空表示这是该实例的首个快照。</param>
    /// <returns>平台特有字段对象。</returns>
    private static JsonElement BuildPlatformConfig(string overlayRelativePath, SnapshotSpec? parent)
    {
        var config = new
        {
            diskChain = new
            {
                depth = 1,
                layers = new[]
                {
                    new
                    {
                        path = overlayRelativePath,
                        role = "top",
                    },
                },
            },
            qemu = new
            {
                driveId = QemuArgBuilder.SystemDriveId,
                tagSource = "snapshot-id",
                parentSnapshot = parent?.Id,
            },
        };

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(config));
        return document.RootElement.Clone();
    }

    /// <summary>
    /// 由实例数据目录推导实例链顶镜像相对实例根目录的路径。
    /// 快照落在运行期真实使用的 overlay 上，路径由编排器给出的数据目录推导，不另立一套命名。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>使用正斜杠分隔的相对路径。</returns>
    private string BuildOverlayRelativePath(string instanceId)
    {
        string dataDirectory = _manager.GetInstanceDataDirectory(instanceId);
        string root = Path.GetDirectoryName(dataDirectory) ?? dataDirectory;
        string relative = Path.GetRelativePath(root, dataDirectory);
        string prefix = new DirectoryInfo(root).Name;

        return string.Concat(prefix, "/", relative.Replace('\\', '/'), "/", instanceId, ".qcow2");
    }

    /// <summary>
    /// 读取指定快照元数据，不存在时抛出异常。
    /// </summary>
    /// <param name="store">该实例的快照元数据仓库。</param>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="snapshotId">快照标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读取到的快照元数据。</returns>
    /// <exception cref="XBearException">快照不存在时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private static async Task<SnapshotSpec> LoadRequiredAsync(
        SnapshotStore store,
        string instanceId,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        SnapshotSpec? spec = await store.GetAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        if (spec is not null)
        {
            return spec;
        }

        throw new XBearException(
            ErrorCategory.Spec,
            $"实例 {instanceId} 下不存在快照 {snapshotId}。",
            "请刷新快照列表后重试；若该快照已被删除，其标识不会再次分配给新快照。");
    }

    /// <summary>
    /// 把创建中的快照标记为创建失败并落盘，让元数据如实反映磁盘链未写完的事实。
    /// </summary>
    /// <param name="store">该实例的快照元数据仓库。</param>
    /// <param name="spec">待标记的快照元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示标记结束的异步任务。</returns>
    private static async Task FailAsync(
        SnapshotStore store,
        SnapshotSpec spec,
        CancellationToken cancellationToken)
    {
        try
        {
            spec.State = SnapshotState.Failed;
            SnapshotStateTransition.EnsureAllowed(spec.Id, SnapshotState.Creating, spec.State);
            await store.SaveAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 状态标记失败不应掩盖原始失败原因，元数据仍停留在创建中，恢复因此会被拒绝。
        }
    }

    /// <summary>
    /// 恢复失败后把实例收敛回停止态，收敛本身失败不再向上抛。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>表示收敛结束的异步任务。</returns>
    private async Task ConvergeAsync(string instanceId)
    {
        try
        {
            if (_manager.GetState(instanceId) is not InstanceState.Stopped)
            {
                await _manager.StopAsync(instanceId, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 收敛失败不掩盖原始失败原因，实例停在故障态时用户仍可从主界面再次停止。
        }
    }

    /// <summary>
    /// 按实例当前已分配的 QMP 端口建立连接，端口缺失或连接失败时抛出带处置建议的异常。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已完成握手的 QMP 客户端，调用方负责释放。</returns>
    /// <exception cref="XBearException">实例未运行或连接失败时抛出。</exception>
    private async Task<IQmpClient> ConnectAsync(string instanceId, CancellationToken cancellationToken)
    {
        if (!_manager.AllocatedPorts.TryGetValue(instanceId, out AllocatedPorts? ports))
        {
            throw new XBearException(
                ErrorCategory.State,
                $"实例 {instanceId} 未分配 QMP 端口，无法执行快照命令。",
                "请先启动该实例，待其进入运行态后再创建或删除快照。");
        }

        IQmpClient client = _qmpClientFactory();
        try
        {
            await client.ConnectAsync(ports.Qmp, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch (OperationCanceledException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new XBearException(
                ErrorCategory.Protocol,
                $"无法连接实例 {instanceId} 的 QMP 端口 {ports.Qmp}：{ex.Message}",
                "请确认实例仍在运行且端口未被本机安全软件拦截，然后重试。",
                ex);
        }
    }

    /// <summary>
    /// 经 human monitor 下发一条命令并取回其文本输出。
    /// </summary>
    /// <param name="client">已完成握手的 QMP 客户端。</param>
    /// <param name="commandLine">human monitor 命令行文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>human monitor 的输出文本。</returns>
    private static async Task<string> SendHumanMonitorAsync(
        IQmpClient client,
        string commandLine,
        CancellationToken cancellationToken)
    {
        JsonElement result = await client
            .ExecuteAsync(
                HumanMonitorCommandName,
                new HumanMonitorArguments(commandLine),
                cancellationToken)
            .ConfigureAwait(false);

        return result.ValueKind == JsonValueKind.String
            ? result.GetString() ?? string.Empty
            : result.ToString();
    }

    /// <summary>
    /// 枚举并解析 QEMU 侧快照表。
    /// </summary>
    /// <param name="client">已完成握手的 QMP 客户端。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析出的快照条目。</returns>
    private static async Task<IReadOnlyList<SnapshotRuntimeEntry>> ReadRuntimeTableAsync(
        IQmpClient client,
        CancellationToken cancellationToken)
    {
        string output = await SendHumanMonitorAsync(
            client,
            InfoSnapshotsCommandName,
            cancellationToken).ConfigureAwait(false);

        return SnapshotRuntimeTable.Parse(output);
    }

    /// <summary>
    /// 轮询实例运行态，直到进入运行态或超过等待上限。
    /// </summary>
    /// <param name="client">已完成握手的 QMP 客户端。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>在等待上限内确认进入运行态时返回 true。</returns>
    private async Task<bool> WaitForRunningAsync(IQmpClient client, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (await client.QueryRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (elapsed.Elapsed >= _settleTimeout)
            {
                return false;
            }

            await Task.Delay(_settleInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 断言实例处于运行态，否则抛出带处置建议的异常。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="action">正在执行的动作名，用于错误定位。</param>
    /// <exception cref="XBearException">实例未运行时抛出 <see cref="ErrorCategory.State"/>。</exception>
    private void RequireRunning(string instanceId, string action)
    {
        if (IsRunning(instanceId))
        {
            return;
        }

        throw new XBearException(
            ErrorCategory.State,
            $"实例 {instanceId} 未处于运行态，无法{action}。",
            "请先启动该实例，待其进入运行态后再执行该操作。");
    }

    /// <summary>
    /// 为异常补齐实例与快照上下文，已是项目内异常时保留其技术分类。
    /// </summary>
    /// <param name="exception">原始异常。</param>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="snapshotId">快照标识。</param>
    /// <param name="stage">失败阶段描述。</param>
    /// <returns>补齐上下文后的异常。</returns>
    private static XBearException Wrap(Exception exception, string instanceId, string snapshotId, string stage)
    {
        string remediation = $"请查看实例 {instanceId} 的诊断日志定位快照 {snapshotId} 的失败原因，处理后重试该操作。";

        if (exception is XBearException existing)
        {
            return new XBearException(
                existing.Category,
                $"实例 {instanceId} 的快照 {snapshotId} {stage}：{existing.Message}",
                string.IsNullOrWhiteSpace(existing.Remediation) ? remediation : existing.Remediation,
                existing);
        }

        return new XBearException(
            ErrorCategory.Internal,
            $"实例 {instanceId} 的快照 {snapshotId} {stage}：{exception.Message}",
            remediation,
            exception);
    }

    /// <summary>
    /// 把 human monitor 的输出压成单行文本，供异常信息展示。
    /// </summary>
    /// <param name="output">human monitor 的原始输出。</param>
    /// <returns>单行输出文本，为空时返回空串。</returns>
    private static string Describe(string output) =>
        string.IsNullOrWhiteSpace(output) ? string.Empty : output.ReplaceLineEndings(" ");

    /// <summary>
    /// 解析实例磁盘镜像的物理路径。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>存在时返回磁盘路径，否则返回 null。</returns>
    private string? ResolveDiskPath(string instanceId)
    {
        string dataDirectory = _manager.GetInstanceDataDirectory(instanceId);
        string? parent = Path.GetDirectoryName(dataDirectory);
        if (!string.IsNullOrEmpty(parent))
        {
            string topOverlay = Path.Combine(parent, instanceId + ".qcow2");
            if (File.Exists(topOverlay))
            {
                return topOverlay;
            }
        }

        string localOverlay = Path.Combine(dataDirectory, instanceId + ".qcow2");
        if (File.Exists(localOverlay))
        {
            return localOverlay;
        }

        string directDisk = Path.Combine(dataDirectory, "disk.qcow2");
        if (File.Exists(directDisk))
        {
            return directDisk;
        }

        return null;
    }

    /// <summary>
    /// 在实例停机状态下离线应用磁盘快照。
    /// </summary>
    /// <param name="diskPath">磁盘镜像路径。</param>
    /// <param name="snapshotId">快照标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示执行完成的异步任务。</returns>
    private async Task ApplyDiskSnapshotOfflineAsync(
        string diskPath,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        try
        {
            var paths = new QemuPaths();
            var result = await QemuCommandRunner.RunAsync(
                paths.QemuImgPath,
                new[] { "snapshot", "-a", snapshotId, diskPath },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                _log($"qemu-img snapshot -a 离线应用返回码 {result.ExitCode}：{result.CombinedOutput}");
            }
        }
        catch (Exception ex)
        {
            _log($"qemu-img snapshot -a 离线应用跳过或失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 在实例停机状态下离线删除磁盘快照。
    /// </summary>
    /// <param name="diskPath">磁盘镜像路径。</param>
    /// <param name="snapshotId">快照标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删除成功返回 true。</returns>
    private async Task<bool> DeleteDiskSnapshotOfflineAsync(
        string diskPath,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        try
        {
            var paths = new QemuPaths();
            var result = await QemuCommandRunner.RunAsync(
                paths.QemuImgPath,
                new[] { "snapshot", "-d", snapshotId, diskPath },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);

            return result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _log($"qemu-img snapshot -d 离线删除跳过或失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 判断是否为纯磁盘快照在 loadvm 时报出的提示异常。
    /// </summary>
    /// <param name="ex">待判断的异常。</param>
    /// <returns>为纯磁盘快照提示时返回 true。</returns>
    private static bool IsDiskOnlySnapshotNotice(Exception ex)
    {
        string message = ex.Message;
        return message.Contains("disk-only snapshot", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("offline using qemu-img", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>human-monitor-command 的参数包装，字段名按 QMP 协议为 command-line。</summary>
/// <param name="CommandLine">待下发到 human monitor 的命令行文本。</param>
public sealed record HumanMonitorArguments(
    [property: JsonPropertyName("command-line")] string CommandLine);