using XBear.Core.Diagnostics;

namespace XBear.App.Presentation;

/// <summary>
/// 错误分类的界面文案。展示标题使用标准四层分类的名称，技术分类与详情一并保留，
/// 供诊断展开时定位到具体失败步骤。
/// </summary>
/// <param name="Title">展示标题，为标准分类名与技术分类名的组合。</param>
/// <param name="Layer">标准错误分类层级。</param>
/// <param name="LayerTermId">标准分类的术语标识，代码与诊断日志引用该值。</param>
/// <param name="TechnicalName">技术分类名，诊断展开时对照使用。</param>
/// <param name="TechnicalDetail">技术分类的具体说明，诊断展开时对照使用。</param>
public sealed record ErrorCategoryText(
    string Title,
    ErrorLayer Layer,
    string LayerTermId,
    string TechnicalName,
    string TechnicalDetail);

/// <summary>
/// 把 <see cref="ErrorCategory"/> 映射为面向用户的文案。
/// 层级名称取自术语表，技术分类与详情只用于诊断展开，不把异常堆栈直接甩给用户。
/// </summary>
public static class ErrorPresenter
{
    /// <summary>
    /// 取错误分类对应的界面文案，标题带标准分类名。
    /// </summary>
    /// <param name="category">技术错误分类。</param>
    /// <param name="terms">界面文案术语来源。</param>
    /// <returns>分层标题、技术分类名与技术详情。</returns>
    public static ErrorCategoryText Describe(ErrorCategory category, TerminologyCatalog terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        ErrorLayer layer = ErrorLayerMap.Resolve(category);
        string layerName = terms.Zh(TermIdOf(layer));
        (string technicalName, string technicalDetail) = Technical(category);

        return new ErrorCategoryText(
            $"{layerName}：{technicalName}",
            layer,
            TermIdOf(layer),
            technicalName,
            technicalDetail);
    }

    /// <summary>
    /// 取错误分类对应的界面文案。术语来源尚不可用时只给技术分类名，例如应用装配完成前
    /// 的启动期失败，术语表可能尚未读到。
    /// </summary>
    /// <param name="category">技术错误分类。</param>
    /// <returns>技术分类名与详情，层级仍可用于处置判断。</returns>
    public static ErrorCategoryText Describe(ErrorCategory category)
    {
        ErrorLayer layer = ErrorLayerMap.Resolve(category);
        (string technicalName, string technicalDetail) = Technical(category);

        return new ErrorCategoryText(
            technicalName,
            layer,
            TermIdOf(layer),
            technicalName,
            technicalDetail);
    }

    /// <summary>
    /// 把异常整理为界面可直接展示的内容。
    /// </summary>
    /// <param name="exception">待呈现的异常。</param>
    /// <param name="terms">界面文案术语来源，置空时只给技术分类名。</param>
    /// <returns>分层文案与可选的处置建议。</returns>
    public static (ErrorCategoryText Text, string? Remediation) Describe(
        Exception exception,
        TerminologyCatalog? terms = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // 项目内异常带有分类与建议，直接采信；其余异常归入内部错误，避免暴露实现细节。
        return exception is XBearException categorized
            ? (terms is not null ? Describe(categorized.Category, terms) : Describe(categorized.Category), categorized.Remediation)
            : (terms is not null ? Describe(ErrorCategory.Internal, terms) : Describe(ErrorCategory.Internal), null);
    }

    /// <summary>
    /// 取标准层级在术语表中的标识。
    /// </summary>
    /// <param name="layer">标准错误分类层级。</param>
    /// <returns>术语标识。</returns>
    public static string TermIdOf(ErrorLayer layer) =>
        layer switch
        {
            ErrorLayer.Environment => TerminologyCatalog.ErrorEnvironmentTermId,
            ErrorLayer.Provider => TerminologyCatalog.ErrorProviderTermId,
            ErrorLayer.Guest => TerminologyCatalog.ErrorGuestTermId,
            _ => TerminologyCatalog.ErrorUserTermId,
        };

    private static (string Name, string Detail) Technical(ErrorCategory category) =>
        category switch
        {
            ErrorCategory.State => ("当前状态不允许该操作",
                "实例正处于其他流程中，请等待当前操作结束。"),
            ErrorCategory.Dependency => ("缺少外部依赖",
                "所需的外部组件不存在或不可执行。"),
            ErrorCategory.Process => ("外部依赖执行失败",
                "外部组件已找到但执行过程中失败。"),
            ErrorCategory.Protocol => ("与实例的协议交互失败",
                "实例已启动，但通信握手或指令执行没有成功。"),
            ErrorCategory.Port => ("端口不可用",
                "宿主端口被占用或无可用端口可分配。"),
            ErrorCategory.Spec => ("配置不符合契约",
                "配置或共享契约文件不满足约束。"),
            ErrorCategory.Storage => ("磁盘文件操作失败",
                "可写磁盘文件的创建或链式关系校验失败。"),
            ErrorCategory.Identity => ("设备标识不可用",
                "设备标识冲突、不可用或不允许复用。"),
            ErrorCategory.Canceled => ("操作已取消",
                "操作在完成前被取消。"),
            ErrorCategory.Timeout => ("操作超时",
                "等待外部响应超过约定时长。"),
            _ => ("内部错误",
                "出现未归类的内部错误。"),
        };
}