using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SkiaSharp;
using SnapPocket.Annotation.Models;
using ColorDialog = System.Windows.Forms.ColorDialog;

namespace SnapPocket.UI.Annotation;

public partial class AnnotationWindow
{
    private const int MaxStepNumber = 999999;
    private static readonly AnnotationStyle DefaultStyle = new() { StrokeWidth = 4f, FontSize = 22f };
    private static readonly AnnotationStyle HighlighterStyle = DefaultStyle with { Color = new SKColor(255, 238, 88), MarkerWidth = 20 };
    private readonly Dictionary<AnnotationTool, AnnotationStyle> _localPresets = new();

    private static readonly (SKColor Color, string Name)[] ColorPalette =
    [
        (new SKColor(239, 83, 80), "红色"),
        (new SKColor(255, 167, 38), "橙色"),
        (new SKColor(255, 238, 88), "黄色"),
        (new SKColor(102, 187, 106), "绿色"),
        (new SKColor(76, 154, 255), "蓝色"),
        (new SKColor(171, 71, 188), "紫色"),
        (SKColors.White, "白色"),
        (new SKColor(24, 26, 32), "黑色")
    ];

    private float _cropRatio;
    private SKPoint _cropEndPt;
    private Button? _resetPresetButton;

    private Dictionary<AnnotationTool, AnnotationStyle> ToolPresets => _configService?.Config.AnnotationPresets ?? _localPresets;
    private AnnotationStyle PresetFor(AnnotationTool tool) => ToolPresets.GetValueOrDefault(tool,
        tool == AnnotationTool.Highlighter ? HighlighterStyle : DefaultStyle);

    private AnnotationItem? SelectedItem
        => _selectedItemId is Guid id ? _items.Find(item => item.Id == id) : null;

    private void RefreshToolOptions()
    {
        ToolOptionsPanel.Children.Clear();
        _resetPresetButton = null;
        var selected = SelectedItem;
        var tool = _activeTool == AnnotationTool.Select ? selected?.Tool ?? AnnotationTool.Select : _activeTool;
        var style = selected?.Tool == tool ? selected.Style : PresetFor(tool);

        if (tool == AnnotationTool.Select)
        {
            ToolOptionsPanel.Children.Add(OptionLabel("点选批注可调整样式"));
            return;
        }

        if (_activeTool == AnnotationTool.Select)
        {
            var toolButton = new[] { ToolRect, ToolEllipse, ToolLine, ToolArrow, ToolPen, ToolHighlighter,
                ToolText, ToolMosaic, ToolBlur, ToolStep }.FirstOrDefault(button => button.Tag is string tag && tag == tool.ToString());
            if (toolButton?.Content is string caption)
                ToolOptionsPanel.Children.Add(OptionLabel(caption.Split('(')[0], new Thickness(0, 0, 14, 0)));
        }

        void Change(Func<AnnotationStyle, AnnotationStyle> change, bool rebuild = false)
            => ChangeToolStyle(tool, change, rebuild);

        void StrokeOptions(bool allowCap)
        {
            AddColor("线色", style.Color, value => Change(s => s with { Color = value }));
            AddNumber("线宽", (int)style.StrokeWidth, 1, 64, "px", value => Change(s => s with { StrokeWidth = value }));
            AddChoice("线型", style.Pattern, value => Change(s => s with { Pattern = value }, rebuild: allowCap),
                (StrokePattern.Solid, "实线"), (StrokePattern.Dashed, "虚线"), (StrokePattern.Dotted, "点线"));
            if (allowCap && style.Pattern == StrokePattern.Solid)
                AddChoice("端点", style.FlatEnds, value => Change(s => s with { FlatEnds = value }),
                    (false, "圆头"), (true, "平头"));
        }

        switch (tool)
        {
            case AnnotationTool.Rectangle:
            case AnnotationTool.Ellipse:
                AddChoice("样式", style.ShapeMode, value => Change(s => s with { ShapeMode = value }, rebuild: true),
                    (ShapeMode.Outline, "描边"), (ShapeMode.Fill, "填充"), (ShapeMode.OutlineAndFill, "描边 + 填充"),
                    (ShapeMode.SolidCover, "实色遮盖"));
                if (style.HasOutline)
                    StrokeOptions(allowCap: false);
                if (style.ShapeMode == ShapeMode.SolidCover)
                {
                    AddColor("遮盖色", style.CoverColor, value => Change(s => s with { CoverColor = value }));
                }
                else if (style.ShapeMode != ShapeMode.Outline)
                {
                    AddColor("填充色", style.FillColor, value => Change(s => s with { FillColor = value }));
                    AddNumber("填充不透明度", style.FillOpacity, 1, 100, "%", value => Change(s => s with { FillOpacity = value }));
                }
                if (tool == AnnotationTool.Rectangle && style.ShapeMode != ShapeMode.SolidCover)
                    AddNumber("圆角", (int)style.CornerRadius, 0, 128, "px", value => Change(s => s with { CornerRadius = value }));
                break;

            case AnnotationTool.Line:
                StrokeOptions(allowCap: true);
                break;

            case AnnotationTool.Arrow:
                StrokeOptions(allowCap: false);
                AddChoice("方向", style.ArrowPlacement, value => Change(s => s with { ArrowPlacement = value }),
                    (ArrowPlacement.End, "终点"), (ArrowPlacement.Start, "起点"), (ArrowPlacement.Both, "双向"));
                AddChoice("箭头", style.ArrowTip, value => Change(s => s with { ArrowTip = value }),
                    (ArrowTip.Filled, "实心"), (ArrowTip.Open, "开放"));
                AddChoice("箭头大小", style.ArrowScale, value => Change(s => s with { ArrowScale = value }),
                    (0.65f, "小"), (1f, "中"), (1.5f, "大"));
                break;

            case AnnotationTool.Pen:
            case AnnotationTool.Highlighter:
                bool marker = tool == AnnotationTool.Highlighter;
                AddColor("颜色", style.Color, value => Change(s => s with { Color = value }));
                AddNumber(marker ? "笔头宽度" : "笔宽", (int)(marker ? style.EffectiveMarkerWidth : style.StrokeWidth),
                    1, marker ? 160 : 64, "px", value => Change(s => marker ? s with { MarkerWidth = value } : s with { StrokeWidth = value }));
                AddNumber("不透明度", style.Opacity ?? (marker ? 41 : 100), 1, 100, "%", value => Change(s => s with { Opacity = value }));
                if (marker)
                    AddChoice("笔头", style.FlatEnds, value => Change(s => s with { FlatEnds = value }),
                        (false, "圆头"), (true, "平头"));
                else
                    AddToggle("平滑笔迹", style.SmoothPen, value => Change(s => s with { SmoothPen = value }));
                break;

            case AnnotationTool.Text:
                AddColor("文字颜色", style.Color, value => Change(s => s with { Color = value }));
                AddNumber("字号", (int)style.FontSize, 8, 144, "px", value => Change(s => s with { FontSize = value }));
                AddToggle("加粗", style.Bold, value => Change(s => s with { Bold = value }));
                AddToggle("斜体", style.Italic, value => Change(s => s with { Italic = value }));
                AddToggle("下划线", style.Underline, value => Change(s => s with { Underline = value }));
                AddChoice("文字效果", style.TextBackdrop, value => Change(s => s with { TextBackdrop = value }, rebuild: true),
                    (TextBackdrop.None, "无"), (TextBackdrop.Shadow, "阴影"), (TextBackdrop.Outline, "描边"), (TextBackdrop.Background, "底色"));
                if (style.TextBackdrop is TextBackdrop.Outline or TextBackdrop.Background)
                {
                    AddColor(style.TextBackdrop == TextBackdrop.Outline ? "描边色" : "文字底色", style.TextBackdropColor,
                        value => Change(s => s with { TextBackdropColor = value }));
                }
                break;

            case AnnotationTool.Mosaic:
            case AnnotationTool.Blur:
                if (tool == AnnotationTool.Mosaic)
                    AddNumber("像素块大小", style.EffectiveBlockSize, 4, 80, "px", value => Change(s => s with { MosaicBlockSize = value }));
                else
                    AddNumber("模糊强度", (int)Math.Round(style.EffectiveBlurStrength), 2, 32, "px", value => Change(s => s with { BlurStrength = value }));
                AddChoice("区域形状", style.OvalMask, value => Change(s => s with { OvalMask = value }),
                    (false, "矩形"), (true, "椭圆"));
                break;

            case AnnotationTool.StepBadge:
                AddChoice("徽标", style.SquareBadge, value => Change(s => s with { SquareBadge = value }),
                    (false, "圆形"), (true, "圆角方形"));
                AddColor("底色", style.Color, value => Change(s => s with { Color = value }));
                AddColor("号码颜色", style.BadgeTextColor, value => Change(s => s with { BadgeTextColor = value }));
                AddNumber("大小", (int)Math.Round(style.BadgeRadius * 2f), 12, 128, "px", value => Change(s => s with { BadgeDiameter = value }));
                if (selected?.Tool == AnnotationTool.StepBadge)
                    AddNumber("当前编号", selected.StepNumber, 1, MaxStepNumber, "", ChangeSelectedStepNumber);
                if (_activeTool == AnnotationTool.StepBadge)
                    AddNumber("下个编号", _nextStepNumber, 1, MaxStepNumber, "", value => _nextStepNumber = value);
                break;

            case AnnotationTool.Crop:
                AddChoice("裁剪比例", _cropRatio, ChangeCropRatio,
                    (0f, "自由"), (1f, "1:1"), (4f / 3, "4:3"), (3f / 2, "3:2"), (16f / 9, "16:9"), (9f / 16, "9:16"));
                break;
        }

        if (_activeTool == tool && tool != AnnotationTool.Crop)
        {
            var reset = new Button
            {
                Content = "恢复默认",
                Style = (Style)FindResource("SubtleButtonStyle"),
                Padding = new Thickness(7, 3, 7, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = ToolPresets.ContainsKey(tool) ? Visibility.Visible : Visibility.Collapsed,
                ToolTip = "恢复此工具的新建样式"
            };
            _resetPresetButton = reset;
            reset.Click += (_, _) =>
            {
                ToolPresets.Remove(tool);
                _configService?.Save();
                _selectedItemId = null;
                RefreshCanvas();
                RefreshToolOptions();
            };
            ToolOptionsPanel.Children.Add(reset);
        }
    }

    private void ChangeToolStyle(AnnotationTool tool, Func<AnnotationStyle, AnnotationStyle> change, bool rebuild)
    {
        if (_activeTool == tool)
        {
            var current = PresetFor(tool);
            var next = change(current);
            if (next != current)
            {
                ToolPresets[tool] = next;
                bool saved = _configService?.Save() ?? true;
                if (!saved)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, RefreshToolOptions);
                else if (_resetPresetButton is { } reset)
                    reset.Visibility = Visibility.Visible;
            }
        }

        if (SelectedItem is { } item && item.Tool == tool)
        {
            var updated = change(item.Style);
            if (updated != item.Style)
            {
                _history.PushVectorUndo(_items, _nextStepNumber);
                item.Style = updated;
                UpdateUndoRedoButtons();
                RefreshCanvas();
            }
        }

        if (rebuild)
            RefreshToolOptions();
    }

    private void ChangeSelectedStepNumber(int value)
    {
        if (SelectedItem is not { Tool: AnnotationTool.StepBadge } item || item.StepNumber == value)
            return;
        _history.PushVectorUndo(_items, _nextStepNumber);
        item.StepNumber = value;
        UpdateUndoRedoButtons();
        RefreshCanvas();
    }

    private void ChangeCropRatio(float ratio)
    {
        _cropRatio = ratio;
        if (_pendingCropRect.HasValue)
        {
            _pendingCropRect = CropBounds(_cropEndPt);
            RefreshCanvas();
        }
    }

    private SKRect CropBounds(SKPoint end)
    {
        if (_cropRatio <= 0)
            return AnnotationItem.NormalizeRect(_cropStartPt, end);

        float dx = end.X - _cropStartPt.X;
        float dy = end.Y - _cropStartPt.Y;
        bool toLeft = dx < 0 || (dx == 0 && _cropStartPt.X > _baseBitmap.Width / 2f);
        bool upward = dy < 0 || (dy == 0 && _cropStartPt.Y > _baseBitmap.Height / 2f);
        float availableWidth = toLeft ? _cropStartPt.X : _baseBitmap.Width - _cropStartPt.X;
        float availableHeight = upward ? _cropStartPt.Y : _baseBitmap.Height - _cropStartPt.Y;
        float width = Math.Min(Math.Max(Math.Abs(dx), Math.Abs(dy) * _cropRatio),
            Math.Min(availableWidth, availableHeight * _cropRatio));
        float height = width / _cropRatio;
        var adjusted = new SKPoint(_cropStartPt.X + (toLeft ? -width : width),
            _cropStartPt.Y + (upward ? -height : height));
        return AnnotationItem.NormalizeRect(_cropStartPt, adjusted);
    }

    private void AddChoice<T>(string label, T value, Action<T> apply, params (T Value, string Label)[] choices)
    {
        var combo = new ComboBox { Height = 28, MinWidth = 66, Padding = new Thickness(6, 3, 23, 3) };
        foreach (var choice in choices)
            combo.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
        combo.SelectedIndex = Array.FindIndex(choices, choice => EqualityComparer<T>.Default.Equals(choice.Value, value));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T selected })
                apply(selected);
        };
        AddOption(label, combo);
    }

    private void AddNumber(string label, int value, int min, int max, string unit, Action<int> apply)
    {
        var input = new TextBox
        {
            Text = value.ToString(CultureInfo.InvariantCulture),
            Width = max > 1000 ? 60 : 40,
            Height = 28,
            Padding = new Thickness(4, 1, 4, 1),
            TextAlignment = TextAlignment.Center,
            Style = (Style)FindResource("ModernTextBoxStyle"),
            ToolTip = $"{min}–{max} {unit} · ↑/↓ 微调".Trim()
        };
        InputMethod.SetIsInputMethodEnabled(input, false);
        input.PreviewTextInput += (_, args) => args.Handled = args.Text.Any(c => !char.IsAsciiDigit(c));
        DataObject.AddPastingHandler(input, (_, args) =>
        {
            if (args.DataObject.GetData(DataFormats.UnicodeText) is not string digits || digits.Any(c => !char.IsAsciiDigit(c)))
                args.CancelCommand();
        });

        void Commit(int number)
        {
            int bounded = Math.Clamp(number, min, max);
            input.Text = bounded.ToString(CultureInfo.InvariantCulture);
            if (bounded == value)
                return;
            value = bounded;
            apply(bounded);
        }

        int Read() => int.TryParse(input.Text, out int number) ? number : value;

        input.GotKeyboardFocus += (_, _) => input.SelectAll();
        input.LostKeyboardFocus += (_, _) => Commit(Read());
        input.PreviewKeyDown += (_, args) =>
        {
            switch (args.Key)
            {
                case Key.Enter:
                    Commit(Read());
                    Keyboard.Focus(CanvasHost);
                    args.Handled = true;
                    return;
                case Key.Escape:
                    Commit(value);
                    Keyboard.Focus(CanvasHost);
                    args.Handled = true;
                    return;
                case Key.Up: Commit(Math.Clamp(Read(), min, max) + 1); break;
                case Key.Down: Commit(Math.Clamp(Read(), min, max) - 1); break;
                default: return;
            }
            input.SelectAll();
            args.Handled = true;
        };
        AddOption(label, input, unit);
    }

    private void AddToggle(string label, bool value, Action<bool> apply)
    {
        var check = new CheckBox
        {
            Content = label,
            IsChecked = value,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 13, 2)
        };
        check.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        check.Click += (_, _) => apply(check.IsChecked == true);
        ToolOptionsPanel.Children.Add(check);
    }

    private void AddColor(string label, SKColor value, Action<SKColor> apply)
    {
        var swatch = ColorSwatch(value);
        var dropdown = new StackPanel { Orientation = Orientation.Horizontal };
        dropdown.Children.Add(swatch);
        dropdown.Children.Add(OptionLabel("▾", new Thickness(4, 0, 0, 0)));
        var button = new Button
        {
            Content = dropdown,
            Height = 28,
            Padding = new Thickness(6, 2, 5, 2),
            Style = (Style)FindResource("SubtleButtonStyle")
        };

        void Choose(SKColor color)
        {
            if (value == color) return;
            value = color;
            swatch.Background = ColorBrush(color);
            button.ToolTip = $"{label} #{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
            apply(color);
        }

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            FontSize = 12
        };
        menu.SetResourceReference(BackgroundProperty, "CardBackgroundBrush");
        menu.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        foreach (var (color, name) in ColorPalette)
        {
            var entry = new MenuItem { Header = name, Icon = ColorSwatch(color) };
            entry.Click += (_, _) => Choose(color);
            menu.Items.Add(entry);
        }

        menu.Items.Add(new Separator());
        var custom = new MenuItem { Header = "自定义颜色…" };
        custom.Click += (_, _) =>
        {
            using var dialog = new ColorDialog
            {
                FullOpen = true,
                Color = System.Drawing.Color.FromArgb(value.Red, value.Green, value.Blue)
            };
            if (dialog.ShowDialog(new DialogOwner(new WindowInteropHelper(this).Handle)) == System.Windows.Forms.DialogResult.OK)
                Choose(new SKColor(dialog.Color.R, dialog.Color.G, dialog.Color.B));
        };
        menu.Items.Add(custom);
        button.ContextMenu = menu;
        button.ToolTip = $"{label} #{value.Red:X2}{value.Green:X2}{value.Blue:X2}";
        button.Click += (_, _) => menu.IsOpen = true;
        AddOption(label, button);
    }

    private void AddOption(string label, Control control, string unit = "")
    {
        var group = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 13, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        control.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(control, label);
        group.Children.Add(OptionLabel(label, new Thickness(0, 0, 5, 0)));
        group.Children.Add(control);
        if (unit.Length > 0)
            group.Children.Add(OptionLabel(unit, new Thickness(3, 0, 0, 0)));
        ToolOptionsPanel.Children.Add(group);
    }

    private static TextBlock OptionLabel(string text, Thickness margin = default)
    {
        var block = new TextBlock { Text = text, Margin = margin, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return block;
    }

    private static Border ColorSwatch(SKColor color)
    {
        var swatch = new Border
        {
            Width = 15,
            Height = 15,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Background = ColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center
        };
        swatch.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
        return swatch;
    }

    private static SolidColorBrush ColorBrush(SKColor color)
        => new(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));

    private sealed record DialogOwner(IntPtr Handle) : System.Windows.Forms.IWin32Window;
}
