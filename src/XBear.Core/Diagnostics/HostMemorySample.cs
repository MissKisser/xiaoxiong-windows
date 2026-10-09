using System.Text.Json.Serialization;

namespace XBear.Core.Diagnostics;

/// <summary>
/// 宿主物理内存采样。物理内存总量决定多开上限，
/// 空闲内存则是判断「本次多开数据是否可信」的必要协变量：
/// 宿主内存紧张时 QEMU 进程会被换出，冷启动耗时随之失真。
/// </summary>
public sealed class HostMemorySample
{
    /// <summary>物理内存总量（字节）。</summary>
    [JsonPropertyName("totalPhysicalBytes")]
    public long TotalPhysicalBytes { get; set; }

    /// <summary>可用物理内存（字节）。</summary>
    [JsonPropertyName("availablePhysicalBytes")]
    public long AvailablePhysicalBytes { get; set; }

    /// <summary>物理内存总量（GB）。</summary>
    [JsonPropertyName("totalPhysicalGB")]
    public double TotalPhysicalGB { get; set; }

    /// <summary>可用物理内存（GB）。</summary>
    [JsonPropertyName("availablePhysicalGB")]
    public double AvailablePhysicalGB { get; set; }

    /// <summary>采样时间点。</summary>
    [JsonPropertyName("sampledAt")]
    public DateTimeOffset SampledAt { get; set; }
}

/// <summary>宿主物理内存采样器契约。</summary>
public interface IHostMemorySampler
{
    /// <summary>取一次宿主物理内存采样。</summary>
    /// <returns>采样结果，检测不可用时返回 null。</returns>
    HostMemorySample? Sample();
}