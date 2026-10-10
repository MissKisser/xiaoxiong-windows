using System.Windows;
using XBear.App.ViewModels;

namespace XBear.App.Views;

/// <summary>
/// 应用管理窗口。支持已安装应用列举、应用包安装、拉起应用与卸载应用。
/// </summary>
public partial class ApplicationWindow : Window
{
    private readonly ApplicationWindowViewModel _viewModel;

    /// <summary>
    /// 构造应用管理窗口。
    /// </summary>
    /// <param name="viewModel">应用管理窗口视图模型。</param>
    /// <param name="owner">宿主所有者窗口，为空时独立显示。</param>
    public ApplicationWindow(ApplicationWindowViewModel viewModel, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        if (owner is not null)
        {
            Owner = owner;
        }

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshAsync();
    }
}
