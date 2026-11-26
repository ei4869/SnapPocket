using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;
using SnapPocket.Annotation;
using SnapPocket.Annotation.Commands;
using SnapPocket.Annotation.Models;
using SnapPocket.Core.Services;

namespace SnapPocket.UI.Annotation;

public enum AnnotationResultAction
{
    Cancel,
    Save,
    Pin
}

public partial class AnnotationWindow : Window
{
    private readonly UndoRedoStack _history = new();
    private readonly List<AnnotationItem> _items = [];
    private readonly string _targetDirectoryHint;
    private readonly ConfigService? _configService;

    private SKBitmap _baseBitmap;

    private AnnotationTool _activeTool = AnnotationTool.Select;
    private int _nextStepNumber = 1;

    private Guid? _selectedItemId;
    private AnnotationItem? _drawingItem;
    private bool _isDraggingInteraction;
    private ResizeHandle _activeResizeHandle = ResizeHandle.None;
    private SKPoint _lastDragImagePt;
    private bool _pushedUndoForCurrentDrag;

    private SKRect? _pendingCropRect;
    private SKPoint _cropStartPt;

    private AnnotationItem? _editingTextItem;
    private bool _isOpeningInlineText;
    private double _displayScale = 1.0;

    // Canvas panning state (Middle mouse or Space + Left mouse)
    private bool _isSpacePanHeld;
    private bool _isPanningCanvas;
    private Point _panStartPoint;
    private double _panStartOffsetX;
    private double _panStartOffsetY;

    public AnnotationResultAction ResultAction { get; private set; } = AnnotationResultAction.Cancel;
    public SKBitmap? FinalFlattenedBitmap { get; private set; }

    public AnnotationWindow(SKBitmap sourceBitmap, string targetDirectoryHint, ConfigService? configService = null)
    {
        InitializeComponent();

        _targetDirectoryHint = targetDirectoryHint;
        _configService = configService;
        // Editing replaces the base image on crop/rotate. The initial pixels can be shared.
        sourceBitmap.SetImmutable();
        _baseBitmap = new SKBitmap();
        if (!sourceBitmap.ExtractSubset(_baseBitmap, new SKRectI(0, 0, sourceBitmap.Width, sourceBitmap.Height)))
        {
            _baseBitmap.Dispose();
            _baseBitmap = sourceBitmap.Copy();
            _baseBitmap.SetImmutable();
        }

        UpdateStatusInfo();
        RefreshToolOptions();

        Loaded += (_, _) =>
        {
            CanvasScrollViewer.ScrollChanged += (_, _) => QueuePreview();
            CanvasScrollViewer.SizeChanged += (_, _) => QueuePreview();
            DpiChanged += (_, _) => QueuePreview();
            FitCanvasToWorkArea();
            RefreshCanvas();
            UpdateUndoRedoButtons();
            Activate();
        };

        Closed += (_, _) =>
        {
            _history.Dispose();
            ClosePreview();
            _baseBitmap.Dispose();
        };
    }

    private void UpdateStatusInfo()
    {
        StatusInfoText.Text = $"尺寸 {_baseBitmap.Width} × {_baseBitmap.Height}  ·  缩放 {(int)Math.Round(_displayScale * 100)}%  ·  目标目录：{_targetDirectoryHint}";
        if (ZoomPercentButton is not null)
        {
            ZoomPercentButton.Content = $"{(int)Math.Round(_displayScale * 100)}%";
        }
    }

    private void FitCanvasToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        double maxCanvasW = Math.Max(480, wa.Width * 0.84 - 60);
        double maxCanvasH = Math.Max(320, wa.Height * 0.84 - 190);

        double scaleX = maxCanvasW / _baseBitmap.Width;
        double scaleY = maxCanvasH / _baseBitmap.Height;
        _displayScale = Math.Min(1.0, Math.Min(scaleX, scaleY));

        ApplyCanvasDisplayScale();

        Width = Math.Clamp(CanvasHost.Width + 84, MinWidth, wa.Width * 0.92);
        Height = Math.Clamp(CanvasHost.Height + 215, MinHeight, wa.Height * 0.92);
        Left = wa.Left + (wa.Width - Width) / 2.0;
        Top = wa.Top + (wa.Height - Height) / 2.0;
    }

    private void FitCanvasToCurrentViewport()
    {
        double availW = Math.Max(200, CanvasScrollViewer.ViewportWidth - 36);
        double availH = Math.Max(160, CanvasScrollViewer.ViewportHeight - 36);
        double scale = Math.Min(1.0, Math.Min(availW / _baseBitmap.Width, availH / _baseBitmap.Height));
        SetZoom(scale);
    }

    private void ApplyCanvasDisplayScale()
    {
        double displayW = Math.Max(1, _baseBitmap.Width * _displayScale);
        double displayH = Math.Max(1, _baseBitmap.Height * _displayScale);

        CanvasHost.Width = displayW;
        CanvasHost.Height = displayH;
        UpdateStatusInfo();
    }

    private void SetZoom(double newScale, Point? anchorViewportPos = null)
    {
        double clamped = Math.Clamp(newScale, 0.20, 6.00);
        if (Math.Abs(clamped - _displayScale) < 0.0001) return;

        double oldScale = _displayScale;
        double relX = 0.5, relY = 0.5;
        if (anchorViewportPos is Point anchor && CanvasHost.ActualWidth > 0 && CanvasHost.ActualHeight > 0)
        {
            var ptOnCanvas = CanvasScrollViewer.TranslatePoint(anchor, CanvasHost);
            relX = Math.Clamp(ptOnCanvas.X / CanvasHost.ActualWidth, 0, 1);
            relY = Math.Clamp(ptOnCanvas.Y / CanvasHost.ActualHeight, 0, 1);
        }

        _displayScale = clamped;
        ApplyCanvasDisplayScale();
        RefreshCanvas();

        if (anchorViewportPos is Point anchorPos)
        {
            CanvasScrollViewer.UpdateLayout();
            double newCanvasX = relX * CanvasHost.Width;
            double newCanvasY = relY * CanvasHost.Height;
            CanvasScrollViewer.ScrollToHorizontalOffset(Math.Max(0, newCanvasX - anchorPos.X + 18));
            CanvasScrollViewer.ScrollToVerticalOffset(Math.Max(0, newCanvasY - anchorPos.Y + 18));
        }
        else if (Math.Abs(oldScale - clamped) > 0.0001)
        {
            CanvasScrollViewer.UpdateLayout();
        }
    }

    private void UpdateCanvasAfterResize()
    {
        if (IsLoaded)
            FitCanvasToCurrentViewport();
        // The fit scale can be identical even when the source dimensions change.
        ApplyCanvasDisplayScale();
    }

    private void RefreshCanvas()
    {
        DeleteSelectedButton.IsEnabled = _selectedItemId.HasValue;
        CropConfirmBar.Visibility = _pendingCropRect.HasValue ? Visibility.Visible : Visibility.Collapsed;
        QueuePreview();
    }

    private void UpdateUndoRedoButtons()
    {
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
    }

    private SKPoint ToImagePoint(Point dipPoint)
    {
        float x = (float)Math.Clamp(dipPoint.X / _displayScale, 0, _baseBitmap.Width);
        float y = (float)Math.Clamp(dipPoint.Y / _displayScale, 0, _baseBitmap.Height);
        return new SKPoint(x, y);
    }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isSpacePanHeld || _isPanningCanvas)
            return;

        if (InlineTextBox.Visibility == Visibility.Visible)
        {
            if (InlineTextBox.IsMouseOver)
                return;
            CommitInlineText();
        }

        Keyboard.Focus(CanvasHost);
        var posDip = e.GetPosition(CanvasHost);
        var imgPt = ToImagePoint(posDip);
        float hitTol = (float)(9.0 / Math.Max(0.25, _displayScale));

        if (_activeTool == AnnotationTool.Crop)
        {
            _isDraggingInteraction = true;
            _cropStartPt = imgPt;
            _cropEndPt = imgPt;
            _pendingCropRect = CropBounds(imgPt);
            CanvasHost.CaptureMouse();
            RefreshCanvas();
            return;
        }

        if (_activeTool == AnnotationTool.Select)
        {
            // Check resize handles of currently selected item first
            if (_selectedItemId.HasValue)
            {
                var currentSel = _items.FirstOrDefault(i => i.Id == _selectedItemId.Value);
                if (currentSel is not null)
                {
                    var handle = currentSel.HitTestHandles(imgPt, hitTol * 1.3f);
                    if (handle != ResizeHandle.None)
                    {
                        _activeResizeHandle = handle;
                        _isDraggingInteraction = true;
                        _lastDragImagePt = imgPt;
                        _pushedUndoForCurrentDrag = false;
                        CanvasHost.CaptureMouse();
                        return;
                    }
                }
            }

            // Otherwise hit-test items from top to bottom
            AnnotationItem? hit = null;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].HitTestBody(imgPt, hitTol))
                {
                    hit = _items[i];
                    break;
                }
            }

            if (_selectedItemId != hit?.Id)
            {
                _selectedItemId = hit?.Id;
                RefreshToolOptions();
            }
            if (hit is not null)
            {
                if (e.ClickCount == 2 && hit.Tool == AnnotationTool.Text)
                {
                    BeginInlineTextEdit(hit);
                    e.Handled = true;
                    return;
                }

                _activeResizeHandle = ResizeHandle.Body;
                _isDraggingInteraction = true;
                _lastDragImagePt = imgPt;
                _pushedUndoForCurrentDrag = false;
                CanvasHost.CaptureMouse();
            }

            RefreshCanvas();
            return;
        }

        if (_activeTool == AnnotationTool.StepBadge)
        {
            _history.PushVectorUndo(_items, _nextStepNumber);
            var step = new AnnotationItem
            {
                Tool = AnnotationTool.StepBadge,
                Style = PresetFor(AnnotationTool.StepBadge),
                Start = imgPt,
                End = imgPt,
                StepNumber = _nextStepNumber
            };
            _nextStepNumber = Math.Min(_nextStepNumber + 1, MaxStepNumber);
            _items.Add(step);
            _selectedItemId = step.Id;
            RefreshToolOptions();
            UpdateUndoRedoButtons();
            RefreshCanvas();
            return;
        }

        if (_activeTool == AnnotationTool.Text)
        {
            var textItem = new AnnotationItem
            {
                Tool = AnnotationTool.Text,
                Style = PresetFor(AnnotationTool.Text),
                Start = imgPt,
                End = imgPt,
                Text = string.Empty
            };
            BeginInlineTextEdit(textItem, isNewItem: true);
            e.Handled = true;
            return;
        }

        // Standard drag-to-create shapes / pen / mosaic / blur
        _history.PushVectorUndo(_items, _nextStepNumber);
        _drawingItem = new AnnotationItem
        {
            Tool = _activeTool,
            Style = PresetFor(_activeTool),
            Start = imgPt,
            End = imgPt
        };

        if (_activeTool is AnnotationTool.Pen or AnnotationTool.Highlighter)
        {
            _drawingItem.Points.Add(imgPt);
        }

        _items.Add(_drawingItem);
        _selectedItemId = _drawingItem.Id;
        _isDraggingInteraction = true;
        CanvasHost.CaptureMouse();
        UpdateUndoRedoButtons();
        RefreshCanvas();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingInteraction) return;

        var imgPt = ToImagePoint(e.GetPosition(CanvasHost));

        if (_activeTool == AnnotationTool.Crop)
        {
            var previous = _pendingCropRect;
            _cropEndPt = imgPt;
            _pendingCropRect = CropBounds(imgPt);
            if (previous is { Width: > 1, Height: > 1 } before && _pendingCropRect is { Width: > 1, Height: > 1 } after)
            {
                var area = SKRect.Union(before, after);
                area.Inflate(4f * PreviewUiScale, 4f * PreviewUiScale);
                QueuePreview(area);
            }
            else
                QueuePreview();
            return;
        }

        if (_activeTool == AnnotationTool.Select && _selectedItemId.HasValue)
        {
            var sel = _items.FirstOrDefault(i => i.Id == _selectedItemId.Value);
            if (sel is null) return;

            float dx = imgPt.X - _lastDragImagePt.X;
            float dy = imgPt.Y - _lastDragImagePt.Y;
            if (Math.Abs(dx) > 0.2f || Math.Abs(dy) > 0.2f)
            {
                if (!_pushedUndoForCurrentDrag)
                {
                    _history.PushVectorUndo(_items, _nextStepNumber);
                    _pushedUndoForCurrentDrag = true;
                    UpdateUndoRedoButtons();
                }

                var previous = GetPreviewBounds(sel);
                if (_activeResizeHandle == ResizeHandle.Body)
                {
                    sel.Translate(dx, dy);
                }
                else
                {
                    var anchor = _activeResizeHandle == ResizeHandle.StartPoint ? sel.End : sel.Start;
                    sel.ApplyResize(_activeResizeHandle, AdjustLinearEndpoint(sel.Tool, anchor, imgPt));
                }

                _lastDragImagePt = imgPt;
                QueueEditedItem(sel, previous);
            }
            return;
        }

        if (_drawingItem is not { } draft) return;
        var previousDraft = GetPreviewBounds(draft);
        UpdateDrawing(draft, imgPt);
        QueueEditedItem(draft, previousDraft);
    }

    private void UpdateDrawing(AnnotationItem item, SKPoint point)
    {
        if (item.Tool is AnnotationTool.Pen or AnnotationTool.Highlighter)
        {
            var last = item.Points[^1];
            float dx = point.X - last.X;
            float dy = point.Y - last.Y;
            if (dx * dx + dy * dy >= 4f)
                item.Points.Add(point);
        }
        else
        {
            item.End = AdjustLinearEndpoint(item.Tool, item.Start, point);
        }
    }

    private SKPoint AdjustLinearEndpoint(AnnotationTool tool, SKPoint start, SKPoint end)
    {
        if (tool is not (AnnotationTool.Line or AnnotationTool.Arrow) ||
            (Keyboard.Modifiers & ModifierKeys.Shift) == 0 ||
            start.X < 0 || start.X > _baseBitmap.Width || start.Y < 0 || start.Y > _baseBitmap.Height)
            return end;

        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        float step = MathF.PI / 4f;
        float angle = MathF.Round(MathF.Atan2(dy, dx) / step) * step;
        float x = MathF.Round(MathF.Cos(angle));
        float y = MathF.Round(MathF.Sin(angle));
        float distance = (dx * x + dy * y) / (x * x + y * y);
        float maxX = x == 0 ? float.PositiveInfinity : x > 0 ? _baseBitmap.Width - start.X : start.X;
        float maxY = y == 0 ? float.PositiveInfinity : y > 0 ? _baseBitmap.Height - start.Y : start.Y;
        distance = Math.Clamp(distance, 0, Math.Min(maxX, maxY));
        return new SKPoint(start.X + x * distance, start.Y + y * distance);
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingInteraction) return;
        _isDraggingInteraction = false;
        _activeResizeHandle = ResizeHandle.None;
        CanvasHost.ReleaseMouseCapture();

        if (_activeTool == AnnotationTool.Crop)
        {
            _cropEndPt = ToImagePoint(e.GetPosition(CanvasHost));
            _pendingCropRect = CropBounds(_cropEndPt);
            if (_pendingCropRect is { Width: < 8 } or { Height: < 8 })
            {
                _pendingCropRect = null;
            }
            RefreshCanvas();
            return;
        }

        if (_drawingItem is not null)
        {
            UpdateDrawing(_drawingItem, ToImagePoint(e.GetPosition(CanvasHost)));
            // Remove tiny accidental click dots for box/line tools
            if (_drawingItem.Tool is not (AnnotationTool.Pen or AnnotationTool.Highlighter))
            {
                float ddx = _drawingItem.End.X - _drawingItem.Start.X;
                float ddy = _drawingItem.End.Y - _drawingItem.Start.Y;
                float dist = MathF.Sqrt(ddx * ddx + ddy * ddy);
                if (dist < 4f)
                {
                    _items.Remove(_drawingItem);
                    _selectedItemId = null;
                }
            }
            _drawingItem = null;
            RefreshToolOptions();
            RefreshCanvas();
        }
    }

    private void BeginInlineTextEdit(AnnotationItem item, bool isNewItem = false)
    {
        _isOpeningInlineText = true;
        _editingTextItem = item;
        InlineTextBox.Tag = isNewItem;
        InlineTextBox.Text = item.Text;
        InlineTextBox.FontSize = Math.Max(13, item.FontSize * _displayScale);
        InlineTextBox.Foreground = ColorBrush(item.Color);
        InlineTextBox.FontWeight = item.Style.Bold ? FontWeights.Bold : FontWeights.Normal;
        InlineTextBox.FontStyle = item.Style.Italic ? FontStyles.Italic : FontStyles.Normal;
        InlineTextBox.TextDecorations = item.Style.Underline ? TextDecorations.Underline : null;
        float luminance = item.Color.Red * 0.299f + item.Color.Green * 0.587f + item.Color.Blue * 0.114f;
        InlineTextBox.Background = luminance < 100 ? Brushes.WhiteSmoke : ColorBrush(new SKColor(20, 23, 31));
        InlineTextBox.CaretBrush = InlineTextBox.Foreground;
        Canvas.SetLeft(InlineTextBox, Math.Clamp(item.Start.X * _displayScale, 4, Math.Max(4, CanvasHost.Width - 150)));
        Canvas.SetTop(InlineTextBox, Math.Clamp(item.Start.Y * _displayScale, 4, Math.Max(4, CanvasHost.Height - 44)));
        InlineTextBox.Visibility = Visibility.Visible;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            InlineTextBox.Focus();
            Keyboard.Focus(InlineTextBox);
            InlineTextBox.CaretIndex = InlineTextBox.Text.Length;
            _isOpeningInlineText = false;
        });
    }

    private void InlineTextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Keep clicks inside the text box from bubbling up to Canvas_MouseDown
        e.Handled = false;
    }

    private void CommitInlineText()
    {
        if (_editingTextItem is null || InlineTextBox.Visibility != Visibility.Visible)
            return;

        bool isNew = InlineTextBox.Tag is true;
        string text = InlineTextBox.Text.TrimEnd();
        var item = _editingTextItem;
        _editingTextItem = null;
        InlineTextBox.Visibility = Visibility.Collapsed;

        if (isNew && !string.IsNullOrWhiteSpace(text))
        {
            _history.PushVectorUndo(_items, _nextStepNumber);
            item.Text = text;
            _items.Add(item);
            _selectedItemId = item.Id;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, RefreshToolOptions);
            UpdateUndoRedoButtons();
        }
        else if (!isNew && item.Text != text)
        {
            _history.PushVectorUndo(_items, _nextStepNumber);
            item.Text = text;
            if (string.IsNullOrWhiteSpace(text))
            {
                _items.Remove(item);
                _selectedItemId = null;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, RefreshToolOptions);
            }
            UpdateUndoRedoButtons();
        }

        RefreshCanvas();
    }

    private void InlineTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _editingTextItem = null;
            InlineTextBox.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            // Enter commits text directly; Shift+Enter inserts a new line
            CommitInlineText();
            e.Handled = true;
        }
    }

    private void InlineTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isOpeningInlineText) return;
        CommitInlineText();
    }

    private void ApplyCrop_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingCropRect is not { Width: >= 8, Height: >= 8 } crop)
            return;

        int left = Math.Clamp((int)Math.Round(crop.Left), 0, _baseBitmap.Width - 1);
        int top = Math.Clamp((int)Math.Round(crop.Top), 0, _baseBitmap.Height - 1);
        int right = Math.Clamp((int)Math.Round(crop.Right), left + 1, _baseBitmap.Width);
        int bottom = Math.Clamp((int)Math.Round(crop.Bottom), top + 1, _baseBitmap.Height);
        int newW = right - left;
        int newH = bottom - top;
        if (newW < 4 || newH < 4) return;

        _history.PushUndo(_baseBitmap, _items, _nextStepNumber);

        var cropped = new SKBitmap(newW, newH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(cropped))
        using (var baseImg = SKImage.FromBitmap(_baseBitmap))
        {
            canvas.DrawImage(baseImg, new SKRect(left, top, right, bottom), new SKRect(0, 0, newW, newH), SKSamplingOptions.Default);
        }

        _baseBitmap.Dispose();
        _baseBitmap = cropped;
        _baseBitmap.SetImmutable();

        foreach (var item in _items)
        {
            item.Translate(-left, -top);
        }

        _pendingCropRect = null;
        _selectedItemId = null;
        UpdateCanvasAfterResize();
        RefreshToolOptions();
        UpdateUndoRedoButtons();
        RefreshCanvas();
    }

    private void CancelCrop_Click(object sender, RoutedEventArgs e)
    {
        _pendingCropRect = null;
        RefreshCanvas();
    }

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag && Enum.TryParse<AnnotationTool>(tag, out var tool))
        {
            SelectTool(tool);
        }
    }

    private void SelectTool(AnnotationTool tool)
    {
        CommitInlineText();
        if (tool != AnnotationTool.Select && tool != _activeTool)
            _selectedItemId = null;
        _activeTool = tool;
        _pendingCropRect = null;
        CanvasHost.Cursor = tool == AnnotationTool.Select ? Cursors.Arrow : Cursors.Cross;

        ToolSelect.IsChecked = tool == AnnotationTool.Select;
        ToolRect.IsChecked = tool == AnnotationTool.Rectangle;
        ToolEllipse.IsChecked = tool == AnnotationTool.Ellipse;
        ToolLine.IsChecked = tool == AnnotationTool.Line;
        ToolArrow.IsChecked = tool == AnnotationTool.Arrow;
        ToolPen.IsChecked = tool == AnnotationTool.Pen;
        ToolHighlighter.IsChecked = tool == AnnotationTool.Highlighter;
        ToolText.IsChecked = tool == AnnotationTool.Text;
        ToolMosaic.IsChecked = tool == AnnotationTool.Mosaic;
        ToolBlur.IsChecked = tool == AnnotationTool.Blur;
        ToolStep.IsChecked = tool == AnnotationTool.StepBadge;
        ToolCrop.IsChecked = tool == AnnotationTool.Crop;

        RefreshToolOptions();
        RefreshCanvas();
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => PerformUndo();
    private void RedoButton_Click(object sender, RoutedEventArgs e) => PerformRedo();

    private void PerformUndo()
    {
        var snap = _history.Undo(_baseBitmap, _items, _nextStepNumber);
        if (snap is null) return;
        RestoreSnapshot(snap);
    }

    private void PerformRedo()
    {
        var snap = _history.Redo(_baseBitmap, _items, _nextStepNumber);
        if (snap is null) return;
        RestoreSnapshot(snap);
    }

    private void RestoreSnapshot(AnnotationSnapshot snap)
    {
        bool sizeChanged = false;
        if (snap.TakeBitmap() is { } restored)
        {
            sizeChanged = restored.Width != _baseBitmap.Width || restored.Height != _baseBitmap.Height;
            _baseBitmap.Dispose();
            _baseBitmap = restored;
            _baseBitmap.SetImmutable();
        }
        snap.Dispose();

        _items.Clear();
        _items.AddRange(snap.Items);
        _nextStepNumber = snap.NextStepNumber;
        if (SelectedItem is null)
            _selectedItemId = null;
        _pendingCropRect = null;

        if (sizeChanged)
            UpdateCanvasAfterResize();
        else
            UpdateStatusInfo();

        UpdateUndoRedoButtons();
        RefreshToolOptions();
        RefreshCanvas();
    }

    // --- Image Rotate / Flip ---
    private void RotateLeft_Click(object sender, RoutedEventArgs e) => ApplyImageTransform(ImageTransformKind.RotateLeft90);
    private void RotateRight_Click(object sender, RoutedEventArgs e) => ApplyImageTransform(ImageTransformKind.RotateRight90);
    private void FlipHorizontal_Click(object sender, RoutedEventArgs e) => ApplyImageTransform(ImageTransformKind.FlipHorizontal);
    private void FlipVertical_Click(object sender, RoutedEventArgs e) => ApplyImageTransform(ImageTransformKind.FlipVertical);

    private void ApplyImageTransform(ImageTransformKind kind)
    {
        CommitInlineText();
        _history.PushUndo(_baseBitmap, _items, _nextStepNumber);

        var transformed = ImageTransformHelper.ApplyTransform(_baseBitmap, _items, kind);
        bool sizeChanged = transformed.Width != _baseBitmap.Width || transformed.Height != _baseBitmap.Height;
        _baseBitmap.Dispose();
        _baseBitmap = transformed;
        _baseBitmap.SetImmutable();

        _pendingCropRect = null;
        _selectedItemId = null;

        if (sizeChanged)
            UpdateCanvasAfterResize();
        else
            UpdateStatusInfo();

        UpdateUndoRedoButtons();
        RefreshToolOptions();
        RefreshCanvas();
    }

    // --- Canvas Zoom & Pan ---
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_displayScale * 1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_displayScale / 1.2);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => FitCanvasToCurrentViewport();

    private void ZoomToggleFit_Click(object sender, RoutedEventArgs e)
    {
        if (Math.Abs(_displayScale - 1.0) < 0.02)
            FitCanvasToCurrentViewport();
        else
            SetZoom(1.0);
    }

    private void CanvasScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            double factor = e.Delta > 0 ? 1.12 : (1.0 / 1.12);
            SetZoom(_displayScale * factor, e.GetPosition(CanvasScrollViewer));
        }
    }

    private void CanvasScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        bool startPan = e.ChangedButton == MouseButton.Middle ||
                        (e.ChangedButton == MouseButton.Left && _isSpacePanHeld);
        if (!startPan) return;

        _isPanningCanvas = true;
        _panStartPoint = e.GetPosition(CanvasScrollViewer);
        _panStartOffsetX = CanvasScrollViewer.HorizontalOffset;
        _panStartOffsetY = CanvasScrollViewer.VerticalOffset;
        CanvasScrollViewer.CaptureMouse();
        CanvasHost.Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    private void CanvasScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanningCanvas) return;
        var curr = e.GetPosition(CanvasScrollViewer);
        CanvasScrollViewer.ScrollToHorizontalOffset(_panStartOffsetX - (curr.X - _panStartPoint.X));
        CanvasScrollViewer.ScrollToVerticalOffset(_panStartOffsetY - (curr.Y - _panStartPoint.Y));
        e.Handled = true;
    }

    private void CanvasScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanningCanvas) return;
        _isPanningCanvas = false;
        CanvasScrollViewer.ReleaseMouseCapture();
        CanvasHost.Cursor = _isSpacePanHeld ? Cursors.Hand : (_activeTool == AnnotationTool.Select ? Cursors.Arrow : Cursors.Cross);
        e.Handled = true;
    }

    private void OnWindowKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _isSpacePanHeld)
        {
            _isSpacePanHeld = false;
            if (!_isPanningCanvas)
                CanvasHost.Cursor = _activeTool == AnnotationTool.Select ? Cursors.Arrow : Cursors.Cross;
        }
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e) => DeleteSelectedItem();

    private void DeleteSelectedItem()
    {
        if (!_selectedItemId.HasValue) return;
        var sel = _items.FirstOrDefault(i => i.Id == _selectedItemId.Value);
        if (sel is null) return;

        _history.PushVectorUndo(_items, _nextStepNumber);
        _items.Remove(sel);
        _selectedItemId = null;
        UpdateUndoRedoButtons();
        RefreshToolOptions();
        RefreshCanvas();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (InlineTextBox.Visibility == Visibility.Visible ||
            (ToolOptionsPanel.IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox or ComboBox))
            return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.Z)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) PerformRedo();
                else PerformUndo();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Y)
            {
                PerformRedo();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.L)
            {
                ApplyImageTransform(ImageTransformKind.RotateLeft90);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.R)
            {
                ApplyImageTransform(ImageTransformKind.RotateRight90);
                e.Handled = true;
                return;
            }
            if (e.Key is Key.OemPlus or Key.Add)
            {
                SetZoom(_displayScale * 1.2);
                e.Handled = true;
                return;
            }
            if (e.Key is Key.OemMinus or Key.Subtract)
            {
                SetZoom(_displayScale / 1.2);
                e.Handled = true;
                return;
            }
            if (e.Key is Key.D0 or Key.NumPad0)
            {
                FitCanvasToCurrentViewport();
                e.Handled = true;
                return;
            }
            if (e.Key is Key.D1 or Key.NumPad1)
            {
                SetZoom(1.0);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Space)
        {
            _isSpacePanHeld = true;
            if (!_isPanningCanvas)
                CanvasHost.Cursor = Cursors.Hand;
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (_pendingCropRect.HasValue)
                {
                    _pendingCropRect = null;
                    RefreshCanvas();
                }
                else
                {
                    ResultAction = AnnotationResultAction.Cancel;
                    DialogResult = false;
                    Close();
                }
                e.Handled = true;
                break;

            case Key.Enter:
                if (_pendingCropRect.HasValue)
                {
                    ApplyCrop_Click(this, new RoutedEventArgs());
                }
                else
                {
                    FinishWithAction(AnnotationResultAction.Save);
                }
                e.Handled = true;
                break;

            case Key.Delete:
            case Key.Back:
                DeleteSelectedItem();
                e.Handled = true;
                break;

            case Key.V: SelectTool(AnnotationTool.Select); break;
            case Key.R: SelectTool(AnnotationTool.Rectangle); break;
            case Key.O: SelectTool(AnnotationTool.Ellipse); break;
            case Key.L: SelectTool(AnnotationTool.Line); break;
            case Key.A: SelectTool(AnnotationTool.Arrow); break;
            case Key.P: SelectTool(AnnotationTool.Pen); break;
            case Key.H: SelectTool(AnnotationTool.Highlighter); break;
            case Key.T: SelectTool(AnnotationTool.Text); break;
            case Key.M: SelectTool(AnnotationTool.Mosaic); break;
            case Key.B: SelectTool(AnnotationTool.Blur); break;
            case Key.N: SelectTool(AnnotationTool.StepBadge); break;
            case Key.C: SelectTool(AnnotationTool.Crop); break;
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e) => FinishWithAction(AnnotationResultAction.Pin);
    private void SaveButton_Click(object sender, RoutedEventArgs e) => FinishWithAction(AnnotationResultAction.Save);

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ResultAction = AnnotationResultAction.Cancel;
        DialogResult = false;
        Close();
    }

    private void FinishWithAction(AnnotationResultAction action)
    {
        CommitInlineText();
        FinalFlattenedBitmap = AnnotationRenderer.RenderFlattened(_baseBitmap, _items);
        ResultAction = action;
        DialogResult = true;
        Close();
    }
}
