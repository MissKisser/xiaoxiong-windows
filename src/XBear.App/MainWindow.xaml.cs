using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using XBear.App.Services;

namespace XBear.App;

/// <summary>主窗口代码后置，仅负责文件选择对话框，不承载业务逻辑。</summary>
public partial class MainWindow : Window
{
    private DiagnosticsExporter? _diagnostics;

    /// <summary>
    /// 构造主窗口。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnCreateInstance(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel)
        {
            return;
        }

        AppServices services = ((App)Application.Current).Services;

        var editor = new ViewModels.InstanceEditorViewModel(
            services.Repository,
            services.Images,
            services.Audit,
            services.Terms);

        var window = new Views.InstanceEditorWindow(editor) { Owner = this };

        if (window.ShowDialog() == true)
        {
            _ = viewModel.RefreshAsync();
        }
    }

    /// <summary>
    /// 选择诊断包输出目录。目录由用户在系统对话框中指定。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnChooseDiagnosticsDirectory(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel)
        {
            return;
        }

        _diagnostics ??= ((App)Application.Current).Services.Diagnostics;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = viewModel.ExportDiagnosticsText,
            FileName = "xbear-diagnostics.zip"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string directory = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
        viewModel.ExportDiagnosticsCommand.Execute(directory);
    }

    /// <summary>
    /// 选择本地 Android 镜像文件并触发导入。文件路径由用户在系统对话框中指定。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnChooseBaseImageToImport(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = viewModel.ImportBaseImageText,
            Filter = "Android 镜像文件 (*.iso;*.img;*.qcow2)|*.iso;*.img;*.qcow2|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        viewModel.ImportBaseImageCommand.Execute(dialog.FileName);
    }
}