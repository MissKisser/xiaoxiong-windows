namespace XBear.Core.Abstractions;

/// <summary>为单个实例分配的宿主端口组。</summary>
/// <param name="Adb">adb 宿主端口，默认自 5555 起。</param>
/// <param name="Qmp">QMP 宿主端口，默认自 5556 起。</param>
/// <param name="Vnc">VNC 宿主端口，默认自 5900 起。</param>
public sealed record AllocatedPorts(int Adb, int Qmp, int Vnc);

/// <summary>端口分配器，保证多开时同宿主端口不冲突。</summary>
public interface IPortAllocator
{
    /// <summary>预检一组端口是否均未被占用。</summary>
    /// <param name="ports">待预检的端口组。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部空闲返回 true，否则返回 false。</returns>
    Task<bool> AreAvailableAsync(AllocatedPorts ports, CancellationToken cancellationToken = default);

    /// <summary>探测并占用一组端口组。</summary>
    /// <param name="instanceId">占用者标识，用于释放时精确匹配。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="Diagnostics.XBearException">无可用端口时抛出 <see cref="Diagnostics.ErrorCategory.Port"/>。</exception>
    Task<AllocatedPorts> AcquireAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>释放此前为实例占用的端口组。</summary>
    /// <param name="instanceId">实例标识。</param>
    void Release(string instanceId);
}