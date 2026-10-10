using System.Windows;
using XBear.App.Presentation;
using XBear.App.ViewModels;
using XBear.Core.Projection;

namespace XBear.App.Views;

/// <summary>
/// 投屏窗口的宿主契约。界面层只依赖本契约，因此命令层的去重与置前行为
/// 可以脱离真实窗口验证，真实窗口由 WPF 实现承接。
/// </summary>
public interface IProjectionWindowHost
{
    /// <summary>
    /// 为指定实例打开投屏窗口。同一实例已有投屏窗口时置前而不是重复打开。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="instanceName">实例显示名，用于窗口标题。</param>
    /// <param name="owner">宿主窗口。</param>
    void Open(string instanceId, string instanceName, Window? owner);

    /// <summary>
    /// 关闭指定实例的投屏窗口。窗口不存在时不做任何事。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    void Close(string instanceId);
}

/// <summary>
/// 投屏窗口的 WPF 宿主。按实例维护已打开的窗口，保证同一实例只有一扇投屏窗口，
/// 重复打开时置前而不是再建一扇；窗口关闭时从登记表移除，投屏会话由窗口自身收敛。
/// </summary>
public sealed class ProjectionWindowHost : IProjectionWindowHost
{
    private readonly IProjectionService _projection;
    private readonly TerminologyCatalog _terms;
    private readonly Func<ProjectionWindowViewModel, Window?, ProjectionWindow> _factory;
    private readonly Dictionary<string, ProjectionWindow> _windows = new(StringComparer.Ordinal);

    /// <summary>
    /// 构造投屏窗口宿主。
    /// </summary>
    /// <param name="projection">投屏服务。</param>
    /// <param name="terms">界面文案术语来源。</param>
    /// <param name="factory">投屏窗口构造委托，为空时使用默认构造。</param>
    public ProjectionWindowHost(
        IProjectionService projection,
        TerminologyCatalog terms,
        Func<ProjectionWindowViewModel, Window?, ProjectionWindow>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(terms);

        _projection = projection;
        _terms = terms;
        _factory = factory ?? ((viewModel, owner) => new ProjectionWindow(viewModel, owner));
    }

    /// <summary>当前已打开投屏窗口的实例标识。</summary>
    public IReadOnlyCollection<string> OpenInstanceIds => _windows.Keys;

    /// <inheritdoc />
    public void Open(string instanceId, string instanceName, Window? owner)
    {
        if (_windows.TryGetValue(instanceId, out ProjectionWindow? existing))
        {
            // 同一实例已开投屏：置前即可，重建窗口会白白断掉正在进行的会话。
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var viewModel = new ProjectionWindowViewModel(
            _projection,
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
        if (_windows.Remove(instanceId, out ProjectionWindow? window))
        {
            window.Close();
        }
    }

    /// <summary>
    /// 把动作编组到界面线程。取帧循环在后台线程推进，
    /// 画面与绑定只能在界面线程上改写。
    /// </summary>
    /// <param name="action">待执行动作。</param>
    private void DispatcherPost(Action action)
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