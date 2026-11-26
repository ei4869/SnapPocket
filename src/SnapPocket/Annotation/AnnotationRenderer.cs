using SkiaSharp;
using SnapPocket.Annotation.Models;

namespace SnapPocket.Annotation;

public static class AnnotationRenderer
{
    public static SKBitmap RenderFlattened(SKBitmap baseBitmap, IReadOnlyList<AnnotationItem> items)
    {
        var output = baseBitmap.Copy();
        using var canvas = new SKCanvas(output);
        DrawAllItems(canvas, output, items, selectedId: null, cropPreview: null, uiScale: 1f);
        return output;
    }

    public static void RenderInteractive(
        SKBitmap targetBuffer,
        SKBitmap baseBitmap,
        IReadOnlyList<AnnotationItem> items,
        Guid? selectedId,
        SKRect? cropPreview,
        float uiScale)
    {
        using var canvas = new SKCanvas(targetBuffer);
        using var baseImg = SKImage.FromBitmap(baseBitmap);
        using var basePaint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawImage(baseImg, 0, 0, SKSamplingOptions.Default, basePaint);
        DrawAllItems(canvas, targetBuffer, items, selectedId, cropPreview, uiScale);
    }

    private static void DrawAllItems(
        SKCanvas canvas,
        SKBitmap workingBitmap,
        IReadOnlyList<AnnotationItem> items,
        Guid? selectedId,
        SKRect? cropPreview,
        float uiScale)
    {
        foreach (var item in items)
            if (IsPrivacyEffect(item.Tool))
                DrawPrivacyEffect(canvas, workingBitmap, item.Tool, item.GetBounds(), item.Style);

        DrawForeground(canvas, workingBitmap.Width, workingBitmap.Height, items, selectedId, cropPreview, uiScale);
    }

    internal static bool IsPrivacyEffect(AnnotationTool tool) => tool is AnnotationTool.Mosaic or AnnotationTool.Blur;

    internal static void DrawPrivacyEffect(SKCanvas canvas, SKBitmap bitmap, AnnotationTool tool, SKRect rect, AnnotationStyle style)
    {
        if (tool == AnnotationTool.Mosaic)
            DrawMosaic(canvas, bitmap, rect, style);
        else
            DrawBlur(canvas, bitmap, rect, style);
    }

    internal static void DrawForeground(SKCanvas canvas, int width, int height, IReadOnlyList<AnnotationItem> items,
        Guid? selectedId, SKRect? cropPreview, float uiScale, SKRect? visible = null)
    {
        foreach (var item in items)
        {
            if (IsPrivacyEffect(item.Tool) || (visible is { } clip && !InputBounds(item, uiScale).IntersectsWith(clip)))
                continue;
            DrawVectorItem(canvas, item);
        }

        if (selectedId.HasValue)
        {
            var selected = items.FirstOrDefault(i => i.Id == selectedId.Value);
            if (selected is not null)
            {
                DrawSelectionAdorner(canvas, selected, uiScale);
            }
        }

        if (cropPreview is { Width: > 1, Height: > 1 } crop)
        {
            using var dimPaint = new SKPaint { Color = new SKColor(0, 0, 0, 140), Style = SKPaintStyle.Fill };
            using var pb = new SKPathBuilder();
            pb.FillType = SKPathFillType.EvenOdd;
            pb.AddRect(new SKRect(0, 0, width, height));
            pb.AddRect(crop);
            using var path = pb.Detach();
            canvas.DrawPath(path, dimPaint);

            using var borderPaint = new SKPaint
            {
                Color = new SKColor(76, 154, 255, 255),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(2f, 2f * uiScale),
                IsAntialias = true
            };
            canvas.DrawRect(crop, borderPaint);
        }
    }

    internal static SKRect InputBounds(AnnotationItem item, float uiScale)
    {
        var bounds = item.GetBounds();
        float margin = item.Tool switch
        {
            AnnotationTool.Arrow => MathF.Max(13f, item.Style.StrokeWidth * 4.5f) * item.Style.ArrowScale + item.Style.StrokeWidth,
            AnnotationTool.Text => item.Style.FontSize,
            _ => item.Style.StrokeWidth
        };
        float padding = MathF.Max(margin + 2f, 10f * MathF.Max(uiScale, 1f));
        bounds.Inflate(padding, padding);
        return bounds;
    }

    private static void DrawMosaic(SKCanvas canvas, SKBitmap source, SKRect rect, AnnotationStyle style)
    {
        int left = Math.Clamp((int)Math.Floor(rect.Left), 0, source.Width);
        int top = Math.Clamp((int)Math.Floor(rect.Top), 0, source.Height);
        int right = Math.Clamp((int)Math.Ceiling(rect.Right), 0, source.Width);
        int bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom), 0, source.Height);
        if (right - left < 2 || bottom - top < 2) return;

        int blockSize = style.EffectiveBlockSize;
        canvas.Flush();
        using var restore = new SKAutoCanvasRestore(canvas);
        ClipEffectMask(canvas, rect, style.OvalMask);
        using var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false, BlendMode = SKBlendMode.Src };

        for (int y = top; y < bottom; y += blockSize)
        {
            int bh = Math.Min(blockSize, bottom - y);
            for (int x = left; x < right; x += blockSize)
            {
                int bw = Math.Min(blockSize, right - x);
                int sampleX = Math.Clamp(x + bw / 2, 0, source.Width - 1);
                int sampleY = Math.Clamp(y + bh / 2, 0, source.Height - 1);
                paint.Color = source.GetPixel(sampleX, sampleY);
                canvas.DrawRect(x, y, bw, bh, paint);
            }
        }
    }

    private static void DrawBlur(SKCanvas canvas, SKBitmap source, SKRect mask, AnnotationStyle style)
    {
        var bounds = new SKRect(0, 0, source.Width, source.Height);
        var rect = SKRect.Intersect(mask, bounds);
        if (rect.Width < 2 || rect.Height < 2) return;

        float sigma = style.EffectiveBlurStrength;
        int padding = (int)Math.Ceiling(3f * sigma);
        var sampleBounds = new SKRectI(
            Math.Max(0, (int)Math.Floor(rect.Left) - padding),
            Math.Max(0, (int)Math.Floor(rect.Top) - padding),
            Math.Min(source.Width, (int)Math.Ceiling(rect.Right) + padding),
            Math.Min(source.Height, (int)Math.Ceiling(rect.Bottom) + padding));
        canvas.Flush();
        using var subset = new SKBitmap();
        if (!source.ExtractSubset(subset, sampleBounds))
            return;
        using var pixels = subset.Copy();
        using var srcImg = SKImage.FromBitmap(pixels);

        using var restore = new SKAutoCanvasRestore(canvas);
        ClipEffectMask(canvas, mask, style.OvalMask);
        using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { ImageFilter = filter, BlendMode = SKBlendMode.Src };
        canvas.DrawImage(srcImg, sampleBounds.Left, sampleBounds.Top, SKSamplingOptions.Default, paint);
    }

    private static void ClipEffectMask(SKCanvas canvas, SKRect rect, bool oval)
    {
        if (oval)
        {
            using var builder = new SKPathBuilder();
            builder.AddOval(rect);
            using var path = builder.Detach();
            canvas.ClipPath(path, SKClipOperation.Intersect, antialias: true);
        }
        else
        {
            canvas.ClipRect(rect);
        }
    }

    private static void DrawVectorItem(SKCanvas canvas, AnnotationItem item)
    {
        switch (item.Tool)
        {
            case AnnotationTool.Rectangle:
            case AnnotationTool.Ellipse:
                DrawShape(canvas, item);
                break;

            case AnnotationTool.Line:
                using (var paint = CreateStyledStroke(item.Style))
                    canvas.DrawLine(item.Start, item.End, paint);
                break;

            case AnnotationTool.Arrow:
                DrawArrow(canvas, item);
                break;

            case AnnotationTool.Pen:
            case AnnotationTool.Highlighter:
                DrawFreehand(canvas, item);
                break;

            case AnnotationTool.Text:
                DrawText(canvas, item);
                break;

            case AnnotationTool.StepBadge:
                DrawStepBadge(canvas, item);
                break;
        }
    }

    private static void DrawShape(SKCanvas canvas, AnnotationItem item)
    {
        var style = item.Style;
        var rect = AnnotationItem.NormalizeRect(item.Start, item.End);
        float radius = style.ShapeMode == ShapeMode.SolidCover
            ? 0 : Math.Clamp(style.CornerRadius, 0, Math.Min(rect.Width, rect.Height) / 2f);

        void Draw(SKPaint paint)
        {
            if (item.Tool == AnnotationTool.Ellipse)
                canvas.DrawOval(rect, paint);
            else if (radius > 0)
                canvas.DrawRoundRect(rect, radius, radius, paint);
            else
                canvas.DrawRect(rect, paint);
        }

        if (style.ShapeMode != ShapeMode.Outline)
        {
            using var fill = new SKPaint
            {
                Color = style.ShapeMode == ShapeMode.SolidCover ? style.CoverColor.WithAlpha(255)
                    : AnnotationStyle.WithOpacity(style.FillColor, style.FillOpacity),
                IsAntialias = true
            };
            Draw(fill);
        }
        if (style.HasOutline)
        {
            using var stroke = CreateStyledStroke(style);
            Draw(stroke);
        }
    }

    private static void DrawArrow(SKCanvas canvas, AnnotationItem item)
    {
        var style = item.Style;
        var start = item.Start;
        var end = item.End;
        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 2f) return;

        float angle = MathF.Atan2(dy, dx);
        float headLength = Math.Min(Math.Max(13f, style.StrokeWidth * 4.5f) * style.ArrowScale, length * 0.45f);
        bool startHead = style.ArrowPlacement != ArrowPlacement.End;
        bool endHead = style.ArrowPlacement != ArrowPlacement.Start;
        float trim = style.ArrowTip == ArrowTip.Filled ? headLength * 0.72f : 0;
        float trimX = dx / length * trim;
        float trimY = dy / length * trim;
        var shaftStart = startHead ? new SKPoint(start.X + trimX, start.Y + trimY) : start;
        var shaftEnd = endHead ? new SKPoint(end.X - trimX, end.Y - trimY) : end;

        using var shaftPaint = CreateStyledStroke(style);
        canvas.DrawLine(shaftStart, shaftEnd, shaftPaint);
        using var headPaint = CreateStrokePaint(style.Color, style.StrokeWidth);
        if (style.ArrowTip == ArrowTip.Filled)
            headPaint.Style = SKPaintStyle.Fill;
        if (endHead)
            DrawArrowHead(canvas, end, angle, headLength, headPaint);
        if (startHead)
            DrawArrowHead(canvas, start, angle + MathF.PI, headLength, headPaint);
    }

    private static void DrawArrowHead(SKCanvas canvas, SKPoint tip, float angle, float length, SKPaint paint)
    {
        const float spread = 0.46f;
        using var builder = new SKPathBuilder();
        builder.MoveTo(tip.X - length * MathF.Cos(angle - spread), tip.Y - length * MathF.Sin(angle - spread));
        builder.LineTo(tip);
        builder.LineTo(tip.X - length * MathF.Cos(angle + spread), tip.Y - length * MathF.Sin(angle + spread));
        if (paint.Style == SKPaintStyle.Fill)
            builder.Close();
        using var path = builder.Detach();
        canvas.DrawPath(path, paint);
    }

    private static void DrawFreehand(SKCanvas canvas, AnnotationItem item)
    {
        var points = item.Points;
        if (points.Count == 0) return;

        var style = item.Style;
        bool marker = item.Tool == AnnotationTool.Highlighter;
        var color = style.Opacity is int opacity
            ? AnnotationStyle.WithOpacity(style.Color, opacity)
            : marker ? style.Color.WithAlpha(105) : style.Color;
        float width = marker ? style.EffectiveMarkerWidth : style.StrokeWidth;
        using var paint = CreateStrokePaint(color, width);
        if (style.FlatEnds)
            paint.StrokeCap = SKStrokeCap.Butt;

        if (points.Count == 1)
        {
            paint.Style = SKPaintStyle.Fill;
            float radius = width / 2f;
            var point = points[0];
            if (style.FlatEnds)
                canvas.DrawRect(point.X - radius, point.Y - radius, width, width, paint);
            else
                canvas.DrawCircle(point, radius, paint);
            return;
        }

        using var builder = new SKPathBuilder();
        builder.MoveTo(points[0]);
        for (int i = 1; i < points.Count - 1; i++)
        {
            if (style.SmoothPen)
            {
                var mid = new SKPoint((points[i].X + points[i + 1].X) / 2f, (points[i].Y + points[i + 1].Y) / 2f);
                builder.QuadTo(points[i], mid);
            }
            else
            {
                builder.LineTo(points[i]);
            }
        }
        builder.LineTo(points[^1]);
        using var path = builder.Detach();
        canvas.DrawPath(path, paint);
    }

    private static void DrawText(SKCanvas canvas, AnnotationItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Text)) return;

        var style = item.Style;
        var lines = item.Text.Replace("\r\n", "\n").Split('\n');
        using var font = style.CreateTextFont();
        using var textPaint = new SKPaint { Color = style.Color, IsAntialias = true };
        using var backdropPaint = new SKPaint { Color = style.TextBackdropColor, IsAntialias = true };
        using var underlinePaint = CreateStrokePaint(style.Color, Math.Max(1f, style.FontSize * 0.06f));
        if (style.TextBackdrop == TextBackdrop.Background)
            canvas.DrawRoundRect(item.GetBounds(), 4, 4, backdropPaint);
        else if (style.TextBackdrop == TextBackdrop.Outline)
        {
            backdropPaint.Style = SKPaintStyle.Stroke;
            backdropPaint.StrokeWidth = Math.Max(2, style.FontSize * 0.14f);
            backdropPaint.StrokeJoin = SKStrokeJoin.Round;
        }
        else if (style.TextBackdrop == TextBackdrop.Shadow)
        {
            backdropPaint.Color = new SKColor(0, 0, 0, 140);
        }

        float lineHeight = style.FontSize * 1.32f;
        float baseline = item.Start.Y + style.FontSize;

        foreach (var line in lines)
        {
            if (style.TextBackdrop == TextBackdrop.Shadow)
                canvas.DrawText(line, item.Start.X + 1.5f, baseline + 1.5f, SKTextAlign.Left, font, backdropPaint);
            else if (style.TextBackdrop == TextBackdrop.Outline)
                canvas.DrawText(line, item.Start.X, baseline, SKTextAlign.Left, font, backdropPaint);

            canvas.DrawText(line, item.Start.X, baseline, SKTextAlign.Left, font, textPaint);
            if (style.Underline && line.Length > 0)
            {
                float y = baseline + Math.Max(2f, style.FontSize * 0.08f);
                canvas.DrawLine(item.Start.X, y, item.Start.X + font.MeasureText(line), y, underlinePaint);
            }
            baseline += lineHeight;
        }
    }

    private static void DrawStepBadge(SKCanvas canvas, AnnotationItem item)
    {
        var style = item.Style;
        float radius = style.BadgeRadius;

        void Draw(SKPoint center, SKPaint paint)
        {
            if (style.SquareBadge)
            {
                var rect = new SKRect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);
                canvas.DrawRoundRect(rect, radius * 0.25f, radius * 0.25f, paint);
            }
            else
            {
                canvas.DrawCircle(center, radius, paint);
            }
        }

        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 95), IsAntialias = true };
        using var fill = new SKPaint { Color = style.Color, IsAntialias = true };
        using var ring = CreateStrokePaint(SKColors.White, 2f);
        Draw(new SKPoint(item.Start.X + 1.5f, item.Start.Y + 2f), shadow);
        Draw(item.Start, fill);
        Draw(item.Start, ring);

        string label = item.StepNumber.ToString();
        using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
        using var font = new SKFont(typeface, radius * 1.15f);
        using var textPaint = new SKPaint { Color = style.BadgeTextColor, IsAntialias = true };
        float labelWidth = font.MeasureText(label);
        if (labelWidth > radius * 1.6f)
            font.Size *= radius * 1.6f / labelWidth;

        var metrics = font.Metrics;
        float baseline = item.Start.Y - (metrics.Ascent + metrics.Descent) / 2f;
        canvas.DrawText(label, item.Start.X, baseline, SKTextAlign.Center, font, textPaint);
    }

    private static SKPaint CreateStyledStroke(AnnotationStyle style)
    {
        var paint = CreateStrokePaint(style.Color, style.StrokeWidth);
        if (style.Pattern == StrokePattern.Dashed || (style.Pattern == StrokePattern.Solid && style.FlatEnds))
            paint.StrokeCap = SKStrokeCap.Butt;

        if (style.Pattern != StrokePattern.Solid)
        {
            float width = style.StrokeWidth;
            float[] intervals = style.Pattern == StrokePattern.Dashed
                ? [Math.Max(6f, width * 3.4f), Math.Max(4f, width * 2f)]
                : [Math.Max(0.1f, width * 0.16f), Math.Max(4f, width * 2.15f)];
            using var effect = SKPathEffect.CreateDash(intervals, 0);
            paint.PathEffect = effect;
        }
        return paint;
    }

    private static void DrawSelectionAdorner(SKCanvas canvas, AnnotationItem item, float uiScale)
    {
        float s = Math.Max(1f, uiScale);
        if (item.Tool is AnnotationTool.Line or AnnotationTool.Arrow)
        {
            DrawHandleDot(canvas, item.Start, 5.5f * s);
            DrawHandleDot(canvas, item.End, 5.5f * s);
            return;
        }

        using var effect = SKPathEffect.CreateDash([6f * s, 4f * s], 0);
        using var dashPaint = new SKPaint
        {
            Color = new SKColor(76, 154, 255, 240),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f * s,
            IsAntialias = true,
            PathEffect = effect
        };
        var bounds = item.GetBounds();
        bounds.Inflate(3f * s, 3f * s);
        canvas.DrawRect(bounds, dashPaint);

        if (item.Tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Mosaic or AnnotationTool.Blur)
        {
            var r = AnnotationItem.NormalizeRect(item.Start, item.End);
            DrawHandleDot(canvas, new SKPoint(r.Left, r.Top), 5.5f * s);
            DrawHandleDot(canvas, new SKPoint(r.Right, r.Top), 5.5f * s);
            DrawHandleDot(canvas, new SKPoint(r.Left, r.Bottom), 5.5f * s);
            DrawHandleDot(canvas, new SKPoint(r.Right, r.Bottom), 5.5f * s);
        }
    }

    private static void DrawHandleDot(SKCanvas canvas, SKPoint center, float radius)
    {
        using var fill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var stroke = new SKPaint { Color = new SKColor(76, 154, 255, 255), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.5f, radius * 0.35f), IsAntialias = true };
        canvas.DrawCircle(center, radius, fill);
        canvas.DrawCircle(center, radius, stroke);
    }

    private static SKPaint CreateStrokePaint(SKColor color, float width) => new()
    {
        Color = color,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = width,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
        IsAntialias = true
    };
}

