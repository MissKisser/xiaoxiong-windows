using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Adb;
using XBear.Core.Diagnostics;
using XBear.Core.Input;
using XBear.Core.Qmp;

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
/// 实例生命周期编排。负责启动序列的状态推进、overlay 准备、端口分配与进程拉起、
/// 调试通路就绪后的净桌面初始化，以及停止序列的进程终止与端口释放。
/// 启动任一环节失败时统一置为 <see cref="InstanceState.Faulted"/>、释放已占用端口并抛出带处置建议的异常，
/// 保证失败后可直接重试启动；用户主动取消不属于失败，端口同样释放但状态回到 <see cref="InstanceState.Stopped"/>。
/// </summary>
public sealed class InstanceManager
{
    /// <summary>调试通路就绪探测的默认等待上限，覆盖实测的冷启动量级。</summary>
    public static readonly TimeSpan DefaultDebugChannelProbeTimeout = TimeSpan.FromSeconds(120);

    /// <summary>调试通路就绪探测的重试间隔。</summary>
    public static readonly TimeSpan DefaultDebugChannelProbeInterval = TimeSpan.FromSeconds(2);

    /// <summary>单次 adb 连接尝试的等待上限。</summary>
    public static readonly TimeSpan DefaultDebugChannelAttemptTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 停止前 guest 刷盘的等待上限。
    /// guest 内模块文件写入后若 QEMU 直接退出而页缓存未刷，文件会退化为 0 字节，
    /// 因此停止必须先让 guest 执行一次 sync；但刷盘属尽力而为，超时或失败都不阻塞停止。
    /// 刷盘只在该实例启动时已确认调试通路就绪时发起，未确认过的实例直接跳过。
    /// </summary>
    public static readonly TimeSpan DefaultShutdownFlushTimeout = TimeSpan.FromSeconds(3);

    /// <summary>停止前向 guest 下发的刷盘命令。</summary>
    public const string ShutdownFlushCommand = "sync";

    /// <summary>
    /// 净桌面初始化的命令序列，按顺序下发以把刚启动的实例整理成可驱动的干净桌面。
    /// 序列末尾另追加一次刷盘命令，让本步骤写入的安全设置落盘。
    /// </summary>
    public static readonly IReadOnlyList<string> CleanDesktopCommands =
    [
        // 解除锁屏对上层窗口合成的阻断：先持久化禁用系统锁屏，再解除当前 Keyguard 展示态。
        // 锁屏存在时上层窗口的可见性判定恒为不可见，输入事件无法到达应用窗口。
        "settings put secure lockscreen.disabled 1",
        "locksettings set-disabled true",
        "wm dismiss-keyguard",

        // 收回顶部下滑手势的焦点占用：通知栏一旦展开会持续抢占输入焦点。
        "cmd statusbar collapse",

        // 关闭首启时系统弹出的桌面选择器叠层，避免其覆盖在标准桌面之上拦截后续操作。
        "input keyevent 4",
    ];

    /// <summary>净桌面初始化序列执行完毕后用于让安全设置落盘的刷盘命令。</summary>
    public const string CleanDesktopFlushCommand = "sync";

    private readonly IInstanceRepository _repository;
    private readonly IPortAllocator _portAllocator;
    private readonly IQemuArgBuilder _argBuilder;
    private readonly IQemuLauncher _launcher;
    private readonly IQcow2Manager _qcow2Manager;
    private readonly string _imagesRoot;
    private readonly string _instancesRoot;
    private readonly Spec.SpecValidator? _specValidator;
    private readonly IImageCatalog? _imageCatalog;
    private readonly Func<IQmpClient> _qmpClientFactory;
    private readonly Func<IAdbClient> _adbClientFactory;
    private readonly IMetricsRecorder _metricsRecorder;
    private readonly IDensityAdvisor _densityAdvisor;
    private readonly TimeSpan _debugChannelProbeTimeout;
    private readonly TimeSpan _debugChannelProbeInterval;
    private readonly TimeSpan _debugChannelAttemptTimeout;
    private readonly TimeSpan _shutdownFlushTimeout;
    private readonly CleanDesktopOptions _cleanDesktopOptions;

    private readonly ConcurrentDictionary<string, InstanceState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AllocatedPorts> _allocatedPorts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _debugChannelReady = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, QemuProcessHandle> _handles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProbingInputChannel> _inputChannels = new(StringComparer.Ordinal);
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
    /// <param name="specValidator">
    /// 实例配置的 Schema 校验器，为 null 时跳过启动前校验。
    /// </param>
    /// <param name="imageCatalog">
    /// 镜像清单目录，为 null 时按镜像未知处理，不下发镜像保真度相关引导参数。
    /// </param>
    /// <param name="qmpClientFactory">
    /// QMP 客户端工厂，缺省时按实例端口新建客户端。
    /// </param>
    /// <param name="adbClientFactory">
    /// adb 客户端工厂，缺省时按实例端口新建客户端。
    /// </param>
    /// <param name="metricsRecorder">
    /// 指标采集器，缺省时按实例根目录新建采集器并接入宿主内存采样。
    /// </param>
    /// <param name="densityAdvisor">
    /// 多开密度顾问，缺省时按系统宿主内存与性能基线契约新建。
    /// </param>
    /// <param name="debugChannelProbeTimeout">
    /// 调试通路就绪探测的等待上限。为 <see cref="TimeSpan.Zero"/> 时不启用探测，
    /// 此时 metrics.json 如实把就绪时间点留空，组合根按生产配置显式传入。
    /// </param>
    /// <param name="debugChannelProbeInterval">调试通路就绪探测的重试间隔。</param>
    /// <param name="debugChannelAttemptTimeout">单次 adb 连接尝试的等待上限。</param>
    /// <param name="shutdownFlushTimeout">
    /// 停止前 guest 刷盘的等待上限。为 <see cref="TimeSpan.Zero"/> 时不刷盘，
    /// 停止流程与该配置无关的调用点保持既有行为。
    /// </param>
    /// <param name="cleanDesktopOptions">
    /// 净桌面初始化配置选项。为 null 时使用默认配置（默认启用，等待上限 5 秒）。
    /// </param>
    public InstanceManager(
        IInstanceRepository repository,
        IPortAllocator portAllocator,
        IQemuArgBuilder argBuilder,
        IQemuLauncher launcher,
        IQcow2Manager qcow2Manager,
        string imagesRoot,
        string instancesRoot,
        Spec.SpecValidator? specValidator = null,
        IImageCatalog? imageCatalog = null,
        Func<IQmpClient>? qmpClientFactory = null,
        Func<IAdbClient>? adbClientFactory = null,
        IMetricsRecorder? metricsRecorder = null,
        IDensityAdvisor? densityAdvisor = null,
        TimeSpan? debugChannelProbeTimeout = null,
        TimeSpan? debugChannelProbeInterval = null,
        TimeSpan? debugChannelAttemptTimeout = null,
        TimeSpan? shutdownFlushTimeout = null,
        CleanDesktopOptions? cleanDesktopOptions = null)
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
        _specValidator = specValidator;
        _imageCatalog = imageCatalog;
        _qmpClientFactory = qmpClientFactory ?? (static () => new QmpClient());
        _adbClientFactory = adbClientFactory ?? (static () => new AdbClient());
        _metricsRecorder = metricsRecorder ??
            new MetricsRecorder(instancesRoot, hostMemorySampler: new WindowsHostMemoryDetector());
        _densityAdvisor = densityAdvisor ?? new InstanceDensityAdvisor();
        _debugChannelProbeTimeout = debugChannelProbeTimeout ?? TimeSpan.Zero;
        _debugChannelProbeInterval = debugChannelProbeInterval > TimeSpan.Zero
            ? debugChannelProbeInterval.Value
            : DefaultDebugChannelProbeInterval;
        _debugChannelAttemptTimeout = debugChannelAttemptTimeout > TimeSpan.Zero
            ? debugChannelAttemptTimeout.Value
            : DefaultDebugChannelAttemptTimeout;
        // TimeSpan.Zero 是调用方显式关闭刷盘的显式取值，其余未配置时回落到默认上限。
        _shutdownFlushTimeout = shutdownFlushTimeout == TimeSpan.Zero
            ? TimeSpan.Zero
            : shutdownFlushTimeout > TimeSpan.Zero
                ? shutdownFlushTimeout.Value
                : DefaultShutdownFlushTimeout;
        _cleanDesktopOptions = cleanDesktopOptions ?? new CleanDesktopOptions();
    }

    /// <summary>净桌面初始化配置选项。</summary>
    public CleanDesktopOptions CleanDesktopOptions => _cleanDesktopOptions;

    /// <summary>指标采集器。</summary>
    public IMetricsRecorder MetricsRecorder => _metricsRecorder;

    /// <summary>多开密度顾问。</summary>
    public IDensityAdvisor DensityAdvisor => _densityAdvisor;

    /// <summary>获取实例专属数据目录路径。</summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>数据目录路径。</returns>
    public string GetInstanceDataDirectory(string instanceId) =>
        Path.Combine(_instancesRoot, instanceId);

    /// <summary>获取实例 metrics.json 文件绝对路径。</summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>文件绝对路径。</returns>
    public string GetMetricsFilePath(string instanceId) =>
        _metricsRecorder.GetMetricsFilePath(instanceId);

    /// <summary>
    /// 获取当前宿主对应的建议并行实例上限与档位建议。
    /// </summary>
    /// <param name="currentCount">当前实例数，若为 null 则以当前运行中实例数计算。</param>
    /// <param name="instanceTerm">用于提示文案的实例概念中文词，默认“实例”。</param>
    /// <returns>密度建议信息。</returns>
    public DensityAdvice GetDensityAdvice(int? currentCount = null, string instanceTerm = "实例")
    {
        int count = currentCount ?? _states.Values.Count(s => s == InstanceState.Running);
        return _densityAdvisor.Evaluate(count, instanceTerm);
    }

    /// <summary>
    /// 比对基线延迟与多实例并发实测延迟，评估延迟劣化是否在容许范围内。
    /// </summary>
    /// <param name="baselineLatency">单实例基线延迟。</param>
    /// <param name="measuredLatency">多实例并发实测延迟。</param>
    /// <param name="regression">指定的劣化上限契约，缺省时从基线配置读取。</param>
    /// <returns>劣化评估结论。</returns>
    public RegressionEvaluation EvaluateRegression(
        double baselineLatency,
        double measuredLatency,
        Spec.DensityRegression? regression = null) =>
        _densityAdvisor.EvaluateRegression(baselineLatency, measuredLatency, regression);

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
    /// 停止实例。先让 guest 尽力刷盘，再终止进程，最后释放端口并置为已停止。
    /// 刷盘只在该实例启动时已确认调试通路就绪时发起，且严格限时：
    /// 未确认就绪、未连通或超时都只跳过，不影响停止结果；
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

    /// <summary>
    /// 探测实例当前可用的输入通路。实例未运行时不发起连接，直接返回尚未探测的结论；
    /// 已运行时按该实例已分配的 QMP 与 adb 宿主端口建立探测式通道，并返回如实结论。
    /// 探测所需的 QMP 与 adb 客户端由本方法按实例端口创建，随通道一并释放，不跨实例复用。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>输入通路探测结论，含各通路失败原因。</returns>
    public async Task<InputProbeResult> ProbeInputChannelAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        // 未进入运行态就没有可连的端口，如实报告尚未探测，不伪造通路可用。
        if (GetState(instanceId) is not InstanceState.Running)
        {
            return new InputProbeResult(InputChannelKind.Unknown);
        }

        if (!_allocatedPorts.TryGetValue(instanceId, out AllocatedPorts? ports))
        {
            return new InputProbeResult(
                InputChannelKind.Unavailable,
                "实例已处于运行态，但未查到已分配的宿主端口。",
                "请停止后重新启动实例，使端口重新分配后再探测。");
        }

        ProbingInputChannel channel = GetOrCreateInputChannel(instanceId, ports);

        try
        {
            return await channel.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消按取消原样上抛，不谎报为通路不可用。
            throw;
        }
        catch (Exception ex)
        {
            // 探测过程本身已把各通路失败原因并入结论，此处只兜住通道层面的意外异常。
            return new InputProbeResult(
                InputChannelKind.Unavailable,
                "输入通道探测未能完成。",
                ex.Message);
        }
    }

    /// <summary>
    /// 取该实例的探测式输入通道，首次调用时按已分配端口创建并缓存，
    /// 使同一实例的跨次探测能累计原生通路的连续失败次数。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="ports">该实例已分配的宿主端口组。</param>
    /// <returns>该实例专属的探测式输入通道。</returns>
    private ProbingInputChannel GetOrCreateInputChannel(string instanceId, AllocatedPorts ports)
    {
        if (_inputChannels.TryGetValue(instanceId, out ProbingInputChannel? existing))
        {
            return existing;
        }

        var channel = new ProbingInputChannel(
            _qmpClientFactory(),
            _adbClientFactory(),
            new InputChannelOptions
            {
                QmpPort = ports.Qmp,
                AdbPort = ports.Adb,
            });

        return _inputChannels.GetOrAdd(instanceId, channel);
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
        _metricsRecorder.OnStarting(instanceId);

        AllocatedPorts? allocated = null;
        QemuProcessHandle? handle = null;

        try
        {
            Spec.InstanceSpec spec = await LoadSpecAsync(instanceId, cancellationToken).ConfigureAwait(false);
            Spec.ImageSpec? image = ResolveImageSpec(spec.ImageRef);
            string baseImagePath = ResolveBaseImagePath(spec.ImageRef);
            string overlayPath = await EnsureOverlayAsync(instanceId, baseImagePath, spec.ImageRef, cancellationToken)
                .ConfigureAwait(false);

            allocated = await _portAllocator.AcquireAsync(instanceId, cancellationToken).ConfigureAwait(false);
            _allocatedPorts[instanceId] = allocated;

            IReadOnlyList<string> arguments =
                _argBuilder.BuildStartArguments(spec, image, overlayPath, allocated);
            handle = await _launcher.StartAsync(spec, image, overlayPath, allocated, cancellationToken)
                .ConfigureAwait(false);
            _handles[instanceId] = handle;

            // 进程拉起与调试通路就绪是两个量级相差一个数量级以上的阶段，
            // 指标按阶段分开记录，不把进程创建耗时冒充冷启动耗时。
            _metricsRecorder.OnProcessSpawned(instanceId, handle);
            SetState(instanceId, InstanceState.Running);

            string? unavailableReason = await WaitForDebugChannelAsync(allocated.Adb, cancellationToken)
                .ConfigureAwait(false);
            if (unavailableReason is null)
            {
                // 就绪结论既进指标，也作为停止前刷盘的准入依据：只有确认过 adbd 通的实例才值得再连一次。
                _debugChannelReady[instanceId] = true;
                _metricsRecorder.OnDebugChannelReady(instanceId);

                await InitializeCleanDesktopAsync(allocated.Adb, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _metricsRecorder.OnDebugChannelUnavailable(instanceId, unavailableReason);
            }
        }
        catch (OperationCanceledException)
        {
            _metricsRecorder.OnStartupFailed(instanceId);

            // 用户主动取消不是故障：同样回收进程与端口，但状态回到已停止，取消异常原样上抛。
            if (handle is not null)
            {
                await SafeDisposeAsync(handle).ConfigureAwait(false);
            }

            await ReleaseInputChannelAsync(instanceId).ConfigureAwait(false);
            ReleasePorts(instanceId);

            SetState(instanceId, InstanceState.Stopped);
            throw;
        }
        catch (Exception ex)
        {
            _metricsRecorder.OnStartupFailed(instanceId);

            // 回收顺序：先杀进程再放端口，避免端口被残留进程继续占用。
            if (handle is not null)
            {
                await SafeDisposeAsync(handle).ConfigureAwait(false);
            }

            await ReleaseInputChannelAsync(instanceId).ConfigureAwait(false);
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
            try
            {
                await _metricsRecorder.StopAndPersistAsync(instanceId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 指标落盘异常不阻止进程正常终止
            }

            if (_handles.TryRemove(instanceId, out QemuProcessHandle? handle))
            {
                // 刷盘必须排在 QMP quit 之前：QEMU 一旦退出，guest 就没有机会把页缓存写回磁盘，
                // 刚写入的模块文件会退化为 0 字节。刷盘尽力而为，绝不影响后续的进程终止。
                await FlushGuestFileSystemAsync(instanceId, cancellationToken).ConfigureAwait(false);

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
            // 输入通道持有指向这些端口的连接，同样在此一并释放。
            await ReleaseInputChannelAsync(instanceId).ConfigureAwait(false);
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

        ValidateAgainstSchema(instanceId, spec);

        return spec;
    }

    /// <summary>
    /// 启动前用共享契约校验实例配置。把已解析的配置重新序列化后送 Schema 校验，
    /// 能抓到枚举取值越界与字段丢失。未装配校验器时跳过，保持既有调用点行为不变。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="spec">待校验的实例配置。</param>
    /// <exception cref="XBearException">配置不符合共享契约时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private void ValidateAgainstSchema(string instanceId, Spec.InstanceSpec spec)
    {
        if (_specValidator is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(spec, Spec.SpecLoader.SerializerOptions);
        _specValidator.ValidateInstance(json).EnsureValid();
    }

    private string ResolveBaseImagePath(string imageRef) =>
        Path.IsPathRooted(imageRef)
            ? imageRef
            : Path.Combine(_imagesRoot, imageRef + ".qcow2");

    /// <summary>
    /// 按镜像引用解析镜像清单。目录缺失或引用对不上时返回 null，
    /// 调用方据此按镜像未知处理，不臆测任何保真度结论。
    /// </summary>
    /// <param name="imageRef">实例配置中的镜像引用。</param>
    /// <returns>镜像清单，未登记时返回 null。</returns>
    private Spec.ImageSpec? ResolveImageSpec(string imageRef)
        => _imageCatalog is null ? null : _imageCatalog.Find(imageRef);

    /// <summary>
    /// 探测调试通路何时就绪。冷启动耗时以 adb 通路可连接为终点，
    /// 未启用探测、探测超时或探测被取消时如实返回原因，由调用方把就绪时间点留空。
    /// </summary>
    /// <param name="adbPort">该实例分配到的宿主 adb 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>就绪时返回 null，否则返回未就绪的原因。</returns>
    private async Task<string?> WaitForDebugChannelAsync(int adbPort, CancellationToken cancellationToken)
    {
        if (_debugChannelProbeTimeout <= TimeSpan.Zero)
        {
            return "本轮启动未配置调试通路就绪探测，冷启动耗时未采集。";
        }

        var elapsed = Stopwatch.StartNew();
        int attempts = 0;
        while (true)
        {
            attempts++;
            if (await TryDebugChannelHandshakeAsync(adbPort, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (elapsed.Elapsed >= _debugChannelProbeTimeout)
            {
                return $"调试通路在 {_debugChannelProbeTimeout.TotalSeconds:F0} 秒内未就绪，共尝试 {attempts} 次。";
            }

            try
            {
                await Task.Delay(_debugChannelProbeInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 探测被取消时进程已经在运行，如实记为未就绪，不把已在跑的实例拆掉。
                return "调试通路就绪探测被取消，冷启动耗时未采集。";
            }
        }
    }

    /// <summary>
    /// 尝试一次 adb 连接握手。hostfwd 在宿主侧立即接受连接，
    /// 因此每次尝试都必须带等待上限，adbd 未就绪时按失败处理而不是挂死。
    /// </summary>
    /// <param name="adbPort">该实例分配到的宿主 adb 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>握手成功时返回 true。</returns>
    private async Task<bool> TryDebugChannelHandshakeAsync(int adbPort, CancellationToken cancellationToken)
    {
        try
        {
            await using IAdbClient client = _adbClientFactory();
            await client.ConnectAsync(adbPort, cancellationToken, _debugChannelAttemptTimeout)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 在实例退出前尽力让 guest 把页缓存刷到磁盘。guest 内刚写入的模块文件若未刷盘，
    /// QEMU 直接退出后会成为 0 字节，因此停止流程必须先下发一次 sync。
    /// 刷盘只在该实例启动时已确认调试通路就绪时才发起：没确认过 adbd 通的实例连上去也是徒劳，
    /// 白等一个等待上限。该步骤同样严格限时，未配置刷盘、未确认就绪、adbd 掉线、
    /// 命令失败或超时都只跳过，不向上抛也不改变实例状态——刷盘失败最多丢失最近一次写入，
    /// 而停止失败会让实例无法回收，代价远大于刷盘收益。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示刷盘尝试已结束的异步任务，完成不代表刷盘一定成功。</returns>
    private async Task FlushGuestFileSystemAsync(string instanceId, CancellationToken cancellationToken)
    {
        if (_shutdownFlushTimeout <= TimeSpan.Zero
            || !_debugChannelReady.TryGetValue(instanceId, out bool ready)
            || !ready
            || !_allocatedPorts.TryGetValue(instanceId, out AllocatedPorts? ports))
        {
            return;
        }

        try
        {
            // 连接与命令共用同一个限时令牌，guest 侧 sync 卡住也不会拖住停止流程。
            using var flushSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            flushSource.CancelAfter(_shutdownFlushTimeout);

            // 客户端按本次刷盘单独创建并随方法一并释放，不跨调用复用也不留下悬挂连接。
            await using IAdbClient client = _adbClientFactory();
            await client.ConnectAsync(ports.Adb, flushSource.Token, _shutdownFlushTimeout).ConfigureAwait(false);
            await client.ShellAsync(ShutdownFlushCommand, flushSource.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 刷盘是尽力而为：任何失败都不阻塞停止，也不改变实例状态。
        }
    }

    /// <summary>
    /// 在调试通路确认就绪后把实例整理成干净可驱动的桌面。
    /// 按序下发 <see cref="CleanDesktopCommands"/> 并以一次刷盘收尾，让本步骤写入的安全设置落盘。
    /// 该步骤与停止前刷盘同理属尽力而为：整体严格限时，任何单条命令失败都只记录不外抛，
    /// 既不阻断启动也不改变实例状态——桌面不干净最多影响后续交互观感，
    /// 而启动失败会让实例直接不可用，代价远大于本步骤的收益。
    /// </summary>
    /// <param name="adbPort">该实例分配到的宿主 adb 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示初始化尝试已结束的异步任务，完成不代表每条命令都执行成功。</returns>
    private async Task InitializeCleanDesktopAsync(int adbPort, CancellationToken cancellationToken)
    {
        if (!_cleanDesktopOptions.Enabled || _cleanDesktopOptions.Timeout <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            // 连接与全部命令共用同一个限时令牌，guest 侧任一条命令卡住都不会拖住启动流程。
            using var cleanSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cleanSource.CancelAfter(_cleanDesktopOptions.Timeout);
            CancellationToken token = cleanSource.Token;

            foreach (string command in CleanDesktopCommands)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                await RunCleanDesktopCommandAsync(adbPort, command, token, cancellationToken).ConfigureAwait(false);
            }

            if (!token.IsCancellationRequested)
            {
                await RunCleanDesktopCommandAsync(adbPort, CleanDesktopFlushCommand, token, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 净桌面初始化是尽力而为：整体异常不阻断启动，也不改变实例状态。
        }
    }

    /// <summary>
    /// 下发单条净桌面 shell 命令。
    /// adbd 在一条 shell 命令跑完后即关闭该连接，因此每条命令单独建立并释放一次连接，
    /// 与宿主 adb 逐条执行命令的行为一致，也避免后续命令落在已被关闭的连接上。
    /// </summary>
    /// <param name="adbPort">该实例分配到的宿主 adb 端口。</param>
    /// <param name="command">待下发的命令与参数。</param>
    /// <param name="token">结合整体等待上限的执行令牌。</param>
    /// <param name="callerToken">调用方的取消令牌，用于区分主动取消与等待上限到期。</param>
    /// <returns>表示该条命令尝试已结束的异步任务。</returns>
    private async Task RunCleanDesktopCommandAsync(
        int adbPort,
        string command,
        CancellationToken token,
        CancellationToken callerToken)
    {
        try
        {
            await using IAdbClient client = _adbClientFactory();
            await client.ConnectAsync(adbPort, token, _cleanDesktopOptions.Timeout).ConfigureAwait(false);
            await client.ShellAsync(command, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 单条命令失败不阻断后续命令：命令之间彼此独立，缺一条只是少解决一个叠层问题。
        }
    }

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
        _debugChannelReady.TryRemove(instanceId, out _);

        if (_allocatedPorts.TryRemove(instanceId, out _))
        {
            _portAllocator.Release(instanceId);
        }
    }

    /// <summary>
    /// 释放该实例的探测式输入通道。通道持有按实例端口建立的 QMP 与 adb 连接，
    /// 必须随端口一并释放，否则会留下指向已回收端口的悬挂连接。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>异步任务。</returns>
    private async Task ReleaseInputChannelAsync(string instanceId)
    {
        if (!_inputChannels.TryRemove(instanceId, out ProbingInputChannel? channel))
        {
            return;
        }

        try
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放连接失败不应影响端口回收与状态推进，实例已经停下来了。
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