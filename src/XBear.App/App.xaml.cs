using System.Configuration;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
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

    /// <summary>
    /// 创建应用并挂上全局异常兜底。
    /// 未处理异常若逃逸到进程顶层，界面层无法呈现错误，表现是进程直接消失；
    /// 这里把异常落盘，使启动失败可被事后诊断。
    /// </summary>
    public App()
    {
        NormalizeCulture();
        DispatcherUnhandledException += (_, args) => WriteCrashLog(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteCrashLog(args.ExceptionObject as Exception);
        };
    }

    /// <summary>
    /// 把当前线程与 WPF 的语言统一到系统实际安装的非中性文化。
    ///
    /// 部分系统只安装了个别语言，此时进程继承的区域设置可能指向一个并未安装的文化，
    /// WPF 绑定在解析 <c>xml:lang</c> 时会抛出「找不到对应的非中性文化」而中断数据绑定。
    /// 这里只接受系统真正装有的文化，否则回落到系统的默认非中性文化。
    /// </summary>
    private static void NormalizeCulture()
    {
        var current = CultureInfo.CurrentUICulture;
        var target = FindInstalledCulture(current.Name);
        if (target is null || string.Equals(target.Name, current.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CultureInfo.DefaultThreadCurrentUICulture = target;
        CultureInfo.CurrentUICulture = target;

        // 数据绑定的语言默认取自 FrameworkElement.Language，用已安装的具体文化覆盖，
        // 避免绑定引擎去解析一个系统并未安装的区域设置。
        var language = (System.Windows.Markup.XmlLanguage)System.Windows.Markup.XmlLanguage
            .GetLanguage(target.IetfLanguageTag);
        System.Windows.FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(System.Windows.FrameworkElement),
            new System.Windows.FrameworkPropertyMetadata(language));
    }

    /// <summary>
    /// 在系统已安装的非中性文化中找出与指定名称最接近的一个。
    ///
    /// 判定依据是系统实际枚举出的文化集合，而不是名称本身：
    /// 进程继承的区域设置可能指向一个并未真正安装的文化，
    /// 仅比对名称会误以为它可用。
    /// </summary>
    /// <param name="name">期望的区域设置名称。</param>
    /// <returns>找到的具体文化，找不到时返回 null。</returns>
    private static CultureInfo? FindInstalledCulture(string name)
    {
        var installed = CultureInfo.GetCultures(CultureTypes.AllCultures)
            .Where(c => !c.IsNeutralCulture)
            .ToList();

        if (installed.Count == 0)
        {
            return null;
        }

        if (installed.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return CultureInfo.CurrentUICulture;
        }

        return installed.FirstOrDefault(c =>
                   string.Equals(c.TwoLetterISOLanguageName, name, StringComparison.OrdinalIgnoreCase))
               ?? installed[0];
    }

    /// <summary>
    /// 把未处理异常写入应用数据目录下的崩溃日志，失败不影响退出流程。
    /// </summary>
    /// <param name="exception">待记录的异常，可为空。</param>
    private static void WriteCrashLog(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            string directory = ResolveDataRoot();
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "crash.log");

            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:O}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 记录失败不得掩盖原始异常。
        }
    }

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

        // 退出时必须释放投屏服务：它持有到实例的 VNC 与 QMP 连接，
        // 不随进程一并回收会留下半开的连接，下次启动时端口可能仍被占用。
        Exit += async (_, _) =>
        {
            if (_services is not null)
            {
                await _services.Projection.DisposeAsync().ConfigureAwait(true);
            }
        };

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
                _services.Terms,
                version: null,
                importer: _services.Importer,
                bootAssetExtractor: _services.BootAssetExtractor,
                projectionWindows: _services.ProjectionWindows,
                fileTransferWindows: _services.FileTransferWindows,
                applicationWindows: _services.ApplicationWindows,
                moduleWindows: _services.ModuleWindows);

            // 投屏窗口归属主窗口，主窗口最小化时投屏一并最小化，任务栏不出现多余条目。
            ((MainViewModel)main.DataContext).ProjectionOwner = main;

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