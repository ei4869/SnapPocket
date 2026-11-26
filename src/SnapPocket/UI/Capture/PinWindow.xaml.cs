using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SkiaSharp;
using SnapPocket.Core.Interop;
using SnapPocket.Core.Models;
using SnapPocket.Core.Services;
using SnapPocket.UI.Annotation;

namespace SnapPocket.UI.Capture;

public partial class PinWindow : Window
{
    private enum PinDragMode
    {
        None,
        Move,
        ResizeTopLeft,
        ResizeTopRight,
        ResizeBottomLeft,
        ResizeBottomRight,
        ResizeLeft,
        ResizeRight,
        ResizeTop,
        ResizeBottom
    }

    // Outer margin (2*2) + outer border (1*2) + inner border (1.5*2) = 9 DIPs total chrome
    private const double FrameChromeDip = 9.0;
    private const double CornerHitSize = 15.0;
    private const double EdgeHitSize = 7.0;

    private readonly StorageService _storageService;
    private readonly AppContextInfo _sourceContext;
    private readonly Action<SaveOutcome>? _onSaved;
    private readonly DispatcherTimer _hudHideTimer;
    private readonly ConfigService? _configService;
    private bool _exporting;

    private SKBitmap _bitmap;
    private double _zoom = 1.0;

    private IntPtr _hwnd;
    private PinDragMode _dragMode = PinDragMode.None;
    private NativeMethods.POINT _dragStartCursorPhys;
    private NativeMethods.RECT _dragStartRectPhys;

    public PinWindow(
        SKBitmap bitmap,
        AppContextInfo sourceContext,
        StorageService storageService,
        Action<SaveOutcome>? onSaved = null,
        System.Drawing.Rectangle? initialPhysicalRect = null,
        ConfigService? configService = null)
    {
        InitializeComponent();

        _bitmap = bitmap.Copy();
        _storageService = storageService;
        _sourceContext = sourceContext;
        _onSaved = onSaved;
        _configService = configService;

        _hudHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1050) };
        _hudHideTimer.Tick += (_, _) =>
        {
            _hudHideTimer.Stop();
            ScaleHud.Visibility = Visibility.Collapsed;
        };

        PinImage.Source = StorageService.ToBitmapSource(_bitmap);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
        };

        Loaded += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var (dpiX, dpiY) = GetDpiScale();
            int chromePxX = (int)Math.Round(FrameChromeDip * dpiX);
            int chromePxY = (int)Math.Round(FrameChromeDip * dpiY);

            var workAreaPhys = NativeMethods.GetMonitorWorkAreaForPoint(
                initialPhysicalRect is { Width: > 0 } ir
                    ? new NativeMethods.POINT { X = ir.Left + ir.Width / 2, Y = ir.Top + ir.Height / 2 }
                    : new NativeMethods.POINT { X = 0, Y = 0 });

            double maxW = workAreaPhys.Width * 0.72;
            double maxH = workAreaPhys.Height * 0.72;
            if (_bitmap.Width > maxW || _bitmap.Height > maxH)
            {
                _zoom = Math.Min(maxW / _bitmap.Width, maxH / _bitmap.Height);
            }

            int targetImgW = Math.Max(40, (int)Math.Round(_bitmap.Width * _zoom));
            int targetImgH = Math.Max(40, (int)Math.Round(_bitmap.Height * _zoom));
            int winW = targetImgW + chromePxX;
            int winH = targetImgH + chromePxY;

            int leftPx, topPx;
            if (initialPhysicalRect is { Width: > 0, Height: > 0 } r)
            {
                leftPx = Math.Clamp(r.Left - chromePxX / 2, workAreaPhys.Left, Math.Max(workAreaPhys.Left, workAreaPhys.Right - winW));
                topPx = Math.Clamp(r.Top - chromePxY / 2, workAreaPhys.Top, Math.Max(workAreaPhys.Top, workAreaPhys.Bottom - winH));
            }
            else
            {
                leftPx = workAreaPhys.Left + (workAreaPhys.Width - winW) / 2;
                topPx = workAreaPhys.Top + (workAreaPhys.Height - winH) / 2;
            }

            ApplyAtomicBoundsPixels(leftPx, topPx, winW, winH);
        };

        Closed += (_, _) =>
        {
            _hudHideTimer.Stop();
            _bitmap.Dispose();
        };
    }

    private (double DpiX, double DpiY) GetDpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        return (
            source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0,
            source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0);
    }

    /// <summary>
    /// Updates both position and size in a single atomic Win32 SetWindowPos call,
    /// completely eliminating multi-pass WPF move/resize jitter.
    /// </summary>
    private void ApplyAtomicBoundsPixels(int x, int y, int widthPx, int heightPx)
    {
        if (_hwnd == IntPtr.Zero)
            _hwnd = new WindowInteropHelper(this).Handle;

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                x,
                y,
                widthPx,
                heightPx,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        else
        {
            var (dpiX, dpiY) = GetDpiScale();
            Left = x / dpiX;
            Top = y / dpiY;
            Width = widthPx / dpiX;
            Height = heightPx / dpiY;
        }
    }

    private void ApplyZoomCentered(double nextZoom)
    {
        var (dpiX, dpiY) = GetDpiScale();
        int chromePxX = (int)Math.Round(FrameChromeDip * dpiX);
        int chromePxY = (int)Math.Round(FrameChromeDip * dpiY);

        double minZoom = Math.Max(48.0 / Math.Max(1, _bitmap.Width), 48.0 / Math.Max(1, _bitmap.Height));
        _zoom = Math.Clamp(nextZoom, Math.Min(minZoom, 0.15), 5.0);

        int newImgW = Math.Max(40, (int)Math.Round(_bitmap.Width * _zoom));
        int newImgH = Math.Max(40, (int)Math.Round(_bitmap.Height * _zoom));
        int newWinW = newImgW + chromePxX;
        int newWinH = newImgH + chromePxY;

        if (_hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(_hwnd, out var curr))
        {
            int centerX = (curr.Left + curr.Right) / 2;
            int centerY = (curr.Top + curr.Bottom) / 2;
            int newX = centerX - newWinW / 2;
            int newY = centerY - newWinH / 2;
            ApplyAtomicBoundsPixels(newX, newY, newWinW, newWinH);
        }
        else
        {
            ApplyAtomicBoundsPixels((int)Math.Round(Left * dpiX), (int)Math.Round(Top * dpiY), newWinW, newWinH);
        }

        ShowHud($"{(int)Math.Round(_zoom * 100)}%  ·  {newImgW}×{newImgH}");
    }

    private void ShowHud(string text)
    {
        ScaleHudText.Text = text;
        ScaleHud.Visibility = Visibility.Visible;
        _hudHideTimer.Stop();
        _hudHideTimer.Start();
    }

    private PinDragMode HitTestDragMode(Point pos)
    {
        double w = ContentGrid.ActualWidth;
        double h = ContentGrid.ActualHeight;
        if (w <= 0 || h <= 0) return PinDragMode.Move;

        bool left = pos.X <= CornerHitSize;
        bool right = pos.X >= w - CornerHitSize;
        bool top = pos.Y <= CornerHitSize;
        bool bottom = pos.Y >= h - CornerHitSize;

        if (top && left) return PinDragMode.ResizeTopLeft;
        if (top && right) return PinDragMode.ResizeTopRight;
        if (bottom && left) return PinDragMode.ResizeBottomLeft;
        if (bottom && right) return PinDragMode.ResizeBottomRight;

        if (pos.X <= EdgeHitSize) return PinDragMode.ResizeLeft;
        if (pos.X >= w - EdgeHitSize) return PinDragMode.ResizeRight;
        if (pos.Y <= EdgeHitSize) return PinDragMode.ResizeTop;
        if (pos.Y >= h - EdgeHitSize) return PinDragMode.ResizeBottom;

        return PinDragMode.Move;
    }

    private void UpdateCursorForMode(PinDragMode mode)
    {
        ContentGrid.Cursor = mode switch
        {
            PinDragMode.ResizeTopLeft or PinDragMode.ResizeBottomRight => Cursors.SizeNWSE,
            PinDragMode.ResizeTopRight or PinDragMode.ResizeBottomLeft => Cursors.SizeNESW,
            PinDragMode.ResizeLeft or PinDragMode.ResizeRight => Cursors.SizeWE,
            PinDragMode.ResizeTop or PinDragMode.ResizeBottom => Cursors.SizeNS,
            _ => Cursors.SizeAll
        };
    }

    private void ContentGrid_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(ContentGrid);
        var mode = HitTestDragMode(pos);

        if (e.ClickCount == 2 && mode == PinDragMode.Move)
        {
            Close();
            return;
        }

        if (mode == PinDragMode.Move)
        {
            DragMove();
            return;
        }

        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out _dragStartRectPhys))
            return;

        _dragMode = mode;
        NativeMethods.GetCursorPos(out _dragStartCursorPhys);
        ContentGrid.CaptureMouse();
        e.Handled = true;
    }

    private void ContentGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMode == PinDragMode.None)
        {
            UpdateCursorForMode(HitTestDragMode(e.GetPosition(ContentGrid)));
            return;
        }

        if (!NativeMethods.GetCursorPos(out var currPhys)) return;

        var (dpiX, dpiY) = GetDpiScale();
        int chromePxX = (int)Math.Round(FrameChromeDip * dpiX);
        int chromePxY = (int)Math.Round(FrameChromeDip * dpiY);

        int dx = currPhys.X - _dragStartCursorPhys.X;
        int dy = currPhys.Y - _dragStartCursorPhys.Y;

        double startImgW = Math.Max(40, _dragStartRectPhys.Width - chromePxX);
        double startImgH = Math.Max(40, _dragStartRectPhys.Height - chromePxY);
        double aspect = (double)_bitmap.Width / Math.Max(1, _bitmap.Height);

        double minImgW = Math.Max(48.0, 48.0 * aspect);
        double maxImgW = _bitmap.Width * 5.0;

        double targetImgW = startImgW;
        bool anchorRight = false;
        bool anchorBottom = false;

        switch (_dragMode)
        {
            case PinDragMode.ResizeBottomRight:
                targetImgW = Math.Abs(dx) >= Math.Abs(dy * aspect)
                    ? startImgW + dx
                    : (startImgH + dy) * aspect;
                break;

            case PinDragMode.ResizeBottomLeft:
                anchorRight = true;
                targetImgW = Math.Abs(dx) >= Math.Abs(dy * aspect)
                    ? startImgW - dx
                    : (startImgH + dy) * aspect;
                break;

            case PinDragMode.ResizeTopRight:
                anchorBottom = true;
                targetImgW = Math.Abs(dx) >= Math.Abs(dy * aspect)
                    ? startImgW + dx
                    : (startImgH - dy) * aspect;
                break;

            case PinDragMode.ResizeTopLeft:
                anchorRight = true;
                anchorBottom = true;
                targetImgW = Math.Abs(dx) >= Math.Abs(dy * aspect)
                    ? startImgW - dx
                    : (startImgH - dy) * aspect;
                break;

            case PinDragMode.ResizeRight:
                targetImgW = startImgW + dx;
                break;

            case PinDragMode.ResizeLeft:
                anchorRight = true;
                targetImgW = startImgW - dx;
                break;

            case PinDragMode.ResizeBottom:
                targetImgW = (startImgH + dy) * aspect;
                break;

            case PinDragMode.ResizeTop:
                anchorBottom = true;
                targetImgW = (startImgH - dy) * aspect;
                break;
        }

        targetImgW = Math.Clamp(targetImgW, minImgW, Math.Max(minImgW, maxImgW));
        int newImgW = Math.Max(40, (int)Math.Round(targetImgW));
        int newImgH = Math.Max(40, (int)Math.Round(newImgW / aspect));
        int newWinW = newImgW + chromePxX;
        int newWinH = newImgH + chromePxY;

        int newX = anchorRight ? _dragStartRectPhys.Right - newWinW : _dragStartRectPhys.Left;
        int newY = anchorBottom ? _dragStartRectPhys.Bottom - newWinH : _dragStartRectPhys.Top;

        _zoom = (double)newImgW / Math.Max(1, _bitmap.Width);
        ApplyAtomicBoundsPixels(newX, newY, newWinW, newWinH);
        ShowHud($"{(int)Math.Round(_zoom * 100)}%  ·  {newImgW}×{newImgH}");
    }

    private void ContentGrid_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode == PinDragMode.None) return;
        _dragMode = PinDragMode.None;
        ContentGrid.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            double delta = e.Delta > 0 ? 0.08 : -0.08;
            Opacity = Math.Clamp(Opacity + delta, 0.22, 1.0);
            ShowHud($"不透明度 {(int)Math.Round(Opacity * 100)}%");
            return;
        }

        double factor = e.Delta > 0 ? 1.08 : (1.0 / 1.08);
        ApplyZoomCentered(_zoom * factor);
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        FrameBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(76, 154, 255));
        CornerGripsLayer.Visibility = Visibility.Visible;
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragMode != PinDragMode.None) return;
        FrameBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(136, 76, 154, 255));
        CornerGripsLayer.Visibility = Visibility.Collapsed;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private async void SaveMenuItem_Click(object sender, RoutedEventArgs e) => await SavePinAsync();

    private async Task SavePinAsync()
    {
        if (_exporting)
            return;
        _exporting = true;
        using var image = _bitmap.Copy();
        try
        {
            var outcome = await _storageService.SaveAndCopyAsync(image, _sourceContext, Core.Models.CaptureMode.Pin, DateTime.Now);
            _onSaved?.Invoke(outcome);
            if (!outcome.Success && IsLoaded)
                ShowHud("保存失败，可再次尝试");
        }
        finally
        {
            _exporting = false;
        }
    }

    private async void CopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting)
            return;
        _exporting = true;
        using var image = _bitmap.Copy();
        try
        {
            bool copied = await _storageService.TryCopyAsync(image);
            if (IsLoaded)
                ShowHud(copied ? "已复制" : "复制失败，可再次尝试");
        }
        finally
        {
            _exporting = false;
        }
    }

    private async void AnnotateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting)
            return;
        var editor = new AnnotationWindow(_bitmap, "贴图二次标注", _configService) { Owner = this };
        if (editor.ShowDialog() == true && editor.FinalFlattenedBitmap is not null)
        {
            _bitmap.Dispose();
            _bitmap = editor.FinalFlattenedBitmap;
            PinImage.Source = StorageService.ToBitmapSource(_bitmap);
            ApplyZoomCentered(_zoom);

            if (editor.ResultAction == AnnotationResultAction.Save)
                await SavePinAsync();
        }
    }

    private void PresetZoomMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && double.TryParse(mi.Tag?.ToString(), CultureInfo.InvariantCulture, out double preset))
        {
            ApplyZoomCentered(preset);
        }
    }

    private void ResetOpacityMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Opacity = 1.0;
        ShowHud("不透明度 100%");
    }

    private void CloseMenuItem_Click(object sender, RoutedEventArgs e) => Close();
}
