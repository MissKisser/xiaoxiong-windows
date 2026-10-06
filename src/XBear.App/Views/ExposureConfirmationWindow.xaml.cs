using System.Windows;
using XBear.App.Presentation;
using XBear.App.ViewModels;

namespace XBear.App.Views;

/// <summary>
/// 暴露级别阻断式确认对话框。切换到局域网或公网时弹出，
/// 要求用户显式勾选风险确认后才能继续，未勾选则不允许继续。
/// </summary>
public partial class ExposureConfirmationWindow : Window
{
    private readonly InstanceEditorViewModel _viewModel;

    /// <summary>
    /// 构造确认对话框。
    /// </summary>
    /// <param name="viewModel">实例创建视图模型，提供风险文案与勾选状态。</param>
    public ExposureConfirmationWindow(InstanceEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = _viewModel = viewModel;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.ConfirmExposure())
        {
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelExposure();
        DialogResult = false;
        Close();
    }
}