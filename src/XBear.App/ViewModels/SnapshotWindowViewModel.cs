using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XBear.App.Presentation;
using XBear.Core.Snapshots;
using XBear.Core.Spec;

namespace XBear.App.ViewModels;

/// <summary>
/// 快照列表项视图模型。按契约字段展示快照的标识、名称、创建时刻、状态与备注。
/// </summary>
public sealed partial class SnapshotItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _createdAtText = string.Empty;

    [ObservableProperty]
    private string _stateText = string.Empty;

    [ObservableProperty]
    private string _noteText = string.Empty;

    [ObservableProperty]
    private string _parentText = string.Empty;

    [ObservableProperty]
    private bool _isRestorable;

    [ObservableProperty]
    private bool _isChainRoot;

    /// <summary>
    /// 从快照元数据构造列表项。
    /// </summary>
    /// <param name="spec">快照元数据。</param>
    public SnapshotItemViewModel(SnapshotSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        UpdateFrom(spec);
    }

    /// <summary>
    /// 使用快照元数据更新属性。
    /// </summary>
    /// <param name="spec">快照元数据。</param>
    public void UpdateFrom(SnapshotSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        Id = spec.Id;
        DisplayName = string.IsNullOrWhiteSpace(spec.DisplayName)
            ? RenderMoment(spec.CreatedAt)
            : spec.DisplayName;
        CreatedAtText = spec.CreatedAt;
        StateText = SnapshotStateTransition.Describe(spec.State);
        NoteText = string.IsNullOrWhiteSpace(spec.Note) ? "未填写备注" : spec.Note;
        ParentText = spec.IsChainRoot() ? "该实例的首个快照" : $"父快照 {spec.ParentRef}";
        IsRestorable = spec.IsRestorable();
        IsChainRoot = spec.IsChainRoot();
    }

    /// <summary>
    /// 快照未命名时由创建时刻渲染显示名，与契约中缺省显示名的约定一致。
    /// </summary>
    /// <param name="createdAt">快照的创建时刻文本。</param>
    /// <returns>供列表辨认的显示名。</returns>
    private static string RenderMoment(string createdAt)
    {
        return DateTimeOffset.TryParse(
            createdAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset moment)
            ? moment.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : createdAt;
    }
}

/// <summary>
/// 快照管理窗口视图模型。支持快照列举、创建、恢复与删除，恢复与删除均需二次确认。
/// </summary>
public sealed partial class SnapshotWindowViewModel : ObservableObject
{
    private readonly SnapshotService _service;
    private readonly TerminologyCatalog _terms;
    private readonly Action<Action> _postToUi;
    private readonly Func<string, bool> _confirmRestoreHandler;
    private readonly Func<string, bool> _confirmDeleteHandler;

    [ObservableProperty]
    private string _windowTitle = string.Empty;

    [ObservableProperty]
    private SnapshotItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _newSnapshotName = string.Empty;

    [ObservableProperty]
    private string _newSnapshotNote = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _lastOperationResultText = string.Empty;

    [ObservableProperty]
    private bool _hasOperationFailure;

    /// <summary>是否存在操作结果需要提示。</summary>
    public bool HasOperationResult => !string.IsNullOrEmpty(LastOperationResultText);

    /// <summary>快照列表集合。</summary>
    public ObservableCollection<SnapshotItemViewModel> Snapshots { get; } = new();

    /// <summary>所属实例标识。</summary>
    public string InstanceId { get; }

    /// <summary>所属实例显示名。</summary>
    public string InstanceName { get; }

    /// <summary>
    /// 构造快照管理窗口视图模型。
    /// </summary>
    /// <param name="service">快照服务。</param>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="instanceName">实例显示名。</param>
    /// <param name="terms">术语表目录。</param>
    /// <param name="postToUi">编组到界面线程的委托。</param>
    /// <param name="confirmRestoreHandler">恢复确认委托。</param>
    /// <param name="confirmDeleteHandler">删除确认委托。</param>
    public SnapshotWindowViewModel(
        SnapshotService service,
        string instanceId,
        string instanceName,
        TerminologyCatalog terms,
        Action<Action>? postToUi = null,
        Func<string, bool>? confirmRestoreHandler = null,
        Func<string, bool>? confirmDeleteHandler = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(terms);

        _service = service;
        _terms = terms;
        _postToUi = postToUi ?? (static action => action());
        _confirmRestoreHandler = confirmRestoreHandler ?? DefaultConfirmRestore;
        _confirmDeleteHandler = confirmDeleteHandler ?? DefaultConfirmDelete;

        InstanceId = instanceId;
        InstanceName = instanceName;

        WindowTitle = string.Concat(instanceName, " · ", terms.Snapshot);
    }

    partial void OnSelectedItemChanged(SnapshotItemViewModel? value)
    {
        RestoreCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    partial void OnNewSnapshotNameChanged(string value)
    {
        CreateCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        CreateCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    partial void OnLastOperationResultTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasOperationResult));
    }

    /// <summary>创建快照的按钮文案。</summary>
    public string CreateSnapshotText => string.Concat("创建", _terms.Snapshot);

    /// <summary>恢复快照的按钮文案。</summary>
    public string RestoreSnapshotText => string.Concat("恢复到", _terms.Snapshot);

    /// <summary>删除快照的按钮文案。</summary>
    public string DeleteSnapshotText => string.Concat("删除", _terms.Snapshot);

    /// <summary>快照列表标题。</summary>
    public string SnapshotListTitle => string.Concat(InstanceName, " 的", _terms.Snapshot);

    /// <summary>创建区域的提示文案，说明只有运行中的实例可以创建快照。</summary>
    public string CreateHintText =>
        string.Concat(
            _terms.Snapshot,
            "在实例运行中创建。恢复到较早的",
            _terms.Snapshot,
            "会把实例回退到该时刻，实例需要重新启动。");

    /// <summary>刷新快照列表。</summary>
    /// <returns>异步任务。</returns>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            IReadOnlyList<SnapshotSpec> list = await _service.ListAsync(InstanceId).ConfigureAwait(false);

            PostToUi(() =>
            {
                Snapshots.Clear();
                foreach (SnapshotSpec spec in list)
                {
                    Snapshots.Add(new SnapshotItemViewModel(spec));
                }
            });
        }
        catch (Exception ex)
        {
            ReportFailure($"刷新{_terms.Snapshot}列表失败", ex);
        }
        finally
        {
            PostToUi(() =>
            {
                IsBusy = false;
            });
        }
    }

    /// <summary>是否允许刷新快照列表。</summary>
    public bool CanRefresh => !IsBusy;

    /// <summary>是否允许创建快照，要求填写了名称且实例处于运行态。</summary>
    public bool CanCreate => !IsBusy && !string.IsNullOrWhiteSpace(NewSnapshotName);

    /// <summary>
    /// 为运行中的实例创建快照。名称必填，备注可选。
    /// </summary>
    /// <returns>异步任务。</returns>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    public async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(NewSnapshotName))
        {
            return;
        }

        IsBusy = true;
        try
        {
            SnapshotSpec created = await _service
                .CreateAsync(InstanceId, NewSnapshotName, NewSnapshotNote)
                .ConfigureAwait(false);

            PostToUi(() =>
            {
                LastOperationResultText =
                    string.Concat("已创建", _terms.Snapshot, "：", created.Id, "，创建时刻 ", created.CreatedAt);
                HasOperationFailure = false;
                NewSnapshotName = string.Empty;
                NewSnapshotNote = string.Empty;
            });

            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportFailure(string.Concat("创建", _terms.Snapshot, "失败"), ex);
        }
        finally
        {
            PostToUi(() =>
            {
                IsBusy = false;
            });
        }
    }

    /// <summary>是否允许恢复选中的快照。</summary>
    public bool CanRestore => !IsBusy && SelectedItem is { IsRestorable: true };

    /// <summary>
    /// 把实例回退到选中的快照。恢复会先停止实例再重新启动，执行前必须二次确认。
    /// </summary>
    /// <returns>异步任务。</returns>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    public async Task RestoreAsync()
    {
        if (SelectedItem is not { IsRestorable: true })
        {
            return;
        }

        string snapshotId = SelectedItem.Id;
        string message = string.Concat(
            "确定要把实例恢复到",
            _terms.Snapshot,
            " ",
            snapshotId,
            " 吗？实例会先停止再重新启动，该时刻之后的改动将无法找回。");

        if (!_confirmRestoreHandler(message))
        {
            return;
        }

        IsBusy = true;
        try
        {
            SnapshotRestoreOutcome outcome = await _service
                .RestoreAsync(InstanceId, snapshotId)
                .ConfigureAwait(false);

            PostToUi(() =>
            {
                LastOperationResultText = string.Concat(
                    "已恢复到",
                    _terms.Snapshot,
                    " ",
                    outcome.SnapshotId,
                    "，实例已重新启动，当前状态：",
                    outcome.IsRunning ? "运行中" : "已停止");
                HasOperationFailure = !outcome.IsRunning;
            });

            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportFailure(string.Concat("恢复到", _terms.Snapshot, "失败"), ex);
        }
        finally
        {
            PostToUi(() =>
            {
                IsBusy = false;
            });
        }
    }

    /// <summary>是否允许删除选中的快照。</summary>
    public bool CanDelete => !IsBusy && SelectedItem is not null;

    /// <summary>
    /// 删除选中的快照。删除前进行二次确认。
    /// </summary>
    /// <returns>异步任务。</returns>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    public async Task DeleteAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        string snapshotId = SelectedItem.Id;
        string message = string.Concat(
            "确定要删除",
            _terms.Snapshot,
            " ",
            snapshotId,
            " 吗？删除后无法再恢复到该时刻。");

        if (!_confirmDeleteHandler(message))
        {
            return;
        }

        IsBusy = true;
        try
        {
            SnapshotDeleteOutcome outcome = await _service
                .DeleteAsync(InstanceId, snapshotId)
                .ConfigureAwait(false);

            PostToUi(() =>
            {
                LastOperationResultText = string.Concat("已删除", _terms.Snapshot, " ", outcome.SnapshotId);
                HasOperationFailure = !outcome.MetadataRemoved;
            });

            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportFailure(string.Concat("删除", _terms.Snapshot, "失败"), ex);
        }
        finally
        {
            PostToUi(() =>
            {
                IsBusy = false;
            });
        }
    }

    /// <summary>
    /// 把操作失败的原因统一呈现到结果栏，异常文案直接沿用服务层的分类与处置建议。
    /// </summary>
    /// <param name="stage">失败阶段描述。</param>
    /// <param name="exception">捕获到的异常。</param>
    private void ReportFailure(string stage, Exception exception)
    {
        string detail = exception is Core.Diagnostics.XBearException xbear && !string.IsNullOrWhiteSpace(xbear.Remediation)
            ? xbear.Message + " " + xbear.Remediation
            : exception.Message;

        PostToUi(() =>
        {
            LastOperationResultText = string.Concat(stage, "：", detail);
            HasOperationFailure = true;
        });
    }

    private void PostToUi(Action action)
    {
        try
        {
            _postToUi(action);
        }
        catch (TaskCanceledException)
        {
            // 界面已关闭。
        }
    }

    private static bool DefaultConfirmRestore(string message)
    {
        return MessageBox.Show(
            message,
            "恢复快照确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private static bool DefaultConfirmDelete(string message)
    {
        return MessageBox.Show(
            message,
            "删除快照确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }
}