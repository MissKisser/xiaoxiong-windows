namespace XBear.Core.Diagnostics;

/// <summary>
/// 标准错误分类的四个层级。层级回答「问题出在哪里、该由谁处理」，
/// <see cref="ErrorCategory"/> 的技术分类回答「哪一步失败」，两者一一映射后界面才既能
/// 给出可执行的处置指引，又不会把实现细节直接甩给用户。
/// </summary>
public enum ErrorLayer
{
    /// <summary>宿主环境错误：宿主不满足运行前提，须在启动前拦截并给出修复指引。</summary>
    Environment = 0,

    /// <summary>虚拟化实现错误：虚拟化实现内部失败，记录日志后实例转为 failed，允许重试。</summary>
    Provider = 1,

    /// <summary>实例内错误：实例内出现问题，超时后转为 failed 并保留日志。</summary>
    Guest = 2,

    /// <summary>配置错误：配置本身非法，立即返回，不进入 failed 态。</summary>
    User = 3,
}

/// <summary>
/// 技术错误分类到标准四层分类的映射。技术分类保持不变，便于日志与诊断展开时精确定位；
/// 界面呈现时一律使用层级分类的名称。
/// </summary>
public static class ErrorLayerMap
{
    /// <summary>技术分类全集，供映射完整性核对使用。</summary>
    public static IReadOnlyList<ErrorCategory> Categories { get; } =
        Enum.GetValues<ErrorCategory>();

    /// <summary>
    /// 解析技术分类所属的标准层级。未声明的取值一律归入配置错误：
    /// 立即返回且不改变实例状态，是未知分类下最保守的处置方式。
    /// </summary>
    /// <param name="category">技术错误分类。</param>
    /// <returns>所属的标准层级。</returns>
    public static ErrorLayer Resolve(ErrorCategory category) =>
        category switch
        {
            // 外部依赖缺失即宿主缺少虚拟化组件，属启动前就该拦截的宿主环境问题。
            ErrorCategory.Dependency => ErrorLayer.Environment,

            // 虚拟化实现内部失败：外部组件已找到，执行过程本身出错，可重试。
            ErrorCategory.Process => ErrorLayer.Provider,

            // 未归类的内部错误同属实现内部失败。
            ErrorCategory.Internal => ErrorLayer.Provider,

            // 与实例的协议交互失败、等待响应超时，症结都在实例内未就绪。
            ErrorCategory.Protocol => ErrorLayer.Guest,
            ErrorCategory.Timeout => ErrorLayer.Guest,

            // 状态不允许、端口被占用、配置不满足契约、磁盘不可写、标识不可用与用户取消，
            // 都是配置或操作本身的问题，立即返回即可，不必把实例打成 failed。
            ErrorCategory.State => ErrorLayer.User,
            ErrorCategory.Port => ErrorLayer.User,
            ErrorCategory.Spec => ErrorLayer.User,
            ErrorCategory.Storage => ErrorLayer.User,
            ErrorCategory.Identity => ErrorLayer.User,
            ErrorCategory.Canceled => ErrorLayer.User,

            _ => ErrorLayer.User,
        };
}