using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XBear.App.Presentation;
using XBear.Core.Modules;

namespace XBear.App.ViewModels;

/// <summary>
/// 模块列表项视图模型。
/// </summary>
public sealed partial class ModuleItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _remotePath = string.Empty;

    [ObservableProperty]
    private string _installStateText = "已安装";

    /// <summary>
    /// 从已安装模块模型构造列表项。
    /// </summary>
    /// <param name="module">已安装模块模型。</param>
    public ModuleItemViewModel(InstalledModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        UpdateFrom(module);
    }

    /// <summary>
    /// 使用已安装模块模型更新属性。
    /// </summary>
    /// <param name="module">已安装模块模型。</param>
    public void UpdateFrom(InstalledModule module)
    {
        Id = module.Id;
        RemotePath = module.RemotePath;
        InstallStateText = "已安装";
    }
}

/// <summary>
/// 模块管理窗口视图模型。支持已安装模块列举、模块 ZIP 包安装与模块卸载。
/// </summary>
public sealed partial class ModuleWindowViewModel : ObservableObject
{
    private readonly ModuleInstallerService _service;
    private readonly TerminologyCatalog _terms;
    private readonly Action<Action> _postToUi;
    private readonly Func<string?, string?> _zipFileDialogHandler;
    private readonly Func<string, bool> _confirmUninstallHandler;

    [ObservableProperty]
    private string _windowTitle = string.Empty;

    [ObservableProperty]
    private ModuleItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _selectedZipPath = string.Empty;

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

    /// <summary>模块列表集合。</summary>
    public ObservableCollection<ModuleItemViewModel> Modules { get; } = new();

    /// <summary>所属实例标识。</summary>
    public string InstanceId { get; }

    /// <summary>所属实例显示名。</summary>
    public string InstanceName { get; }

    /// <summary>实例映射的 adb 端口。</summary>
    public int AdbPort { get; }

    /// <summary>
    /// 构造模块管理窗口视图模型。
    /// </summary>
    /// <param name="service">模块安装服务。</param>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="instanceName">实例显示名。</param>
    /// <param name="adbPort">adb 端口。</param>
    /// <param name="terms">术语表目录。</param>
    /// <param name="postToUi">编组到界面线程的委托。</param>
    /// <param name="zipFileDialogHandler">模块包文件选择委托。</param>
    /// <param name="confirmUninstallHandler">卸载确认对话框委托。</param>
    public ModuleWindowViewModel(
        ModuleInstallerService service,
        string instanceId,
        string instanceName,
        int adbPort,
        TerminologyCatalog terms,
        Action<Action>? postToUi = null,
        Func<string?, string?>? zipFileDialogHandler = null,
        Func<string, bool>? confirmUninstallHandler = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(terms);

        _service = service;
        _terms = terms;
        _postToUi = postToUi ?? (action => action());
        _zipFileDialogHandler = zipFileDialogHandler ?? DefaultZipFileDialog;
        _confirmUninstallHandler = confirmUninstallHandler ?? DefaultConfirmUninstall;

        InstanceId = instanceId;
        InstanceName = instanceName;
        AdbPort = adbPort;

        WindowTitle = string.Concat(instanceName, " · ", terms.Module);
    }

    partial void OnSelectedItemChanged(ModuleItemViewModel? value)
    {
        UninstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedZipPathChanged(string value)
    {
        InstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnLastOperationResultTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasOperationResult));
    }

    /// <summary>
    /// 刷新实例内已安装的模块列表。
    /// </summary>
    /// <returns>异步任务。</returns>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var target = new ModuleTarget(InstanceId, AdbPort);
            IReadOnlyList<InstalledModule> list = await _service.ListAsync(target).ConfigureAwait(false);

            PostToUi(() =>
            {
                Modules.Clear();
                foreach (InstalledModule module in list)
                {
                    Modules.Add(new ModuleItemViewModel(module));
                }
            });
        }
        catch (Exception ex)
        {
            PostToUi(() =>
            {
                LastOperationResultText = $"刷新模块列表失败：{ex.Message}";
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

    /// <summary>是否允许刷新模块列表。</summary>
    public bool CanRefresh => !IsBusy;

    /// <summary>
    /// 浏览选择待安装的模块 ZIP 包。
    /// </summary>
    [RelayCommand]
    public void BrowseZip()
    {
        string? path = _zipFileDialogHandler("选择模块包文件");
        if (!string.IsNullOrWhiteSpace(path))
        {
            SelectedZipPath = path;
        }
    }

    /// <summary>是否允许执行安装模块。</summary>
    public bool CanInstall => !IsBusy && !string.IsNullOrWhiteSpace(SelectedZipPath);

    /// <summary>
    /// 安装选中的模块 ZIP 包到实例。
    /// </summary>
    /// <returns>异步任务。</returns>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    public async Task InstallAsync()
    {
        IsBusy = true;
        IsInstalling = true;

        var target = new ModuleTarget(InstanceId, AdbPort);
        string zipPath = SelectedZipPath;

        try
        {
            ModuleInstallResult result = await _service.InstallAsync(target, zipPath).ConfigureAwait(false);

            PostToUi(() =>
            {
                LastOperationResultText = $"安装模块成功：{result.ModuleId}（已推送 {result.FileCount} 个文件）";
                HasOperationFailure = false;
                SelectedZipPath = string.Empty;
            });
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PostToUi(() =>
            {
                LastOperationResultText = $"安装模块异常：{ex.Message}";
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

    /// <summary>是否允许卸载模块。</summary>
    public bool CanUninstall => !IsBusy && SelectedItem is not null;

    /// <summary>
    /// 卸载选中的模块。卸载前进行二次确认。
    /// </summary>
    /// <returns>异步任务。</returns>
    [RelayCommand(CanExecute = nameof(CanUninstall))]
    public async Task UninstallAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        string moduleId = SelectedItem.Id;
        if (!_confirmUninstallHandler($"确定要卸载模块 {moduleId} 吗？"))
        {
            return;
        }

        IsBusy = true;
        var target = new ModuleTarget(InstanceId, AdbPort);

        try
        {
            bool removed = await _service.UninstallAsync(target, moduleId).ConfigureAwait(false);

            PostToUi(() =>
            {
                if (removed)
                {
                    LastOperationResultText = $"卸载模块成功：{moduleId}";
                    HasOperationFailure = false;
                }
                else
                {
                    LastOperationResultText = $"模块 {moduleId} 不存在";
                    HasOperationFailure = true;
                }
            });
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PostToUi(() =>
            {
                LastOperationResultText = $"卸载模块异常：{ex.Message}";
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

    private static string? DefaultZipFileDialog(string? title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title ?? "选择模块包文件",
            Filter = "模块包 (*.zip)|*.zip|所有文件 (*.*)|*.*"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static bool DefaultConfirmUninstall(string message)
    {
        return MessageBox.Show(
            message,
            "卸载模块确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }
}
