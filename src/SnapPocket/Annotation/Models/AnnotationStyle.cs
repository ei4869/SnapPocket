using SkiaSharp;

namespace SnapPocket.Annotation.Models;

public enum ShapeMode { Outline, Fill, OutlineAndFill, SolidCover }
public enum StrokePattern { Solid, Dashed, Dotted }
public enum ArrowPlacement { End, Start, Both }
public enum ArrowTip { Filled, Open }
public enum TextBackdrop { Shadow, None, Outline, Background }

public sealed record AnnotationStyle
{
    public SKColor Color { get; init; } = new(239, 83, 80);
    public float StrokeWidth { get; init; } = 3f;
    public float FontSize { get; init; } = 20f;
    public StrokePattern Pattern { get; init; }
    public bool FlatEnds { get; init; }
    public ShapeMode ShapeMode { get; init; }
    public SKColor FillColor { get; init; } = new(255, 238, 88);
    public SKColor CoverColor { get; init; } = new(24, 26, 32);
    public int FillOpacity { get; init; } = 35;
    public float CornerRadius { get; init; }
    public ArrowPlacement ArrowPlacement { get; init; }
    public ArrowTip ArrowTip { get; init; }
    public float ArrowScale { get; init; } = 1f;
    public bool SmoothPen { get; init; } = true;
    public int? Opacity { get; init; }
    public float? MarkerWidth { get; init; }
    public bool Bold { get; init; } = true;
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public TextBackdrop TextBackdrop { get; init; }
    public SKColor TextBackdropColor { get; init; } = new(24, 26, 32);
    public int? MosaicBlockSize { get; init; }
    public float? BlurStrength { get; init; }
    public bool OvalMask { get; init; }
    public float? BadgeDiameter { get; init; }
    public bool SquareBadge { get; init; }
    public SKColor BadgeTextColor { get; init; } = SKColors.White;

    public bool HasOutline => ShapeMode is ShapeMode.Outline or ShapeMode.OutlineAndFill;

    public float EffectiveMarkerWidth => MarkerWidth ?? Math.Max(14f, StrokeWidth * 4.5f);
    public float BadgeRadius => BadgeDiameter / 2f ?? Math.Max(14f, FontSize * 0.72f);
    public int EffectiveBlockSize => Math.Clamp(MosaicBlockSize ?? Math.Clamp((int)(StrokeWidth * 3.5f), 8, 28), 4, 80);
    public float EffectiveBlurStrength => Math.Clamp(BlurStrength ?? Math.Clamp(StrokeWidth * 2.4f, 5f, 24f), 2f, 32f);
    public static SKColor WithOpacity(SKColor color, int percent)
        => color.WithAlpha((byte)Math.Round(color.Alpha * Math.Clamp(percent, 0, 100) / 100f));

    public SKFont CreateTextFont()
    {
        var weight = Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        var slant = Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
        using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", weight, SKFontStyleWidth.Normal, slant);
        var font = new SKFont(typeface, FontSize);
        if (Italic && typeface?.FontSlant == SKFontStyleSlant.Upright)
            font.SkewX = -0.23f;
        return font;
    }

    internal AnnotationStyle Normalize() => this with
    {
        StrokeWidth = Math.Clamp(StrokeWidth, 1, 64),
        FontSize = Math.Clamp(FontSize, 8, 144),
        Pattern = Enum.IsDefined(Pattern) ? Pattern : StrokePattern.Solid,
        ShapeMode = Enum.IsDefined(ShapeMode) ? ShapeMode : ShapeMode.Outline,
        FillOpacity = Math.Clamp(FillOpacity, 1, 100),
        CornerRadius = Math.Clamp(CornerRadius, 0, 128),
        ArrowPlacement = Enum.IsDefined(ArrowPlacement) ? ArrowPlacement : ArrowPlacement.End,
        ArrowTip = Enum.IsDefined(ArrowTip) ? ArrowTip : ArrowTip.Filled,
        ArrowScale = ArrowScale is 0.65f or 1f or 1.5f ? ArrowScale : 1f,
        Opacity = Opacity is int opacity ? Math.Clamp(opacity, 1, 100) : null,
        MarkerWidth = MarkerWidth is float width ? Math.Clamp(width, 1, 160) : null,
        TextBackdrop = Enum.IsDefined(TextBackdrop) ? TextBackdrop : TextBackdrop.Shadow,
        MosaicBlockSize = MosaicBlockSize is int block ? Math.Clamp(block, 4, 80) : null,
        BlurStrength = BlurStrength is float strength ? Math.Clamp(strength, 2, 32) : null,
        BadgeDiameter = BadgeDiameter is float size ? Math.Clamp(size, 12, 128) : null
    };
}
