using System.Runtime.InteropServices;
using XBear.Core.Spec;

namespace XBear.Core.Instances;

/// <summary>宿主物理内存检测器契约。</summary>
public interface IHostMemoryDetector
{
    /// <summary>获取宿主物理内存总量（GB）。</summary>
    /// <returns>物理内存容量，单位为 GB。</returns>
    double GetTotalPhysicalMemoryGB();
}

/// <summary>
/// 基于 Windows API (GlobalMemoryStatusEx) 检测物理内存。
/// 若检测失败或非 Windows 环境则安全回退到 GC 内存信息或保底值。
/// </summary>
public sealed class WindowsHostMemoryDetector : IHostMemoryDetector
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <inheritdoc />
    public double GetTotalPhysicalMemoryGB()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var stat = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref stat) && stat.ullTotalPhys > 0)
                {
                    return stat.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                }
            }
        }
        catch
        {
            // P/Invoke 失败时回退
        }

        long availableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (availableBytes > 0)
        {
            return availableBytes / (1024.0 * 1024.0 * 1024.0);
        }

        return 16.0;
    }
}

/// <summary>宿主多实例密度建议评估结论。</summary>
/// <param name="HostMemoryGB">宿主实际物理内存（GB）。</param>
/// <param name="Tier">匹配到的密度档位名称（如“入门”、“主流”），未达基线档位时为 null。</param>
/// <param name="RecommendedLimit">建议并行运行或创建的实例上限数。</param>
/// <param name="CurrentCount">当前参与评估的实例数量。</param>
/// <param name="IsExceeded">当前实例数是否已达到或超出建议上限。</param>
/// <param name="Message">面向界面的非阻断提示文案。</param>
public sealed record DensityAdvice(
    double HostMemoryGB,
    string? Tier,
    int RecommendedLimit,
    int CurrentCount,
    bool IsExceeded,
    string Message);

/// <summary>延迟劣化评估结论。</summary>
/// <param name="BaselineLatency">单实例基线延迟。</param>
/// <param name="MeasuredLatency">多实例并发实测延迟。</param>
/// <param name="DegradationPercent">实测劣化百分比。</param>
/// <param name="MaxDegradationPercent">允许的最大劣化百分比上限。</param>
/// <param name="IsAcceptable">劣化是否在容许上限内。</param>
/// <param name="Message">评估描述文案。</param>
public sealed record RegressionEvaluation(
    double BaselineLatency,
    double MeasuredLatency,
    double DegradationPercent,
    double MaxDegradationPercent,
    bool IsAcceptable,
    string Message);

/// <summary>多开密度顾问契约。</summary>
public interface IDensityAdvisor
{
    /// <summary>评估当前实例数量是否超出宿主建议上限，并生成非阻断建议。</summary>
    /// <param name="currentCount">当前实例数（已存在或正在运行的实例数）。</param>
    /// <param name="instanceTerm">用于提示文案的实例概念中文词，默认“实例”。</param>
    /// <returns>密度建议结论。</returns>
    DensityAdvice Evaluate(int currentCount, string instanceTerm = "实例");

    /// <summary>比对单实例基线延迟与多实例并发实测延迟，判断延迟劣化是否超限。</summary>
    /// <param name="baselineLatency">单实例基线延迟。</param>
    /// <param name="measuredLatency">多实例并发实测延迟。</param>
    /// <param name="regression">指定的劣化上限契约，缺省时读取性能基线配置。</param>
    /// <returns>劣化评估结论。</returns>
    RegressionEvaluation EvaluateRegression(
        double baselineLatency,
        double measuredLatency,
        DensityRegression? regression = null);
}

/// <summary>
/// 多开密度顾问。结合宿主物理内存与性能基线契约中的 BaselineDensityProfiles，
/// 计算当前宿主对应的建议并行实例上限与档位，并在超限时提供非阻断提示通道与劣化比较 API。
/// </summary>
public sealed class InstanceDensityAdvisor : IDensityAdvisor
{
    private readonly IHostMemoryDetector _memoryDetector;
    private readonly Func<PerformanceBaseline?> _baselineProvider;

    /// <summary>
    /// 构造密度顾问。
    /// </summary>
    /// <param name="memoryDetector">宿主内存检测器，缺省使用 Windows API 检测器。</param>
    /// <param name="baselineProvider">性能基线提供者，缺省从默认规格读取器加载。</param>
    public InstanceDensityAdvisor(
        IHostMemoryDetector? memoryDetector = null,
        Func<PerformanceBaseline?>? baselineProvider = null)
    {
        _memoryDetector = memoryDetector ?? new WindowsHostMemoryDetector();
        _baselineProvider = baselineProvider ?? ResolveDefaultBaseline;
    }

    /// <inheritdoc />
    public DensityAdvice Evaluate(int currentCount, string instanceTerm = "实例")
    {
        if (string.IsNullOrWhiteSpace(instanceTerm))
        {
            instanceTerm = "实例";
        }

        double hostMemoryGB = _memoryDetector.GetTotalPhysicalMemoryGB();
        PerformanceBaseline? baseline = null;
        try
        {
            baseline = _baselineProvider();
        }
        catch
        {
            // 规格文件未准备好时采用默认基线档位
        }

        (string? tier, int limit) = CalculateTierAndLimit(hostMemoryGB, baseline);
        bool isExceeded = currentCount >= limit;

        string message = isExceeded
            ? $"宿主物理内存为 {hostMemoryGB:F1} GB（档位：{tier ?? "未分档"}），建议并行{instanceTerm}上限为 {limit} 个。当前已有 {currentCount} 个{instanceTerm}，继续创建可能超出建议上限。"
            : $"宿主物理内存为 {hostMemoryGB:F1} GB（档位：{tier ?? "未分档"}），建议并行{instanceTerm}上限为 {limit} 个。";

        return new DensityAdvice(
            hostMemoryGB,
            tier,
            limit,
            currentCount,
            isExceeded,
            message);
    }

    /// <inheritdoc />
    public RegressionEvaluation EvaluateRegression(
        double baselineLatency,
        double measuredLatency,
        DensityRegression? regression = null)
    {
        if (baselineLatency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baselineLatency), "基线延迟必须大于 0。");
        }

        DensityRegression activeRegression = regression ?? ResolveRegressionFromBaseline();
        double maxDegradation = activeRegression.MaxDegradationPercent;

        double degradationPercent = Math.Round(((measuredLatency - baselineLatency) / baselineLatency) * 100.0, 2);
        bool isAcceptable = activeRegression.AcceptsDegradation(degradationPercent);

        string message = isAcceptable
            ? $"延迟由 {baselineLatency:F1} ms 变为 {measuredLatency:F1} ms，劣化幅度为 {degradationPercent:F1}%，未超过容许上限 {maxDegradation:F1}%。"
            : $"延迟由 {baselineLatency:F1} ms 变为 {measuredLatency:F1} ms，劣化幅度为 {degradationPercent:F1}%，已超出容许上限 {maxDegradation:F1}%。";

        return new RegressionEvaluation(
            baselineLatency,
            measuredLatency,
            degradationPercent,
            maxDegradation,
            isAcceptable,
            message);
    }

    /// <summary>
    /// 根据宿主物理内存与性能基线档位计算档位与并行实例上限。
    /// 考虑系统硬件保留内存，允许向上容差 0.5 GB（例如 15.8 GB 宿主按 16 GB 档位计算）。
    /// </summary>
    /// <param name="hostMemoryGB">宿主实际物理内存（GB）。</param>
    /// <param name="baseline">性能基线。</param>
    /// <returns>匹配的档位名与建议并行上限。</returns>
    public static (string? Tier, int RecommendedLimit) CalculateTierAndLimit(
        double hostMemoryGB,
        PerformanceBaseline? baseline)
    {
        var profiles = baseline?.Density?.Profiles;
        if (profiles is null || profiles.Count == 0)
        {
            // 契约未提供时使用默认规则：16GB 对应 2 个，32GB 对应 3 个，不足 16GB 对应 1 个
            if (hostMemoryGB >= 31.5)
            {
                return ("主流", 3);
            }
            if (hostMemoryGB >= 15.5)
            {
                return ("入门", 2);
            }
            return (null, 1);
        }

        // 按 hostMemoryGB 升序排序
        var sorted = profiles.OrderBy(p => p.HostMemoryGB).ToList();
        BaselineDensityProfile? matched = null;

        foreach (var profile in sorted)
        {
            if (hostMemoryGB + 0.5 >= profile.HostMemoryGB)
            {
                matched = profile;
            }
        }

        if (matched is null)
        {
            return (null, 1);
        }

        int limit = (int)Math.Round(matched.TargetInstances.Value);
        return (matched.Tier, Math.Max(1, limit));
    }

    private static PerformanceBaseline? ResolveDefaultBaseline()
    {
        try
        {
            return SpecLoader.Default.LoadBaseline();
        }
        catch
        {
            return null;
        }
    }

    private DensityRegression ResolveRegressionFromBaseline()
    {
        try
        {
            var baseline = _baselineProvider();
            if (baseline?.Density?.Regression is not null && baseline.Density.Regression.MaxDegradationPercent > 0)
            {
                return baseline.Density.Regression;
            }
        }
        catch
        {
            // 回退到默认 30% 契约
        }

        return new DensityRegression
        {
            MaxDegradationPercent = 30.0,
            Note = "并行实例数达标的附加条件是延迟劣化不超过 30%。",
        };
    }
}
