using System.Windows;
using XBear.App.ViewModels;

namespace XBear.App.Views;

/// <summary>
/// 文件传输窗口。支持方向选择、宿主文件浏览、实例路径输入、冲突策略选择与传输任务状态列表展示。
/// </summary>
public partial class FileTransferWindow : Window
{
    /// <summary>
    /// 构造文件传输窗口。
    /// </summary>
    /// <param name="viewModel">文件传输窗口视图模型。</param>
    /// <param name="owner">宿主所有者窗口，为空时独立显示。</param>
    public FileTransferWindow(FileTransferWindowViewModel viewModel, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;

        if (owner is not null)
        {
            Owner = owner;
        }
    }
}
