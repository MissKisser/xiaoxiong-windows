using System.Windows;
using System.Windows.Media.Imaging;
using XBear.App.Theme;
using XBear.App.ViewModels;

namespace XBear.App.Views;

/// <summary>
/// 关于窗口代码后置。展示产品版本、规格版本与构建标识。
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>
    /// 构造关于窗口。
    /// </summary>
    public AboutWindow()
    {
        InitializeComponent();
        SetWindowIcon();
    }

    /// <summary>
    /// 以指定主视图模型构造关于窗口。
    /// </summary>
    /// <param name="viewModel">主视图模型，提供版本契约属性绑定。</param>
    public AboutWindow(MainViewModel viewModel)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private void SetWindowIcon()
    {
        if (WindowIcon.Create(Application.Current?.Resources ?? new ResourceDictionary()) is BitmapSource icon)
        {
            Icon = icon;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
