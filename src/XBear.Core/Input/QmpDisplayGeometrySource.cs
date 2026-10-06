using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Input;

/// <summary>
/// 从 QMP 取 guest 显示尺寸。让 QEMU 把当前画面写到指定文件，
/// 再从该文件的头部读出宽高，这样尺寸来自 guest 当前实际显示分辨率，而不是任何写死的默认值。
/// </summary>
public sealed class QmpDisplayGeometrySource
{
    private readonly IQmpClient _qmpClient;
    private readonly InputChannelOptions _options;
    private readonly Func<ScreenGeometry?>? _fallback;

    /// <summary>
    /// 初始化显示尺寸来源。
    /// </summary>
    /// <param name="qmpClient">QMP 客户端。</param>
    /// <param name="options">输入通道参数，提供端口与临时文件目录。</param>
    /// <param name="fallback">查询失败时使用的兜底尺寸，为 null 时直接返回失败。</param>
    public QmpDisplayGeometrySource(
        IQmpClient qmpClient,
        InputChannelOptions options,
        Func<ScreenGeometry?>? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(qmpClient);
        ArgumentNullException.ThrowIfNull(options);

        _qmpClient = qmpClient;
        _options = options;
        _fallback = fallback;
    }

    /// <summary>
    /// 查询 guest 显示尺寸。取不到时返回调用方给的兜底尺寸，两者都没有则为 null，
    /// 由调用方决定是按无缩放处理还是报错，绝不猜一个分辨率。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>guest 显示尺寸，无法确定时为 null。</returns>
    public async Task<ScreenGeometry?> TryGetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await QueryAsync(cancellationToken).ConfigureAwait(false) ?? _fallback?.Invoke();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 尺寸是尽力而为的辅助信息，拿不到时退回调用方给的尺寸并保留原因，不影响投递主流程。
            ScreenGeometry? fallback = _fallback?.Invoke();
            if (fallback is null)
            {
                throw new XBearException(
                    ErrorCategory.Protocol,
                    $"无法确定 guest 显示尺寸：{ex.Message}",
                    "请确认实例已完全启动，或由调用方直接提供显示尺寸。",
                    ex);
            }

            return fallback;
        }
    }

    private async Task<ScreenGeometry?> QueryAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.ScreenshotDirectory);
        string path = Path.Combine(
            _options.ScreenshotDirectory,
            $"xbear-geometry-{Guid.NewGuid():N}.ppm");

        try
        {
            await _qmpClient
                .ExecuteAsync(
                    "screendump",
                    new Dictionary<string, object>
                    {
                        ["filename"] = path,
                        ["format"] = "ppm",
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!File.Exists(path))
            {
                return null;
            }

            return PpmHeader.TryReadFrom(path);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时文件清理失败不影响投递结果，交给系统临时目录回收。
        }
    }
}
