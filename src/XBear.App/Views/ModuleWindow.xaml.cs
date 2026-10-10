using System.Windows;
using XBear.App.ViewModels;

namespace XBear.App.Views;

/// <summary>
/// 模块管理窗口。支持已安装模块列举、模块 ZIP 包安装与模块卸载。
/// </summary>
public partial class ModuleWindow : Window
{
    private readonly ModuleWindowViewModel _viewModel;

    /// <summary>
    /// 构造模块管理窗口。
    /// </summary>
    /// <param name="viewModel">模块管理窗口视图模型。</param>
    /// <param name="owner">宿主所有者窗口，为空时独立显示。</param>
    public ModuleWindow(ModuleWindowViewModel viewModel, Window? owner = null)
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
