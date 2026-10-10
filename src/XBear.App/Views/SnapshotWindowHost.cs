using System.Windows;
using XBear.App.Presentation;
using XBear.App.ViewModels;
using XBear.Core.Snapshots;

namespace XBear.App.Views;

/// <summary>
/// 快照管理窗口宿主契约。支持按实例打开与关闭快照管理窗口。
/// </summary>
public interface ISnapshotWindowHost
{
    /// <summary>
    /// 为指定实例打开快照管理窗口。同一实例已有窗口时置前。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="instanceName">实例显示名，用于窗口标题。</param>
    /// <param name="owner">宿主窗口。</param>
    void Open(string instanceId, string instanceName, Window? owner);

    /// <summary>
    /// 关闭指定实例的快照管理窗口。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    void Close(string instanceId);
}

/// <summary>
/// 快照管理窗口的 WPF 宿主。按实例维护窗口单例，支持重复打开置前与实例故障时收敛。
/// </summary>
/// <remarks>
/// 与投屏、模块等窗口不同，快照窗口不在实例停止时自动收敛：
/// 恢复流程本身要经历停止与重新启动，停止即关闭会让用户看不到恢复结果。
/// 窗口仅在实例进入故障态时收敛关闭。
/// </remarks>
public sealed class SnapshotWindowHost : ISnapshotWindowHost
{
    private readonly SnapshotService _service;
    private readonly TerminologyCatalog _terms;
    private readonly Func<SnapshotWindowViewModel, Window?, SnapshotWindow> _factory;
    private readonly Dictionary<string, SnapshotWindow> _windows = new(StringComparer.Ordinal);

    /// <summary>
    /// 构造快照管理窗口宿主。
    /// </summary>
    /// <param name="service">快照服务。</param>
    /// <param name="terms">界面文案术语来源。</param>
    /// <param name="factory">窗口构造工厂。</param>
    public SnapshotWindowHost(
        SnapshotService service,
        TerminologyCatalog terms,
        Func<SnapshotWindowViewModel, Window?, SnapshotWindow>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(terms);

        _service = service;
        _terms = terms;
        _factory = factory ?? ((viewModel, owner) => new SnapshotWindow(viewModel, owner));
    }

    /// <summary>当前已打开快照管理窗口的实例标识集合。</summary>
    public IReadOnlyCollection<string> OpenInstanceIds => _windows.Keys;

    /// <inheritdoc />
    public void Open(string instanceId, string instanceName, Window? owner)
    {
        if (_windows.TryGetValue(instanceId, out SnapshotWindow? existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var viewModel = new SnapshotWindowViewModel(
            _service,
            instanceId,
            instanceName,
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
        if (_windows.Remove(instanceId, out SnapshotWindow? window))
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