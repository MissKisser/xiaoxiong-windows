namespace XBear.Core.Instances;

/// <summary>
/// 净桌面初始化参数。
/// 控制实例在调试通路就绪后是否自动把桌面整理成干净可驱动的状态，以及该步骤的整体等待上限。
/// </summary>
public sealed class CleanDesktopOptions
{
    /// <summary>净桌面初始化整体等待上限的缺省值。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 是否在启动流程中执行净桌面初始化，缺省开启。
    /// 关闭后启动流程与既有行为完全一致，不向 guest 下发任何初始化命令。
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 净桌面初始化的整体等待上限，缺省为 <see cref="DefaultTimeout"/>。
    /// 该上限覆盖全部命令的连接与执行总耗时；为 <see cref="TimeSpan.Zero"/> 时同样按关闭处理。
    /// </summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;
}
