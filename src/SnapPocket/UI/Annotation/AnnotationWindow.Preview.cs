using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using SnapPocket.Annotation;
using SnapPocket.Annotation.Models;

namespace SnapPocket.UI.Annotation;

public partial class AnnotationWindow
{
    private readonly AnnotationPreviewRenderer _previewRenderer = new();
    private WriteableBitmap? _previewBitmap;
    private Int32Rect _previewViewport;
    private DpiScale _previewDpi;
    private double _previewZoom;
    private SKRect? _dirtyPreview;
    private bool _fullPreviewPending;
    private bool _renderQueued;

    private float PreviewUiScale => (float)(1.0 / Math.Max(0.25, _displayScale));

    private SKRect GetPreviewBounds(AnnotationItem item) => AnnotationRenderer.InputBounds(item, PreviewUiScale);

    private void QueueEditedItem(AnnotationItem item, SKRect previous)
    {
        // A changing privacy effect can change the pixels sampled by later overlapping effects.
        QueuePreview(AnnotationRenderer.IsPrivacyEffect(item.Tool) ? null : SKRect.Union(previous, GetPreviewBounds(item)));
    }

    private void QueuePreview(SKRect? area = null)
    {
        if (area is { } dirty)
            _dirtyPreview = _dirtyPreview is { } pending ? SKRect.Union(pending, dirty) : dirty;
        else
            _fullPreviewPending = true;

        if (_renderQueued || !IsLoaded)
            return;
        _renderQueued = true;
        CompositionTarget.Rendering += OnPreviewRendering;
    }

    private void OnPreviewRendering(object? sender, EventArgs e)
    {
        if (!RenderPreview())
            return;

        _dirtyPreview = null;
        _fullPreviewPending = false;
        _renderQueued = false;
        CompositionTarget.Rendering -= OnPreviewRendering;
    }

    private bool RenderPreview()
    {
        if (CanvasScrollViewer.Template.FindName("PART_ScrollContentPresenter", CanvasScrollViewer) is not ScrollContentPresenter presenter)
            return true;

        var position = presenter.TranslatePoint(new Point(), CanvasHost);
        var visible = Rect.Intersect(new Rect(position, presenter.RenderSize), new Rect(CanvasHost.RenderSize));
        if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0)
            return true;

        var dpi = VisualTreeHelper.GetDpi(CanvasHost);
        int left = (int)Math.Floor(visible.Left * dpi.DpiScaleX);
        int top = (int)Math.Floor(visible.Top * dpi.DpiScaleY);
        int width = (int)Math.Ceiling(visible.Right * dpi.DpiScaleX) - left;
        int height = (int)Math.Ceiling(visible.Bottom * dpi.DpiScaleY) - top;
        var viewport = new Int32Rect(left, top, width, height);
        bool full = _fullPreviewPending || viewport != _previewViewport || _displayScale != _previewZoom ||
            dpi.DpiScaleX != _previewDpi.DpiScaleX || dpi.DpiScaleY != _previewDpi.DpiScaleY;

        var bitmap = _previewBitmap;
        bool newSurface = bitmap is null || bitmap.PixelWidth != width || bitmap.PixelHeight != height;
        if (newSurface)
        {
            bitmap = new WriteableBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32, null);
            full = true;
        }

        float sx = (float)(_displayScale * dpi.DpiScaleX);
        float sy = (float)(_displayScale * dpi.DpiScaleY);
        var changed = new Int32Rect(0, 0, width, height);
        if (!full && _dirtyPreview is { } dirty)
        {
            int x0 = Math.Clamp((int)MathF.Floor(dirty.Left * sx) - left - 1, 0, width);
            int y0 = Math.Clamp((int)MathF.Floor(dirty.Top * sy) - top - 1, 0, height);
            int x1 = Math.Clamp((int)MathF.Ceiling(dirty.Right * sx) - left + 1, 0, width);
            int y1 = Math.Clamp((int)MathF.Ceiling(dirty.Bottom * sy) - top + 1, 0, height);
            if (x0 >= x1 || y0 >= y1)
                return true;
            changed = new Int32Rect(x0, y0, x1 - x0, y1 - y0);
        }

        if (!bitmap!.TryLock(new Duration(TimeSpan.Zero)))
            return false;

        try
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, bitmap.BackBuffer, bitmap.BackBufferStride);
            var canvas = surface.Canvas;
            var clip = new SKRect(changed.X, changed.Y, changed.X + changed.Width, changed.Y + changed.Height);
            canvas.ClipRect(clip);
            canvas.DrawColor(SKColors.Transparent, SKBlendMode.Src);
            canvas.Translate(-left, -top);
            canvas.Scale(sx, sy);
            var imageArea = new SKRect((clip.Left + left) / sx, (clip.Top + top) / sy,
                (clip.Right + left) / sx, (clip.Bottom + top) / sy);
            _previewRenderer.Draw(canvas, _baseBitmap, _items, _selectedItemId, _pendingCropRect, PreviewUiScale, imageArea);
            canvas.Flush();
            bitmap.AddDirtyRect(changed);
        }
        finally
        {
            bitmap.Unlock();
        }

        if (newSurface)
        {
            _previewBitmap = bitmap;
            PreviewImage.Source = bitmap;
        }
        if (full)
        {
            Canvas.SetLeft(PreviewImage, left / dpi.DpiScaleX);
            Canvas.SetTop(PreviewImage, top / dpi.DpiScaleY);
            PreviewImage.Width = width / dpi.DpiScaleX;
            PreviewImage.Height = height / dpi.DpiScaleY;
            _previewViewport = viewport;
            _previewDpi = dpi;
            _previewZoom = _displayScale;
        }
        return true;
    }

    private void ClosePreview()
    {
        CompositionTarget.Rendering -= OnPreviewRendering;
        PreviewImage.Source = null;
        _previewBitmap = null;
        _previewRenderer.Dispose();
    }
}
