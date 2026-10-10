using System.Windows;
using XBear.App.ViewModels;

namespace XBear.App.Views;

/// <summary>
/// 快照管理窗口。支持快照列举、创建、恢复与删除。
/// </summary>
public partial class SnapshotWindow : Window
{
    private readonly SnapshotWindowViewModel _viewModel;

    /// <summary>
    /// 构造快照管理窗口。
    /// </summary>
    /// <param name="viewModel">快照管理窗口视图模型。</param>
    /// <param name="owner">宿主所有者窗口，为空时独立显示。</param>
    public SnapshotWindow(SnapshotWindowViewModel viewModel, Window? owner = null)
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