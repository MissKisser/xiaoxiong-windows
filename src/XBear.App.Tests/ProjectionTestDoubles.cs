using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XBear.Core.Abstractions;
using XBear.Core.Projection;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 投屏服务替身。可预置会话、画面与实测帧率，并按需触发画面到达与分辨率变化通知，
/// 使视图模型的编组逻辑与输入投递可在无真实实例时被验证。
/// </summary>
internal sealed class StubProjectionService : IProjectionService
{
    private readonly Dictionary<string, ProjectionSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProjectionFrame> _frames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double?> _measured = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProjectionInputInjector> _injectors = new(StringComparer.Ordinal);

    /// <summary>画面到达通知次数。</summary>
    public int FrameArrivedCount { get; private set; }

    /// <summary>分辨率变化通知次数。</summary>
    public int ResolutionChangedCount { get; private set; }

    /// <summary>会话状态变化通知次数。</summary>
    public int SessionStateChangedCount { get; private set; }

    /// <summary>停止会话调用次数，按实例标识记录。</summary>
    public Dictionary<string, int> StopCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>启动会话调用次数。</summary>
    public int StartCount { get; private set; }

    /// <summary>释放调用次数。</summary>
    public int DisposeCount { get; private set; }

    /// <summary>画面到达事件。</summary>
    public event EventHandler<ProjectionFrameArrivedEventArgs>? FrameArrived;

    /// <summary>分辨率变化事件。</summary>
    public event EventHandler<ProjectionResolutionChangedEventArgs>? ResolutionChanged;

    /// <summary>会话状态变化事件。</summary>
    public event EventHandler<ProjectionSessionStateChangedEventArgs>? SessionStateChanged;

    /// <summary>
    /// 登记一个已激活的会话。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="width">画面宽度。</param>
    /// <param name="height">画面高度。</param>
    /// <param name="injector">该实例的输入注入入口，为空表示会话不承载输入。</param>
    public void Register(
        string instanceId,
        int width,
        int height,
        ProjectionInputInjector? injector = null)
    {
        var session = new ProjectionSession(
            $"proj-{instanceId}-stub0001",
            instanceId,
            width,
            height,
            targetFps: 30);

        session.Activate(DateTimeOffset.UtcNow);
        _sessions[instanceId] = session;

        if (injector is not null)
        {
            _injectors[instanceId] = injector;
        }
    }

    /// <summary>
    /// 写入一帧画面并触发画面到达通知。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="width">画面宽度。</param>
    /// <param name="height">画面高度。</param>
    /// <param name="sequence">画面序号。</param>
    /// <param name="fill">像素填充值。</param>
    public void PublishFrame(string instanceId, int width, int height, long sequence, byte fill = 0x40)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = fill;
            pixels[i + 3] = 0xFF;
        }

        _frames[instanceId] = new ProjectionFrame(width, height, sequence, DateTimeOffset.UtcNow, pixels);
        _measured[instanceId] = sequence > 1 ? sequence / 2d : null;

        FrameArrivedCount++;
        FrameArrived?.Invoke(
            this,
            new ProjectionFrameArrivedEventArgs(
                instanceId,
                "session",
                sequence,
                new ScreenGeometry(width, height),
                DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// 触发分辨率变化通知并同步会话规格。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="previousWidth">变化前宽度。</param>
    /// <param name="previousHeight">变化前高度。</param>
    /// <param name="width">变化后宽度。</param>
    /// <param name="height">变化后高度。</param>
    public void RaiseResolutionChanged(
        string instanceId,
        int previousWidth,
        int previousHeight,
        int width,
        int height)
    {
        if (_sessions.TryGetValue(instanceId, out ProjectionSession? session))
        {
            session.UpdateResolution(width, height);
        }

        ResolutionChangedCount++;
        ResolutionChanged?.Invoke(
            this,
            new ProjectionResolutionChangedEventArgs(
                instanceId,
                "session",
                new ScreenGeometry(previousWidth, previousHeight),
                new ScreenGeometry(width, height)));
    }

    /// <summary>
    /// 触发会话状态变化通知。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="previous">变化前状态。</param>
    /// <param name="current">变化后状态。</param>
    /// <param name="error">异常导致终止时的异常。</param>
    public void RaiseSessionStateChanged(
        string instanceId,
        ProjectionState previous,
        ProjectionState current,
        Exception? error = null)
    {
        SessionStateChangedCount++;
        SessionStateChanged?.Invoke(
            this,
            new ProjectionSessionStateChangedEventArgs(instanceId, "session", previous, current, error));
    }

    /// <inheritdoc />
    public Task<ProjectionSession> StartAsync(
        string instanceId,
        int? targetFps = 30,
        CancellationToken cancellationToken = default)
    {
        StartCount++;
        return Task.FromResult(_sessions[instanceId]);
    }

    /// <inheritdoc />
    public Task StopAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        StopCounts[instanceId] = StopCounts.TryGetValue(instanceId, out int count) ? count + 1 : 1;
        _sessions.Remove(instanceId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ProjectionSession? GetSession(string instanceId) =>
        _sessions.TryGetValue(instanceId, out ProjectionSession? session) ? session : null;

    /// <inheritdoc />
    public ProjectionFrame? GetLatestFrame(string instanceId) =>
        _frames.TryGetValue(instanceId, out ProjectionFrame? frame) ? frame : null;

    /// <inheritdoc />
    public ProjectionFrame? GetLatestFrameInto(string instanceId, byte[] destination)
    {
        ProjectionFrame? frame = GetLatestFrame(instanceId);
        if (frame is null || destination.Length < frame.Pixels.Length)
        {
            return null;
        }

        frame.Pixels.CopyTo(destination, 0);
        return new ProjectionFrame(frame.Width, frame.Height, frame.Sequence, frame.CapturedAt, destination);
    }

    /// <inheritdoc />
    public double? GetMeasuredFps(string instanceId) =>
        _measured.TryGetValue(instanceId, out double? measured) ? measured : null;

    /// <inheritdoc />
    public ProjectionInputInjector? GetInputInjector(string instanceId) =>
        _injectors.TryGetValue(instanceId, out ProjectionInputInjector? injector) ? injector : null;

    /// <inheritdoc />
    public ProjectionStreamDiagnostics? GetStreamDiagnostics(string instanceId) => null;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>记录注入命令的 QMP 客户端替身，使输入投递可被断言。</summary>
internal sealed class RecordingInjectionQmpClient : IQmpClient
{
    /// <summary>已执行命令名序列。</summary>
    public List<string> Commands { get; } = new();

    /// <summary>已执行命令的参数序列。</summary>
    public List<object?> Arguments { get; } = new();

    /// <summary>连接次数。</summary>
    public int ConnectCount { get; private set; }

    /// <inheritdoc />
    public Task<IReadOnlySet<string>> ConnectAsync(int port, CancellationToken cancellationToken = default)
    {
        ConnectCount++;
        return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
    }

    /// <inheritdoc />
    public Task<JsonElement> ExecuteAsync(
        string command,
        object? arguments = null,
        CancellationToken cancellationToken = default)
    {
        Commands.Add(command);
        Arguments.Add(arguments);

        using var document = JsonDocument.Parse("{}");
        return Task.FromResult(document.RootElement.Clone());
    }

    /// <inheritdoc />
    public Task<bool> QueryRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    /// <inheritdoc />
    public Task<bool> RequestShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}