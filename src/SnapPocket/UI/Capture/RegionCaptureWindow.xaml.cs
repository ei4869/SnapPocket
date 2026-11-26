using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using SnapPocket.Core.Interop;
using SnapPocket.Core.Services;

namespace SnapPocket.UI.Capture;

public partial class RegionCaptureWindow : Window
{
    private readonly SKBitmap _fullSnapshot;
    private readonly System.Drawing.Rectangle _virtualBounds;
    private readonly List<SnapCandidate> _snapCandidates;
    private readonly WriteableBitmap _loupeBitmap;
    private readonly byte[] _loupePixels = new byte[11 * 11 * 4];

    private bool _isDragging;
    private Point _dragStartDip;

    public System.Drawing.Rectangle SelectedPhysicalRect { get; private set; } = System.Drawing.Rectangle.Empty;
    public SKBitmap? CroppedBitmap { get; private set; }

    public RegionCaptureWindow(
        SKBitmap fullSnapshot,
        System.Drawing.Rectangle virtualBounds,
        List<SnapCandidate> snapCandidates,
        string? customHint = null)
    {
        InitializeComponent();

        _fullSnapshot = fullSnapshot;
        _virtualBounds = virtualBounds;
        _snapCandidates = snapCandidates;

        if (!string.IsNullOrWhiteSpace(customHint))
            HintPillText.Text = customHint;

        BackgroundImage.Source = StorageService.ToBitmapSource(_fullSnapshot);
        _loupeBitmap = new WriteableBitmap(11, 11, 96, 96, PixelFormats.Bgra32, null);
        MagnifierImage.Source = _loupeBitmap;

        SourceInitialized += OnSourceInitializedPosition;
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            PositionHintPill();
            UpdateDimOverlay(Rect.Empty);
        };
    }

    private void OnSourceInitializedPosition(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            _virtualBounds.Left,
            _virtualBounds.Top,
            _virtualBounds.Width,
            _virtualBounds.Height,
            NativeMethods.SWP_SHOWWINDOW);
    }

    private void PositionHintPill()
    {
        HintPill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = HintPill.DesiredSize.Width;
        var (scaleX, scaleY) = GetBitmapToDipScale();
        double primaryOriginX = Math.Max(0, -_virtualBounds.Left / scaleX);
        double primaryOriginY = Math.Max(0, -_virtualBounds.Top / scaleY);
        var primaryW = SystemParameters.PrimaryScreenWidth;
        Canvas.SetLeft(HintPill, Math.Max(16, primaryOriginX + (primaryW - w) / 2.0));
        Canvas.SetTop(HintPill, primaryOriginY + 18);
    }

    private (double ScaleX, double ScaleY) GetBitmapToDipScale()
    {
        double w = ActualWidth > 0 ? ActualWidth : _virtualBounds.Width;
        double h = ActualHeight > 0 ? ActualHeight : _virtualBounds.Height;
        return (_fullSnapshot.Width / w, _fullSnapshot.Height / h);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
        }
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        Cancel();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _dragStartDip = e.GetPosition(RootGrid);
        RootGrid.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var posDip = e.GetPosition(RootGrid);
        var (scaleX, scaleY) = GetBitmapToDipScale();

        int imgX = Math.Clamp((int)Math.Round(posDip.X * scaleX), 0, _fullSnapshot.Width - 1);
        int imgY = Math.Clamp((int)Math.Round(posDip.Y * scaleY), 0, _fullSnapshot.Height - 1);

        UpdateMagnifier(posDip, imgX, imgY);

        if (_isDragging)
        {
            var dragRect = new Rect(_dragStartDip, posDip);

            if (dragRect.Width >= 4 || dragRect.Height >= 4)
                UpdateSelectionVisuals(dragRect, scaleX, scaleY);
        }
        else
        {
            int screenX = _virtualBounds.Left + imgX;
            int screenY = _virtualBounds.Top + imgY;
            var hoveredSnap = WindowSnapHelper.FindTopmostCandidateAt(_snapCandidates, screenX, screenY);

            if (hoveredSnap is not null)
            {
                var r = hoveredSnap.Bounds;
                var snapDip = new Rect(
                    (r.Left - _virtualBounds.Left) / scaleX,
                    (r.Top - _virtualBounds.Top) / scaleY,
                    r.Width / scaleX,
                    r.Height / scaleY);
                UpdateSelectionVisuals(snapDip, scaleX, scaleY);
            }
            else
            {
                SelectionBorder.Visibility = Visibility.Collapsed;
                SizeBadge.Visibility = Visibility.Collapsed;
                UpdateDimOverlay(Rect.Empty);
            }
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        RootGrid.ReleaseMouseCapture();

        var posDip = e.GetPosition(RootGrid);
        var (scaleX, scaleY) = GetBitmapToDipScale();

        var dragRect = new Rect(_dragStartDip, posDip);
        System.Drawing.Rectangle cropRelative;

        if ((posDip - _dragStartDip).Length < 5)
        {
            int screenX = _virtualBounds.Left + (int)Math.Round(posDip.X * scaleX);
            int screenY = _virtualBounds.Top + (int)Math.Round(posDip.Y * scaleY);
            var candidate = WindowSnapHelper.FindTopmostCandidateAt(_snapCandidates, screenX, screenY);
            if (candidate is null)
            {
                Cancel();
                return;
            }
            var b = candidate.Bounds;
            cropRelative = new System.Drawing.Rectangle(
                b.Left - _virtualBounds.Left,
                b.Top - _virtualBounds.Top,
                b.Width,
                b.Height);
        }
        else if (dragRect.Width >= 3 && dragRect.Height >= 3)
        {
            cropRelative = System.Drawing.Rectangle.FromLTRB(
                (int)Math.Round(dragRect.Left * scaleX),
                (int)Math.Round(dragRect.Top * scaleY),
                (int)Math.Round(dragRect.Right * scaleX),
                (int)Math.Round(dragRect.Bottom * scaleY));
        }
        else
        {
            Cancel();
            return;
        }

        // Clamp strictly within snapshot bounds
        var imgBounds = new System.Drawing.Rectangle(0, 0, _fullSnapshot.Width, _fullSnapshot.Height);
        cropRelative = System.Drawing.Rectangle.Intersect(cropRelative, imgBounds);
        if (cropRelative.Width < 2 || cropRelative.Height < 2)
        {
            Cancel();
            return;
        }

        SelectedPhysicalRect = new System.Drawing.Rectangle(
            _virtualBounds.Left + cropRelative.Left,
            _virtualBounds.Top + cropRelative.Top,
            cropRelative.Width,
            cropRelative.Height);

        CroppedBitmap = CropSkBitmap(_fullSnapshot, cropRelative);
        DialogResult = true;
        Close();
    }

    private void UpdateSelectionVisuals(Rect selDip, double scaleX, double scaleY)
    {
        SelectionBorder.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionBorder, selDip.X);
        Canvas.SetTop(SelectionBorder, selDip.Y);
        SelectionBorder.Width = selDip.Width;
        SelectionBorder.Height = selDip.Height;

        UpdateDimOverlay(selDip);

        int physW = Math.Max(1, (int)Math.Round(selDip.Width * scaleX));
        int physH = Math.Max(1, (int)Math.Round(selDip.Height * scaleY));
        SizeBadgeText.Text = $"{physW} × {physH}";
        SizeBadge.Visibility = Visibility.Visible;
        SizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double badgeX = selDip.X;
        double badgeY = selDip.Y - SizeBadge.DesiredSize.Height - 6;
        if (badgeY < 8) badgeY = selDip.Y + 6;
        Canvas.SetLeft(SizeBadge, Math.Clamp(badgeX, 6, Math.Max(6, ActualWidth - SizeBadge.DesiredSize.Width - 6)));
        Canvas.SetTop(SizeBadge, badgeY);
    }

    private void UpdateDimOverlay(Rect cutoutDip)
    {
        double w = ActualWidth > 0 ? ActualWidth : _virtualBounds.Width;
        double h = ActualHeight > 0 ? ActualHeight : _virtualBounds.Height;
        var fullGeo = new RectangleGeometry(new Rect(0, 0, w, h));

        if (cutoutDip.IsEmpty || cutoutDip.Width <= 0 || cutoutDip.Height <= 0)
        {
            DimOverlayPath.Data = fullGeo;
        }
        else
        {
            var holeGeo = new RectangleGeometry(cutoutDip);
            DimOverlayPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, fullGeo, holeGeo);
        }
    }

    private void UpdateMagnifier(Point cursorDip, int centerX, int centerY)
    {
        int half = 5;
        int idx = 0;
        SKColor centerColor = SKColors.Black;

        for (int dy = -half; dy <= half; dy++)
        {
            int sy = Math.Clamp(centerY + dy, 0, _fullSnapshot.Height - 1);
            for (int dx = -half; dx <= half; dx++)
            {
                int sx = Math.Clamp(centerX + dx, 0, _fullSnapshot.Width - 1);
                var c = _fullSnapshot.GetPixel(sx, sy);
                if (dx == 0 && dy == 0) centerColor = c;

                _loupePixels[idx++] = c.Blue;
                _loupePixels[idx++] = c.Green;
                _loupePixels[idx++] = c.Red;
                _loupePixels[idx++] = 255;
            }
        }

        _loupeBitmap.WritePixels(new Int32Rect(0, 0, 11, 11), _loupePixels, 11 * 4, 0);
        ColorSwatch.Background = new SolidColorBrush(Color.FromRgb(centerColor.Red, centerColor.Green, centerColor.Blue));
        MagnifierInfoText.Text = $"{_virtualBounds.Left + centerX},{_virtualBounds.Top + centerY}  #{centerColor.Red:X2}{centerColor.Green:X2}{centerColor.Blue:X2}";

        MagnifierPanel.Visibility = Visibility.Visible;
        MagnifierPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double mw = MagnifierPanel.DesiredSize.Width;
        double mh = MagnifierPanel.DesiredSize.Height;

        double left = cursorDip.X + 20;
        double top = cursorDip.Y + 20;
        if (left + mw > ActualWidth - 12) left = cursorDip.X - mw - 20;
        if (top + mh > ActualHeight - 12) top = cursorDip.Y - mh - 20;

        Canvas.SetLeft(MagnifierPanel, Math.Max(8, left));
        Canvas.SetTop(MagnifierPanel, Math.Max(8, top));
    }

    private static SKBitmap CropSkBitmap(SKBitmap src, System.Drawing.Rectangle rect)
    {
        var cropped = new SKBitmap(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(cropped);
        using var srcImg = SKImage.FromBitmap(src);
        var srcRect = new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var dstRect = new SKRect(0, 0, rect.Width, rect.Height);
        canvas.DrawImage(srcImg, srcRect, dstRect, SKSamplingOptions.Default);
        return cropped;
    }

    private void Cancel()
    {
        DialogResult = false;
        Close();
    }
}
