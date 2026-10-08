using System.Collections.ObjectModel;
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
    private readonly Dictionary<string, ImageSpec> _images;
    private InputProbeResult _inputChannel = new(InputChannelKind.Unknown);

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

    /// <summary>
    /// 构造主视图模型。
    /// </summary>
    /// <param name="repository">实例配置仓库。</param>
    /// <param name="manager">实例生命周期编排器，未接入时为 null，界面退化为只读浏览。</param>
    /// <param name="images">镜像清单，按标识索引。</param>
    /// <param name="diagnostics">诊断包导出器。</param>
    /// <param name="terms">界面文案术语来源。</param>
    /// <param name="version">版本契约文档，为空时从诊断包导出器获取。</param>
    public MainViewModel(
        IInstanceRepository repository,
        InstanceManager? manager,
        IReadOnlyDictionary<string, ImageSpec> images,
        DiagnosticsExporter diagnostics,
        TerminologyCatalog terms,
        VersionDocument? version = null)
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
    {
        ArgumentNullException.ThrowIfNull(result);
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