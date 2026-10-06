using XBear.Core.Diagnostics;

namespace XBear.App.Presentation;

/// <summary>错误分类的界面文案。</summary>
/// <param name="Title">错误标题。</param>
/// <param name="Description">分类含义的中文说明。</param>
public sealed record ErrorCategoryText(string Title, string Description);

/// <summary>
/// 把 <see cref="ErrorCategory"/> 映射为面向用户的中文文案。
/// 只呈现分类说明与处置建议，不把异常堆栈直接甩给用户。
/// </summary>
public static class ErrorPresenter
{
    /// <summary>
    /// 取错误分类对应的界面文案。
    /// </summary>
    /// <param name="category">错误分类。</param>
    /// <returns>标题与说明文案。</returns>
    public static ErrorCategoryText Describe(ErrorCategory category) =>
        category switch
        {
            ErrorCategory.State => new ErrorCategoryText(
                "当前状态不允许该操作",
                "实例正处于其他流程中，请等待当前操作结束。"),
            ErrorCategory.Dependency => new ErrorCategoryText(
                "缺少外部依赖",
                "所需的外部组件不存在或不可执行。"),
            ErrorCategory.Process => new ErrorCategoryText(
                "外部依赖执行失败",
                "外部组件已找到但执行过程中失败。"),
            ErrorCategory.Protocol => new ErrorCategoryText(
                "与实例的协议交互失败",
                "实例已启动，但通信握手或指令执行没有成功。"),
            ErrorCategory.Port => new ErrorCategoryText(
                "端口不可用",
                "宿主端口被占用或无可用端口可分配。"),
            ErrorCategory.Spec => new ErrorCategoryText(
                "配置不符合契约",
                "配置或共享契约文件不满足约束。"),
            ErrorCategory.Storage => new ErrorCategoryText(
                "磁盘文件操作失败",
                "可写磁盘文件的创建或链式关系校验失败。"),
            ErrorCategory.Identity => new ErrorCategoryText(
                "设备标识不可用",
                "设备标识冲突、不可用或不允许复用。"),
            ErrorCategory.Canceled => new ErrorCategoryText(
                "操作已取消",
                "操作在完成前被取消。"),
            ErrorCategory.Timeout => new ErrorCategoryText(
                "操作超时",
                "等待外部响应超过约定时长。"),
            _ => new ErrorCategoryText(
                "内部错误",
                "出现未归类的内部错误。")
        };

    /// <summary>
    /// 把异常整理为界面可直接展示的内容。
    /// </summary>
    /// <param name="exception">待呈现的异常。</param>
    /// <returns>标题、说明与可选的处置建议。</returns>
    public static (ErrorCategoryText Text, string? Remediation) Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // 项目内异常带有分类与建议，直接采信；其余异常归入内部错误，避免暴露实现细节。
        return exception is XBearException categorized
            ? (Describe(categorized.Category), categorized.Remediation)
            : (Describe(ErrorCategory.Internal), null);
    }
}