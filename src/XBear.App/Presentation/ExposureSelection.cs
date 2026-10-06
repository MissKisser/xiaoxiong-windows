using XBear.Core.Abstractions;

namespace XBear.App.Presentation;

/// <summary>暴露级别变更的判定结果。</summary>
public enum ExposureChangeOutcome
{
    /// <summary>无需确认即可生效，含降级到回环与首次选择回环。</summary>
    Applied = 0,

    /// <summary>需要用户显式勾选风险确认后才可继续。</summary>
    RequiresAcknowledgement = 1,

    /// <summary>用户未勾选风险确认，变更被阻断。</summary>
    Blocked = 2
}

/// <summary>暴露级别选择的展示文案。</summary>
/// <param name="Level">暴露级别标识。</param>
/// <param name="Title">级别名称。</param>
/// <param name="Warning">该级别的风险提示。</param>
public sealed record ExposureLevelOption(string Level, string Title, string Warning);

/// <summary>
/// 暴露级别选择状态机。默认回环，切到局域网给出提示，切到公网必须先获得显式风险确认。
/// 纯逻辑，不依赖界面，便于直接单测。
/// </summary>
public sealed class ExposureSelection
{
    /// <summary>回环，仅本机可达。</summary>
    public const string Loopback = "loopback";

    /// <summary>局域网，同网段设备可连接。</summary>
    public const string Lan = "lan";

    /// <summary>公网，任何可达网络的主机均可连接。</summary>
    public const string Public = "public";

    private readonly List<ExposureLevelOption> _options;

    /// <summary>
    /// 构造选择状态机。
    /// </summary>
    /// <param name="options">三档暴露级别的展示文案。</param>
    public ExposureSelection(IEnumerable<ExposureLevelOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.ToList();
    }

    /// <summary>当前已生效的暴露级别，缺省为回环，绝不默认对外。</summary>
    public string Current { get; private set; } = Loopback;

    /// <summary>待生效的暴露级别，等于 <see cref="Current"/> 时表示无待确认变更。</summary>
    public string? Pending { get; private set; }

    /// <summary>用户是否已勾选风险确认。</summary>
    public bool IsAcknowledged { get; private set; }

    /// <summary>待确认变更的级别说明，未变更时为空。</summary>
    public string PendingWarning { get; private set; } = string.Empty;

    /// <summary>是否处于阻断式确认流程中。</summary>
    public bool IsConfirmationPending =>
        Pending is not null && !string.Equals(Pending, Current, StringComparison.Ordinal);

    /// <summary>三档暴露级别的展示文案。</summary>
    public IReadOnlyList<ExposureLevelOption> Options => _options;

    /// <summary>
    /// 取消待确认变更，回到已生效级别。
    /// </summary>
    public void CancelPending()
    {
        Pending = null;
        IsAcknowledged = false;
        PendingWarning = string.Empty;
    }

    /// <summary>
    /// 请求切换到目标暴露级别。
    /// </summary>
    /// <param name="target">目标暴露级别标识。</param>
    /// <returns>
    /// 无需确认时返回 <see cref="ExposureChangeOutcome.Applied"/>；
    /// 切到局域网或公网时返回 <see cref="ExposureChangeOutcome.RequiresAcknowledgement"/> 并进入待确认状态。
    /// </returns>
    public ExposureChangeOutcome Request(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        if (string.Equals(target, Current, StringComparison.Ordinal))
        {
            CancelPending();
            return ExposureChangeOutcome.Applied;
        }

        // 切回回环属于降低风险，直接生效，不给二次确认的错觉。
        if (string.Equals(target, Loopback, StringComparison.Ordinal))
        {
            Apply(Loopback);
            return ExposureChangeOutcome.Applied;
        }

        Pending = target;
        IsAcknowledged = false;
        PendingWarning = DescribeWarning(target);
        return ExposureChangeOutcome.RequiresAcknowledgement;
    }

    /// <summary>
    /// 提交待确认变更。未勾选风险确认时不允许继续。
    /// </summary>
    /// <param name="acknowledged">用户是否已勾选「我已理解风险」。</param>
    /// <returns>生效返回 <see cref="ExposureChangeOutcome.Applied"/>，未勾选返回 <see cref="ExposureChangeOutcome.Blocked"/>。</returns>
    public ExposureChangeOutcome Confirm(bool acknowledged)
    {
        if (!IsConfirmationPending)
        {
            return ExposureChangeOutcome.Applied;
        }

        // 公网必须显式勾选才能放行，局域网同样要求一次确认以留下明确意图。
        if (!acknowledged)
        {
            IsAcknowledged = false;
            return ExposureChangeOutcome.Blocked;
        }

        Apply(Pending!);
        return ExposureChangeOutcome.Applied;
    }

    private void Apply(string level)
    {
        Current = level;
        Pending = null;
        IsAcknowledged = false;
        PendingWarning = string.Empty;
    }

    private string DescribeWarning(string level) =>
        _options.FirstOrDefault(o => string.Equals(o.Level, level, StringComparison.Ordinal))?.Warning
        ?? string.Empty;

    /// <summary>
    /// 判断实例是否因暴露级别而非回环而需要持续提示风险。
    /// </summary>
    /// <param name="exposure">实例配置中的暴露级别。</param>
    /// <returns>暴露级别为局域网或公网时返回 true。</returns>
    public static bool RequiresRiskBanner(string? exposure) =>
        string.Equals(exposure, Lan, StringComparison.Ordinal) ||
        string.Equals(exposure, Public, StringComparison.Ordinal);

    /// <summary>
    /// 判断实例的 adb 端口是否实际绑定到对外地址。
    /// 与 Core 侧绑定地址解析保持一致：非回环暴露级别一律绑定 <c>0.0.0.0</c>。
    /// </summary>
    /// <param name="exposure">实例级暴露级别。</param>
    /// <param name="portForwards">端口映射条目。</param>
    /// <returns>存在一条实际监听在 <c>0.0.0.0</c> 的映射时返回 true。</returns>
    public static bool HasExternallyBoundPort(
        string? exposure,
        IEnumerable<Core.Spec.PortForward>? portForwards)
    {
        string upperBound = string.Equals(exposure, Lan, StringComparison.Ordinal)
            ? Lan
            : string.Equals(exposure, Public, StringComparison.Ordinal) ? Public : Loopback;

        foreach (Core.Spec.PortForward forward in portForwards ?? Enumerable.Empty<Core.Spec.PortForward>())
        {
            if (IsExternallyBound(upperBound, forward.Bind))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExternallyBound(string upperBound, string? bind)
    {
        string requested = bind?.Trim().ToLowerInvariant() switch
        {
            "lan" => Lan,
            "public" => Public,
            _ => Loopback
        };

        // 单条映射的 bind 只允许下调，不允许突破实例级暴露上限。
        return Rank(requested) <= Rank(upperBound) && Rank(upperBound) > Rank(Loopback);
    }

    private static int Rank(string exposure) =>
        exposure switch
        {
            Public => 2,
            Lan => 1,
            _ => 0
        };
}