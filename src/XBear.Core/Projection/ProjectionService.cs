using System.Collections.Concurrent;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Qmp;
using XBear.Core.Spec;

namespace XBear.Core.Projection;

/// <summary>画面到达事件参数。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="SessionId">投屏会话标识。</param>
/// <param name="Sequence">画面序号。</param>
/// <param name="Geometry">当前呈现画面的分辨率。</param>
/// <param name="DecodedAt">解码完成时刻。</param>
public sealed record ProjectionFrameArrivedEventArgs(
    string InstanceId,
    string SessionId,
    long Sequence,
    ScreenGeometry Geometry,
    DateTimeOffset DecodedAt);

/// <summary>分辨率变化事件参数。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="SessionId">投屏会话标识。</param>
/// <param name="Previous">变化前的分辨率。</param>
/// <param name="Current">变化后的分辨率。</param>
public sealed record ProjectionResolutionChangedEventArgs(
    string InstanceId,
    string SessionId,
    ScreenGeometry Previous,
    ScreenGeometry Current);

/// <summary>投屏会话状态变化事件参数。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="SessionId">投屏会话标识。</param>
/// <param name="PreviousState">变化前的状态。</param>
/// <param name="CurrentState">变化后的状态。</param>
/// <param name="Error">异常导致会话终止时的异常，正常推进时为 null。</param>
public sealed record ProjectionSessionStateChangedEventArgs(
    string InstanceId,
    string SessionId,
    ProjectionState PreviousState,
    ProjectionState CurrentState,
    Exception? Error = null);

/// <summary>
/// 投屏服务接口。负责按运行中实例的端口建立投屏会话、接收画面帧、
/// 统计实测帧率，并提供输入注入能力与画面观察接口。
/// </summary>
public interface IProjectionService : IAsyncDisposable
{
    /// <summary>画面到达事件。</summary>
    event EventHandler<ProjectionFrameArrivedEventArgs>? FrameArrived;

    /// <summary>分辨率变化事件。</summary>
    event EventHandler<ProjectionResolutionChangedEventArgs>? ResolutionChanged;

    /// <summary>投屏会话状态变化事件。</summary>
    event EventHandler<ProjectionSessionStateChangedEventArgs>? SessionStateChanged;

    /// <summary>
    /// 为指定运行中实例启动投屏会话。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="targetFps">目标帧率，单位 fps，默认 30。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已激活的投屏会话。</returns>
    Task<ProjectionSession> StartAsync(
        string instanceId,
        int? targetFps = 30,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 终止指定实例的投屏会话并清理资源。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task StopAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>获取指定实例当前的投屏会话。</summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>投屏会话，不存在或未启动时为 null。</returns>
    ProjectionSession? GetSession(string instanceId);

    /// <summary>
    /// 获取指定实例最新一帧画面的不可变快照。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>最新画面快照，尚未取到首帧或无活动会话时为 null。</returns>
    ProjectionFrame? GetLatestFrame(string instanceId);

    /// <summary>
    /// 将最新一帧画面的像素复制到调用方给定的数组中。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="destination">目标数组。</param>
    /// <returns>画面快照，尚未取到首帧或无活动会话时为 null。</returns>
    ProjectionFrame? GetLatestFrameInto(string instanceId, byte[] destination);

    /// <summary>获取指定实例当前的实测帧率。</summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>实测帧率，未测量或无活动会话时为 null。</returns>
    double? GetMeasuredFps(string instanceId);

    /// <summary>获取指定实例当前的输入注入入口。</summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>输入注入入口，无活动会话时为 null。</returns>
    ProjectionInputInjector? GetInputInjector(string instanceId);
}

/// <summary>
/// 投屏服务配置参数。
/// </summary>
public sealed class ProjectionServiceOptions
{
    /// <summary>帧率实测滚动窗口，默认 2 秒。</summary>
    public TimeSpan? FrameRateWindow { get; init; }

    /// <summary>输入注入单次超时。</summary>
    public TimeSpan? InputDispatchTimeout { get; init; }

    /// <summary>VNC 连接与取帧超时。</summary>
    public TimeSpan? VncTimeout { get; init; }

    /// <summary>指针坐标域上界，默认 virtio-tablet 的 32767。</summary>
    public int PointerMax { get; init; } = ProjectionInputInjector.DefaultPointerMax;
}

/// <summary>
/// 投屏服务核心实现。为运行中的实例管理投屏会话生命周期，
/// 并发安全地提供双缓冲画面快照、实测帧率和经 QMP 的输入注入。
/// </summary>
public sealed class ProjectionService : IProjectionService
{
    private sealed class InstanceProjectionContext : IAsyncDisposable
    {
        public string InstanceId { get; }
        public ProjectionSession Session { get; }
        public ProjectionFrameStore Frames { get; }
        public FrameRateMeter Meter { get; }
        public VncClient Vnc { get; }
        public IQmpClient Qmp { get; }
        public bool OwnsQmp { get; }
        public ProjectionInputInjector Injector { get; }
        public CancellationTokenSource LoopCancellation { get; } = new();
        public Task? LoopTask { get; set; }
        public Exception? TerminalError { get; set; }

        public InstanceProjectionContext(
            string instanceId,
            ProjectionSession session,
            ProjectionFrameStore frames,
            FrameRateMeter meter,
            VncClient vnc,
            IQmpClient qmp,
            bool ownsQmp,
            ProjectionInputInjector injector)
        {
            InstanceId = instanceId;
            Session = session;
            Frames = frames;
            Meter = meter;
            Vnc = vnc;
            Qmp = qmp;
            OwnsQmp = ownsQmp;
            Injector = injector;
        }

        public async ValueTask DisposeAsync()
        {
            LoopCancellation.Cancel();

            if (LoopTask is { } task)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception)
                {
                }
            }

            await DisposeResourcesAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeResourcesAsync()
        {
            await Vnc.DisposeAsync().ConfigureAwait(false);

            if (OwnsQmp)
            {
                await Qmp.DisposeAsync().ConfigureAwait(false);
            }

            LoopCancellation.Dispose();
        }
    }

    private readonly Func<string, AllocatedPorts?> _portResolver;
    private readonly Func<string, InstanceState>? _stateResolver;
    private readonly Func<IQmpClient> _qmpClientFactory;
    private readonly Func<VncClientOptions?, VncClient> _vncClientFactory;
    private readonly ProjectionServiceOptions _options;

    private readonly ConcurrentDictionary<string, InstanceProjectionContext> _contexts =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    /// <summary>画面到达事件。</summary>
    public event EventHandler<ProjectionFrameArrivedEventArgs>? FrameArrived;

    /// <summary>分辨率变化事件。</summary>
    public event EventHandler<ProjectionResolutionChangedEventArgs>? ResolutionChanged;

    /// <summary>投屏会话状态变化事件。</summary>
    public event EventHandler<ProjectionSessionStateChangedEventArgs>? SessionStateChanged;

    /// <summary>
    /// 初始化投屏服务。
    /// </summary>
    /// <param name="portResolver">按实例标识获取已分配端口的委托。</param>
    /// <param name="stateResolver">按实例标识查询运行态的委托，为空时不预检运行态。</param>
    /// <param name="qmpClientFactory">QMP 客户端工厂，为空时使用默认构造。</param>
    /// <param name="vncClientFactory">VNC 客户端工厂，为空时使用默认构造。</param>
    /// <param name="options">服务配置参数，为空时使用默认配置。</param>
    public ProjectionService(
        Func<string, AllocatedPorts?> portResolver,
        Func<string, InstanceState>? stateResolver = null,
        Func<IQmpClient>? qmpClientFactory = null,
        Func<VncClientOptions?, VncClient>? vncClientFactory = null,
        ProjectionServiceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(portResolver);

        _portResolver = portResolver;
        _stateResolver = stateResolver;
        _qmpClientFactory = qmpClientFactory ?? (static () => new QmpClient());
        _vncClientFactory = vncClientFactory ?? (static opts => new VncClient(opts));
        _options = options ?? new ProjectionServiceOptions();
    }

    /// <summary>
    /// 为指定运行中实例建立投屏会话。
    /// 步骤包括：预检运行态、取得已分配端口、连接 QMP 确认输入通道就绪、
    /// 连接 VNC 完成握手、以初始分辨率创建双缓冲帧存储、将会话置为 active、启动后台取帧循环。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="targetFps">目标帧率，单位 fps，默认 30。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已处于 active 态的投屏会话。</returns>
    /// <exception cref="XBearException">
    /// 实例未运行为 <see cref="ErrorCategory.State"/>；
    /// 端口未分配为 <see cref="ErrorCategory.Port"/>；
    /// 连接或协议失败为 <see cref="ErrorCategory.Protocol"/>。
    /// </exception>
    public async Task<ProjectionSession> StartAsync(
        string instanceId,
        int? targetFps = 30,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_contexts.ContainsKey(instanceId))
            {
                throw new XBearException(
                    ErrorCategory.State,
                    $"实例 {instanceId} 已存在进行中的投屏会话，无法重复启动。",
                    "请先停止当前投屏会话后再重新启动。");
            }

            if (_stateResolver is not null && _stateResolver(instanceId) != InstanceState.Running)
            {
                throw new XBearException(
                    ErrorCategory.State,
                    $"实例 {instanceId} 当前未处于运行态，无法启动投屏会话。",
                    "请先启动实例，确认实例处于运行态后再建立投屏。");
            }

            AllocatedPorts? ports = _portResolver(instanceId);
            if (ports is null)
            {
                throw new XBearException(
                    ErrorCategory.Port,
                    $"未找到实例 {instanceId} 所分配的宿主端口。",
                    "请确认实例已正常启动并分配了 VNC 与 QMP 端口。");
            }

            IQmpClient qmp = _qmpClientFactory();
            bool qmpConnected = false;
            try
            {
                await qmp.ConnectAsync(ports.Qmp, cancellationToken).ConfigureAwait(false);
                qmpConnected = true;
            }
            catch (Exception ex)
            {
                await qmp.DisposeAsync().ConfigureAwait(false);
                throw new XBearException(
                    ErrorCategory.Protocol,
                    $"连接实例 {instanceId} 的 QMP 通道失败：{ex.Message}",
                    "请确认实例已启动并且 QMP 端口可访问。",
                    ex);
            }

            var vncOptions = new VncClientOptions
            {
                Timeout = _options.VncTimeout,
                Shared = true,
            };
            VncClient vnc = _vncClientFactory(vncOptions);

            var tempFrames = new ProjectionFrameStore(100, 100);
            VncServerInfo serverInfo;
            try
            {
                serverInfo = await vnc.ConnectAsync(ports.Vnc, tempFrames, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                await vnc.DisposeAsync().ConfigureAwait(false);
                if (qmpConnected)
                {
                    await qmp.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }

            ScreenGeometry initialGeometry = serverInfo.Geometry;
            var frames = new ProjectionFrameStore(initialGeometry.Width, initialGeometry.Height);

            string sessionId = ProjectionSession.GenerateSessionId(instanceId);
            var session = new ProjectionSession(
                sessionId,
                instanceId,
                initialGeometry.Width,
                initialGeometry.Height,
                targetFps,
                ProjectionPointerDomain.Absolute,
                _options.PointerMax,
                "linux-evdev");

            var meter = new FrameRateMeter(_options.FrameRateWindow);

            var injector = new ProjectionInputInjector(
                qmp,
                () => frames.Geometry,
                _options.PointerMax,
                deviceName: null,
                dispatchTimeout: _options.InputDispatchTimeout);

            var context = new InstanceProjectionContext(
                instanceId,
                session,
                frames,
                meter,
                vnc,
                qmp,
                ownsQmp: true,
                injector);

            vnc.FrameDecoded += (sender, args) =>
            {
                meter.Record(args.DecodedAt);
                session.UpdateMeasuredFps(meter.Measured);

                FrameArrived?.Invoke(
                    this,
                    new ProjectionFrameArrivedEventArgs(
                        instanceId,
                        sessionId,
                        args.Sequence,
                        args.Geometry,
                        args.DecodedAt));
            };

            vnc.ResolutionChanged += (sender, args) =>
            {
                session.UpdateResolution(args.Current.Width, args.Current.Height);
                ResolutionChanged?.Invoke(
                    this,
                    new ProjectionResolutionChangedEventArgs(
                        instanceId,
                        sessionId,
                        args.Previous,
                        args.Current));
            };

            session.Activate(DateTimeOffset.UtcNow);

            _contexts[instanceId] = context;

            context.LoopTask = Task.Run(
                () => RunFrameLoopWorkerAsync(context),
                CancellationToken.None);

            SessionStateChanged?.Invoke(
                this,
                new ProjectionSessionStateChangedEventArgs(
                    instanceId,
                    sessionId,
                    ProjectionState.Pending,
                    ProjectionState.Active));

            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 终止指定实例的投屏会话。将状态推进为 stopped，清理 VNC 与 QMP 连接。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task StopAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        InstanceProjectionContext? context;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _contexts.TryRemove(instanceId, out context);
        }
        finally
        {
            _gate.Release();
        }

        if (context is null)
        {
            return;
        }

        ProjectionState previousState = context.Session.State;
        if (previousState != ProjectionState.Stopped)
        {
            context.Session.Stop(DateTimeOffset.UtcNow);
        }

        await context.DisposeAsync().ConfigureAwait(false);

        SessionStateChanged?.Invoke(
            this,
            new ProjectionSessionStateChangedEventArgs(
                instanceId,
                context.Session.Id,
                previousState,
                ProjectionState.Stopped,
                context.TerminalError));
    }

    /// <inheritdoc />
    public ProjectionSession? GetSession(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return _contexts.TryGetValue(instanceId, out InstanceProjectionContext? context)
            ? context.Session
            : null;
    }

    /// <inheritdoc />
    public ProjectionFrame? GetLatestFrame(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return _contexts.TryGetValue(instanceId, out InstanceProjectionContext? context)
            ? context.Frames.CaptureLatest()
            : null;
    }

    /// <inheritdoc />
    public ProjectionFrame? GetLatestFrameInto(string instanceId, byte[] destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return _contexts.TryGetValue(instanceId, out InstanceProjectionContext? context)
            ? context.Frames.CaptureLatestInto(destination)
            : null;
    }

    /// <inheritdoc />
    public double? GetMeasuredFps(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return _contexts.TryGetValue(instanceId, out InstanceProjectionContext? context)
            ? context.Meter.Measured
            : null;
    }

    /// <inheritdoc />
    public ProjectionInputInjector? GetInputInjector(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return _contexts.TryGetValue(instanceId, out InstanceProjectionContext? context)
            ? context.Injector
            : null;
    }

    /// <summary>
    /// 释放全部进行中的投屏会话并等待清理完成。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        List<InstanceProjectionContext> all;
        try
        {
            all = _contexts.Values.ToList();
            _contexts.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }

        foreach (InstanceProjectionContext context in all)
        {
            if (context.Session.State != ProjectionState.Stopped)
            {
                try
                {
                    context.Session.Stop(DateTimeOffset.UtcNow);
                }
                catch (XBearException)
                {
                }
            }

            await context.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private async Task RunFrameLoopWorkerAsync(InstanceProjectionContext context)
    {
        Exception? caught = null;
        try
        {
            await context.Vnc.RunFrameLoopAsync(context.Frames, context.LoopCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.LoopCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            caught = ex;
            context.TerminalError = ex;
        }

        if (context.LoopCancellation.IsCancellationRequested)
        {
            return;
        }

        _contexts.TryRemove(context.InstanceId, out _);

        ProjectionState prev = context.Session.State;
        if (prev != ProjectionState.Stopped)
        {
            try
            {
                context.Session.Stop(DateTimeOffset.UtcNow);
            }
            catch (XBearException)
            {
            }
        }

        await context.DisposeResourcesAsync().ConfigureAwait(false);

        SessionStateChanged?.Invoke(
            this,
            new ProjectionSessionStateChangedEventArgs(
                context.InstanceId,
                context.Session.Id,
                prev,
                ProjectionState.Stopped,
                caught));
    }
}
