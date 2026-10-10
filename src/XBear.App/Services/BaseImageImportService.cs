using System.Diagnostics;
using System.IO;
using XBear.Core.Abstractions;
using XBear.Core.Qemu;

namespace XBear.App.Services;

/// <summary>base 镜像导入的阶段。</summary>
public enum BaseImageImportPhase
{
    /// <summary>尚未开始。</summary>
    Idle,

    /// <summary>正在转换。</summary>
    Running,

    /// <summary>本次真正执行了转换，目标已生成。</summary>
    Succeeded,

    /// <summary>目标已是可用的 base 镜像，本次未重复转换。</summary>
    AlreadyImported,

    /// <summary>用户取消。</summary>
    Canceled,

    /// <summary>导入失败。</summary>
    Failed
}

/// <summary>
/// 一次导入的进度快照。百分比取自临时产物的真实体积增长，读不到时为 null。
/// </summary>
/// <param name="Phase">当前阶段。</param>
/// <param name="Percent">已写入百分比，取不到实测值时为 null。</param>
/// <param name="StageText">面向用户的阶段文案。</param>
/// <param name="Detail">Core 输出的最近一条过程说明，可能为空。</param>
/// <param name="Elapsed">从开始到本次快照的已用时长。</param>
public sealed record BaseImageImportProgress(
    BaseImageImportPhase Phase,
    int? Percent,
    string StageText,
    string Detail,
    TimeSpan Elapsed);

/// <summary>
/// 把 Core 的 base 镜像导入能力包装成界面可绑定的进度状态。
///
/// Core 只提供 <c>Action&lt;string&gt;</c> 形式的日志出口，生产环境用户看不到；
/// 本服务在此之上补两件事：把每条日志转成阶段反馈，并按固定间隔测量临时产物的真实体积，
/// 让耗时几十秒的转换在界面上持续可见，而不是停在起始状态。
/// </summary>
public sealed class BaseImageImportService
{
    /// <summary>Core 转换期间使用的临时产物后缀。</summary>
    public const string ImportingSuffix = ".importing";

    /// <summary>开始导入时的阶段文案。</summary>
    public const string PreparingText = "正在准备导入";

    /// <summary>本次真正执行转换后的完成文案。</summary>
    public const string ImportedText = "导入完成，base 镜像已就绪";

    /// <summary>目标已可用、未重复转换时的文案。</summary>
    public const string ReusedText = "该 base 镜像已可用，未重复转换";

    /// <summary>镜像根目录名，与应用装配时使用的约定一致。</summary>
    public const string ImagesDirectoryName = "images";

    /// <summary>默认的进度采样间隔。</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(400);

    private readonly string _imagesRoot;
    private readonly Func<Action<string>, IQcow2Manager> _managerFactory;
    private readonly TimeSpan _pollInterval;

    /// <summary>base 镜像输出根目录。</summary>
    public string ImagesRoot => _imagesRoot;

    /// <summary>
    /// 以默认采样间隔构造导入服务。
    /// </summary>
    /// <param name="imagesRoot">base 镜像的输出根目录。</param>
    /// <param name="managerFactory">
    /// 按日志出口创建镜像管理器；为 null 时直接使用真实 qemu-img 执行边界。
    /// </param>
    public BaseImageImportService(
        string imagesRoot,
        Func<Action<string>, IQcow2Manager>? managerFactory = null)
        : this(imagesRoot, managerFactory, DefaultPollInterval)
    {
    }

    /// <summary>
    /// 以指定采样间隔构造导入服务。
    /// </summary>
    /// <param name="imagesRoot">base 镜像的输出根目录。</param>
    /// <param name="managerFactory">
    /// 按日志出口创建镜像管理器；为 null 时直接使用真实 qemu-img 执行边界。
    /// </param>
    /// <param name="pollInterval">进度采样间隔。</param>
    public BaseImageImportService(
        string imagesRoot,
        Func<Action<string>, IQcow2Manager>? managerFactory,
        TimeSpan pollInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagesRoot);

        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollInterval),
                pollInterval,
                "进度采样间隔必须为正值。");
        }

        _imagesRoot = imagesRoot;
        _managerFactory = managerFactory ?? CreateDefaultManager;
        _pollInterval = pollInterval;
    }

    /// <summary>
    /// 应用默认的镜像根目录，即程序目录下的 images 子目录。
    /// </summary>
    public static string DefaultImagesRoot =>
        Path.Combine(AppContext.BaseDirectory, ImagesDirectoryName);

    /// <summary>
    /// 由源镜像路径推导出 base 镜像的输出路径。
    /// </summary>
    /// <param name="sourceImagePath">源镜像路径。</param>
    /// <returns>镜像根目录下的 qcow2 文件路径。</returns>
    public string BuildBaseImagePath(string sourceImagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagePath);

        string stem = Path.GetFileNameWithoutExtension(sourceImagePath);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "android";
        }

        return Path.Combine(_imagesRoot, stem + "." + Qcow2Manager.Qcow2Format);
    }

    /// <summary>
    /// 导入一个源镜像，并在过程中持续上报进度。
    ///
    /// 转换先写入同目录的临时产物再落位，因此取消或失败不会留下半成品；
    /// 本方法不清理产物，原子落位由 Core 负责。
    /// </summary>
    /// <param name="sourceImagePath">源镜像路径。</param>
    /// <param name="report">进度上报回调，可从任意线程调用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>终态进度；被取消时抛出 <see cref="OperationCanceledException"/>，失败时上抛 Core 异常。</returns>
    /// <exception cref="XBear.Core.Diagnostics.XBearException">源镜像不可用或转换失败时抛出。</exception>
    public async Task<BaseImageImportProgress> ImportAsync(
        string sourceImagePath,
        Action<BaseImageImportProgress> report,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagePath);
        ArgumentNullException.ThrowIfNull(report);

        string baseImagePath = BuildBaseImagePath(sourceImagePath);
        string temporaryPath = baseImagePath + ImportingSuffix;
        long sourceLength = ReadLength(sourceImagePath);

        var stopwatch = Stopwatch.StartNew();
        string detail = string.Empty;
        int? percent = null;

        void Publish(BaseImageImportPhase phase, string stageText, int? value)
        {
            report(new BaseImageImportProgress(phase, value, stageText, detail, stopwatch.Elapsed));
        }

        void PublishLog(string message)
        {
            detail = message;
            Publish(BaseImageImportPhase.Running, DescribeRunningStage(stopwatch.Elapsed), percent);
        }

        async Task PollAsync(CancellationToken token)
        {
            while (true)
            {
                await Task.Delay(_pollInterval, token).ConfigureAwait(true);
                percent = MeasurePercent(temporaryPath, sourceLength);
                Publish(BaseImageImportPhase.Running, DescribeRunningStage(stopwatch.Elapsed), percent);
            }
        }

        Publish(BaseImageImportPhase.Running, PreparingText, null);

        using var polling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Core 的日志在内部 await 之后触发，不保证回到界面线程；此处只转发，由调用方负责线程归属。
        IQcow2Manager manager = _managerFactory(PublishLog);
        Task<bool> importing = manager.ImportBaseImageAsync(sourceImagePath, baseImagePath, cancellationToken);
        Task pollingTask = PollAsync(polling.Token);

        bool converted;
        try
        {
            converted = await importing.ConfigureAwait(true);
        }
        finally
        {
            polling.Cancel();
            await DrainAsync(pollingTask).ConfigureAwait(true);
            stopwatch.Stop();
        }

        var finished = new BaseImageImportProgress(
            converted ? BaseImageImportPhase.Succeeded : BaseImageImportPhase.AlreadyImported,
            100,
            converted ? ImportedText : ReusedText,
            detail,
            stopwatch.Elapsed);

        report(finished);
        return finished;
    }

    /// <summary>阶段文案附带真实已用时长，百分比暂不可测时用户仍能看到进度在走。</summary>
    /// <param name="elapsed">已用时长。</param>
    /// <returns>阶段文案。</returns>
    private static string DescribeRunningStage(TimeSpan elapsed) =>
        $"正在转换为 base 镜像（已用 {elapsed.TotalSeconds:F0} 秒）";

    /// <summary>
    /// 按临时产物与源镜像的体积比估算已写入百分比，读不到任一侧时返回 null 而不是编造数值。
    /// </summary>
    /// <param name="temporaryPath">临时产物路径。</param>
    /// <param name="sourceLength">源镜像体积字节数。</param>
    /// <returns>0 至 99 的百分比，或 null。</returns>
    private static int? MeasurePercent(string temporaryPath, long sourceLength)
    {
        if (sourceLength <= 0)
        {
            return null;
        }

        long written = ReadLength(temporaryPath);
        if (written <= 0)
        {
            return null;
        }

        // 转换未结束时不给 100，避免进度条提前跑满后原地不动。
        long percent = written * 100 / sourceLength;
        return (int)Math.Clamp(percent, 0L, 99L);
    }

    /// <summary>读取文件体积，文件缺失或不可读时返回 0。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>体积字节数。</returns>
    private static long ReadLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0L;
        }
        catch (IOException)
        {
            return 0L;
        }
        catch (UnauthorizedAccessException)
        {
            return 0L;
        }
    }

    /// <summary>等待采样任务收尾，采样因取消而结束时不再上抛。</summary>
    /// <param name="task">采样任务。</param>
    /// <returns>异步任务。</returns>
    private static async Task DrainAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 采样任务以取消结束是预期收尾，原始异常由调用方处理。
        }
    }

    /// <summary>按日志出口创建使用真实 qemu-img 执行边界的镜像管理器。</summary>
    /// <param name="log">过程日志出口。</param>
    /// <returns>镜像管理器。</returns>
    private static IQcow2Manager CreateDefaultManager(Action<string> log)
    {
        var paths = new QemuPaths();
        return new Qcow2Manager(paths, null, null, log);
    }
}
