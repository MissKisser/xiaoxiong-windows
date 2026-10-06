using System.Collections.Concurrent;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Instances;

/// <summary>实例状态变更通知。</summary>
public sealed class InstanceStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// 初始化状态变更通知。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="oldState">变更前状态。</param>
    /// <param name="newState">变更后状态。</param>
    public InstanceStateChangedEventArgs(string instanceId, InstanceState oldState, InstanceState newState)
    {
        InstanceId = instanceId;
        OldState = oldState;
        NewState = newState;
    }

    /// <summary>实例标识。</summary>
    public string InstanceId { get; }

    /// <summary>变更前状态。</summary>
    public InstanceState OldState { get; }

    /// <summary>变更后状态。</summary>
    public InstanceState NewState { get; }
}

/// <summary>
/// 实例生命周期编排。负责启动序列的状态推进、overlay 准备、端口分配与进程拉起，
/// 以及停止序列的进程终止与端口释放。
/// 启动任一环节失败时统一置为 <see cref="InstanceState.Faulted"/>、释放已占用端口并抛出带处置建议的异常，
/// 保证失败后可直接重试启动；用户主动取消不属于失败，端口同样释放但状态回到 <see cref="InstanceState.Stopped"/>。
/// </summary>
public sealed class InstanceManager
{
    private readonly IInstanceRepository _repository;
    private readonly IPortAllocator _portAllocator;
    private readonly IQemuArgBuilder _argBuilder;
    private readonly IQemuLauncher _launcher;
    private readonly IQcow2Manager _qcow2Manager;
    private readonly string _imagesRoot;
    private readonly string _instancesRoot;

    private readonly ConcurrentDictionary<string, InstanceState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AllocatedPorts> _allocatedPorts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, QemuProcessHandle> _handles = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 初始化生命周期编排器。
    /// </summary>
    /// <param name="repository">实例配置仓库。</param>
    /// <param name="portAllocator">端口分配器。</param>
    /// <param name="argBuilder">QEMU 参数生成器。</param>
    /// <param name="launcher">QEMU 进程拉起器。</param>
    /// <param name="qcow2Manager">overlay 链管理。</param>
    /// <param name="imagesRoot">只读 base 镜像根目录。</param>
    /// <param name="instancesRoot">每实例可写 overlay 根目录。</param>
    public InstanceManager(
        IInstanceRepository repository,
        IPortAllocator portAllocator,
        IQemuArgBuilder argBuilder,
        IQemuLauncher launcher,
        IQcow2Manager qcow2Manager,
        string imagesRoot,
        string instancesRoot)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(portAllocator);
        ArgumentNullException.ThrowIfNull(argBuilder);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(qcow2Manager);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagesRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(instancesRoot);

        _repository = repository;
        _portAllocator = portAllocator;
        _argBuilder = argBuilder;
        _launcher = launcher;
        _qcow2Manager = qcow2Manager;
        _imagesRoot = imagesRoot;
        _instancesRoot = instancesRoot;
    }

    /// <summary>状态变更通知，供界面绑定。</summary>
    public event EventHandler<InstanceStateChangedEventArgs>? StateChanged;

    /// <summary>当前处于占用状态的端口组，仅包含已分配且尚未释放的实例。</summary>
    public IReadOnlyDictionary<string, AllocatedPorts> AllocatedPorts => _allocatedPorts;

    /// <summary>
    /// 读取实例当前运行态，未登记过的实例视为已停止。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>实例当前状态。</returns>
    public InstanceState GetState(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return _states.TryGetValue(instanceId, out InstanceState state) ? state : InstanceState.Stopped;
    }

    /// <summary>
    /// 启动实例。依次完成状态校验、配置读取、overlay 准备、端口分配、参数生成与进程拉起。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务，进程拉起并置为运行中后完成。</returns>
    /// <exception cref="XBearException">
    /// 状态不允许启动时为 <see cref="ErrorCategory.State"/>；
    /// 配置缺失或不满足契约时为 <see cref="ErrorCategory.Spec"/>；
    /// overlay 创建失败为 <see cref="ErrorCategory.Storage"/>；
    /// 端口分配失败为 <see cref="ErrorCategory.Port"/>；
    /// 进程拉起失败为 <see cref="ErrorCategory.Process"/>。
    /// </exception>
    public async Task StartAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 停止实例。先终止进程，再释放端口，最后置为已停止。
    /// 端口释放在进程终止失败时同样执行，避免端口泄漏。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务，进程退出且端口释放后完成。</returns>
    /// <exception cref="XBearException">状态不允许停止时为 <see cref="ErrorCategory.State"/>，进程终止失败为 <see cref="ErrorCategory.Process"/>。</exception>
    public async Task StopAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartCoreAsync(string instanceId, CancellationToken cancellationToken)
    {
        // Faulted 表示上一次启动失败但资源已回收，允许直接重试；其余非停止态一律拒绝。
        InstanceState current = GetState(instanceId);
        if (current is not (InstanceState.Stopped or InstanceState.Faulted))
        {
            throw new XBearException(
                ErrorCategory.State,
                $"实例当前状态为 {current}，不允许启动。",
                "请先停止该实例，或等待当前启动或停止流程结束。");
        }

        SetState(instanceId, InstanceState.Starting);

        AllocatedPorts? allocated = null;
        QemuProcessHandle? handle = null;

        try
        {
            Spec.InstanceSpec spec = await LoadSpecAsync(instanceId, cancellationToken).ConfigureAwait(false);
            string baseImagePath = ResolveBaseImagePath(spec.ImageRef);
            string overlayPath = await EnsureOverlayAsync(instanceId, baseImagePath, spec.ImageRef, cancellationToken)
                .ConfigureAwait(false);

            allocated = await _portAllocator.AcquireAsync(instanceId, cancellationToken).ConfigureAwait(false);
            _allocatedPorts[instanceId] = allocated;

            IReadOnlyList<string> arguments = _argBuilder.BuildStartArguments(spec, overlayPath, allocated);
            handle = await _launcher.StartAsync(spec, overlayPath, allocated, cancellationToken).ConfigureAwait(false);
            _handles[instanceId] = handle;

            SetState(instanceId, InstanceState.Running);
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消不是故障：同样回收进程与端口，但状态回到已停止，取消异常原样上抛。
            if (handle is not null)
            {
                await SafeDisposeAsync(handle).ConfigureAwait(false);
            }

            ReleasePorts(instanceId);

            SetState(instanceId, InstanceState.Stopped);
            throw;
        }
        catch (Exception ex)
        {
            // 回收顺序：先杀进程再放端口，避免端口被残留进程继续占用。
            if (handle is not null)
            {
                await SafeDisposeAsync(handle).ConfigureAwait(false);
            }

            ReleasePorts(instanceId);

            SetState(instanceId, InstanceState.Faulted);
            throw Wrap(ex, instanceId, "启动失败");
        }
    }

    private async Task StopCoreAsync(string instanceId, CancellationToken cancellationToken)
    {
        InstanceState current = GetState(instanceId);
        if (current is InstanceState.Stopped)
        {
            return;
        }

        if (current is InstanceState.Starting or InstanceState.Stopping)
        {
            throw new XBearException(
                ErrorCategory.State,
                $"实例当前状态为 {current}，不允许停止。",
                "请等待当前启动或停止流程结束后重试。");
        }

        SetState(instanceId, InstanceState.Stopping);

        Exception? failure = null;
        try
        {
            if (_handles.TryRemove(instanceId, out QemuProcessHandle? handle))
            {
                await handle.StopAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                await handle.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消不是故障：端口在 finally 中归还，状态回到已停止，取消异常原样上抛。
            SetState(instanceId, InstanceState.Stopped);
            throw;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            // 无论进程是否成功终止，端口都必须归还，否则重试启动会一直撞端口占用。
            ReleasePorts(instanceId);
        }

        if (failure is not null)
        {
            SetState(instanceId, InstanceState.Faulted);
            throw Wrap(failure, instanceId, "停止失败");
        }

        SetState(instanceId, InstanceState.Stopped);
    }

    private async Task<Spec.InstanceSpec> LoadSpecAsync(string instanceId, CancellationToken cancellationToken)
    {
        Spec.InstanceSpec? spec = await _repository.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (spec is null)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"实例 {instanceId} 的配置不存在。",
                "请先创建该实例的配置，再执行启动。");
        }

        if (string.IsNullOrWhiteSpace(spec.ImageRef))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"实例 {instanceId} 未指定镜像引用。",
                "请在实例配置中填写镜像引用，并确认该镜像已导入。");
        }

        return spec;
    }

    private string ResolveBaseImagePath(string imageRef) =>
        Path.IsPathRooted(imageRef)
            ? imageRef
            : Path.Combine(_imagesRoot, imageRef + ".qcow2");

    private async Task<string> EnsureOverlayAsync(
        string instanceId,
        string baseImagePath,
        string imageRef,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_instancesRoot);
        string overlayPath = Path.Combine(_instancesRoot, instanceId + ".qcow2");

        if (File.Exists(overlayPath))
        {
            return overlayPath;
        }

        if (!File.Exists(baseImagePath))
        {
            throw new XBearException(
                ErrorCategory.Dependency,
                $"实例 {instanceId} 引用的镜像 {imageRef} 不存在。",
                "请先在镜像管理中导入该镜像，或修改实例的镜像引用。");
        }

        try
        {
            await _qcow2Manager.CreateOverlayAsync(baseImagePath, overlayPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not XBearException)
        {
            throw new XBearException(
                ErrorCategory.Storage,
                $"实例 {instanceId} 的可写 overlay 创建失败：{ex.Message}",
                "请检查磁盘剩余空间与镜像目录写入权限后重试启动。",
                ex);
        }

        return overlayPath;
    }

    private void ReleasePorts(string instanceId)
    {
        if (_allocatedPorts.TryRemove(instanceId, out _))
        {
            _portAllocator.Release(instanceId);
        }
    }

    private void SetState(string instanceId, InstanceState state)
    {
        InstanceState previous = GetState(instanceId);
        if (previous == state)
        {
            return;
        }

        _states[instanceId] = state;
        StateChanged?.Invoke(this, new InstanceStateChangedEventArgs(instanceId, previous, state));
    }

    private static XBearException Wrap(Exception ex, string instanceId, string stage)
    {
        string defaultRemediation = $"请查看实例 {instanceId} 的诊断日志定位原因，处理后重试该操作。";

        // 已经是项目内异常时保留其分类，仅补齐阶段上下文与处置建议。
        if (ex is XBearException existing)
        {
            return new XBearException(
                existing.Category,
                $"实例 {instanceId} {stage}：{existing.Message}",
                string.IsNullOrWhiteSpace(existing.Remediation) ? defaultRemediation : existing.Remediation,
                existing);
        }

        return new XBearException(
            ErrorCategory.Internal,
            $"实例 {instanceId} {stage}：{ex.Message}",
            defaultRemediation,
            ex);
    }

    private static async Task SafeDisposeAsync(QemuProcessHandle handle)
    {
        try
        {
            await handle.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 启动失败路径上终止进程本身可能再抛，不能让它掩盖原始失败原因。
        }

        try
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}