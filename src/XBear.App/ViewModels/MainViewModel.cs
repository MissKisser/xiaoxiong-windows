using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XBear.App.Presentation;
using XBear.App.Services;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.App.ViewModels;

/// <summary>主窗口视图模型，承载实例列表、选中详情、顶部风险标识与错误呈现。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IInstanceRepository _repository;
    private readonly InstanceManager? _manager;
    private readonly DiagnosticsExporter _diagnostics;
    private readonly TerminologyCatalog _terms;
    private readonly BaseImageImportService _importer;
    private readonly BootAssetExtractorService? _bootAssetExtractor;
    private readonly Dictionary<string, ImageSpec> _images;
    private CancellationTokenSource? _importCts;
    private InputProbeResult _inputChannel = new(InputChannelKind.Unknown);
    private readonly Views.IProjectionWindowHost? _projectionWindows;
    private readonly Views.IFileTransferWindowHost? _fileTransferWindows;
    private readonly Views.IApplicationWindowHost? _applicationWindows;
    private readonly Views.IModuleWindowHost? _moduleWindows;
    private readonly Views.ISnapshotWindowHost? _snapshotWindows;

    [ObservableProperty]
    private InstanceListItemViewModel? _selected;

    [ObservableProperty]
    private string _errorTitle = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _errorRemediation = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private string _importStageText = string.Empty;

    [ObservableProperty]
    private string _importDetailText = string.Empty;

    [ObservableProperty]
    private double _importPercent;

    [ObservableProperty]
    private string _importPercentText = string.Empty;

    [ObservableProperty]
    private bool _hasImportProgress;

    [ObservableProperty]
    private string _importFailureText = string.Empty;

    /// <summary>
    /// 构造主视图模型。
    /// </summary>
    /// <param name="repository">实例配置仓库。</param>
    /// <param name="manager">实例生命周期编排器，未接入时为 null，界面退化为只读浏览。</param>
    /// <param name="images">镜像清单，按标识索引。</param>
    /// <param name="diagnostics">诊断包导出器。</param>
    /// <param name="terms">界面文案术语来源。</param>
    /// <param name="version">版本契约文档，为空时从诊断包导出器获取。</param>
    /// <param name="importer">base 镜像导入服务，为 null 时使用默认镜像根目录下的真实导入。</param>
    /// <param name="bootAssetExtractor">引导资产提取服务，为 null 时不执行引导资产提取与定制。</param>
    /// <param name="projectionWindows">投屏窗口宿主，为 null 时不提供投屏入口。</param>
    /// <param name="fileTransferWindows">文件传输窗口宿主，为 null 时不提供文件传输入口。</param>
    /// <param name="applicationWindows">应用管理窗口宿主，为 null 时不提供应用管理入口。</param>
    /// <param name="moduleWindows">模块管理窗口宿主，为 null 时不提供模块管理入口。</param>
    /// <param name="snapshotWindows">快照管理窗口宿主，为 null 时不提供快照管理入口。</param>
    public MainViewModel(
        IInstanceRepository repository,
        InstanceManager? manager,
        IReadOnlyDictionary<string, ImageSpec> images,
        DiagnosticsExporter diagnostics,
        TerminologyCatalog terms,
        VersionDocument? version = null,
        BaseImageImportService? importer = null,
        BootAssetExtractorService? bootAssetExtractor = null,
        Views.IProjectionWindowHost? projectionWindows = null,
        Views.IFileTransferWindowHost? fileTransferWindows = null,
        Views.IApplicationWindowHost? applicationWindows = null,
        Views.IModuleWindowHost? moduleWindows = null,
        Views.ISnapshotWindowHost? snapshotWindows = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(terms);

        _repository = repository;
        _manager = manager;
        _images = new Dictionary<string, ImageSpec>(images, StringComparer.Ordinal);
        _diagnostics = diagnostics;
        _terms = terms;
        _importer = importer ?? new BaseImageImportService(BaseImageImportService.DefaultImagesRoot);
        _bootAssetExtractor = bootAssetExtractor;
        _projectionWindows = projectionWindows;
        _fileTransferWindows = fileTransferWindows;
        _applicationWindows = applicationWindows;
        _moduleWindows = moduleWindows;
        _snapshotWindows = snapshotWindows;

        VersionDocument versionDoc = version ?? diagnostics.Version;
        ProductVersion = versionDoc.Product.Version;
        SpecVersion = versionDoc.Spec.Version;
        BuildId = version is not null
            ? versionDoc.FormatBuildId(DiagnosticsExporter.ResolveCommitShort())
            : diagnostics.BuildId;

        _riskBannerText = string.Empty;
        _detailText = string.Empty;

        if (_manager is not null)
        {
            _manager.StateChanged += OnManagerStateChanged;
        }
    }

    /// <summary>产品版本，来自版本契约，禁止硬编码。</summary>
    public string ProductVersion { get; }

    /// <summary>规格版本，来自版本契约，禁止硬编码。</summary>
    public string SpecVersion { get; }

    /// <summary>构建标识，由产品版本与提交短哈希拼装而成。</summary>
    public string BuildId { get; }

    /// <summary>打开关于窗口的回调动作，供界面或测试接管。</summary>
    public Action? ShowAboutAction { get; set; }

    /// <summary>打开关于窗口命令。</summary>
    [RelayCommand]
    public void OpenAbout()
    {
        if (ShowAboutAction is not null)
        {
            ShowAboutAction();
            return;
        }

        var window = new Views.AboutWindow(this);
        window.ShowDialog();
    }

    /// <summary>全部实例。</summary>
    public ObservableCollection<InstanceListItemViewModel> Instances { get; } = new();

    /// <summary>实例标题文案，取自术语表。</summary>
    public string InstanceTerm => _terms.Instance;

    /// <summary>镜像标题文案，取自术语表。</summary>
    public string ImageTerm => _terms.Image;

    /// <summary>暴露级别标题文案，取自术语表。</summary>
    public string ExposureTerm => _terms.Exposure;

    /// <summary>输入通道标题文案，取自术语表。</summary>
    public string InputChannelTerm => _terms.InputChannel;

    /// <summary>保真度标题文案，取自术语表。</summary>
    public string FidelityTerm => _terms.Fidelity;

    /// <summary>启动动作文案，取自术语表。</summary>
    public string StartTerm => _terms.Start;

    /// <summary>停止动作文案，取自术语表。</summary>
    public string StopTerm => _terms.Stop;

    /// <summary>创建实例按钮文案。</summary>
    public string CreateInstanceText => $"创建{_terms.Instance}";

    /// <summary>导出诊断包按钮文案。</summary>
    public string ExportDiagnosticsText => $"导出{_terms.Diagnostics}";

    /// <summary>清除错误按钮文案。</summary>
    public string DismissErrorText => "知道了";

    /// <summary>空列表提示。</summary>
    public string EmptyHintText => $"尚未创建{_terms.Instance}。";

    /// <summary>镜像导入区标题文案。</summary>
    public string ImportSectionTitle => $"{_terms.Image}导入";

    /// <summary>导入按钮文案。</summary>
    public string ImportBaseImageText => $"导入{_terms.Image}";

    /// <summary>取消导入按钮文案。</summary>
    public string CancelImportText => "取消导入";

    /// <summary>导入区说明文案。</summary>
    public string ImportHintText =>
        $"选择本地 Android {_terms.Image}文件（ISO 或 img），转换为{_terms.Instance}可用的 base 镜像。"
        + "转换耗时与文件体积和磁盘速度相关，通常需要几十秒，进度会实时显示，过程中可随时取消。";

    /// <summary>导入失败后的阶段文案。</summary>
    public string ImportFailedText => $"{_terms.Image}导入失败";

    /// <summary>导入被取消后的阶段文案。</summary>
    public string ImportCanceledText => "已取消导入，未生成任何文件";

    /// <summary>是否允许开始导入。</summary>
    public bool CanImportBaseImage => !IsImporting;

    /// <summary>是否允许取消导入。</summary>
    public bool CanCancelImport => IsImporting;

    [ObservableProperty]
    private string _riskBannerText;

    [ObservableProperty]
    private string _detailText;

    [ObservableProperty]
    private string _inputChannelText = string.Empty;

    [ObservableProperty]
    private string _inputChannelDetail = string.Empty;

    [ObservableProperty]
    private bool _inputChannelUnavailable;

    /// <summary>保真度六项的逐条展示内容。</summary>
    public ObservableCollection<FidelityItemPresentation> FidelityItems { get; } = new();

    /// <summary>保真度区标题文案。</summary>
    public string FidelityTitle => $"{_terms.Fidelity}（逐条实测）";

    /// <summary>实例详情区标题文案，取自术语表。</summary>
    public string DetailTitle => $"{_terms.Instance}详情";

    /// <summary>输入通道区标题文案。</summary>
    public string InputChannelTitle => $"{_terms.InputChannel}状态";

    /// <summary>是否需要显示顶部风险标识。</summary>
    public bool HasRisk => !string.IsNullOrEmpty(RiskBannerText);

    /// <summary>启动按钮是否可用。</summary>
    public bool CanStart =>
        _manager is not null &&
        Selected is not null &&
        InstanceStateMapper.Describe(Selected.State).CanStart;

    /// <summary>停止按钮是否可用。</summary>
    public bool CanStop =>
        _manager is not null &&
        Selected is not null &&
        InstanceStateMapper.Describe(Selected.State).CanStop;

    /// <summary>投屏入口是否可用。投屏要求实例处于运行态，未运行时不得开放。</summary>
    public bool CanOpenProjection =>
        _projectionWindows is not null &&
        Selected is not null &&
        Selected.State == InstanceState.Running;

    /// <summary>打开投屏按钮文案。</summary>
    public string OpenProjectionText => $"打开{_terms.Projection}";

    /// <summary>
    /// 为选中且运行中的实例打开投屏窗口。同一实例已开投屏时置前而不是重复打开。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenProjection))]
    public void OpenProjection()
    {
        if (_projectionWindows is null || Selected is null)
        {
            return;
        }

        _projectionWindows.Open(Selected.Id, Selected.DisplayName, ProjectionOwner);
    }

    /// <summary>
    /// 关闭选中实例的投屏窗口。未打开时不做任何事。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenProjection))]
    public void CloseSelectedProjection()
    {
        if (_projectionWindows is null || Selected is null)
        {
            return;
        }

        _projectionWindows.Close(Selected.Id);
    }

    /// <summary>文件传输入口是否可用。要求实例处于运行态。</summary>
    public bool CanOpenFileTransfer =>
        _fileTransferWindows is not null &&
        Selected is not null &&
        Selected.State == InstanceState.Running;

    /// <summary>打开文件传输按钮文案。</summary>
    public string OpenFileTransferText => string.Concat("打开", _terms.FileTransfer);

    /// <summary>
    /// 为选中且运行中的实例打开文件传输窗口。同一实例已有窗口时置前。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenFileTransfer))]
    public void OpenFileTransfer()
    {
        if (_fileTransferWindows is null || Selected is null)
        {
            return;
        }

        _fileTransferWindows.Open(Selected.Id, Selected.DisplayName, ProjectionOwner);
    }

    /// <summary>
    /// 关闭选中实例的文件传输窗口。未打开时不做任何事。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenFileTransfer))]
    public void CloseSelectedFileTransfer()
    {
        if (_fileTransferWindows is null || Selected is null)
        {
            return;
        }

        _fileTransferWindows.Close(Selected.Id);
    }

    /// <summary>应用管理入口是否可用。要求实例处于运行态。</summary>
    public bool CanOpenApplications =>
        _applicationWindows is not null &&
        Selected is not null &&
        Selected.State == InstanceState.Running;

    /// <summary>管理应用按钮文案。</summary>
    public string OpenApplicationsText => string.Concat("管理", _terms.Application);

    /// <summary>
    /// 为选中且运行中的实例打开应用管理窗口。同一实例已有窗口时置前。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenApplications))]
    public void OpenApplications()
    {
        if (_applicationWindows is null || Selected is null)
        {
            return;
        }

        _applicationWindows.Open(Selected.Id, Selected.DisplayName, ProjectionOwner);
    }

    /// <summary>
    /// 关闭选中实例的应用管理窗口。未打开时不做任何事。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenApplications))]
    public void CloseSelectedApplications()
    {
        if (_applicationWindows is null || Selected is null)
        {
            return;
        }

        _applicationWindows.Close(Selected.Id);
    }

    /// <summary>模块管理入口是否可用。要求实例处于运行态。</summary>
    public bool CanOpenModules =>
        _moduleWindows is not null &&
        Selected is not null &&
        Selected.State == InstanceState.Running;

    /// <summary>管理模块按钮文案。</summary>
    public string OpenModulesText => string.Concat("管理", _terms.Module);

    /// <summary>
    /// 为选中且运行中的实例打开模块管理窗口。同一实例已有窗口时置前。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenModules))]
    public void OpenModules()
    {
        if (_moduleWindows is null || Selected is null)
        {
            return;
        }

        _moduleWindows.Open(Selected.Id, Selected.DisplayName, ProjectionOwner);
    }

    /// <summary>
    /// 关闭选中实例的模块管理窗口。未打开时不做任何事。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenModules))]
    public void CloseSelectedModules()
    {
        if (_moduleWindows is null || Selected is null)
        {
            return;
        }

        _moduleWindows.Close(Selected.Id);
    }

    /// <summary>
    /// 快照管理入口是否可用。快照可列举也可删除，因此只要求选中了实例；
    /// 是否允许创建由快照管理窗口按实例运行态裁定。
    /// </summary>
    public bool CanOpenSnapshots => _snapshotWindows is not null && Selected is not null;

    /// <summary>管理快照按钮文案。</summary>
    public string OpenSnapshotsText => string.Concat("管理", _terms.Snapshot);

    /// <summary>
    /// 为选中的实例打开快照管理窗口。同一实例已有窗口时置前。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenSnapshots))]
    public void OpenSnapshots()
    {
        if (_snapshotWindows is null || Selected is null)
        {
            return;
        }

        _snapshotWindows.Open(Selected.Id, Selected.DisplayName, ProjectionOwner);
    }

    /// <summary>
    /// 关闭选中实例的快照管理窗口。未打开时不做任何事。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenSnapshots))]
    public void CloseSelectedSnapshots()
    {
        if (_snapshotWindows is null || Selected is null)
        {
            return;
        }

        _snapshotWindows.Close(Selected.Id);
    }

    /// <summary>投屏窗口的宿主窗口，供视图模型在开窗时指定归属。</summary>
    public Window? ProjectionOwner { get; set; }

    /// <summary>
    /// 从仓库加载实例列表并刷新选中项。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            IReadOnlyList<InstanceSpec> specs = await _repository.ListAsync(cancellationToken);
            string? selectedId = Selected?.Id;

            Instances.Clear();
            foreach (InstanceSpec spec in specs)
            {
                Instances.Add(BuildItem(spec));
            }

            Selected = selectedId is null
                ? Instances.FirstOrDefault()
                : Instances.FirstOrDefault(i => string.Equals(i.Id, selectedId, StringComparison.Ordinal))
                      ?? Instances.FirstOrDefault();
        }
        catch (Exception ex) when (Present(ex))
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 启动选中实例。启动成功后立即探测该实例的输入通道，使界面反映真实生效的通路。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    [RelayCommand(CanExecute = nameof(CanStart))]
    public async Task StartSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (_manager is null || Selected is null)
        {
            return;
        }

        string instanceId = Selected.Id;
        await RunGuardedAsync(() => _manager.StartAsync(instanceId, cancellationToken)).ConfigureAwait(true);

        // 只有真正进入运行态才探测；启动失败时保留原有结论并由错误横幅说明原因。
        if (_manager.GetState(instanceId) is not InstanceState.Running)
        {
            return;
        }

        await ProbeInputChannelAsync(instanceId, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// 重新探测选中实例的输入通道。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    [RelayCommand]
    public async Task ProbeSelectedInputChannelAsync(CancellationToken cancellationToken = default)
    {
        if (_manager is null || Selected is null)
        {
            return;
        }

        await ProbeInputChannelAsync(Selected.Id, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// 停止选中实例。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    [RelayCommand(CanExecute = nameof(CanStop))]
    public async Task StopSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (_manager is null || Selected is null)
        {
            return;
        }

        await RunGuardedAsync(() => _manager.StopAsync(Selected.Id, cancellationToken)).ConfigureAwait(true);
    }

    /// <summary>
    /// 把用户选择的本地 Android 镜像导入为 base 镜像。
    ///
    /// 转换耗时较长，过程通过阶段文案、实测百分比与最近一条过程说明反馈；
    /// 用户可在任何阶段取消，取消后不会留下半成品。
    /// </summary>
    /// <param name="sourceImagePath">用户选择的源镜像路径。</param>
    /// <returns>异步任务。</returns>
    [RelayCommand(CanExecute = nameof(CanImportBaseImage))]
    public async Task ImportBaseImageAsync(string sourceImagePath)
    {
        if (string.IsNullOrWhiteSpace(sourceImagePath))
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _importCts = cts;

        DismissError();
        IsImporting = true;
        HasImportProgress = true;
        ImportFailureText = string.Empty;
        ImportPercent = 0;
        ImportPercentText = string.Empty;
        ImportDetailText = string.Empty;
        ImportStageText = BaseImageImportService.PreparingText;

        try
        {
            await _importer.ImportAsync(sourceImagePath, OnImportProgress, cts.Token).ConfigureAwait(true);

            if (_bootAssetExtractor is not null && IsIsoSource(sourceImagePath))
            {
                cts.Token.ThrowIfCancellationRequested();

                (string kernelName, string initrdName) = ResolveBootAssetNames(sourceImagePath);
                string stem = Path.GetFileNameWithoutExtension(sourceImagePath);
                string targetDir = Path.Combine(_importer.ImagesRoot, stem);

                await _bootAssetExtractor.ExtractAsync(
                    sourceImagePath,
                    kernelName,
                    initrdName,
                    OnExtractProgress,
                    targetDir,
                    cts.Token).ConfigureAwait(true);

                SyncBootAssetsToImagesRoot(targetDir, _importer.ImagesRoot, kernelName, initrdName);
            }

            ImportPercent = 100;
            ImportPercentText = "100%";
            ImportStageText = BaseImageImportService.ImportedText;
        }
        catch (OperationCanceledException)
        {
            // 取消不是失败：Core 已把半成品清理掉，只需如实告知本次没有产出。
            ImportStageText = ImportCanceledText;
            ImportPercentText = string.Empty;
        }
        catch (Exception ex)
        {
            // 无论何种异常都把原因摆到界面上，不让转换失败表现为无反馈。
            ImportStageText = ImportFailedText;
            ImportPercentText = string.Empty;
            ImportFailureText = DescribeImportFailure(ex);
            Present(ex);
        }
        finally
        {
            _importCts = null;
            IsImporting = false;
        }
    }

    /// <summary>
    /// 取消正在进行的镜像导入。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancelImport))]
    public void CancelImport() => _importCts?.Cancel();

    /// <summary>
    /// 接收导入进度。Core 的进度回调可能来自后台线程，这里统一切回界面线程再改属性。
    /// </summary>
    /// <param name="progress">进度快照。</param>
    private void OnImportProgress(BaseImageImportProgress progress)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyImportProgress(progress);
            return;
        }

        dispatcher.BeginInvoke(new Action(() => ApplyImportProgress(progress)));
    }

    /// <summary>
    /// 把进度快照刷到界面。缺失的过程说明保留上一条，避免采样节奏把已有信息冲掉。
    /// </summary>
    /// <param name="progress">进度快照。</param>
    private void ApplyImportProgress(BaseImageImportProgress progress)
    {
        HasImportProgress = true;
        ImportStageText = progress.StageText;

        if (!string.IsNullOrWhiteSpace(progress.Detail))
        {
            ImportDetailText = progress.Detail;
        }

        if (progress.Percent is not int percent)
        {
            return;
        }

        ImportPercent = percent;
        ImportPercentText = $"{percent}%";
    }

    /// <summary>
    /// 接收引导资产提取进度回调。
    /// </summary>
    /// <param name="progress">引导资产提取进度快照。</param>
    private void OnExtractProgress(BootAssetExtractionProgress progress)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyExtractProgress(progress);
            return;
        }

        dispatcher.BeginInvoke(new Action(() => ApplyExtractProgress(progress)));
    }

    /// <summary>
    /// 将引导资产提取进度刷到界面。
    /// </summary>
    /// <param name="progress">引导资产提取进度快照。</param>
    private void ApplyExtractProgress(BootAssetExtractionProgress progress)
    {
        HasImportProgress = true;
        if (progress.Phase == BootAssetExtractionPhase.Running)
        {
            ImportStageText = string.IsNullOrWhiteSpace(progress.CurrentFile)
                ? "正在提取引导资产"
                : $"正在提取引导资产（{progress.CurrentFile}）";
            ImportDetailText = $"正在提取与定制引导资产：{progress.CurrentFile}";
        }
    }

    /// <summary>判断源文件是否为 ISO 镜像或带有 ISO9660 卷头。</summary>
    private static bool IsIsoSource(string path)
    {
        if (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 17 * 2048)
            {
                return false;
            }

            stream.Seek(16 * 2048, SeekOrigin.Begin);
            byte[] header = new byte[6];
            int read = stream.Read(header, 0, 6);
            return read == 6 &&
                   header[0] == 0x01 &&
                   header[1] == (byte)'C' &&
                   header[2] == (byte)'D' &&
                   header[3] == (byte)'0' &&
                   header[4] == (byte)'0' &&
                   header[5] == (byte)'1';
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 解析目标镜像的引导资产文件名（kernel 与 initrd.img）。
    /// </summary>
    private (string Kernel, string Initrd) ResolveBootAssetNames(string sourceImagePath)
    {
        string stem = Path.GetFileNameWithoutExtension(sourceImagePath);
        if (_images.TryGetValue(stem, out ImageSpec? spec) && spec.Boot is not null)
        {
            return (
                string.IsNullOrWhiteSpace(spec.Boot.Kernel) ? "kernel" : spec.Boot.Kernel,
                string.IsNullOrWhiteSpace(spec.Boot.Initrd) ? "initrd.img" : spec.Boot.Initrd);
        }

        foreach (ImageSpec image in _images.Values)
        {
            if (image.Boot is not null)
            {
                return (
                    string.IsNullOrWhiteSpace(image.Boot.Kernel) ? "kernel" : image.Boot.Kernel,
                    string.IsNullOrWhiteSpace(image.Boot.Initrd) ? "initrd.img" : image.Boot.Initrd);
            }
        }

        return ("kernel", "initrd.img");
    }

    /// <summary>
    /// 同步引导资产到镜像根目录，使直接引用根目录与引用子目录的两种路径皆可直达。
    /// </summary>
    private static void SyncBootAssetsToImagesRoot(
        string targetDir,
        string imagesRoot,
        string kernelName,
        string initrdName)
    {
        if (string.Equals(targetDir, imagesRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            string sourceKernel = Path.Combine(targetDir, kernelName);
            string destKernel = Path.Combine(imagesRoot, kernelName);
            if (File.Exists(sourceKernel) && !File.Exists(destKernel))
            {
                File.Copy(sourceKernel, destKernel, overwrite: true);
            }

            string sourceInitrd = Path.Combine(targetDir, initrdName);
            string destInitrd = Path.Combine(imagesRoot, initrdName);
            if (File.Exists(sourceInitrd))
            {
                File.Copy(sourceInitrd, destInitrd, overwrite: true);
            }

            string stockName = initrdName + InitrdCustomizerService.StockSuffix;
            string sourceStock = Path.Combine(targetDir, stockName);
            string destStock = Path.Combine(imagesRoot, stockName);
            if (File.Exists(sourceStock))
            {
                File.Copy(sourceStock, destStock, overwrite: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 把导入异常整理为界面可读的文本，沿用统一的错误分类口径。
    /// </summary>
    /// <param name="ex">导入异常。</param>
    /// <returns>含分类标题、原因与处置建议的文本。</returns>
    private string DescribeImportFailure(Exception ex)
    {
        (Presentation.ErrorCategoryText text, string? remediation) = ErrorPresenter.Describe(ex);

        return string.IsNullOrWhiteSpace(remediation)
            ? $"{text.Title}：{ex.Message}"
            : $"{text.Title}：{ex.Message}{Environment.NewLine}{remediation}";
    }

    /// <summary>刷新导入相关命令的可执行状态。</summary>
    private void NotifyImportCommands()
    {
        ImportBaseImageCommand.NotifyCanExecuteChanged();
        CancelImportCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsImportingChanged(bool value) => NotifyImportCommands();

    /// <summary>
    /// 导出诊断包到用户选择的目录。
    /// </summary>
    /// <param name="directory">用户选择的输出目录。</param>
    /// <returns>导出结果的说明文本，失败时为空。</returns>
    [RelayCommand]
    public async Task<string?> ExportDiagnosticsAsync(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        return await RunGuardedAsync(async () =>
        {
            string? instanceId = Selected?.Id;
            string path = await _diagnostics
                .ExportAsync(directory, instanceId, _repository, _manager)
                .ConfigureAwait(true);
            return path;
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 更新选中实例的输入通道探测结论并刷新展示。
    /// </summary>
    /// <param name="result">输入通路探测结论。</param>
    public void UpdateInputChannel(InputProbeResult result)
    {        ArgumentNullException.ThrowIfNull(result);
        _inputChannel = result;
        RefreshInputChannel();
    }

    /// <summary>
    /// 清除错误提示。
    /// </summary>
    [RelayCommand]
    public void DismissError()
    {
        HasError = false;
        ErrorTitle = string.Empty;
        ErrorMessage = string.Empty;
        ErrorRemediation = string.Empty;
    }

    /// <summary>
    /// 在统一的忙碌态与错误呈现下执行操作。
    /// </summary>
    /// <param name="action">待执行的操作。</param>
    /// <returns>异步任务。</returns>
    private async Task RunGuardedAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex) when (Present(ex))
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 探测指定实例的输入通道并把结论刷到界面。探测失败不抛出未处理异常：
    /// 结论已包含各通路失败原因，直接呈现给用户；仅调用方主动取消才原样上抛。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task ProbeInputChannelAsync(string instanceId, CancellationToken cancellationToken)
    {
        if (_manager is null)
        {
            return;
        }

        try
        {
            InputProbeResult result =
                await _manager.ProbeInputChannelAsync(instanceId, cancellationToken).ConfigureAwait(true);

            // 探测期间用户可能已切换选中项，结论只回填给发起探测的那个实例。
            if (Selected is not null && string.Equals(Selected.Id, instanceId, StringComparison.Ordinal))
            {
                UpdateInputChannel(result);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消不是失败，保留此前的结论。
        }
        catch (Exception ex) when (Present(ex))
        {
        }
    }

    private async Task<string?> RunGuardedAsync(Func<Task<string?>> action)
    {
        IsBusy = true;
        try
        {
            return await action().ConfigureAwait(true);
        }
        catch (Exception ex) when (Present(ex))
        {
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private InstanceListItemViewModel BuildItem(InstanceSpec spec)
    {
        string imageName = _images.TryGetValue(spec.ImageRef, out ImageSpec? image) ? image.DisplayName : string.Empty;
        InstanceState state = _manager?.GetState(spec.Id) ?? InstanceState.Stopped;
        var item = new InstanceListItemViewModel(spec, state, imageName);

        if (_images.TryGetValue(spec.ImageRef, out ImageSpec? meta) &&
            !string.IsNullOrWhiteSpace(meta.AndroidVersion))
        {
            item.AndroidVersion = meta.AndroidVersion;
        }

        return item;
    }

    partial void OnSelectedChanged(InstanceListItemViewModel? value)
    {
        RefreshDetail();
        RefreshRiskBanner();

        StartSelectedCommand.NotifyCanExecuteChanged();
        StopSelectedCommand.NotifyCanExecuteChanged();
        OpenProjectionCommand.NotifyCanExecuteChanged();
        CloseSelectedProjectionCommand.NotifyCanExecuteChanged();
        OpenFileTransferCommand.NotifyCanExecuteChanged();
        CloseSelectedFileTransferCommand.NotifyCanExecuteChanged();
        OpenApplicationsCommand.NotifyCanExecuteChanged();
        CloseSelectedApplicationsCommand.NotifyCanExecuteChanged();
        OpenModulesCommand.NotifyCanExecuteChanged();
        CloseSelectedModulesCommand.NotifyCanExecuteChanged();
        OpenSnapshotsCommand.NotifyCanExecuteChanged();
        CloseSelectedSnapshotsCommand.NotifyCanExecuteChanged();
    }

    private void RefreshDetail()
    {
        FidelityItems.Clear();

        if (Selected is null)
        {
            DetailText = EmptyHintText;
            InputChannelText = "尚未选择";
            InputChannelDetail = string.Empty;
            InputChannelUnavailable = true;
            return;
        }

        DetailText = string.Join(
            Environment.NewLine,
            $"{InstanceTerm}：{Selected.DisplayName}",
            $"{ImageTerm}：{Selected.ImageDisplayName}",
            $"Android 版本：{Selected.AndroidVersion}",
            $"{ExposureTerm}：{Selected.ExposureLabel}");

        RefreshInputChannel();
        RefreshFidelity();
    }

    /// <summary>
    /// 刷新输入通道展示。两条通路都不可用时显示明确原因，不显示成就绪。
    /// </summary>
    private void RefreshInputChannel()
    {
        InputChannelPresentation presentation = InputChannelMapper.Describe(
            new InputProbeResult(
                _inputChannel.Channel,
                _inputChannel.NativeFailure,
                _inputChannel.ProjectionFailure),
            _terms);

        InputChannelText = presentation.Label;
        InputChannelDetail = presentation.Detail;
        InputChannelUnavailable = presentation.IsUnavailable;
    }

    /// <summary>
    /// 刷新保真度六项展示，未实测如实显示为未实测。
    /// </summary>
    private void RefreshFidelity()
    {
        FidelityItems.Clear();

        if (Selected is null ||
            !_images.TryGetValue(Selected.ImageRef, out ImageSpec? image) ||
            image.Verified?.Fidelity is not FidelitySet fidelity)
        {
            return;
        }

        foreach (FidelityItemPresentation item in FidelityMapper.Describe(fidelity))
        {
            FidelityItems.Add(item);
        }
    }

    /// <summary>
    /// 当实例的 adb 端口实际监听在对外地址时，顶部持续显示风险标识，直到配置改回回环。
    /// </summary>
    private void RefreshRiskBanner()
    {
        if (Selected is null || !Selected.IsAdbExternallyBound)
        {
            RiskBannerText = string.Empty;
        }
        else
        {
            RiskBannerText =
                $"风险：{Selected.DisplayName} 的 adb 端口正在监听 0.0.0.0，同网段乃至公网设备均可连接。" +
                $"请把{ExposureTerm}改回 loopback，直到确有对外需求。";
        }

        OnPropertyChanged(nameof(HasRisk));
    }

    private void OnManagerStateChanged(object? sender, InstanceStateChangedEventArgs e)
    {
        InstanceListItemViewModel? item =
            Instances.FirstOrDefault(i => string.Equals(i.Id, e.InstanceId, StringComparison.Ordinal));

        if (item is null)
        {
            return;
        }

        item.ApplyState(e.NewState);

        if (e.NewState is not InstanceState.Running)
        {
            // 实例已停止时投屏画面不再更新，投屏窗口必须随之关闭，
            // 否则窗口会一直停在最后一帧上，让人以为画面还活着。
            _projectionWindows?.Close(e.InstanceId);
            _fileTransferWindows?.Close(e.InstanceId);
            _applicationWindows?.Close(e.InstanceId);
            _moduleWindows?.Close(e.InstanceId);

            // 快照窗口不在此处收敛：恢复流程本身要让实例经历停止与重新启动，
            // 停止即关闭会让用户看不到恢复结果，只在实例进入故障态时收敛。
        }

        OpenProjectionCommand.NotifyCanExecuteChanged();
        CloseSelectedProjectionCommand.NotifyCanExecuteChanged();
        OpenFileTransferCommand.NotifyCanExecuteChanged();
        CloseSelectedFileTransferCommand.NotifyCanExecuteChanged();
        OpenApplicationsCommand.NotifyCanExecuteChanged();
        CloseSelectedApplicationsCommand.NotifyCanExecuteChanged();
        OpenModulesCommand.NotifyCanExecuteChanged();
        CloseSelectedModulesCommand.NotifyCanExecuteChanged();
        OpenSnapshotsCommand.NotifyCanExecuteChanged();
        CloseSelectedSnapshotsCommand.NotifyCanExecuteChanged();

        if (e.NewState == InstanceState.Faulted)
        {
            _snapshotWindows?.Close(e.InstanceId);
        }

        if (Selected is not null && string.Equals(Selected.Id, e.InstanceId, StringComparison.Ordinal))
        {
            RefreshDetail();
            StartSelectedCommand.NotifyCanExecuteChanged();
            StopSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// 统一呈现 <see cref="XBearException"/>：标准四层分类名加处置建议，不暴露堆栈。
    /// 技术分类与详情保留在 <see cref="ErrorCategoryText"/> 中，供诊断展开对照。
    /// </summary>
    /// <param name="ex">待呈现的异常。</param>
    /// <returns>已呈现时返回 true，供调用方用作异常筛选条件。</returns>
    private bool Present(Exception ex)
    {
        (Presentation.ErrorCategoryText text, string? remediation) = ErrorPresenter.Describe(ex, _terms);

        ErrorTitle = text.Title;
        ErrorMessage = ex.Message;
        ErrorRemediation = remediation ?? string.Empty;
        HasError = true;
        return ex is XBearException;
    }
}