using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XBear.App.Presentation;
using XBear.Core.Applications;
using XBear.Core.Spec;

namespace XBear.App.ViewModels;

/// <summary>
/// 应用列表项视图模型。
/// </summary>
public sealed partial class ApplicationItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _packageName = string.Empty;

    [ObservableProperty]
    private string? _versionName;

    [ObservableProperty]
    private int? _versionCode;

    [ObservableProperty]
    private string? _primaryCpuAbi;

    [ObservableProperty]
    private string _installStateText = "已安装";

    /// <summary>
    /// 从契约应用规范构造列表项。
    /// </summary>
    /// <param name="spec">应用规范。</param>
    public ApplicationItemViewModel(ApplicationSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        UpdateFrom(spec);
    }

    /// <summary>
    /// 使用应用规范更新属性。
    /// </summary>
    /// <param name="spec">应用规范。</param>
    public void UpdateFrom(ApplicationSpec spec)
    {
        PackageName = spec.PackageName;
        VersionName = spec.VersionName;
        VersionCode = spec.VersionCode;
        PrimaryCpuAbi = spec.PrimaryCpuAbi;
        InstallStateText = spec.InstallState == ApplicationInstallState.Installed ? "已安装" : "未安装";
    }

    /// <summary>版本名展示文案，缺省时降级为未声明。</summary>
    public string VersionNameText => !string.IsNullOrEmpty(VersionName) ? VersionName : "未声明";

    /// <summary>版本号展示文案，缺省时降级为未声明。</summary>
    public string VersionCodeText => VersionCode.HasValue
        ? VersionCode.Value.ToString(CultureInfo.InvariantCulture)
        : "未声明";

    /// <summary>主 ABI 展示文案，无原生库或缺省时友好降级。</summary>
    public string PrimaryCpuAbiText => PrimaryCpuAbi switch
    {
        "none" => "无原生库",
        null or "" => "未声明",
        _ => PrimaryCpuAbi,
    };
}

/// <summary>
/// 应用管理窗口视图模型。支持已安装应用列举、应用包安装、拉起应用与卸载应用。
/// </summary>
public sealed partial class ApplicationWindowViewModel : ObservableObject
{
    private static readonly Regex PackageNameCandidateRegex = new(
        @"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$",
        RegexOptions.CultureInvariant);

    private readonly ApplicationService _service;
    private readonly TerminologyCatalog _terms;
    private readonly Action<Action> _postToUi;
    private readonly Func<string?, string?> _apkFileDialogHandler;
    private readonly Func<string, bool> _confirmUninstallHandler;

    [ObservableProperty]
    private string _windowTitle = string.Empty;

    [ObservableProperty]
    private ApplicationItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _selectedApkPath = string.Empty;

    [ObservableProperty]
    private string _packageNameInput = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private string _lastOperationResultText = string.Empty;

    [ObservableProperty]
    private bool _hasOperationFailure;

    /// <summary>是否存在操作结果需要提示。</summary>
    public bool HasOperationResult => !string.IsNullOrEmpty(LastOperationResultText);

    /// <summary>应用列表集合。</summary>
    public ObservableCollection<ApplicationItemViewModel> Applications { get; } = new();

    /// <summary>所属实例标识。</summary>
    public string InstanceId { get; }

    /// <summary>所属实例显示名。</summary>
    public string InstanceName { get; }

    /// <summary>实例映射的 adb 端口。</summary>
    public int AdbPort { get; }

    /// <summary>
    /// 构造应用管理窗口视图模型。
    /// </summary>
    /// <param name="service">应用管理服务。</param>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="instanceName">实例显示名。</param>
    /// <param name="adbPort">adb 端口。</param>
    /// <param name="terms">术语表目录。</param>
    /// <param name="postToUi">编组到界面线程的委托。</param>
    /// <param name="apkFileDialogHandler">应用包文件选择委托。</param>
    /// <param name="confirmUninstallHandler">卸载确认对话框委托。</param>
    public ApplicationWindowViewModel(
        ApplicationService service,
        string instanceId,
        string instanceName,
        int adbPort,
        TerminologyCatalog terms,
        Action<Action>? postToUi = null,
        Func<string?, string?>? apkFileDialogHandler = null,
        Func<string, bool>? confirmUninstallHandler = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(terms);

        _service = service;
        _terms = terms;
        _postToUi = postToUi ?? (action => action());
        _apkFileDialogHandler = apkFileDialogHandler ?? DefaultApkFileDialog;
        _confirmUninstallHandler = confirmUninstallHandler ?? DefaultConfirmUninstall;

        InstanceId = instanceId;
        InstanceName = instanceName;
        AdbPort = adbPort;

        WindowTitle = string.Concat(instanceName, " · ", terms.Application);
    }

    partial void OnSelectedItemChanged(ApplicationItemViewModel? value)
    {
        LaunchCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedApkPathChanged(string value)
    {
        InstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnPackageNameInputChanged(string value)
    {
        InstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        LaunchCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnLastOperationResultTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasOperationResult));
    }

    /// <summary>
    /// 刷新实例内已安装的应用列表。
    /// </summary>
    /// <returns>异步任务。</returns>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var target = new ApplicationTarget(InstanceId, AdbPort);
            IReadOnlyList<ApplicationSpec> list = await _service.ListAsync(target).ConfigureAwait(false);

            PostToUi(() =>
            {
                Applications.Clear();
                foreach (ApplicationSpec spec in list)
                {
                    Applications.Add(new ApplicationItemViewModel(spec));
                }
            });
        }
        catch (Exception ex)
        {
            PostToUi(() =>
            {
                LastOperationResultText = $"刷新应用列表失败：{ex.Message}";
                HasOperationFailure = true;
            });
        }
        finally
        {
            PostToUi(() =>
            {
                IsBusy = false;
            });
        }
    }

    /// <summary>是否允许执行刷新。</summary>
    public bool CanRefresh => !IsBusy;

    /// <summary>
    /// 浏览选择待安装的应用包 APK 文件。
    /// </summary>
    [RelayCommand]
    public void BrowseApk()
    {
        string? path = _apkFileDialogHandler("选择应用包文件");
        if (!string.IsNullOrWhiteSpace(path))
        {
            SelectedApkPath = path;

            string baseName = Path.GetFileNameWithoutExtension(path);
            if (PackageNameCandidateRegex.IsMatch(baseName))
            {
                PackageNameInput = baseName;
            }
        }
    }

    /// <summary>是否允许执行安装应用包。</summary>
    public bool CanInstall =>
        !IsBusy &&
        !string.IsNullOrWhiteSpace(SelectedApkPath) &&
        !string.IsNullOrWhiteSpace(PackageNameInput);

    /// <summary>
    /// 安装选中的应用包到实例中。
    /// </summary>
    /// <returns>异步任务。</returns>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    public async Task InstallAsync()
    {
        IsBusy = true;
        IsInstalling = true;

        var target = new ApplicationTarget(InstanceId, AdbPort);
        string apkPath = SelectedApkPath;
        string packageName = PackageNameInput.Trim();

        try
        {
            ApplicationOperation op = await _service.InstallAsync(target, apkPath, packageName).ConfigureAwait(false);

            if (op.Result == ApplicationOperationResult.Success)
            {
                PostToUi(() =>
                {
                    LastOperationResultText = $"安装应用包成功（{op.PackageName}，耗时 {op.DurationMs} ms）";
                    HasOperationFailure = false;
                    SelectedApkPath = string.Empty;
                    PackageNameInput = string.Empty;
                });
                await RefreshAsync().ConfigureAwait(false);
            }
            else
            {
                PostToUi(() =>
                {
                    string code = op.FailureReason?.Code ?? "UNKNOWN";
                    string msg = op.FailureReason?.Message ?? string.Empty;
                    LastOperationResultText = $"安装应用包失败：[{code}] {msg}";
                    HasOperationFailure = true;
                });
            }
        }
        catch (Exception ex)
        {
            PostToUi(() =>
            {
                LastOperationResultText = $"安装应用包异常：{ex.Message}";
                HasOperationFailure = true;
            });
        }
        finally
        {
            PostToUi(() =>
            {
                IsInstalling = false;
                IsBusy = false;
            });
        }
    }

    /// <summary>是否允许拉起应用。</summary>
    public bool CanLaunch => !IsBusy && SelectedItem is not null;

    /// <summary>
    /// 拉起选中的应用。
    /// </summary>
    /// <returns>异步任务。</returns>
    [RelayCommand(CanExecute = nameof(CanLaunch))]
    public async Task LaunchAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        IsBusy = true;
        var target = new ApplicationTarget(InstanceId, AdbPort);
        string packageName = SelectedItem.PackageName;

        try
        {
            ApplicationOperation op = await _service.LaunchAsync(target, packageName).ConfigureAwait(false);

            PostToUi(() =>
            {
                if (op.Result == ApplicationOperationResult.Success)
                {
                    LastOperationResultText = $"拉起应用成功（{op.PackageName}，耗时 {op.DurationMs} ms）";
                    HasOperationFailure = false;
                }
                else
                {
                    string code = op.FailureReason?.Code ?? "UNKNOWN";
                    string msg = op.FailureReason?.Message ?? string.Empty;
                    LastOperationResultText = $"拉起应用失败：[{code}] {msg}";
                    HasOperationFailure = true;
                }
            });
        }
        catch (Exception ex)
        {
            PostToUi(() =>
            {
                LastOperationResultText = $"拉起应用异常：{ex.Message}";
                HasOperationFailure = true;
            });
        }
        finally
        {
            PostToUi(() =>
            {
                IsBusy = false;
            });
        }
    }

    /// <summary>是否允许卸载应用。</summary>
    public bool CanUninstall => !IsBusy && SelectedItem is not null;

    /// <summary>
    /// 卸载选中的应用。卸载前进行二次确认。
    /// </summary>
    /// <returns>异步任务。</returns>
    [RelayCommand(CanExecute = nameof(CanUninstall))]
    public async Task UninstallAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        string packageName = SelectedItem.PackageName;
        if (!_confirmUninstallHandler($"确定要卸载应用 {packageName} 吗？"))
        {
            return;
        }

        IsBusy = true;
        var target = new ApplicationTarget(InstanceId, AdbPort);

        try
        {
            ApplicationOperation op = await _service.UninstallAsync(target, packageName).ConfigureAwait(false);

            if (op.Result == ApplicationOperationResult.Success)
            {
                PostToUi(() =>
                {
                    LastOperationResultText = $"卸载应用成功（{op.PackageName}）";
                    HasOperationFailure = false;
                });
                await RefreshAsync().ConfigureAwait(false);
            }
            else
            {
                PostToUi(() =>
                {
                    string code = op.FailureReason?.Code ?? "UNKNOWN";
                    string msg = op.FailureReason?.Message ?? string.Empty;
                    LastOperationResultText = $"卸载应用失败：[{code}] {msg}";
                    HasOperationFailure = true;
                });
            }
        }
        catch (Exception ex)
        {
            PostToUi(() =>
            {
                LastOperationResultText = $"卸载应用异常：{ex.Message}";
                HasOperationFailure = true;
            });
        }
        finally
        {
            PostToUi(() =>
            {
                IsBusy = false;
            });
        }
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

    private static string? DefaultApkFileDialog(string? title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title ?? "选择应用包文件",
            Filter = "应用包 (*.apk)|*.apk|所有文件 (*.*)|*.*"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static bool DefaultConfirmUninstall(string message)
    {
        return MessageBox.Show(
            message,
            "卸载应用确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }
}
