using SkiaSharp;

namespace SnapPocket.Annotation.Models;

public enum AnnotationTool
{
    Select,
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Pen,
    Highlighter,
    Text,
    Mosaic,
    Blur,
    StepBadge,
    Crop
}

public enum ResizeHandle
{
    None,
    Body,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    StartPoint,
    EndPoint
}

public sealed class AnnotationItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public AnnotationTool Tool { get; set; }
    public AnnotationStyle Style { get; set; } = new();
    public SKColor Color { get => Style.Color; set => Style = Style with { Color = value }; }
    public float StrokeWidth { get => Style.StrokeWidth; set => Style = Style with { StrokeWidth = value }; }
    public float FontSize { get => Style.FontSize; set => Style = Style with { FontSize = value }; }
    public SKPoint Start { get; set; }
    public SKPoint End { get; set; }
    public List<SKPoint> Points { get; set; } = [];
    public string Text { get; set; } = string.Empty;
    public int StepNumber { get; set; } = 1;

    public AnnotationItem Clone()
    {
        return new AnnotationItem
        {
            Id = Id,
            Tool = Tool,
            Style = Style,
            Start = Start,
            End = End,
            Points = [.. Points],
            Text = Text,
            StepNumber = StepNumber
        };
    }

    public SKRect GetBounds()
    {
        switch (Tool)
        {
            case AnnotationTool.Pen:
            case AnnotationTool.Highlighter:
                if (Points.Count == 0) return SKRect.Empty;
                float minX = Points[0].X, maxX = Points[0].X;
                float minY = Points[0].Y, maxY = Points[0].Y;
                foreach (var p in Points)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                }
                float pad = Math.Max(6f, FreehandWidth / 2f);
                return new SKRect(minX - pad, minY - pad, maxX + pad, maxY + pad);

            case AnnotationTool.StepBadge:
                float r = Style.BadgeRadius;
                return new SKRect(Start.X - r, Start.Y - r, Start.X + r, Start.Y + r);

            case AnnotationTool.Text:
                var lines = (Text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
                float maxLineW = 40f;
                using (var font = Style.CreateTextFont())
                {
                    foreach (var line in lines)
                    {
                        float w = font.MeasureText(string.IsNullOrEmpty(line) ? " " : line);
                        if (w > maxLineW) maxLineW = w;
                    }
                }
                float totalH = Math.Max(FontSize * 1.35f, lines.Length * FontSize * 1.35f);
                return new SKRect(Start.X - 4, Start.Y - 4, Start.X + maxLineW + 4, Start.Y + totalH + 4);

            default:
                return NormalizeRect(Start, End);
        }
    }

    public void Translate(float dx, float dy)
    {
        Start = new SKPoint(Start.X + dx, Start.Y + dy);
        End = new SKPoint(End.X + dx, End.Y + dy);
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new SKPoint(Points[i].X + dx, Points[i].Y + dy);
        }
    }

    public void ApplyResize(ResizeHandle handle, SKPoint target)
    {
        if (Tool is AnnotationTool.Line or AnnotationTool.Arrow)
        {
            if (handle == ResizeHandle.StartPoint) Start = target;
            else if (handle == ResizeHandle.EndPoint) End = target;
            return;
        }

        if (Tool is AnnotationTool.Pen or AnnotationTool.Highlighter or AnnotationTool.StepBadge or AnnotationTool.Text)
            return;

        var rect = NormalizeRect(Start, End);
        float left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom;

        switch (handle)
        {
            case ResizeHandle.TopLeft:
                left = target.X;
                top = target.Y;
                break;
            case ResizeHandle.TopRight:
                right = target.X;
                top = target.Y;
                break;
            case ResizeHandle.BottomLeft:
                left = target.X;
                bottom = target.Y;
                break;
            case ResizeHandle.BottomRight:
                right = target.X;
                bottom = target.Y;
                break;
        }

        Start = new SKPoint(left, top);
        End = new SKPoint(right, bottom);
    }

    public ResizeHandle HitTestHandles(SKPoint pt, float handleRadius)
    {
        if (Tool is AnnotationTool.Line or AnnotationTool.Arrow)
        {
            if (Distance(pt, Start) <= handleRadius) return ResizeHandle.StartPoint;
            if (Distance(pt, End) <= handleRadius) return ResizeHandle.EndPoint;
            return ResizeHandle.None;
        }

        if (Tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Mosaic or AnnotationTool.Blur)
        {
            var r = NormalizeRect(Start, End);
            if (Distance(pt, new SKPoint(r.Left, r.Top)) <= handleRadius) return ResizeHandle.TopLeft;
            if (Distance(pt, new SKPoint(r.Right, r.Top)) <= handleRadius) return ResizeHandle.TopRight;
            if (Distance(pt, new SKPoint(r.Left, r.Bottom)) <= handleRadius) return ResizeHandle.BottomLeft;
            if (Distance(pt, new SKPoint(r.Right, r.Bottom)) <= handleRadius) return ResizeHandle.BottomRight;
        }

        return ResizeHandle.None;
    }

    public bool HitTestBody(SKPoint pt, float tolerance)
    {
        switch (Tool)
        {
            case AnnotationTool.Line:
            case AnnotationTool.Arrow:
                return DistanceToSegment(pt, Start, End) <= Math.Max(tolerance, StrokeWidth + 5f);

            case AnnotationTool.Pen:
            case AnnotationTool.Highlighter:
                float range = FreehandWidth / 2f + tolerance;
                if (Points.Count == 1)
                    return Distance(pt, Points[0]) <= range;
                for (int i = 1; i < Points.Count; i++)
                {
                    if (DistanceToSegment(pt, Points[i - 1], Points[i]) <= range)
                        return true;
                }
                return false;

            case AnnotationTool.StepBadge:
                float r = Style.BadgeRadius;
                if (Style.SquareBadge)
                    return Math.Max(Math.Abs(pt.X - Start.X), Math.Abs(pt.Y - Start.Y)) <= r + tolerance * 0.5f;
                return Distance(pt, Start) <= r + tolerance * 0.5f;

            case AnnotationTool.Text:
            case AnnotationTool.Mosaic:
            case AnnotationTool.Blur:
                if (Style.OvalMask && Tool != AnnotationTool.Text)
                    return HitTestEllipse(GetBounds(), pt, tolerance, filled: true);

                var b = GetBounds();
                b.Inflate(tolerance * 0.5f, tolerance * 0.5f);
                return b.Contains(pt);

            case AnnotationTool.Rectangle:
                var rect = NormalizeRect(Start, End);
                var outer = rect;
                float rectTolerance = tolerance + (Style.HasOutline ? StrokeWidth / 2f : 0);
                outer.Inflate(rectTolerance, rectTolerance);
                if (Style.ShapeMode != ShapeMode.Outline)
                    return outer.Contains(pt);

                var inner = rect;
                inner.Inflate(-rectTolerance, -rectTolerance);
                return outer.Contains(pt) && (inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(pt));

            case AnnotationTool.Ellipse:
                return HitTestEllipse(NormalizeRect(Start, End), pt,
                    tolerance + (Style.HasOutline ? StrokeWidth / 2f : 0),
                    filled: Style.ShapeMode != ShapeMode.Outline);

            default:
                return false;
        }
    }

    private float FreehandWidth => Tool == AnnotationTool.Highlighter ? Style.EffectiveMarkerWidth : StrokeWidth;

    private static bool HitTestEllipse(SKRect rect, SKPoint pt, float tolerance, bool filled)
    {
        if (rect.Width < 2 || rect.Height < 2) return false;
        float rx = rect.Width / 2f;
        float ry = rect.Height / 2f;
        float nx = (pt.X - rect.MidX) / rx;
        float ny = (pt.Y - rect.MidY) / ry;
        float distance = MathF.Sqrt(nx * nx + ny * ny);
        float relativeTolerance = Math.Max(0.16f, tolerance / Math.Max(10f, Math.Min(rx, ry)));
        return filled ? distance <= 1f + relativeTolerance : Math.Abs(distance - 1f) <= relativeTolerance;
    }

    public static SKRect NormalizeRect(SKPoint a, SKPoint b)
    {
        return new SKRect(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Max(a.X, b.X),
            Math.Max(a.Y, b.Y));
    }

    private static float Distance(SKPoint a, SKPoint b)
        => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static float DistanceToSegment(SKPoint p, SKPoint a, SKPoint b)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        float lenSq = dx * dx + dy * dy;
        if (lenSq < 0.0001f) return Distance(p, a);
        float t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0f, 1f);
        return Distance(p, new SKPoint(a.X + t * dx, a.Y + t * dy));
    }
}
