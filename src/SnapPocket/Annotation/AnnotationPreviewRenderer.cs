using SkiaSharp;
using SnapPocket.Annotation.Models;

namespace SnapPocket.Annotation;

internal sealed class AnnotationPreviewRenderer : IDisposable
{
    private readonly record struct Mask(AnnotationTool Tool, SKRect Bounds, AnnotationStyle Style);

    private List<Mask> _appliedMasks = [];
    private List<Mask> _masks = [];
    private SKBitmap? _source;
    private SKBitmap? _privacyLayer;
    private SKRectI _privacyBounds;

    public void Draw(SKCanvas canvas, SKBitmap source, IReadOnlyList<AnnotationItem> items,
        Guid? selectedId, SKRect? crop, float uiScale, SKRect visible)
    {
        UpdatePrivacyLayer(source, items);

        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear);
        canvas.DrawBitmap(source, 0, 0, sampling, paint);
        if (_privacyLayer is not null && ((SKRect)_privacyBounds).IntersectsWith(visible))
            canvas.DrawBitmap(_privacyLayer, _privacyBounds.Left, _privacyBounds.Top, sampling, paint);

        AnnotationRenderer.DrawForeground(canvas, source.Width, source.Height, items, selectedId, crop, uiScale, visible);
    }

    private void UpdatePrivacyLayer(SKBitmap source, IReadOnlyList<AnnotationItem> items)
    {
        _masks.Clear();
        var imageBounds = new SKRect(0, 0, source.Width, source.Height);
        var region = SKRect.Empty;
        foreach (var item in items)
        {
            if (!AnnotationRenderer.IsPrivacyEffect(item.Tool))
                continue;
            var mask = item.GetBounds();
            var clipped = SKRect.Intersect(mask, imageBounds);
            if (clipped.IsEmpty)
                continue;

            _masks.Add(new Mask(item.Tool, mask, item.Style));
            float margin = 2f + (item.Tool == AnnotationTool.Blur ? MathF.Ceiling(3f * item.Style.EffectiveBlurStrength) : 0f);
            clipped.Inflate(margin, margin);
            region = region.IsEmpty ? clipped : SKRect.Union(region, clipped);
        }

        if (ReferenceEquals(_source, source) && _appliedMasks.SequenceEqual(_masks))
            return;

        _privacyLayer?.Dispose();
        _privacyLayer = null;
        _source = source;
        (_masks, _appliedMasks) = (_appliedMasks, _masks);
        if (_appliedMasks.Count == 0)
            return;

        _privacyBounds = SKRectI.Ceiling(SKRect.Intersect(region, imageBounds));
        // Round the left and top outwards as well, so the bitmap stays aligned to the original pixels.
        _privacyBounds.Left = (int)MathF.Floor(Math.Max(0, region.Left));
        _privacyBounds.Top = (int)MathF.Floor(Math.Max(0, region.Top));
        using var subset = new SKBitmap();
        if (!source.ExtractSubset(subset, _privacyBounds))
            throw new InvalidOperationException("无法读取批注遮挡区域。");
        _privacyLayer = subset.Copy();
        using (var canvas = new SKCanvas(_privacyLayer))
        {
            foreach (var mask in _appliedMasks)
            {
                var rect = mask.Bounds;
                rect.Offset(-_privacyBounds.Left, -_privacyBounds.Top);
                AnnotationRenderer.DrawPrivacyEffect(canvas, _privacyLayer, mask.Tool, rect, mask.Style);
            }
        }
        _privacyLayer.SetImmutable();
    }

    public void Dispose() => _privacyLayer?.Dispose();
}
