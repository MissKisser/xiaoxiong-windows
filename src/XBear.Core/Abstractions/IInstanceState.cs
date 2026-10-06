namespace XBear.Core.Abstractions;

/// <summary>实例运行态。</summary>
public enum InstanceState
{
    /// <summary>已创建，未运行。</summary>
    Stopped = 0,

    /// <summary>正在启动。</summary>
    Starting = 1,

    /// <summary>已运行。</summary>
    Running = 2,

    /// <summary>正在停止。</summary>
    Stopping = 3,

    /// <summary>启动失败，需查看诊断信息。</summary>
    Faulted = 4
}