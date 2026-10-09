using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using XBear.Core.Abstractions;

namespace XBear.Core.Diagnostics;

/// <summary>
/// 实例启动指标。启动被拆成两个语义明确的阶段：
/// 进程拉起（宿主侧把 QEMU 进程创建出来）与调试通路就绪（冷启动完成、adb 通路可连接）。
/// 二者的量级相差一个数量级以上，混在一个「耗时」字段里会得出与真实冷启动无关的结论。
/// </summary>
public sealed class InstanceStartupMetric
{
    /// <summary>发起启动时间点。</summary>
    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>QEMU 进程拉起完成时间点，未拉起成功时为 null。</summary>
    [JsonPropertyName("processSpawnedAt")]
    public DateTimeOffset? ProcessSpawnedAt { get; set; }

    /// <summary>进程拉起耗时（秒），即从发起到 QEMU 进程创建成功的间隔。</summary>
    [JsonPropertyName("processSpawnSeconds")]
    public double? ProcessSpawnSeconds { get; set; }

    /// <summary>进程拉起耗时（毫秒）。</summary>
    [JsonPropertyName("processSpawnMs")]
    public double? ProcessSpawnMs { get; set; }

    /// <summary>调试通路就绪时间点，未就绪时为 null。</summary>
    [JsonPropertyName("readyAt")]
    public DateTimeOffset? ReadyAt { get; set; }

    /// <summary>冷启动耗时（秒），即从发起到调试通路可连接的间隔；未就绪时为 null。</summary>
    [JsonPropertyName("coldStartSeconds")]
    public double? ColdStartSeconds { get; set; }

    /// <summary>冷启动耗时（毫秒），未就绪时为 null。</summary>
    [JsonPropertyName("coldStartMs")]
    public double? ColdStartMs { get; set; }

    /// <summary>QEMU 进程是否成功拉起。</summary>
    [JsonPropertyName("processSpawned")]
    public bool ProcessSpawned { get; set; }

    /// <summary>调试通路是否已确认可连接，未确认前不得为 true。</summary>
    [JsonPropertyName("debugChannelReady")]
    public bool DebugChannelReady { get; set; }

    /// <summary>启动失败或未就绪的原因，如实记录而不留空。</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>单次工作集物理内存采样记录。</summary>
public sealed class MemorySample
{
    /// <summary>采样时间点。</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>工作集物理内存大小（字节）。</summary>
    [JsonPropertyName("workingSetBytes")]
    public long WorkingSetBytes { get; set; }

    /// <summary>工作集物理内存大小（MB）。</summary>
    [JsonPropertyName("workingSetMB")]
    public double WorkingSetMB { get; set; }
}

/// <summary>实例指标聚合摘要。</summary>
public sealed class InstanceMetricsSummary
{
    /// <summary>内存采样次数。</summary>
    [JsonPropertyName("sampleCount")]
    public int SampleCount { get; set; }

    /// <summary>工作集物理内存最小值（字节）。</summary>
    [JsonPropertyName("minWorkingSetBytes")]
    public long? MinWorkingSetBytes { get; set; }

    /// <summary>工作集物理内存最大值（字节）。</summary>
    [JsonPropertyName("maxWorkingSetBytes")]
    public long? MaxWorkingSetBytes { get; set; }

    /// <summary>最新一次采样的物理内存（字节）。</summary>
    [JsonPropertyName("latestWorkingSetBytes")]
    public long? LatestWorkingSetBytes { get; set; }

    /// <summary>峰值物理内存占用（GB）。</summary>
    [JsonPropertyName("peakWorkingSetGB")]
    public double? PeakWorkingSetGB { get; set; }
}

/// <summary>实例指标快照文档，序列化后保存为 metrics.json。</summary>
public sealed class InstanceMetricsSnapshot
{
    /// <summary>实例标识。</summary>
    [JsonPropertyName("instanceId")]
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>快照记录与落盘时间点。</summary>
    [JsonPropertyName("recordedAt")]
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>启动分阶段耗时指标。</summary>
    [JsonPropertyName("startup")]
    public InstanceStartupMetric? Startup { get; set; }

    /// <summary>发起启动时的宿主物理内存采样，作为多开数据的可信度前提。</summary>
    [JsonPropertyName("hostMemory")]
    public HostMemorySample? HostMemory { get; set; }

    /// <summary>周期采样的内存序列。</summary>
    [JsonPropertyName("memorySamples")]
    public List<MemorySample> MemorySamples { get; set; } = new();

    /// <summary>指标统计摘要。</summary>
    [JsonPropertyName("summary")]
    public InstanceMetricsSummary? Summary { get; set; }
}

/// <summary>指标采集器契约。</summary>
public interface IMetricsRecorder : IAsyncDisposable, IDisposable
{
    /// <summary>记录实例启动开始时间点，并采样一次宿主物理内存。</summary>
    /// <param name="instanceId">实例标识。</param>
    void OnStarting(string instanceId);

    /// <summary>记录 QEMU 进程拉起完成的时间点，并开启工作集内存周期采样。</summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="handle">QEMU 进程句柄。</param>
    void OnProcessSpawned(string instanceId, QemuProcessHandle handle);

    /// <summary>记录调试通路已就绪，冷启动耗时据此结算。</summary>
    /// <param name="instanceId">实例标识。</param>
    void OnDebugChannelReady(string instanceId);

    /// <summary>
    /// 记录调试通路在探测上限内未就绪。
    /// 未就绪时 readyAt 保持为空，不产出任何冷启动耗时结论。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="reason">未就绪的原因。</param>
    void OnDebugChannelUnavailable(string instanceId, string reason);

    /// <summary>记录实例启动失败并停止采集。</summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="reason">失败原因，可为空。</param>
    void OnStartupFailed(string instanceId, string? reason = null);

    /// <summary>手动触发一次内存采样。</summary>
    /// <param name="instanceId">实例标识。</param>
    void SampleMemory(string instanceId);

    /// <summary>停止指定实例的内存采样并将指标落盘至对应数据目录。</summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>生成的 metrics.json 绝对路径。</returns>
    Task<string> StopAndPersistAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>读取当前内存中的指标快照。</summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>指标快照，未采集过时返回 null。</returns>
    InstanceMetricsSnapshot? GetSnapshot(string instanceId);

    /// <summary>获取指定实例的 metrics.json 文件绝对路径。</summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>文件绝对路径。</returns>
    string GetMetricsFilePath(string instanceId);
}

/// <summary>
/// 实例指标采集器。负责分阶段记录实例启动耗时、周期采样 QEMU 进程工作集内存，
/// 并在实例停止时将指标以 JSON 落盘到实例数据目录中的 metrics.json。
/// 时间源与采样周期均可注入，确保测试可控且所有记录均为真实观测值。
/// </summary>
public sealed class MetricsRecorder : IMetricsRecorder
{
    /// <summary>默认指标落盘文件名。</summary>
    public const string MetricsFileName = "metrics.json";

    /// <summary>默认内存采样间隔。</summary>
    public static readonly TimeSpan DefaultSamplingInterval = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Func<string, string> _directoryResolver;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _samplingInterval;
    private readonly IHostMemorySampler? _hostMemorySampler;
    private readonly ConcurrentDictionary<string, InstanceSession> _sessions = new(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>
    /// 构造指标采集器。
    /// </summary>
    /// <param name="instancesRoot">实例根目录。</param>
    /// <param name="timeProvider">时间提供者，缺省使用系统时间。</param>
    /// <param name="samplingInterval">内存采样周期，缺省为 1 秒。</param>
    /// <param name="directoryResolver">实例专属目录解析器，缺省为根目录下同名子目录。</param>
    /// <param name="hostMemorySampler">
    /// 宿主物理内存采样器，为 null 时不记录宿主内存协变量，metrics.json 中该字段整体省略。
    /// </param>
    public MetricsRecorder(
        string instancesRoot,
        TimeProvider? timeProvider = null,
        TimeSpan? samplingInterval = null,
        Func<string, string>? directoryResolver = null,
        IHostMemorySampler? hostMemorySampler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancesRoot);

        _timeProvider = timeProvider ?? TimeProvider.System;
        _samplingInterval = samplingInterval ?? DefaultSamplingInterval;
        _directoryResolver = directoryResolver ?? (id => Path.Combine(instancesRoot, id));
        _hostMemorySampler = hostMemorySampler;
    }

    /// <inheritdoc />
    public void OnStarting(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var session = _sessions.GetOrAdd(instanceId, id => new InstanceSession(id));
        lock (session.Gate)
        {
            session.Timer?.Dispose();
            session.Timer = null;
            session.Handle = null;
            session.Startup = new InstanceStartupMetric
            {
                StartedAt = _timeProvider.GetUtcNow(),
                ProcessSpawned = false,
                DebugChannelReady = false,
            };
            session.HostMemory = SampleHostMemory();
            session.Samples.Clear();
        }
    }

    /// <inheritdoc />
    public void OnProcessSpawned(string instanceId, QemuProcessHandle handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(handle);

        var session = _sessions.GetOrAdd(instanceId, id => new InstanceSession(id));
        lock (session.Gate)
        {
            session.Handle = handle;
            var now = _timeProvider.GetUtcNow();

            if (session.Startup is not null)
            {
                session.Startup.ProcessSpawnedAt = now;
                session.Startup.ProcessSpawned = true;
                var elapsed = now - session.Startup.StartedAt;
                session.Startup.ProcessSpawnSeconds = Math.Max(0, elapsed.TotalSeconds);
                session.Startup.ProcessSpawnMs = Math.Max(0, elapsed.TotalMilliseconds);
            }

            // 采样定时器：进程拉起即启动周期采样，并在初次调度时立即执行一次采样。
            session.Timer?.Dispose();
            if (_samplingInterval > TimeSpan.Zero)
            {
                session.Timer = _timeProvider.CreateTimer(
                    _ => SampleMemory(instanceId),
                    null,
                    TimeSpan.Zero,
                    _samplingInterval);
            }
        }
    }

    /// <inheritdoc />
    public void OnDebugChannelReady(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var session = _sessions.GetOrAdd(instanceId, id => new InstanceSession(id));
        lock (session.Gate)
        {
            if (session.Startup is null)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            session.Startup.DebugChannelReady = true;
            session.Startup.ReadyAt = now;
            var elapsed = now - session.Startup.StartedAt;
            session.Startup.ColdStartSeconds = Math.Max(0, elapsed.TotalSeconds);
            session.Startup.ColdStartMs = Math.Max(0, elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public void OnDebugChannelUnavailable(string instanceId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var session = _sessions.GetOrAdd(instanceId, id => new InstanceSession(id));
        lock (session.Gate)
        {
            if (session.Startup is null || session.Startup.DebugChannelReady)
            {
                return;
            }

            // 未就绪即不产出任何冷启动耗时，readyAt 与就绪标记一并保持为空。
            session.Startup.DebugChannelReady = false;
            session.Startup.ReadyAt = null;
            session.Startup.ColdStartSeconds = null;
            session.Startup.ColdStartMs = null;
            session.Startup.Note = reason;
        }
    }

    /// <inheritdoc />
    public void OnStartupFailed(string instanceId, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        if (_sessions.TryGetValue(instanceId, out var session))
        {
            lock (session.Gate)
            {
                session.Timer?.Dispose();
                session.Timer = null;
                session.Handle = null;
                if (session.Startup is not null)
                {
                    session.Startup.DebugChannelReady = false;
                    session.Startup.ReadyAt = null;
                    session.Startup.ColdStartSeconds = null;
                    session.Startup.ColdStartMs = null;
                    if (!string.IsNullOrWhiteSpace(reason))
                    {
                        session.Startup.Note = reason;
                    }
                }
            }
        }
    }

    /// <inheritdoc />
    public void SampleMemory(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        if (!_sessions.TryGetValue(instanceId, out var session))
        {
            return;
        }

        lock (session.Gate)
        {
            if (session.Handle is null)
            {
                return;
            }

            long? bytes = session.Handle.GetWorkingSetBytes();
            if (bytes is not null && bytes.Value >= 0)
            {
                var sample = new MemorySample
                {
                    Timestamp = _timeProvider.GetUtcNow(),
                    WorkingSetBytes = bytes.Value,
                    WorkingSetMB = Math.Round(bytes.Value / (1024.0 * 1024.0), 2),
                };
                session.Samples.Add(sample);
            }
        }
    }

    /// <inheritdoc />
    public async Task<string> StopAndPersistAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        InstanceMetricsSnapshot snapshot;
        string targetDirectory;
        string targetFilePath;

        if (_sessions.TryGetValue(instanceId, out var session))
        {
            lock (session.Gate)
            {
                session.Timer?.Dispose();
                session.Timer = null;

                // 若尚未采样到任何数据且句柄仍然可用，做一次最终采样尝试。
                if (session.Samples.Count == 0 && session.Handle is not null)
                {
                    long? lastBytes = session.Handle.GetWorkingSetBytes();
                    if (lastBytes is not null && lastBytes.Value >= 0)
                    {
                        session.Samples.Add(new MemorySample
                        {
                            Timestamp = _timeProvider.GetUtcNow(),
                            WorkingSetBytes = lastBytes.Value,
                            WorkingSetMB = Math.Round(lastBytes.Value / (1024.0 * 1024.0), 2),
                        });
                    }
                }

                session.Handle = null;
                snapshot = BuildSnapshotCore(session);
            }
        }
        else
        {
            snapshot = new InstanceMetricsSnapshot
            {
                InstanceId = instanceId,
                RecordedAt = _timeProvider.GetUtcNow(),
            };
        }

        targetDirectory = _directoryResolver(instanceId);
        Directory.CreateDirectory(targetDirectory);
        targetFilePath = Path.Combine(targetDirectory, MetricsFileName);

        string json = JsonSerializer.Serialize(snapshot, SerializerOptions);
        string tempPath = targetFilePath + $".tmp-{Guid.NewGuid():N}";

        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        if (File.Exists(targetFilePath))
        {
            File.Delete(targetFilePath);
        }
        File.Move(tempPath, targetFilePath);

        return targetFilePath;
    }

    /// <inheritdoc />
    public InstanceMetricsSnapshot? GetSnapshot(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        if (!_sessions.TryGetValue(instanceId, out var session))
        {
            return null;
        }

        lock (session.Gate)
        {
            return BuildSnapshotCore(session);
        }
    }

    /// <inheritdoc />
    public string GetMetricsFilePath(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return Path.Combine(_directoryResolver(instanceId), MetricsFileName);
    }

    private HostMemorySample? SampleHostMemory()
    {
        if (_hostMemorySampler is null)
        {
            return null;
        }

        try
        {
            HostMemorySample? sample = _hostMemorySampler.Sample();
            if (sample is not null)
            {
                sample.SampledAt = _timeProvider.GetUtcNow();
            }

            return sample;
        }
        catch (Exception)
        {
            // 宿主内存是协变量而非指标主体，取不到时如实省略，不影响启动指标本身。
            return null;
        }
    }

    private InstanceMetricsSnapshot BuildSnapshotCore(InstanceSession session)
    {
        var snapshot = new InstanceMetricsSnapshot
        {
            InstanceId = session.InstanceId,
            RecordedAt = _timeProvider.GetUtcNow(),
            Startup = session.Startup,
            HostMemory = session.HostMemory,
            MemorySamples = session.Samples.ToList(),
        };

        if (session.Samples.Count > 0)
        {
            long min = session.Samples.Min(s => s.WorkingSetBytes);
            long max = session.Samples.Max(s => s.WorkingSetBytes);
            long latest = session.Samples[^1].WorkingSetBytes;

            snapshot.Summary = new InstanceMetricsSummary
            {
                SampleCount = session.Samples.Count,
                MinWorkingSetBytes = min,
                MaxWorkingSetBytes = max,
                LatestWorkingSetBytes = latest,
                PeakWorkingSetGB = Math.Round(max / (1024.0 * 1024.0 * 1024.0), 3),
            };
        }

        return snapshot;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var session in _sessions.Values)
        {
            lock (session.Gate)
            {
                session.Timer?.Dispose();
                session.Timer = null;
                session.Handle = null;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class InstanceSession
    {
        public InstanceSession(string instanceId)
        {
            InstanceId = instanceId;
        }

        public string InstanceId { get; }
        public object Gate { get; } = new();
        public InstanceStartupMetric? Startup { get; set; }
        public HostMemorySample? HostMemory { get; set; }
        public List<MemorySample> Samples { get; } = new();
        public QemuProcessHandle? Handle { get; set; }
        public ITimer? Timer { get; set; }
    }
}