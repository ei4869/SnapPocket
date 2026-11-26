using SkiaSharp;
using SnapPocket.Annotation.Models;

namespace SnapPocket.Annotation;

public enum ImageTransformKind
{
    RotateLeft90,
    RotateRight90,
    FlipHorizontal,
    FlipVertical
}

public static class ImageTransformHelper
{
    public static SKBitmap ApplyTransform(SKBitmap source, IList<AnnotationItem> items, ImageTransformKind kind)
    {
        int w = source.Width;
        int h = source.Height;

        int dstW = kind is ImageTransformKind.RotateLeft90 or ImageTransformKind.RotateRight90 ? h : w;
        int dstH = kind is ImageTransformKind.RotateLeft90 or ImageTransformKind.RotateRight90 ? w : h;

        var result = new SKBitmap(dstW, dstH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(result))
        using (var img = SKImage.FromBitmap(source))
        {
            switch (kind)
            {
                case ImageTransformKind.RotateRight90:
                    canvas.Translate(h, 0);
                    canvas.RotateDegrees(90);
                    break;
                case ImageTransformKind.RotateLeft90:
                    canvas.Translate(0, w);
                    canvas.RotateDegrees(-90);
                    break;
                case ImageTransformKind.FlipHorizontal:
                    canvas.Translate(w, 0);
                    canvas.Scale(-1, 1);
                    break;
                case ImageTransformKind.FlipVertical:
                    canvas.Translate(0, h);
                    canvas.Scale(1, -1);
                    break;
            }

            canvas.DrawImage(img, 0, 0, SKSamplingOptions.Default);
        }

        // Transform all vector annotation coordinates to match the transformed bitmap
        Func<SKPoint, SKPoint> mapPoint = kind switch
        {
            ImageTransformKind.RotateRight90 => p => new SKPoint(h - p.Y, p.X),
            ImageTransformKind.RotateLeft90 => p => new SKPoint(p.Y, w - p.X),
            ImageTransformKind.FlipHorizontal => p => new SKPoint(w - p.X, p.Y),
            ImageTransformKind.FlipVertical => p => new SKPoint(p.X, h - p.Y),
            _ => p => p
        };

        foreach (var item in items)
        {
            item.Start = mapPoint(item.Start);
            item.End = mapPoint(item.End);
            for (int i = 0; i < item.Points.Count; i++)
            {
                item.Points[i] = mapPoint(item.Points[i]);
            }
        }

        return result;
    }
}
