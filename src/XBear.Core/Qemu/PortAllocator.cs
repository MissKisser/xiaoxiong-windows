using System.Net;
using System.Net.Sockets;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Qemu;

/// <summary>为实例挑选互不冲突的宿主端口，并以真实绑定探测占用情况。</summary>
public sealed class PortAllocator : IPortAllocator
{
    /// <summary>adb 宿主端口的默认起始端口。</summary>
    public const int DefaultAdbStartPort = 5555;

    /// <summary>QMP 宿主端口的默认起始端口。</summary>
    public const int DefaultQmpStartPort = 5556;

    /// <summary>VNC 宿主端口的默认起始端口。</summary>
    public const int DefaultVncStartPort = 5900;

    private const int DefaultMaxCandidateOffsets = 64;
    private const int PortProbeBufferSize = 1;

    private readonly object _gate = new();
    private readonly Dictionary<string, AllocatedPorts> _assignedByInstance =
        new(StringComparer.Ordinal);
    private readonly HashSet<int> _reservedPorts = new();

    private readonly int _adbStartPort;
    private readonly int _qmpStartPort;
    private readonly int _vncStartPort;
    private readonly int _maxCandidateOffsets;

    /// <summary>使用默认起始端口构造分配器。</summary>
    public PortAllocator()
        : this(DefaultAdbStartPort, DefaultQmpStartPort, DefaultVncStartPort, DefaultMaxCandidateOffsets)
    {
    }

    /// <summary>构造分配器并指定起始端口。</summary>
    /// <param name="adbStartPort">adb 起始端口。</param>
    /// <param name="qmpStartPort">QMP 起始端口。</param>
    /// <param name="vncStartPort">VNC 起始端口。</param>
    /// <param name="maxCandidateOffsets">
    /// 每个起始端口最多向后试探的偏移数，达到上限即视为端口耗尽。
    /// </param>
    /// <exception cref="XBearException">起始端口越界或相互重复时抛出 <see cref="ErrorCategory.Port"/>。</exception>
    public PortAllocator(
        int adbStartPort,
        int qmpStartPort,
        int vncStartPort,
        int maxCandidateOffsets = DefaultMaxCandidateOffsets)
    {
        if (!IsValidPort(adbStartPort) || !IsValidPort(qmpStartPort) || !IsValidPort(vncStartPort))
        {
            throw new XBearException(
                ErrorCategory.Port,
                $"端口起始值必须处于 1 至 65535 之间，当前为 {adbStartPort}、{qmpStartPort}、{vncStartPort}。");
        }

        if (adbStartPort == qmpStartPort || adbStartPort == vncStartPort || qmpStartPort == vncStartPort)
        {
            throw new XBearException(
                ErrorCategory.Port,
                $"adb、QMP、VNC 起始端口必须互不相同，当前为 {adbStartPort}、{qmpStartPort}、{vncStartPort}。");
        }

        if (maxCandidateOffsets < 1)
        {
            throw new XBearException(ErrorCategory.Port, "端口试探次数必须大于 0。");
        }

        _adbStartPort = adbStartPort;
        _qmpStartPort = qmpStartPort;
        _vncStartPort = vncStartPort;
        _maxCandidateOffsets = maxCandidateOffsets;
    }

    /// <summary>预检一组端口是否均未被占用。</summary>
    /// <param name="ports">待预检的端口组。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部空闲返回 true，否则返回 false。</returns>
    public Task<bool> AreAvailableAsync(
        AllocatedPorts ports,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ports);

        foreach (var port in EnumeratePorts(ports))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsPortFree(port))
            {
                return Task.FromResult(false);
            }
        }

        return Task.FromResult(true);
    }

    /// <summary>探测并占用一组端口组。</summary>
    /// <param name="instanceId">占用者标识，用于释放时精确匹配。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分配到的端口组，同一实例重复调用返回同一组。</returns>
    /// <exception cref="XBearException">无可用端口时抛出 <see cref="ErrorCategory.Port"/>。</exception>
    public async Task<AllocatedPorts> AcquireAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            throw new XBearException(ErrorCategory.Port, "端口占用者标识不能为空。");
        }

        for (var offset = 0; offset < _maxCandidateOffsets; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = new AllocatedPorts(
                _adbStartPort + offset,
                _qmpStartPort + offset,
                _vncStartPort + offset);

            if (!AreValidDistinct(candidate))
            {
                continue;
            }

            lock (_gate)
            {
                if (_assignedByInstance.TryGetValue(instanceId, out var alreadyAssigned))
                {
                    return alreadyAssigned;
                }

                if (HasReservationConflict(candidate))
                {
                    continue;
                }
            }

            if (!await AreAvailableAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            lock (_gate)
            {
                if (_assignedByInstance.TryGetValue(instanceId, out var alreadyAssigned))
                {
                    return alreadyAssigned;
                }

                if (HasReservationConflict(candidate))
                {
                    continue;
                }

                _assignedByInstance[instanceId] = candidate;
                foreach (var port in EnumeratePorts(candidate))
                {
                    _reservedPorts.Add(port);
                }

                return candidate;
            }
        }

        throw new XBearException(
            ErrorCategory.Port,
            $"无法为实例 {instanceId} 找到空闲的端口组，已试探 {_maxCandidateOffsets} 组。",
            "请关闭其他正在运行的实例后重试；若端口确实空闲，可在 XBear.Core.Qemu.PortAllocator 构造时改用其他起始端口。");
    }

    /// <summary>释放此前为实例占用的端口组。</summary>
    /// <param name="instanceId">实例标识。</param>
    public void Release(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        lock (_gate)
        {
            if (!_assignedByInstance.Remove(instanceId, out var assigned))
            {
                return;
            }

            foreach (var port in EnumeratePorts(assigned))
            {
                _reservedPorts.Remove(port);
            }
        }
    }

    /// <summary>判断候选端口组是否与已登记的占用冲突。</summary>
    /// <param name="candidate">候选端口组。</param>
    /// <returns>存在冲突返回 true。</returns>
    private bool HasReservationConflict(AllocatedPorts candidate)
        => EnumeratePorts(candidate).Any(_reservedPorts.Contains);

    /// <summary>把端口组展开为逐个端口。</summary>
    /// <param name="ports">端口组。</param>
    /// <returns>端口序列。</returns>
    private static IEnumerable<int> EnumeratePorts(AllocatedPorts ports)
    {
        yield return ports.Adb;
        yield return ports.Qmp;
        yield return ports.Vnc;
    }

    /// <summary>以真实绑定尝试判断单个端口是否空闲。</summary>
    /// <param name="port">待探测端口。</param>
    /// <returns>能够独占绑定返回 true。</returns>
    private static bool IsPortFree(int port)
    {
        if (!IsValidPort(port))
        {
            return false;
        }

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(PortProbeBufferSize);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            // 探测结束即释放监听，端口随后可被 QEMU 实际占用。
            listener?.Stop();
        }
    }

    /// <summary>判断端口号是否处于合法范围。</summary>
    /// <param name="port">端口号。</param>
    /// <returns>处于 1 至 65535 之间返回 true。</returns>
    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    /// <summary>判断端口组内三个端口是否合法且互不相同。</summary>
    /// <param name="ports">端口组。</param>
    /// <returns>合法且互不相同返回 true。</returns>
    private static bool AreValidDistinct(AllocatedPorts ports)
        => IsValidPort(ports.Adb)
            && IsValidPort(ports.Qmp)
            && IsValidPort(ports.Vnc)
            && ports.Adb != ports.Qmp
            && ports.Adb != ports.Vnc
            && ports.Qmp != ports.Vnc;
}