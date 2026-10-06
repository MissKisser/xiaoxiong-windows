namespace XBear.Core.Diagnostics;

/// <summary>统一错误分类，两端措辞一致。</summary>
public enum ErrorCategory
{
    /// <summary>实例当前状态不允许该操作。</summary>
    State = 0,

    /// <summary>外部依赖缺失或不可执行。</summary>
    Dependency = 1,

    /// <summary>外部依赖执行失败。</summary>
    Process = 2,

    /// <summary>与 guest 的协议交互失败。</summary>
    Protocol = 3,

    /// <summary>端口被占用或分配失败。</summary>
    Port = 4,

    /// <summary>实例配置不满足契约。</summary>
    Spec = 5,

    /// <summary>磁盘镜像操作失败。</summary>
    Storage = 6,

    /// <summary>设备标识冲突或不可用。</summary>
    Identity = 7,

    /// <summary>用户取消或拒绝。</summary>
    Canceled = 8,

    /// <summary>超时。</summary>
    Timeout = 9,

    /// <summary>不归类的内部错误。</summary>
    Internal = 99
}

/// <summary>项目内统一异常，所有可预期失败均以该类型上报，不抛裸异常。</summary>
public sealed class XBearException : Exception
{
    /// <summary>错误分类。</summary>
    public ErrorCategory Category { get; }

    /// <summary>面向用户的可执行建议，为空表示无可建议项。</summary>
    public string? Remediation { get; }

    public XBearException(
        ErrorCategory category,
        string message,
        string? remediation = null,
        Exception? inner = null)
        : base(message, inner)
    {
        Category = category;
        Remediation = remediation;
    }
}