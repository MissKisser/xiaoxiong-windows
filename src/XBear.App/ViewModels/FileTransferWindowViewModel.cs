using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XBear.App.Presentation;
using XBear.Core.Diagnostics;
using XBear.Core.FileTransfers;
using XBear.Core.Spec;

namespace XBear.App.ViewModels;

/// <summary>
/// 宿主文件对话框请求。
/// </summary>
/// <param name="IsSave">是否为保存文件对话框。</param>
/// <param name="Title">对话框标题。</param>
/// <param name="Filter">文件过滤器。</param>
/// <param name="DefaultFileName">默认文件名。</param>
public sealed record FileTransferDialogRequest(
    bool IsSave,
    string Title,
    string Filter,
    string? DefaultFileName = null);

/// <summary>
/// 传输任务列表项视图模型。
/// </summary>
public sealed partial class FileTransferTaskItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _direction = string.Empty;

    [ObservableProperty]
    private string _directionText = string.Empty;

    [ObservableProperty]
    private string _sourcePath = string.Empty;

    [ObservableProperty]
    private string _targetPath = string.Empty;

    [ObservableProperty]
    private ConflictPolicy _conflictPolicy = ConflictPolicy.Skip;

    [ObservableProperty]
    private string _conflictPolicyText = string.Empty;

    [ObservableProperty]
    private FileTransferState _state = FileTransferState.Queued;

    [ObservableProperty]
    private string _stateText = string.Empty;

    [ObservableProperty]
    private long _bytesTransferred;

    [ObservableProperty]
    private long? _totalBytes;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private string? _failureReason;

    [ObservableProperty]
    private bool _hasFailure;

    /// <summary>
    /// 从传输任务模型构造任务列表项。
    /// </summary>
    /// <param name="task">传输任务模型。</param>
    public FileTransferTaskItemViewModel(FileTransferTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        UpdateFrom(task);
    }

    /// <summary>
    /// 使用传输任务模型更新当前列表项属性。
    /// </summary>
    /// <param name="task">传输任务模型。</param>
    public void UpdateFrom(FileTransferTask task)
    {
        Id = task.Id;
        Direction = task.Direction;
        DirectionText = task.Direction == FileTransferDirections.HostToInstance
            ? "宿主→实例"
            : "实例→宿主";
        SourcePath = task.SourcePath;
        TargetPath = task.TargetPath;
        ConflictPolicy = task.Overwrite;
        ConflictPolicyText = task.Overwrite == ConflictPolicy.Overwrite
            ? "覆盖目标"
            : "拒绝覆盖";
        State = task.State;
        StateText = DescribeState(task.State);
        FailureReason = task.FailureReason;
        HasFailure = !string.IsNullOrWhiteSpace(task.FailureReason);

        FileTransferProgress? progress = task.Progress;
        if (progress is not null)
        {
            BytesTransferred = progress.BytesTransferred ?? 0;
            TotalBytes = progress.TotalBytes;
        }

        UpdateProgressDisplay();
    }

    /// <summary>
    /// 更新当前任务的传输进度。
    /// </summary>
    /// <param name="bytesTransferred">已复制字节数。</param>
    /// <param name="totalBytes">总字节数。</param>
    public void UpdateProgress(long bytesTransferred, long? totalBytes)
    {
        BytesTransferred = bytesTransferred;
        TotalBytes = totalBytes;
        UpdateProgressDisplay();
    }

    private void UpdateProgressDisplay()
    {
        if (State == FileTransferState.Completed)
        {
            ProgressPercent = 100;
            IsIndeterminate = false;
            ProgressText = TotalBytes.HasValue
                ? $"{FormatBytes(TotalBytes.Value)} / {FormatBytes(TotalBytes.Value)}"
                : $"{FormatBytes(BytesTransferred)}（已完成）";
            return;
        }

        if (State is FileTransferState.Failed or FileTransferState.Cancelled)
        {
            IsIndeterminate = false;
            ProgressText = State == FileTransferState.Cancelled ? "已取消" : "传输失败";
            return;
        }

        if (TotalBytes is > 0)
        {
            IsIndeterminate = false;
            ProgressPercent = Math.Clamp((double)BytesTransferred / TotalBytes.Value * 100.0, 0, 100);
            ProgressText = $"{FormatBytes(BytesTransferred)} / {FormatBytes(TotalBytes.Value)}";
        }
        else
        {
            IsIndeterminate = State == FileTransferState.Running;
            ProgressPercent = 0;
            ProgressText = $"{FormatBytes(BytesTransferred)} / 未知";
        }
    }

    private static string DescribeState(FileTransferState state) =>
        state switch
        {
            FileTransferState.Queued => "已受理",
            FileTransferState.Running => "传输中",
            FileTransferState.Completed => "已完成",
            FileTransferState.Failed => "失败",
            FileTransferState.Cancelled => "已取消",
            _ => "未知",
        };

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        if (bytes < 1024 * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }

        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}

/// <summary>
/// 文件传输窗口视图模型。管理双向传输配置、任务列表刷新与传输取消。
/// </summary>
public sealed partial class FileTransferWindowViewModel : ObservableObject
{
    private readonly FileTransferService _service;
    private readonly TerminologyCatalog _terms;
    private readonly Action<Action> _postToUi;
    private readonly Func<FileTransferDialogRequest, string?> _fileDialogHandler;
    private CancellationTokenSource? _activeTransferCts;

    [ObservableProperty]
    private string _windowTitle = string.Empty;

    [ObservableProperty]
    private bool _isHostToInstance = true;

    [ObservableProperty]
    private bool _isInstanceToHost;

    [ObservableProperty]
    private string _hostPath = string.Empty;

    [ObservableProperty]
    private string _guestPath = FileTransferPathValidator.GuestAllowedPrefix;

    [ObservableProperty]
    private ConflictPolicy _overwrite = ConflictPolicy.Skip;

    [ObservableProperty]
    private bool _isOverwrite;

    [ObservableProperty]
    private bool _isSkip = true;

    [ObservableProperty]
    private bool _isTransferring;

    [ObservableProperty]
    private string _failureText = string.Empty;

    [ObservableProperty]
    private string _currentProgressText = string.Empty;

    [ObservableProperty]
    private double _currentProgressPercent;

    [ObservableProperty]
    private bool _isCurrentIndeterminate;

    /// <summary>是否存在失败信息需要展示。</summary>
    public bool HasFailure => !string.IsNullOrEmpty(FailureText);

    /// <summary>传输任务列表。</summary>
    public ObservableCollection<FileTransferTaskItemViewModel> Tasks { get; } = new();

    /// <summary>所属实例标识。</summary>
    public string InstanceId { get; }

    /// <summary>所属实例显示名称。</summary>
    public string InstanceName { get; }

    /// <summary>实例对应的 adb 端口。</summary>
    public int AdbPort { get; }

    /// <summary>
    /// 构造文件传输窗口视图模型。
    /// </summary>
    /// <param name="service">文件传输服务。</param>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="instanceName">实例显示名。</param>
    /// <param name="adbPort">adb 端口。</param>
    /// <param name="terms">术语表目录。</param>
    /// <param name="postToUi">编组到界面线程的委托。</param>
    /// <param name="fileDialogHandler">文件对话框处理委托。</param>
    public FileTransferWindowViewModel(
        FileTransferService service,
        string instanceId,
        string instanceName,
        int adbPort,
        TerminologyCatalog terms,
        Action<Action>? postToUi = null,
        Func<FileTransferDialogRequest, string?>? fileDialogHandler = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(terms);

        _service = service;
        _terms = terms;
        _postToUi = postToUi ?? (action => action());
        _fileDialogHandler = fileDialogHandler ?? DefaultFileDialog;

        InstanceId = instanceId;
        InstanceName = instanceName;
        AdbPort = adbPort;

        WindowTitle = $"{instanceName} · {terms.FileTransfer}";

        LoadExistingTasks();

        _service.TaskStateChanged += OnTaskStateChanged;
        _service.ProgressChanged += OnProgressChanged;
    }

    /// <summary>当前选定的传输方向字符串（host-to-instance 或 instance-to-host）。</summary>
    public string Direction => IsHostToInstance
        ? FileTransferDirections.HostToInstance
        : FileTransferDirections.InstanceToHost;

    partial void OnIsHostToInstanceChanged(bool value)
    {
        if (value && IsInstanceToHost)
        {
            IsInstanceToHost = false;
        }

        OnPropertyChanged(nameof(Direction));
    }

    partial void OnIsInstanceToHostChanged(bool value)
    {
        if (value && IsHostToInstance)
        {
            IsHostToInstance = false;
        }

        OnPropertyChanged(nameof(Direction));
    }

    partial void OnIsOverwriteChanged(bool value)
    {
        if (value)
        {
            if (IsSkip)
            {
                IsSkip = false;
            }

            Overwrite = ConflictPolicy.Overwrite;
        }
    }

    partial void OnIsSkipChanged(bool value)
    {
        if (value)
        {
            if (IsOverwrite)
            {
                IsOverwrite = false;
            }

            Overwrite = ConflictPolicy.Skip;
        }
    }

    partial void OnOverwriteChanged(ConflictPolicy value)
    {
        IsOverwrite = value == ConflictPolicy.Overwrite;
        IsSkip = value != ConflictPolicy.Overwrite;
    }

    partial void OnFailureTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasFailure));
    }

    /// <summary>
    /// 浏览选择宿主侧文件或保存路径。
    /// </summary>
    [RelayCommand]
    public void BrowseHostFile()
    {
        if (IsHostToInstance)
        {
            var request = new FileTransferDialogRequest(
                IsSave: false,
                Title: "选择宿主源文件",
                Filter: "所有文件 (*.*)|*.*");

            string? selected = _fileDialogHandler(request);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                HostPath = selected;
                FailureText = string.Empty;
            }
        }
        else
        {
            string? defaultName = null;
            if (!string.IsNullOrWhiteSpace(GuestPath) && !GuestPath.EndsWith('/'))
            {
                defaultName = Path.GetFileName(GuestPath);
            }

            var request = new FileTransferDialogRequest(
                IsSave: true,
                Title: "选择宿主目标文件位置",
                Filter: "所有文件 (*.*)|*.*",
                DefaultFileName: defaultName);

            string? selected = _fileDialogHandler(request);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                HostPath = selected;
                FailureText = string.Empty;
            }
        }
    }

    /// <summary>
    /// 发起一次文件传输任务。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartTransfer))]
    public async Task StartTransferAsync()
    {
        FailureText = string.Empty;

        // 界面先做本地路径规范化预检，发现格式错误直接展示服务返回原因，不发起无效任务。
        try
        {
            if (IsHostToInstance)
            {
                FileTransferPathValidator.NormalizeAndValidateHostPath(HostPath);
                FileTransferPathValidator.NormalizeAndValidateGuestPath(GuestPath);
            }
            else
            {
                FileTransferPathValidator.NormalizeAndValidateGuestPath(GuestPath);
                FileTransferPathValidator.NormalizeAndValidateHostPath(HostPath);
            }
        }
        catch (XBearException ex)
        {
            FailureText = ex.Message;
            return;
        }

        IsTransferring = true;
        StartTransferCommand.NotifyCanExecuteChanged();
        CancelTransferCommand.NotifyCanExecuteChanged();

        _activeTransferCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _activeTransferCts.Token;

        var target = new FileTransferTarget(InstanceId, AdbPort);
        var request = new FileTransferRequest(
            Direction,
            IsHostToInstance ? HostPath : GuestPath,
            IsHostToInstance ? GuestPath : HostPath,
            Overwrite,
            recursive: false,
            flush: true);

        try
        {
            await _service.TransferAsync(target, request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消，状态已由服务置为 Cancelled，不抛出异常。
        }
        catch (Exception ex)
        {
            PostToUi(() =>
            {
                FailureText = ex.Message;
            });
        }
        finally
        {
            PostToUi(() =>
            {
                IsTransferring = false;
                _activeTransferCts?.Dispose();
                _activeTransferCts = null;
                StartTransferCommand.NotifyCanExecuteChanged();
                CancelTransferCommand.NotifyCanExecuteChanged();
            });
        }
    }

    /// <summary>是否允许发起传输。</summary>
    public bool CanStartTransfer => !IsTransferring;

    /// <summary>是否允许取消当前传输。</summary>
    public bool CanCancelTransfer => IsTransferring;

    /// <summary>
    /// 取消当前正在执行的传输任务。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancelTransfer))]
    public void CancelTransfer()
    {
        _activeTransferCts?.Cancel();
    }

    /// <summary>
    /// 窗口关闭时的清理动作。退订事件并取消正在执行的传输。
    /// </summary>
    /// <returns>异步任务。</returns>
    public Task CloseAsync()
    {
        _service.TaskStateChanged -= OnTaskStateChanged;
        _service.ProgressChanged -= OnProgressChanged;

        _activeTransferCts?.Cancel();
        _activeTransferCts?.Dispose();
        _activeTransferCts = null;

        return Task.CompletedTask;
    }

    private void LoadExistingTasks()
    {
        IReadOnlyList<FileTransferTask> existing = _service.ListTasks(InstanceId);
        foreach (FileTransferTask task in existing)
        {
            Tasks.Add(new FileTransferTaskItemViewModel(task));
        }
    }

    private void OnTaskStateChanged(object? sender, FileTransferTaskEventArgs e)
    {
        if (!string.Equals(e.Task.InstanceRef, InstanceId, StringComparison.Ordinal))
        {
            return;
        }

        PostToUi(() =>
        {
            FileTransferTaskItemViewModel? item = Tasks.FirstOrDefault(t => t.Id == e.Task.Id);
            if (item is null)
            {
                item = new FileTransferTaskItemViewModel(e.Task);
                Tasks.Insert(0, item);
            }
            else
            {
                item.UpdateFrom(e.Task);
            }

            if (e.CurrentState == FileTransferState.Failed && !string.IsNullOrWhiteSpace(e.Task.FailureReason))
            {
                FailureText = e.Task.FailureReason;
            }
        });
    }

    private void OnProgressChanged(object? sender, FileTransferProgressEventArgs e)
    {
        PostToUi(() =>
        {
            FileTransferTaskItemViewModel? item = Tasks.FirstOrDefault(t => t.Id == e.TaskId);
            if (item is not null)
            {
                item.UpdateProgress(e.BytesTransferred, e.TotalBytes);
                CurrentProgressText = item.ProgressText;
                CurrentProgressPercent = item.ProgressPercent;
                IsCurrentIndeterminate = item.IsIndeterminate;
            }
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
            // 窗口关闭后不再受理界面更新。
        }
    }

    private static string? DefaultFileDialog(FileTransferDialogRequest request)
    {
        if (request.IsSave)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = request.Title,
                Filter = request.Filter,
                FileName = request.DefaultFileName ?? string.Empty
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = request.Title,
                Filter = request.Filter
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
    }
}
