using SkiaSharp;
using SnapPocket.Annotation;
using SnapPocket.Annotation.Commands;
using SnapPocket.Annotation.Models;

namespace SnapPocket.Tests;

public class AnnotationStyleTests
{
    [Fact]
    public void ShapesAndLinePatternsApplyToPixelsAndSelection()
    {
        using var source = WhiteImage();
        var rectangle = new AnnotationItem
        {
            Tool = AnnotationTool.Rectangle, Start = new SKPoint(12, 12), End = new SKPoint(72, 62),
            Style = new AnnotationStyle { ShapeMode = ShapeMode.Fill, FillColor = SKColors.CornflowerBlue, FillOpacity = 100, CornerRadius = 18 }
        };
        var ellipse = new AnnotationItem
        {
            Tool = AnnotationTool.Ellipse, Start = new SKPoint(100, 12), End = new SKPoint(156, 62),
            Style = new AnnotationStyle { ShapeMode = ShapeMode.Fill, FillColor = SKColors.Green, FillOpacity = 100 }
        };
        var line = new AnnotationItem
        {
            Tool = AnnotationTool.Line, Start = new SKPoint(20, 80), End = new SKPoint(160, 80),
            Style = new AnnotationStyle { StrokeWidth = 4, Color = SKColors.Black, Pattern = StrokePattern.Dashed }
        };
        using var result = AnnotationRenderer.RenderFlattened(source, [rectangle, ellipse, line]);

        Assert.Equal(SKColors.CornflowerBlue, result.GetPixel(40, 36));
        Assert.Equal(SKColors.White, result.GetPixel(13, 13));
        Assert.Equal(SKColors.Green, result.GetPixel(128, 37));
        Assert.Equal(SKColors.White, result.GetPixel(103, 14));
        Assert.Equal(SKColors.Black, result.GetPixel(26, 80));
        Assert.Equal(SKColors.White, result.GetPixel(37, 80));
        Assert.True(rectangle.HitTestBody(new SKPoint(40, 36), 3));
        Assert.True(ellipse.HitTestBody(new SKPoint(128, 37), 3));
    }

    [Fact]
    public void RenderUsesIndependentMarkerArrowTextAndBadgeOptions()
    {
        using var source = WhiteImage();
        var marker = new AnnotationItem
        {
            Tool = AnnotationTool.Highlighter, Points = [new SKPoint(20, 20), new SKPoint(70, 20)],
            Style = new AnnotationStyle { Color = SKColors.Red, MarkerWidth = 16, Opacity = 50, FlatEnds = true }
        };
        var arrow = new AnnotationItem
        {
            Tool = AnnotationTool.Arrow, Start = new SKPoint(20, 50), End = new SKPoint(100, 50),
            Style = new AnnotationStyle { Color = SKColors.Blue, StrokeWidth = 4, ArrowPlacement = ArrowPlacement.Both, ArrowTip = ArrowTip.Open, ArrowScale = 1.5f }
        };
        var text = new AnnotationItem
        {
            Tool = AnnotationTool.Text, Start = new SKPoint(24, 81), Text = "批注 AB",
            Style = new AnnotationStyle { Color = SKColors.White, TextBackdrop = TextBackdrop.Background, TextBackdropColor = SKColors.Purple, Italic = true, Underline = true }
        };
        var badge = new AnnotationItem
        {
            Tool = AnnotationTool.StepBadge, Start = new SKPoint(150, 92), StepNumber = 100,
            Style = new AnnotationStyle { Color = SKColors.Green, BadgeDiameter = 42, SquareBadge = true, BadgeTextColor = SKColors.Yellow }
        };
        AnnotationItem[] items = [marker, arrow, text, badge];
        using var result = AnnotationRenderer.RenderFlattened(source, items);
        using var preview = WhiteImage();
        preview.Erase(SKColors.Magenta);
        AnnotationRenderer.RenderInteractive(preview, source, items, null, null, 1f);

        var ink = result.GetPixel(40, 20);
        Assert.Equal(255, ink.Red);
        Assert.InRange(ink.Green, 126, 129);
        Assert.Equal(SKColors.White, result.GetPixel(16, 20));
        Assert.Equal(SKColors.White, result.GetPixel(40, 10));
        Assert.Equal(SKColors.Blue, result.GetPixel(40, 40));
        Assert.Equal(SKColors.Blue, result.GetPixel(80, 40));
        Assert.Equal(SKColors.Purple, result.GetPixel(22, 89));
        Assert.Equal(SKColors.Green, result.GetPixel(133, 76));
        Assert.Contains(SKColors.Yellow, result.Pixels);
        Assert.Equal(result.Bytes, preview.Bytes);
    }

    [Fact]
    public void EffectStrengthAndMaskSurviveUndoAndChangeTheOutput()
    {
        using var source = WhiteImage();
        using (var canvas = new SKCanvas(source))
        using (var paint = new SKPaint { Color = SKColors.Black })
        {
            canvas.DrawRect(0, 0, 60, 128, paint);
            canvas.DrawRect(117, 0, 3, 128, paint);
        }
        var mosaic = new AnnotationItem
        {
            Tool = AnnotationTool.Mosaic, Start = new SKPoint(90, 16), End = new SKPoint(170, 96),
            Style = new AnnotationStyle { MosaicBlockSize = 20, OvalMask = true }
        };
        var blur = new AnnotationItem
        {
            Tool = AnnotationTool.Blur, Start = new SKPoint(18, 16), End = new SKPoint(100, 96),
            Style = new AnnotationStyle { BlurStrength = 20, OvalMask = true }
        };
        var items = new[] { mosaic, blur };
        using var history = new UndoRedoStack();
        history.PushUndo(source, items, 1);
        mosaic.Style = mosaic.Style with { MosaicBlockSize = 8, OvalMask = false };
        blur.Style = blur.Style with { BlurStrength = 2 };
        using var undo = history.Undo(source, items, 1);
        Assert.NotNull(undo);
        Assert.Equal(20, undo.Items[0].Style.MosaicBlockSize);
        Assert.True(undo.Items[0].Style.OvalMask);
        Assert.Equal(20f, undo.Items[1].Style.BlurStrength);

        using var restored = AnnotationRenderer.RenderFlattened(source, undo.Items);
        using var changed = AnnotationRenderer.RenderFlattened(source, items);
        Assert.NotEqual(restored.GetPixel(116, 18), changed.GetPixel(116, 18));
        Assert.NotEqual(restored.GetPixel(50, 60), changed.GetPixel(50, 60));
        Assert.Equal(source.GetPixel(18, 16), restored.GetPixel(18, 16));
    }

    [Fact]
    public void ViewportRepaintPreservesOverlappingEffectsAndErasesTheOldArrow()
    {
        using var source = WhiteImage();
        using (var background = new SKCanvas(source))
        using (var paint = new SKPaint())
        {
            for (int x = 0; x < source.Width; x += 6)
            {
                paint.Color = x % 12 == 0 ? SKColors.CadetBlue : SKColors.DarkOrange;
                background.DrawRect(x, 0, 3, source.Height, paint);
            }
        }
        source.SetImmutable();
        var arrow = new AnnotationItem
        {
            Tool = AnnotationTool.Arrow, Start = new(47, 26), End = new(105, 40),
            Style = new AnnotationStyle { StrokeWidth = 4, ArrowPlacement = ArrowPlacement.Both }
        };
        var mosaic = new AnnotationItem
        {
            Tool = AnnotationTool.Mosaic, Start = new(48.5f, 21.5f), End = new(135, 92.5f),
            Style = new AnnotationStyle { MosaicBlockSize = 9, OvalMask = true }
        };
        var blur = new AnnotationItem
        {
            Tool = AnnotationTool.Blur, Start = new(65.5f, 35.5f), End = new(143, 98),
            Style = new AnnotationStyle { BlurStrength = 3, OvalMask = true }
        };
        AnnotationItem[] items = [arrow, mosaic, blur];
        var area = new SKRectI(32, 18, 147, 102);
        using var target = new SKBitmap(area.Width, area.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var renderer = new AnnotationPreviewRenderer();

        void Repaint(SKRect dirty)
        {
            using var canvas = new SKCanvas(target);
            canvas.Translate(-area.Left, -area.Top);
            canvas.ClipRect(dirty);
            renderer.Draw(canvas, source, items, arrow.Id, null, 1, dirty);
        }

        void AssertMatchesFullRender()
        {
            using var expected = new SKBitmap(source.Info);
            AnnotationRenderer.RenderInteractive(expected, source, items, arrow.Id, null, 1);
            using var subset = new SKBitmap();
            Assert.True(expected.ExtractSubset(subset, area));
            using var copied = subset.Copy();
            var expectedPixels = copied.Bytes;
            var actualPixels = target.Bytes;
            int maxChannelDifference = expectedPixels.Zip(actualPixels, (a, b) => Math.Abs(a - b)).Max();
            // We allow subpixel rounding at an antialiased oval edge after translating the mask.
            Assert.InRange(maxChannelDifference, 0, 5);
        }

        Repaint(area);
        AssertMatchesFullRender();
        var oldArea = AnnotationRenderer.InputBounds(arrow, 1);
        arrow.Translate(17, 35);
        Repaint(SKRect.Intersect(area, SKRect.Union(oldArea, AnnotationRenderer.InputBounds(arrow, 1))));
        AssertMatchesFullRender();
        var repainted = target.Bytes;
        Repaint(area);
        Assert.Equal(repainted, target.Bytes);
        blur.Style = blur.Style with { BlurStrength = 8 };
        Repaint(area);
        AssertMatchesFullRender();
    }

    private static SKBitmap WhiteImage()
    {
        var bitmap = new SKBitmap(180, 128, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.White);
        return bitmap;
    }
}
