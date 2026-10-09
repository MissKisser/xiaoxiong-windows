using XBear.Core.Spec;

namespace XBear.Core.Abstractions;

/// <summary>QMP 轻客户端，JSON over stdio。</summary>
public interface IQmpClient : IAsyncDisposable
{
    /// <summary>连接 QMP 并完成握手，返回协商出的能力集。</summary>
    /// <param name="port">QMP 宿主端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlySet<string>> ConnectAsync(int port, CancellationToken cancellationToken = default);

    /// <summary>执行一条 QMP 命令并返回 return 对象。</summary>
    /// <param name="command">命令名。</param>
    /// <param name="arguments">命令参数，可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<System.Text.Json.JsonElement> ExecuteAsync(
        string command,
        object? arguments = null,
        CancellationToken cancellationToken = default);

    /// <summary>查询实例运行状态。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> QueryRunningAsync(CancellationToken cancellationToken = default);

    /// <summary>请求 guest 正常关机。</summary>
    /// <param name="timeout">等待关机完成的超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> RequestShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>adb 轻客户端，直连 TCP，不起 adb.exe 子进程。</summary>
public interface IAdbClient : IAsyncDisposable
{
    /// <summary>连接实例并执行握手，取得协议版本。</summary>
    /// <param name="port">实例在宿主上映射的 adb 端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="timeout">本次连接与握手的等待上限，为空时使用客户端默认上限。</param>
    /// <exception cref="Diagnostics.XBearException">
    /// 连接或握手失败为 <see cref="Diagnostics.ErrorCategory.Protocol"/>，
    /// 超过等待上限为 <see cref="Diagnostics.ErrorCategory.Timeout"/>。
    /// </exception>
    Task<int> ConnectAsync(
        int port,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null);

    /// <summary>以 root 身份执行 shell 命令。</summary>
    /// <param name="command">命令与参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标准输出，失败时抛出 <see cref="Diagnostics.ErrorCategory.Protocol"/>。</returns>
    Task<string> ShellAsync(string command, CancellationToken cancellationToken = default);

    /// <summary>检查实例是否已以 root 身份运行。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> IsRootAsync(CancellationToken cancellationToken = default);

    /// <summary>推送文件到实例。</summary>
    /// <param name="localPath">宿主文件路径。</param>
    /// <param name="remotePath">实例内目标路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task PushAsync(string localPath, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>从实例拉取文件。</summary>
    /// <param name="remotePath">实例内源路径。</param>
    /// <param name="localPath">宿主目标路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task PullAsync(string remotePath, string localPath, CancellationToken cancellationToken = default);
}

/// <summary>输入通道，向实例投递触摸与按键。</summary>
public interface IInputChannel : IAsyncDisposable
{
    /// <summary>当前实际生效的输入通路。</summary>
    InputChannelKind ActiveChannel { get; }

    /// <summary>探测可用输入通路，按原生优先、投屏降级的顺序选定。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>探测结论，含失败原因，用于向用户如实告知而非静默卡住。</returns>
    Task<InputProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>输入通路种类。</summary>
public enum InputChannelKind
{
    /// <summary>尚未探测。</summary>
    Unknown = 0,

    /// <summary>原生 QMP 输入。</summary>
    Native = 1,

    /// <summary>scrcpy 投屏 + input 注入。</summary>
    Projection = 2,

    /// <summary>两条通路均不可用，该镜像不可交互。</summary>
    Unavailable = 3
}

/// <summary>输入通路探测结论。</summary>
/// <param name="Channel">实际生效的通路。</param>
/// <param name="NativeFailure">原生通路的失败原因，可用时为空。</param>
/// <param name="ProjectionFailure">投屏通路的失败原因，可用时为空。</param>
public sealed record InputProbeResult(
    InputChannelKind Channel,
    string? NativeFailure = null,
    string? ProjectionFailure = null);

/// <summary>设备标识生成器，每实例产出唯一且不复用的标识。</summary>
public interface IDeviceIdentityFactory
{
    /// <summary>生成一组全新标识。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<DeviceIdentity> GenerateAsync(CancellationToken cancellationToken = default);
}

/// <summary>实例配置仓库。</summary>
public interface IInstanceRepository
{
    /// <summary>列出全部实例配置。</summary>
    Task<IReadOnlyList<InstanceSpec>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>按标识读取单个实例配置。</summary>
    /// <param name="id">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<InstanceSpec?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>写入实例配置。</summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SaveAsync(InstanceSpec spec, CancellationToken cancellationToken = default);

    /// <summary>删除实例配置，并把其标识移入墓碑以阻止复用。</summary>
    /// <param name="id">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>查询标识是否已被墓碑记录占用。</summary>
    /// <param name="id">实例标识。</param>
    Task<bool> IsTombstonedAsync(string id);
}