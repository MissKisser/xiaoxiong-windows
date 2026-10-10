using System.Windows;
using XBear.App.Presentation;
using XBear.App.ViewModels;
using XBear.Core.Applications;

namespace XBear.App.Views;

/// <summary>
/// 应用管理窗口宿主契约。支持按实例打开与关闭应用管理窗口。
/// </summary>
public interface IApplicationWindowHost
{
    /// <summary>
    /// 为指定实例打开应用管理窗口。同一实例已有窗口时置前。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="instanceName">实例显示名，用于窗口标题。</param>
    /// <param name="owner">宿主窗口。</param>
    void Open(string instanceId, string instanceName, Window? owner);

    /// <summary>
    /// 关闭指定实例的应用管理窗口。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    void Close(string instanceId);
}

/// <summary>
/// 应用管理窗口的 WPF 宿主。按实例维护窗口单例，支持重复打开置前与停止时收敛。
/// </summary>
public sealed class ApplicationWindowHost : IApplicationWindowHost
{
    private readonly ApplicationService _service;
    private readonly Func<string, int?> _getAdbPort;
    private readonly TerminologyCatalog _terms;
    private readonly Func<ApplicationWindowViewModel, Window?, ApplicationWindow> _factory;
    private readonly Dictionary<string, ApplicationWindow> _windows = new(StringComparer.Ordinal);

    /// <summary>
    /// 构造应用管理窗口宿主。
    /// </summary>
    /// <param name="service">应用管理服务。</param>
    /// <param name="getAdbPort">按实例标识获取 adb 端口的委托。</param>
    /// <param name="terms">界面文案术语来源。</param>
    /// <param name="factory">窗口构造工厂。</param>
    public ApplicationWindowHost(
        ApplicationService service,
        Func<string, int?> getAdbPort,
        TerminologyCatalog terms,
        Func<ApplicationWindowViewModel, Window?, ApplicationWindow>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(getAdbPort);
        ArgumentNullException.ThrowIfNull(terms);

        _service = service;
        _getAdbPort = getAdbPort;
        _terms = terms;
        _factory = factory ?? ((vm, owner) => new ApplicationWindow(vm, owner));
    }

    /// <summary>当前已打开应用管理窗口的实例标识集合。</summary>
    public IReadOnlyCollection<string> OpenInstanceIds => _windows.Keys;

    /// <inheritdoc />
    public void Open(string instanceId, string instanceName, Window? owner)
    {
        if (_windows.TryGetValue(instanceId, out ApplicationWindow? existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        int? adbPort = _getAdbPort(instanceId);
        if (adbPort is null)
        {
            return;
        }

        var viewModel = new ApplicationWindowViewModel(
            _service,
            instanceId,
            instanceName,
            adbPort.Value,
            _terms,
            DispatcherPost);

        var window = _factory(viewModel, owner);
        window.Closed += (_, _) => _windows.Remove(instanceId);

        _windows[instanceId] = window;
        window.Show();
    }

    /// <inheritdoc />
    public void Close(string instanceId)
    {
        if (_windows.Remove(instanceId, out ApplicationWindow? window))
        {
            window.Close();
        }
    }

    private static void DispatcherPost(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }
}
