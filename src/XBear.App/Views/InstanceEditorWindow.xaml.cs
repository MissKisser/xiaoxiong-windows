using System.Windows;
using System.Windows.Media.Imaging;
using XBear.App.Presentation;
using XBear.App.Theme;
using XBear.App.ViewModels;

namespace XBear.App.Views;

/// <summary>
/// 实例创建窗口。暴露级别切换到局域网或公网时弹出阻断式确认对话框，
/// 公网必须显式勾选风险确认，未勾选时对话框的确认按钮保持禁用。
/// </summary>
public partial class InstanceEditorWindow : Window
{
    private readonly InstanceEditorViewModel _viewModel;

    /// <summary>
    /// 构造实例创建窗口。
    /// </summary>
    /// <param name="viewModel">实例创建视图模型。</param>
    public InstanceEditorWindow(InstanceEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = _viewModel = viewModel;

        if (WindowIcon.Create(Application.Current?.Resources ?? new ResourceDictionary()) is BitmapSource icon)
        {
            Icon = icon;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelExposure();
        DialogResult = false;
        Close();
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        // 仍有待确认的暴露级别时先走阻断式确认，不允许绕过。
        if (_viewModel.Exposure.IsConfirmationPending)
        {
            bool accepted = await RequestExposureConfirmationAsync().ConfigureAwait(true);
            if (!accepted)
            {
                return;
            }
        }

        if (await _viewModel.CreateAsync().ConfigureAwait(true))
        {
            DialogResult = true;
            Close();
        }
    }

    /// <summary>
    /// 弹出模态确认对话框。返回 false 表示用户取消或未勾选风险确认。
    /// </summary>
    /// <returns>用户确认且变更已生效时返回 true。</returns>
    private Task<bool> RequestExposureConfirmationAsync()
    {
        var dialog = new ExposureConfirmationWindow(_viewModel) { Owner = this };

        // ShowDialog 是模态的，用户必须显式勾选才能放行。
        bool? result = dialog.ShowDialog();
        return Task.FromResult(result == true && !_viewModel.Exposure.IsConfirmationPending);
    }
}