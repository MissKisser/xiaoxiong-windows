using System.Globalization;
using System.Text;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Projection;

/// <summary>
/// 保活通道参数。保活频率决定 guest 侧制造损伤的节拍，
/// 频率越高，服务端刷新定时器被压得越低，代价是 guest 与宿主的固定开销上升。
/// </summary>
public sealed class ProjectionKeepaliveOptions
{
    /// <summary>损伤节拍频率，单位 Hz；为 null 或非正值时使用默认值。</summary>
    public int? FrequencyHz { get; init; }

    /// <summary>启动后确认损伤循环存活的等待上限。</summary>
    public TimeSpan? StartupProbeTimeout { get; init; }

    /// <summary>单条 adb 命令的等待上限。</summary>
    public TimeSpan? CommandTimeout { get; init; }
}

/// <summary>
/// 实例内保活损伤通道：在投屏会话存续期间，让 guest 画面持续存在真实像素变化。
///
/// 服务端的画面推送由自适应刷新定时器驱动，空闲时周期逐次变长，只有扫到真实损伤时才会缩短。
/// 客户端无法伪造损伤，因此要缩短「画面产出到宿主看到」这段等待，
/// 唯一可控且代价可控的手段就是让 guest 侧持续存在极小的真实损伤。
///
/// 损伤源取「帧缓冲左上角单个像素的取值单调递增」：
/// 写入目标固定在状态栏区域的 1 个像素，用户不可见；
/// 取值逐次变化而不是两色交替，是因为服务端按逐块比对判定损伤，
/// 两色交替一旦与刷新周期形成整除关系，相邻两次扫描可能落在同一取值上而被判为无变化；
/// 节拍由 shell 内建的 read 超时实现，写入与等待都不派生进程，实测 guest CPU 占用在千分之几量级。
///
/// 该循环以脱离 adb 会话的方式在 guest 内后台运行，会话结束即被清理；
/// guest 重启后循环自然消失，随后的投屏会话会重新拉起，无需外部守护。
/// </summary>
public sealed class ProjectionKeepalive : IAsyncDisposable
{
    /// <summary>保活默认频率，单位 Hz。</summary>
    public const int DefaultFrequencyHz = 30;

    /// <summary>保活频率下限，单位 Hz。</summary>
    public const int MinFrequencyHz = 1;

    /// <summary>保活频率上限，单位 Hz。</summary>
    public const int MaxFrequencyHz = 60;

    /// <summary>guest 内保活目录。</summary>
    public const string GuestDirectory = "/data/local/tmp/xbear-keepalive";

    /// <summary>guest 内损伤循环脚本路径。</summary>
    public const string GuestScriptPath = GuestDirectory + "/keepalive.sh";

    /// <summary>guest 内损伤循环进程号文件路径。</summary>
    public const string GuestPidPath = GuestDirectory + "/keepalive.pid";

    /// <summary>guest 内用于无进程节拍的管道路径。</summary>
    public const string GuestFifoPath = GuestDirectory + "/keepalive.fifo";

    /// <summary>guest 内逐像素损伤的写入目标。</summary>
    public const string GuestFramebufferPath = "/dev/graphics/fb0";

    /// <summary>损伤取值的循环长度，取值单调递增以保证相邻两次扫描必不相同。</summary>
    public const int DamageValueCount = 256;

    private static readonly TimeSpan DefaultStartupProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(150);

    private readonly IAdbClient _adb;
    private readonly int _adbPort;
    private readonly int _frequencyHz;
    private readonly TimeSpan _startupProbeTimeout;
    private readonly TimeSpan _commandTimeout;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private int _disposed;
    private int _frequency;
    private int _pid;
    private int _active;
    private string? _lastError;

    /// <summary>
    /// 创建保活通道。
    /// </summary>
    /// <param name="adb">已连接的 adb 客户端。</param>
    /// <param name="adbPort">实例在宿主上映射的 adb 端口。</param>
    /// <param name="options">保活参数，为空时使用默认参数。</param>
    public ProjectionKeepalive(
        IAdbClient adb,
        int adbPort,
        ProjectionKeepaliveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(adb);
        if (adbPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(adbPort), "adb 端口必须是合法端口。");
        }

        ProjectionKeepaliveOptions effective = options ?? new ProjectionKeepaliveOptions();
        _adb = adb;
        _adbPort = adbPort;
        _frequencyHz = Normalize(effective.FrequencyHz);
        _frequency = _frequencyHz;
        _startupProbeTimeout = effective.StartupProbeTimeout is { } probe && probe > TimeSpan.Zero
            ? probe
            : DefaultStartupProbeTimeout;
        _commandTimeout = effective.CommandTimeout is { } command && command > TimeSpan.Zero
            ? command
            : DefaultCommandTimeout;
    }

    /// <summary>损伤循环是否已在 guest 内运行。</summary>
    public bool IsActive => Volatile.Read(ref _active) != 0;

    /// <summary>当前损伤节拍频率，单位 Hz。</summary>
    public int FrequencyHz => Volatile.Read(ref _frequency);

    /// <summary>guest 内损伤循环的进程号，未运行时为 null。</summary>
    public int? Pid
    {
        get
        {
            int pid = Volatile.Read(ref _pid);
            return pid > 0 ? pid : null;
        }
    }

    /// <summary>最近一次启停失败的原因，成功时为 null。</summary>
    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>
    /// 生成 guest 内损伤循环的脚本内容。
    /// </summary>
    /// <param name="frequencyHz">损伤节拍频率，单位 Hz。</param>
    /// <returns>可直接交给 guest shell 执行的脚本文本。</returns>
    public static string BuildScript(int frequencyHz)
    {
        double period = 1.0 / Normalize(frequencyHz);
        string pacing = period.ToString("0.####", CultureInfo.InvariantCulture);

        var builder = new StringBuilder();
        builder.Append("#!/system/bin/sh\n");
        builder.Append("F=").Append(GuestFramebufferPath).Append('\n');
        builder.Append("rm -f ").Append(GuestFifoPath).Append('\n');
        builder.Append("mkfifo ").Append(GuestFifoPath).Append('\n');
        builder.Append("exec 3<>").Append(GuestFifoPath).Append('\n');
        builder.Append("echo $$ > ").Append(GuestPidPath).Append('\n');
        builder.Append("while :; do\n");
        for (int value = 0; value < DamageValueCount; value++)
        {
            builder
                .Append("echo -ne '\\")
                .Append(value.ToString("000", CultureInfo.InvariantCulture))
                .Append("' > $F\n");
            builder
                .Append("read -t ")
                .Append(pacing)
                .Append(" _xb <&3\n");
        }

        builder.Append("done\n");
        return builder.ToString();
    }

    /// <summary>
    /// 构造启动损伤循环的 shell 命令。
    /// 三个输入输出流全部重定向，使循环脱离本次 adb 命令流独立存活；
    /// 整条后台命令放在命令组内，使调用方在其后追加退出码查询时仍是一句完整语句。
    /// </summary>
    /// <returns>启动命令文本。</returns>
    /// <summary>guest 内损伤循环的执行日志路径。</summary>
    public const string GuestLogPath = GuestDirectory + "/keepalive.log";

    public static string BuildStartCommand() =>
        $"sh {GuestScriptPath} </dev/null >{GuestLogPath} 2>&1 & true";

    /// <summary>
    /// 构造停止损伤循环并清理其全部落地文件的 shell 命令。
    /// </summary>
    /// <returns>停止命令文本。</returns>
    public static string BuildStopCommand() =>
        $"kill $(cat {GuestPidPath}) 2>/dev/null; rm -f {GuestPidPath} {GuestFifoPath} {GuestScriptPath} {GuestLogPath}";

    /// <summary>
    /// 构造探测损伤循环进程号的 shell 命令。
    /// </summary>
    /// <returns>探测命令文本。</returns>
    public static string BuildProbeCommand() => $"cat {GuestPidPath} 2>/dev/null";

    /// <summary>
    /// 在 guest 内启动损伤循环。启动前先清理可能残留的旧循环，因此重复调用不会叠加。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>启动是否成功；失败时 <see cref="LastError"/> 带有原因。</returns>
    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);

            string script = BuildScript(_frequencyHz);
            string localPath = WriteLocalScript(script);

            try
            {
                await RunAsync($"mkdir -p {GuestDirectory}", cancellationToken).ConfigureAwait(false);
                await _adb.PushAsync(localPath, GuestScriptPath, cancellationToken).ConfigureAwait(false);
                await RunAsync($"chmod 700 {GuestScriptPath}", cancellationToken).ConfigureAwait(false);
                await RunAsync(BuildStartCommand(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TryDeleteLocalScript(localPath);
            }

            int pid = await ProbePidAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _pid, pid);
            Interlocked.Exchange(ref _active, pid > 0 ? 1 : 0);
            if (pid > 0)
            {
                Volatile.Write(ref _lastError, null);
            }
            return pid > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is XBearException or IOException or UnauthorizedAccessException)
        {
            Interlocked.Exchange(ref _active, 0);
            Volatile.Write(ref _pid, 0);
            Volatile.Write(ref _lastError, ex.Message);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 停止 guest 内的损伤循环并清理其落地文件。未启动时也安全调用。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 释放通道内部资源。已停止的循环不会被再次清理。
    /// </summary>
    /// <returns>表示释放完成的异步结果。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 先清理再撤销在途命令：停止命令自身依赖未被取消的执行窗口，
        // 顺序颠倒会让实例内的损伤循环在会话结束后继续存活。
        try
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception)
        {
            // 清理失败不影响宿主侧资源释放。
        }

        _shutdown.Cancel();

        _gate.Dispose();
        _shutdown.Dispose();
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _active) == 0 && Volatile.Read(ref _pid) == 0)
        {
            return;
        }

        try
        {
            await RunAsync(BuildStopCommand(), cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _lastError, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (XBearException ex)
        {
            Volatile.Write(ref _lastError, ex.Message);
        }
        finally
        {
            Volatile.Write(ref _pid, 0);
            Interlocked.Exchange(ref _active, 0);
        }
    }

    private async Task<int> ProbePidAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _startupProbeTimeout;
        string lastProbe = string.Empty;
        while (true)
        {
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            linked.CancelAfter(_commandTimeout);

            int pid = 0;
            try
            {
                string output = await _adb.ShellAsync(BuildProbeCommand(), linked.Token)
                    .ConfigureAwait(false);
                lastProbe = output.Trim();
                int.TryParse(lastProbe, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (XBearException ex)
            {
                // 进程号文件尚未生成时查询会失败，这属于启动过程中的正常状态，继续重试。
                lastProbe = ex.Message;
                pid = 0;
            }

            if (pid > 0)
            {
                return pid;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                string logTail = string.Empty;
                try
                {
                    logTail = await _adb.ShellAsync($"cat {GuestLogPath} 2>/dev/null", linked.Token).ConfigureAwait(false);
                    logTail = logTail.Trim();
                }
                catch (Exception)
                {
                }

                string detail = string.IsNullOrWhiteSpace(logTail)
                    ? lastProbe
                    : $"日志：{logTail}";

                Volatile.Write(
                    ref _lastError,
                    $"损伤循环启动后未在 {_startupProbeTimeout.TotalSeconds:F1} 秒内登记进程号（{detail}）。");
                return 0;
            }

            try
            {
                await Task.Delay(ProbeInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        }
    }

    private async Task RunAsync(string command, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        linked.CancelAfter(_commandTimeout);
        await _adb.ShellAsync(command, linked.Token).ConfigureAwait(false);
    }

    private static string WriteLocalScript(string script)
    {
        string path = Path.Combine(Path.GetTempPath(), $"xbear-keepalive-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static void TryDeleteLocalScript(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 临时文件删除失败不影响保活通道本身。
        }
        catch (UnauthorizedAccessException)
        {
            // 临时文件删除失败不影响保活通道本身。
        }
    }

    private static int Normalize(int? frequencyHz)
    {
        if (frequencyHz is not { } requested || requested <= 0)
        {
            return DefaultFrequencyHz;
        }

        return Math.Clamp(requested, MinFrequencyHz, MaxFrequencyHz);
    }
}