using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XBear.App.Presentation;
using XBear.App.ViewModels;
using XBear.Core.Abstractions;
using XBear.Core.Projection;

namespace XBear.App.Views;

/// <summary>
/// 投屏窗口。画面以 <see cref="WriteableBitmap"/> 直接承接帧快照的 BGRA 像素，
/// 分辨率变化时重建位图并按新宽高比调整外框；指针与键盘事件经坐标换算后
/// 交由视图模型投递到投屏输入通道。窗口关闭时终止投屏会话。
/// </summary>
public partial class ProjectionWindow : Window
{
    private readonly ProjectionWindowViewModel _viewModel;

    private WriteableBitmap? _bitmap;
    private long _paintedSequence = -1;
    private double _chromeHeight;

    /// <summary>
    /// 构造投屏窗口。
    /// </summary>
    /// <param name="viewModel">投屏窗口视图模型。</param>
    /// <param name="owner">宿主窗口，为空时窗口独立显示。</param>
    public ProjectionWindow(ProjectionWindowViewModel viewModel, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        if (owner is not null)
        {
            Owner = owner;
        }

        _viewModel.FrameReady += OnFrameReady;
        _viewModel.FrameSizeChanged += OnFrameSizeChanged;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// 取当前呈现面的设备无关像素尺寸。
    /// </summary>
    /// <returns>呈现面宽度与高度。</returns>
    private (double Width, double Height) MeasureSurface() =>
        (SurfaceHost.ActualWidth, SurfaceHost.ActualHeight);

    /// <summary>
    /// 取窗口边框占用的高度。窗口呈现后才有值，未呈现时按零处理。
    /// </summary>
    /// <returns>标题栏与边框合计高度。</returns>
    private double ResolveChromeHeight() => Math.Max(0, ActualHeight - RenderSize.Height);

    /// <summary>
    /// 把窗口内坐标换算为画面像素坐标。
    /// </summary>
    /// <param name="position">窗口内坐标。</param>
    /// <param name="point">换算得到的画面像素坐标。</param>
    /// <returns>落在画面内时返回 true。</returns>
    private bool TryMap(Point position, out InputPoint point) =>
        _viewModel.TryMapPoint(
            position.X,
            position.Y,
            MeasureSurface().Width,
            MeasureSurface().Height,
            out point);

    /// <summary>
    /// 取当前呈现面所用的设备像素密度。窗口尚未呈现时回落为默认密度。
    /// </summary>
    /// <returns>每英寸像素数。</returns>
    private double ResolveDpi()
    {
        DpiScale scale = VisualTreeHelper.GetDpi(this);
        return scale.DpiScaleX > 0 ? 96 * scale.DpiScaleX : 96;
    }

    /// <summary>
    /// 按画面宽高比调整窗口外框，并限制在屏幕可用范围内。
    /// 尺寸非法时保留当前外框，不让窗口塌缩。
    /// </summary>
    /// <param name="geometry">当前画面尺寸。</param>
    private void FitWindowToFrame(ScreenGeometry geometry)
    {
        _chromeHeight = ResolveChromeHeight();

        (double Width, double Height)? fitted = ProjectionWindowSizing.Fit(
            geometry,
            SystemParameters.WorkArea.Width,
            Math.Max(SystemParameters.WorkArea.Height - _chromeHeight, 1));

        if (fitted is not { } size)
        {
            return;
        }

        SizeToContent = SizeToContent.Manual;
        Width = size.Width;
        Height = size.Height + _chromeHeight;
    }

    /// <summary>
    /// 把一帧像素写入位图。像素已是 BGRA 排列且行跨度与位图背缓冲一致，
    /// 因此可一次性整块拷贝，无需逐像素转换。
    /// </summary>
    /// <param name="frame">画面快照。</param>
    private void Paint(ProjectionFrame frame)
    {
        if (_bitmap is null ||
            _bitmap.PixelWidth != frame.Width ||
            _bitmap.PixelHeight != frame.Height)
        {
            double dpi = ResolveDpi();
            _bitmap = new WriteableBitmap(
                frame.Width,
                frame.Height,
                dpi,
                dpi,
                PixelFormats.Bgra32,
                null);

            FrameImage.Source = _bitmap;
        }

        WriteableBitmap bitmap = _bitmap;
        int byteCount = bitmap.PixelWidth * bitmap.PixelHeight * 4;

        bitmap.Lock();
        try
        {
            Marshal.Copy(frame.Pixels, 0, bitmap.BackBuffer, byteCount);
        }
        finally
        {
            bitmap.Unlock();
        }

        // Unlock 会把脏区提交给合成器并触发重绘，因此这里不需要额外调用刷新。
        _paintedSequence = frame.Sequence;
    }

    /// <summary>
    /// 取最新快照并刷新位图。同一帧序号只绘制一次，避免重复拷贝像素。
    /// </summary>
    private void ApplyLatestFrame()
    {
        ProjectionFrame? frame = _viewModel.CaptureFrame();
        if (frame is null || frame.Sequence == _paintedSequence)
        {
            return;
        }

        Paint(frame);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 首帧可能在窗口呈现之前就已到达，这里补一次，避免空等下一帧才出现画面。
        ApplyLatestFrame();
        SurfaceHost.Focus();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.FrameReady -= OnFrameReady;
        _viewModel.FrameSizeChanged -= OnFrameSizeChanged;
        _ = _viewModel.CloseAsync();
    }

    private void OnFrameReady(object? sender, EventArgs e) => ApplyLatestFrame();

    private void OnFrameSizeChanged(object? sender, ScreenGeometry geometry)
    {
        _bitmap = null;
        FitWindowToFrame(geometry);
        ApplyLatestFrame();
    }

    private async void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        SurfaceHost.Focus();

        if (!TryMap(e.GetPosition(SurfaceHost), out InputPoint point))
        {
            return;
        }

        e.Handled = true;
        await _viewModel.PointerDownAsync(point).ConfigureAwait(true);
    }

    private async void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (!TryMap(e.GetPosition(SurfaceHost), out InputPoint point))
        {
            return;
        }

        e.Handled = true;
        await _viewModel.PointerMoveAsync(point).ConfigureAwait(true);
    }

    private async void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!TryMap(e.GetPosition(SurfaceHost), out InputPoint point))
        {
            return;
        }

        e.Handled = true;
        await _viewModel.PointerUpAsync(point).ConfigureAwait(true);
    }

    private async void OnSurfaceMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        await _viewModel.WheelAsync(e.Delta).ConfigureAwait(true);
    }

    private async void OnSurfaceKeyDown(object sender, KeyEventArgs e)
    {
        if (!ProjectionKeyMap.TryResolve(e.Key, out AndroidKey key))
        {
            return;
        }

        e.Handled = true;
        await _viewModel.KeyDownAsync(key).ConfigureAwait(true);
    }

    private async void OnSurfaceKeyUp(object sender, KeyEventArgs e)
    {
        if (!ProjectionKeyMap.TryResolve(e.Key, out AndroidKey key))
        {
            return;
        }

        e.Handled = true;
        await _viewModel.KeyUpAsync(key).ConfigureAwait(true);
    }
}