using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using XBear.App.Theme;
using XBear.App.ViewModels;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.App;

/// <summary>应用程序入口，负责装配依赖并把设计令牌写入应用资源。</summary>
public partial class App : Application
{
    private AppServices? _services;

    /// <summary>组合根产出的界面依赖。</summary>
    public AppServices Services =>
        _services ?? throw new XBearException(
            ErrorCategory.Internal,
            "应用依赖尚未装配。");

    /// <summary>
    /// 应用启动。令牌写入在创建窗口之前完成，界面不会以缺省样式先显示一帧。
    /// </summary>
    /// <param name="e">启动事件参数。</param>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            SpecLoader loader = SpecLoader.Default;
            _services = AppComposition.Create(ResolveDataRoot(), ResolveImageRoot(), loader);

            // 品牌语义色与令牌同源：palette.json 只登记映射，实际取值仍由令牌提供。
            TokenResources.Apply(_services.Tokens, Resources, BrandPalette.Load(loader));

            var main = new MainWindow();
            main.DataContext = new MainViewModel(
                _services.Repository,
                _services.Manager,
                _services.Images,
                _services.Diagnostics,
                _services.Terms);

            if (Theme.WindowIcon.Create(Resources) is BitmapSource icon)
            {
                main.Icon = icon;
            }

            MainWindow = main;
            main.Show();

            if (main.DataContext is MainViewModel viewModel)
            {
                await viewModel.RefreshAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            // 启动期失败按统一错误呈现，不把堆栈甩给用户。
            (Presentation.ErrorCategoryText text, string? remediation) = Presentation.ErrorPresenter.Describe(ex);

            MessageBox.Show(
                text.Title + Environment.NewLine + ex.Message +
                (string.IsNullOrEmpty(remediation) ? string.Empty : Environment.NewLine + remediation),
                text.Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    private static string ResolveDataRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XBear");

    private static string ResolveImageRoot() =>
        Path.Combine(AppContext.BaseDirectory, "images");
}