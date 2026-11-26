using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SnapPocket.Capture;
using SnapPocket.Core.Interop;
using Rectangle = System.Drawing.Rectangle;
using Screen = System.Windows.Forms.Screen;

namespace SnapPocket.UI.Capture;

public partial class ScrollingPreviewWindow : Window
{
    private const int PreviewWidth = 216;
    private const int FinishHotkeyId = 0x5341;
    private const int CancelHotkeyId = 0x5342;
    private readonly Rectangle _region;
    private (Rectangle Bounds, double Dpi, bool AlignBottom) _dock;
    private IntPtr _windowHandle;
    private Rectangle _windowBounds;
    private bool _finishing;
    private bool _canSavePartial;
    private bool _closed;
    private bool _finishHotkeyRegistered;
    private HwndSource? _source;
    private WriteableBitmap? _previewBitmap;
    private int _previewHeight;

    public event Action<bool>? FinishRequested;
    public event Action? CancelRequested;

    public ScrollingPreviewWindow(Rectangle captureRegion)
    {
        InitializeComponent();
        _region = captureRegion;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowHandle = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(HandleHotkey);
        uint modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT;
        _finishHotkeyRegistered = NativeMethods.RegisterHotKey(_windowHandle, FinishHotkeyId, modifiers, 0x0D);
        if (_finishHotkeyRegistered)
            FinishButton.ToolTip = "Ctrl + Shift + Enter";
        if (NativeMethods.RegisterHotKey(_windowHandle, CancelHotkeyId, modifiers, 0x08))
            CancelButton.ToolTip = "Ctrl + Shift + Backspace";
        _dock = GetDockedBounds();
        FitContent();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        NativeMethods.UnregisterHotKey(_windowHandle, FinishHotkeyId);
        NativeMethods.UnregisterHotKey(_windowHandle, CancelHotkeyId);
        if (_source is { IsDisposed: false })
            _source.RemoveHook(HandleHotkey);
        base.OnClosed(e);
    }

    private IntPtr HandleHotkey(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case FinishHotkeyId:
                    // Saving a partial image requires the explicitly labeled button.
                    if (!_canSavePartial)
                        RequestFinish();
                    handled = true;
                    break;
                case CancelHotkeyId:
                    CancelRequested?.Invoke();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private (Rectangle Bounds, double Dpi, bool AlignBottom) GetDockedBounds()
    {
        const int gap = 8;
        var screens = Screen.AllScreens.OrderByDescending(screen =>
        {
            var intersection = Rectangle.Intersect(_region, screen.Bounds);
            return (long)intersection.Width * intersection.Height;
        }).ToArray();

        var layouts = screens.Select(screen =>
        {
            var area = screen.WorkingArea;
            var monitor = NativeMethods.MonitorFromPoint(
                new NativeMethods.POINT { X = area.Left + area.Width / 2, Y = area.Top + area.Height / 2 },
                NativeMethods.MONITOR_DEFAULTTONEAREST);
            double dpi = NativeMethods.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1;
            int width = Math.Min(area.Width - 2 * gap, (int)Math.Round(PreviewWidth * dpi));
            int height = Math.Min(area.Height - 2 * gap, Math.Max(_region.Height, (int)Math.Round(340 * dpi)));
            return (Area: area, Width: width, Height: height, Dpi: dpi);
        }).ToArray();

        foreach (bool right in new[] { true, false })
        {
            foreach (var (area, width, height, dpi) in layouts)
            {
                int near = right ? Math.Max(area.Left + gap, _region.Right + gap)
                    : Math.Min(area.Right - gap, _region.Left - gap);
                int available = right ? area.Right - gap - near : near - area.Left - gap;
                if (available < 156 * dpi)
                    continue;

                int actualWidth = Math.Min(width, available);
                int top = Math.Clamp(_region.Top, area.Top + gap, area.Bottom - gap - height);
                return (new Rectangle(right ? near : near - actualWidth, top, actualWidth, height), dpi, false);
            }
        }

        foreach (var (area, width, height, dpi) in layouts)
        {
            int below = Math.Max(area.Top + gap, _region.Bottom + gap);
            int above = Math.Min(area.Bottom - gap, _region.Top - gap);
            int belowSpace = area.Bottom - gap - below;
            int aboveSpace = above - area.Top - gap;
            if (Math.Max(belowSpace, aboveSpace) < 120 * dpi)
                continue;

            bool dockBelow = belowSpace >= aboveSpace;
            int actualHeight = Math.Min(height, dockBelow ? belowSpace : aboveSpace);
            int left = Math.Clamp(_region.Right - width, area.Left + gap, area.Right - gap - width);
            return (new Rectangle(left, dockBelow ? below : above - actualHeight, width, actualHeight), dpi, !dockBelow);
        }

        // A full-screen selection can leave no free area. The preview is a layered
        // window, which the scrolling engine's GDI SourceCopy capture omits.
        var fallback = layouts[0];
        var bounds = new Rectangle(fallback.Area.Right - fallback.Width - gap,
            Math.Clamp(_region.Top, fallback.Area.Top + gap, fallback.Area.Bottom - fallback.Height - gap),
            fallback.Width, fallback.Height);
        return (bounds, fallback.Dpi, false);
    }

    private void FitContent()
    {
        var (limit, dpi, alignBottom) = _dock;
        Footer.Measure(new Size(limit.Width / dpi, double.PositiveInfinity));
        int footerHeight = (int)Math.Ceiling(Footer.DesiredSize.Height * dpi);
        int imageHeight = _previewBitmap is { } image
            ? (int)Math.Ceiling((double)limit.Width * _previewHeight / image.PixelWidth)
            : 0;
        int height = Math.Min(limit.Height, footerHeight + imageHeight);
        int top = alignBottom ? limit.Bottom - height : Math.Clamp(_region.Top, limit.Top, limit.Bottom - height);
        var bounds = new Rectangle(limit.Left, top, limit.Width, height);
        if (bounds == _windowBounds)
            return;

        NativeMethods.SetWindowPos(_windowHandle, NativeMethods.HWND_TOPMOST, bounds.X, bounds.Y,
            bounds.Width, bounds.Height, NativeMethods.SWP_NOACTIVATE);
        _windowBounds = bounds;
    }

    public void UpdateProgress(ScrollingCaptureProgress progress)
    {
        if (_closed)
            return;

        if (progress.RequiresPartialConfirmation && !_canSavePartial)
            _finishing = false;
        _canSavePartial = progress.RequiresPartialConfirmation;
        FinishButton.Content = _canSavePartial ? "保存已拼接部分" : "完成拼接";
        FinishButton.ToolTip = !_canSavePartial && _finishHotkeyRegistered ? "Ctrl + Shift + Enter" : null;
        if (!_finishing)
        {
            StatusText.Text = progress.IsWarning
                ? $"已拼接 {progress.TotalHeightPixels} px\n{progress.StatusText}"
                : string.Empty;
            StatusNotice.Visibility = progress.IsWarning ? Visibility.Visible : Visibility.Collapsed;
        }

        bool follow = false;
        if (progress.PreviewUpdate is { } update)
        {
            follow = PreviewScrollViewer.ScrollableHeight - PreviewScrollViewer.VerticalOffset < 12;
            ApplyPreview(update);
        }
        FinishButton.IsEnabled = !_finishing && _previewBitmap is not null;
        FitContent();
        if (follow)
            PreviewScrollViewer.ScrollToEnd();
    }

    private void ApplyPreview(ScrollingPreviewUpdate update)
    {
        var previous = _previewBitmap;
        var bitmap = previous;
        int width = update.Pixels.PixelWidth;
        if (bitmap is null || bitmap.PixelHeight < update.Height)
        {
            int capacity = Math.Min(update.HeightLimit, Math.Max(update.Height, (bitmap?.PixelHeight ?? 128) * 2));
            bitmap = new WriteableBitmap(width, capacity, 96, 96, update.Pixels.Format, null);
        }

        bitmap.Lock();
        try
        {
            int stride = bitmap.BackBufferStride;
            var dirty = new Int32Rect(0, update.Top, width, update.Pixels.PixelHeight);
            if (bitmap != previous && previous is not null && update.Top > 0)
            {
                previous.CopyPixels(new Int32Rect(0, 0, width, update.Top), bitmap.BackBuffer,
                    stride * update.Top, stride);
                dirty = new Int32Rect(0, 0, width, update.Height);
            }
            update.Pixels.CopyPixels(Int32Rect.Empty, bitmap.BackBuffer + update.Top * stride,
                stride * update.Pixels.PixelHeight, stride);
            bitmap.AddDirtyRect(dirty);
        }
        finally
        {
            bitmap.Unlock();
        }

        double displayWidth = _dock.Bounds.Width / _dock.Dpi;
        double scale = displayWidth / width;
        if (bitmap != previous)
        {
            _previewBitmap = bitmap;
            LivePreviewImage.Source = bitmap;
            LivePreviewImage.Width = displayWidth;
            LivePreviewImage.Height = bitmap.PixelHeight * scale;
            PreviewExtent.Width = displayWidth;
        }
        _previewHeight = update.Height;
        PreviewExtent.Height = _previewHeight * scale;
    }

    private void FinishNow_Click(object sender, RoutedEventArgs e) => RequestFinish();

    private void RequestFinish()
    {
        if (_closed || _finishing || !FinishButton.IsEnabled)
            return;

        _finishing = true;
        FinishButton.IsEnabled = false;
        StatusText.Text = _canSavePartial ? "正在生成已拼接部分…" : "正在检查最后一帧…";
        StatusNotice.Visibility = Visibility.Visible;
        FitContent();
        FinishRequested?.Invoke(_canSavePartial);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
}
