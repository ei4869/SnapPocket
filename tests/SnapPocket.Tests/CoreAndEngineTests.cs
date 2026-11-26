using System.IO;
using SkiaSharp;
using SnapPocket.Annotation;
using SnapPocket.Annotation.Commands;
using SnapPocket.Annotation.Models;
using SnapPocket.Capture;
using SnapPocket.Core.Models;
using SnapPocket.Core.Services;

namespace SnapPocket.Tests;

public class CoreAndEngineTests : IDisposable
{
    private readonly string _tempRoot;

    public CoreAndEngineTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SnapPocket_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void HotkeyValidation_AcceptsDefaultLowConflictKeys_AndRejectsDangerousKeys()
    {
        foreach (var hk in AppConfig.CreateDefaultHotkeys().Where(h => h.Enabled))
        {
            var res = HotkeyService.ValidateCombination(hk.Modifiers, hk.VirtualKey);
            Assert.True(res.IsValid, $"Default hotkey for {hk.Action} should be valid: {res.ErrorMessage}");
        }

        // Bare letter rejected
        Assert.False(HotkeyService.ValidateCombination(0, (uint)'A').IsValid);
        // Ctrl+C rejected
        Assert.False(HotkeyService.ValidateCombination(0x0002, (uint)'C').IsValid);
        // Bare F12 rejected (Steam conflict)
        Assert.False(HotkeyService.ValidateCombination(0, 0x7B).IsValid);
        // Win+L rejected
        Assert.False(HotkeyService.ValidateCombination(0x0008, (uint)'L').IsValid);
        // Alt+Tab rejected
        Assert.False(HotkeyService.ValidateCombination(0x0001, 0x09).IsValid);

        // Readable formatting
        Assert.Equal("Ctrl + Shift + 1", HotkeyService.FormatHotkey(0x0006, (uint)'1'));
    }

    [Fact]
    public void DirectoryMemory_RemembersPerApp_FallsBackForDesktopAndMissingDir()
    {
        var cfgService = new ConfigService();
        var defaultDir = Path.Combine(_tempRoot, "Default");
        var gameDir = Path.Combine(_tempRoot, "EldenRingShots");
        Directory.CreateDirectory(defaultDir);
        Directory.CreateDirectory(gameDir);

        cfgService.Config.DefaultSaveDirectory = defaultDir;
        cfgService.Config.AppDirectories.Clear();

        var dirMemory = new DirectoryMemoryService(cfgService);

        var gameCtx = new AppContextInfo
        {
            ProcessName = "eldenring.exe",
            DisplayName = "eldenring",
            IsDesktopContext = false
        };

        // Initially falls back to default
        var beforeAssign = dirMemory.ResolveDirectory(gameCtx, verifyAccessibility: true);
        Assert.False(beforeAssign.IsAppSpecific);
        Assert.Equal(defaultDir, beforeAssign.DirectoryPath);

        // Assign app-specific directory
        dirMemory.AssignDirectoryForContext(gameCtx, gameDir);
        var afterAssign = dirMemory.ResolveDirectory(gameCtx, verifyAccessibility: true);
        Assert.True(afterAssign.IsAppSpecific);
        Assert.Equal(gameDir, afterAssign.DirectoryPath);

        // Desktop context still uses default directory
        var desktopRes = dirMemory.ResolveDirectory(AppContextInfo.Desktop, verifyAccessibility: true);
        Assert.False(desktopRes.IsAppSpecific);
        Assert.Equal(defaultDir, desktopRes.DirectoryPath);

        // Deleting remembered folder gracefully falls back to default with warning flag
        Directory.Delete(gameDir);
        var missingRes = dirMemory.ResolveDirectory(gameCtx, verifyAccessibility: true);
        Assert.True(missingRes.FellBackFromMissingDirectory);
        Assert.Equal(defaultDir, missingRes.DirectoryPath);
    }

    [Fact]
    public void StorageService_BuildsSanitizedFileNames_AndEncodesPngJpgWebP()
    {
        var cfgService = new ConfigService();
        cfgService.Config.DefaultSaveDirectory = _tempRoot;
        var dirMemory = new DirectoryMemoryService(cfgService);
        var storage = new StorageService(cfgService, dirMemory);

        var ctx = new AppContextInfo { DisplayName = "Game:Test/1", ProcessName = "Game.exe", IsDesktopContext = false };
        var ts = new DateTime(2026, 11, 25, 13, 45, 59);
        var name = storage.BuildDefaultFileName(ctx, CaptureMode.Fullscreen, ts);
        Assert.Equal("Game_Test_1_20261125_134559", name);

        using var bmp = new SKBitmap(120, 80, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(40, 120, 220, 255));
        }

        foreach (var fmt in new[] { ImageFormatType.Png, ImageFormatType.Jpg, ImageFormatType.WebP })
        {
            var path = Path.Combine(_tempRoot, $"test_{fmt}.{fmt.ToString().ToLowerInvariant()}");
            StorageService.EncodeToFile(bmp, path, fmt, 90);
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 100);

            using var decoded = SKBitmap.Decode(path);
            Assert.NotNull(decoded);
            Assert.Equal(120, decoded!.Width);
            Assert.Equal(80, decoded.Height);
        }
    }

    [Fact]
    public void AnnotationEngine_RendersAllVectorAndPrivacyTools_SupportsEditAndUndoRedo()
    {
        using var baseBmp = new SKBitmap(300, 200, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(baseBmp))
        {
            c.Clear(SKColors.White);
            using var p = new SKPaint { Color = SKColors.Navy };
            c.DrawRect(20, 20, 80, 80, p);
        }

        var items = new List<AnnotationItem>
        {
            new() { Tool = AnnotationTool.Rectangle, Start = new(10, 10), End = new(100, 90), Color = SKColors.Red, StrokeWidth = 3 },
            new() { Tool = AnnotationTool.Ellipse, Start = new(120, 10), End = new(190, 80), Color = SKColors.Green, StrokeWidth = 3 },
            new() { Tool = AnnotationTool.Arrow, Start = new(20, 140), End = new(160, 140), Color = SKColors.Blue, StrokeWidth = 4 },
            new() { Tool = AnnotationTool.Pen, Points = [new(200, 20), new(230, 40), new(260, 25)], Color = SKColors.Orange, StrokeWidth = 3 },
            new() { Tool = AnnotationTool.Highlighter, Points = [new(200, 70), new(280, 70)], Color = SKColors.Yellow, StrokeWidth = 4 },
            new() { Tool = AnnotationTool.Text, Start = new(15, 160), Text = "Hello 标注", Color = SKColors.Black, FontSize = 18 },
            new() { Tool = AnnotationTool.Mosaic, Start = new(25, 25), End = new(75, 75), StrokeWidth = 4 },
            new() { Tool = AnnotationTool.Blur, Start = new(130, 100), End = new(220, 160), StrokeWidth = 4 },
            new() { Tool = AnnotationTool.StepBadge, Start = new(250, 140), StepNumber = 1, Color = SKColors.Red, FontSize = 20 }
        };

        // Hit testing & secondary editing
        Assert.True(items[0].HitTestBody(new SKPoint(10, 50), 8f));
        Assert.Equal(ResizeHandle.BottomRight, items[0].HitTestHandles(new SKPoint(100, 90), 8f));
        items[0].Translate(5, 5);
        Assert.Equal(15, items[0].Start.X);

        // Undo / Redo stack
        using var undoStack = new UndoRedoStack();
        undoStack.PushUndo(baseBmp, items, 2);
        items.RemoveAt(items.Count - 1);
        Assert.Equal(8, items.Count);

        using var undone = undoStack.Undo(baseBmp, items, 2);
        Assert.NotNull(undone);
        Assert.Equal(9, undone!.Items.Count);
        Assert.True(undoStack.CanRedo);

        // Flattened render produces crisp non-empty bitmap
        using var flattened = AnnotationRenderer.RenderFlattened(baseBmp, undone.Items);
        Assert.Equal(300, flattened.Width);
        Assert.Equal(200, flattened.Height);
        // StepBadge center pixel at (250, 140) should not remain pure white
        var badgeSample = flattened.GetPixel(250, 128);
        Assert.NotEqual(SKColors.White, badgeSample);
    }

    [Fact]
    public void ImageTransformHelper_RotatesAndFlipsBitmapAndAnnotationsAccurately()
    {
        using var bmp = new SKBitmap(200, 100, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.White);
            using var p = new SKPaint { Color = SKColors.Red };
            c.DrawRect(0, 0, 20, 20, p); // Top-left red corner
        }

        var items = new List<AnnotationItem>
        {
            new() { Tool = AnnotationTool.Arrow, Start = new(10, 20), End = new(60, 40) }
        };

        // Rotate 90 CW: (200x100) -> (100x200); (x,y) -> (H-y, x); red top-left moves to top-right
        using var rotRight = ImageTransformHelper.ApplyTransform(bmp, items, ImageTransformKind.RotateRight90);
        Assert.Equal(100, rotRight.Width);
        Assert.Equal(200, rotRight.Height);
        Assert.Equal(new SKPoint(80, 10), items[0].Start);
        Assert.Equal(SKColors.Red, rotRight.GetPixel(90, 10));

        // Rotate 90 CCW back: restores (200x100) and original coordinates
        using var rotBack = ImageTransformHelper.ApplyTransform(rotRight, items, ImageTransformKind.RotateLeft90);
        Assert.Equal(200, rotBack.Width);
        Assert.Equal(100, rotBack.Height);
        Assert.Equal(new SKPoint(10, 20), items[0].Start);
        Assert.Equal(SKColors.Red, rotBack.GetPixel(10, 10));

        // Flip horizontal: (10,20) -> (190,20)
        using var flipH = ImageTransformHelper.ApplyTransform(rotBack, items, ImageTransformKind.FlipHorizontal);
        Assert.Equal(new SKPoint(190, 20), items[0].Start);
        Assert.Equal(SKColors.Red, flipH.GetPixel(190, 10));

        // Flip vertical: (190,20) -> (190,80)
        using var flipV = ImageTransformHelper.ApplyTransform(flipH, items, ImageTransformKind.FlipVertical);
        Assert.Equal(new SKPoint(190, 80), items[0].Start);
        Assert.Equal(SKColors.Red, flipV.GetPixel(190, 90));
    }

    [Fact]
    public void ScrollingEngine_EstimatesExactVerticalShift_AndDetectsBottom()
    {
        // Build a tall synthetic document (400 x 900) with distinct colorful bands
        using var fullDoc = new SKBitmap(400, 900, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(fullDoc))
        {
            c.Clear(SKColors.White);
            for (int row = 0; row < 30; row++)
            {
                byte r = (byte)((row * 47 + 30) % 230);
                byte g = (byte)((row * 83 + 50) % 230);
                byte b = (byte)((row * 131 + 70) % 230);
                using var p = new SKPaint { Color = new SKColor(r, g, b, 255) };
                c.DrawRect(40 + (row % 5) * 20, row * 30 + 4, 240, 18, p);
            }
        }

        // Viewport 1: y = [0..400), Viewport 2: y = [75..475) -> exact downward scroll = 75px
        using var frame1 = CropDoc(fullDoc, 0, 400);
        using var frame2 = CropDoc(fullDoc, 75, 400);

        int estimatedShift = ScrollingCaptureEngine.EstimateVerticalShift(frame1, frame2);
        Assert.InRange(estimatedShift, 73, 77);

        // Identical frame should yield 0 (reached bottom)
        int bottomShift = ScrollingCaptureEngine.EstimateVerticalShift(frame2, frame2);
        Assert.Equal(0, bottomShift);
    }

    private sealed class FakeScrollingCapturer(SKBitmap doc, int[] offsets) : IScreenCapturer
    {
        private int _callIndex;
        public SKBitmap? CaptureRect(System.Drawing.Rectangle physicalBounds)
        {
            int idx = Math.Min(_callIndex++, offsets.Length - 1);
            return CropDoc(doc, offsets[idx], physicalBounds.Height);
        }
        public SKBitmap? CaptureMonitorForPoint(System.Drawing.Point physicalPoint, out System.Drawing.Rectangle bounds) { bounds = default; return null; }
        public SKBitmap? CaptureVirtualScreen(out System.Drawing.Rectangle bounds) { bounds = default; return null; }
        public SKBitmap? CaptureWindow(IntPtr hWnd, out System.Drawing.Rectangle bounds) { bounds = default; return null; }
    }

    [Fact]
    public async Task ScrollingEngine_CaptureManualAsync_StitchesMultiFramesAndEmitsGrowingPreview()
    {
        using var fullDoc = new SKBitmap(360, 900, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(fullDoc))
        {
            c.Clear(SKColors.White);
            for (int row = 0; row < 30; row++)
            {
                byte r = (byte)((row * 53 + 25) % 230);
                byte g = (byte)((row * 97 + 45) % 230);
                byte b = (byte)((row * 137 + 65) % 230);
                using var p = new SKPaint { Color = new SKColor(r, g, b, 255) };
                c.DrawRect(32 + (row % 4) * 18, row * 30 + 5, 220, 18, p);
            }
        }

        // Simulate user scrolling: 0 -> 80 -> 160 -> 240
        var fake = new FakeScrollingCapturer(fullDoc, [0, 80, 160, 240, 240]);
        var engine = new ScrollingCaptureEngine(fake);

        var progressHeights = new List<int>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var progress = new Progress<ScrollingCaptureProgress>(p =>
        {
            if (p.PreviewThumbnail is not null)
                progressHeights.Add(p.TotalHeightPixels);
            if (p.FramesCaptured >= 4)
                cts.Cancel();
        });

        using var stitched = await engine.CaptureManualAsync(
            new System.Drawing.Rectangle(0, 0, 360, 400),
            progress,
            cts.Token);

        Assert.NotNull(stitched);
        Assert.Equal(360, stitched!.Width);
        Assert.InRange(stitched.Height, 634, 646); // Expected ~400 + 240 = 640px
    }

    [Fact]
    public async Task ScrollingEngine_PreservesEveryRowAcrossSmallStepsFastJumpsAndBacktracking()
    {
        const int width = 960;
        const int frameHeight = 400;
        const int finalOffset = 445;
        using var document = new SKBitmap(width, 900, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(document))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint();
            var random = new Random(17);
            for (int y = 0; y < document.Height; y += 20)
            {
                for (int x = 30; x < width - 120; x += 110)
                {
                    paint.Color = new SKColor((byte)random.Next(160), (byte)random.Next(160), (byte)random.Next(160));
                    canvas.DrawRect(x, y + random.Next(5), random.Next(50, 107), random.Next(8, 16), paint);
                }
            }
        }

        using var initial = CropDoc(document, 0, frameHeight);
        var capturer = new FakeScrollingCapturer(document, [3, 120, 407, 397, 407, finalOffset]);
        var engine = new ScrollingCaptureEngine(capturer);
        var previews = new List<ScrollingCaptureProgress>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var progress = new InlineProgress(update =>
        {
            if (update.PreviewThumbnail is not null)
                previews.Add(update);
            if (update.TotalHeightPixels == frameHeight + finalOffset)
                stop.Cancel();
        });

        using var stitched = await engine.CaptureManualAsync(
            new System.Drawing.Rectangle(0, 0, width, frameHeight), progress, stop.Token, initial);

        Assert.NotNull(stitched);
        Assert.Equal(frameHeight + finalOffset, stitched.Height);
        using var expected = CropDoc(document, 0, stitched.Height);
        Assert.Equal(expected.Bytes, stitched.Bytes);
        Assert.True(previews.Count >= 2);
        Assert.Equal(frameHeight, previews[0].TotalHeightPixels);
        Assert.Equal(stitched.Height, previews[^1].TotalHeightPixels);
        byte[] firstPreviewPixel = new byte[4];
        previews[0].PreviewThumbnail!.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), firstPreviewPixel, 4, 0);
        Assert.All(firstPreviewPixel, channel => Assert.Equal(255, channel));
    }

    private sealed class InlineProgress(Action<ScrollingCaptureProgress> report) : IProgress<ScrollingCaptureProgress>
    {
        public void Report(ScrollingCaptureProgress value) => report(value);
    }

    [Fact]
    public async Task ScrollingPreviewDeltasRecreateTheFullPreviewAndKeepAcceptedRows()
    {
        const int width = 480;
        const int frameHeight = 200;
        int[] offsets = [35, 73, 122, 122];
        using var doc = new SKBitmap(width, 460, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(doc))
        using (var paint = new SKPaint())
        {
            canvas.Clear(SKColors.White);
            var colors = new Random(549);
            for (int y = 0; y < doc.Height; y += 6)
            {
                paint.Color = new SKColor((byte)colors.Next(200), (byte)colors.Next(200), (byte)colors.Next(200));
                canvas.DrawRect(30 + y % 89, y, 172, 5, paint);
            }
        }

        var region = new System.Drawing.Rectangle(0, 0, width, frameHeight);
        using var initial = CropDoc(doc, 0, frameHeight);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var finish = new ScrollingFinishRequest();
        var deltas = new List<ScrollingPreviewUpdate>();
        using var merged = await new ScrollingCaptureEngine(new FakeScrollingCapturer(doc, offsets))
            .CaptureWithFinishRequestAsync(region, new InlineProgress(progress =>
            {
                if (progress.PreviewUpdate is { } update)
                    deltas.Add(update);
                if (progress.TotalHeightPixels == frameHeight + offsets[^1])
                    finish.Finish(allowPartial: false);
            }), timeout.Token, initial, finish);
        Assert.NotNull(merged);
        Assert.True(deltas.Count >= 2);
        Assert.Equal(0, deltas[0].Top);
        Assert.True(deltas[^1].Top > 0);

        var last = deltas[^1];
        int stride = last.Pixels.PixelWidth * 4;
        var reconstructed = new byte[stride * last.Height];
        foreach (var delta in deltas)
            delta.Pixels.CopyPixels(reconstructed, stride, delta.Top * stride);

        System.Windows.Media.Imaging.BitmapSource? full = null;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var manual = await new ScrollingCaptureEngine(new FakeScrollingCapturer(doc, offsets))
            .CaptureManualAsync(region, new InlineProgress(progress =>
            {
                if (progress.TotalHeightPixels != frameHeight + offsets[^1] || progress.PreviewThumbnail is null)
                    return;
                full = progress.PreviewThumbnail;
                stop.Cancel();
            }), stop.Token, initial);
        Assert.NotNull(full);
        var fullPixels = new byte[reconstructed.Length];
        full.CopyPixels(fullPixels, stride, 0);
        Assert.Equal(fullPixels, reconstructed);
        using var expected = CropDoc(doc, 0, frameHeight + offsets[^1]);
        Assert.Equal(expected.Bytes, merged!.Bytes);
    }

    [Fact]
    public void GdiConverter_AndMonitorMetrics_WorkAccurately()
    {
        var bounds = SnapPocket.Core.Interop.NativeMethods.GetVirtualScreenBounds();
        Assert.True(bounds.Width > 0 && bounds.Height > 0);

        using var gdi = new System.Drawing.Bitmap(64, 48, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = System.Drawing.Graphics.FromImage(gdi))
        {
            g.Clear(System.Drawing.Color.FromArgb(255, 18, 128, 240));
        }

        using var sk = GdiScreenCapturer.ConvertGdiToSkia(gdi);
        Assert.Equal(64, sk.Width);
        Assert.Equal(48, sk.Height);
        var px = sk.GetPixel(10, 10);
        Assert.Equal(18, px.Red);
        Assert.Equal(128, px.Green);
        Assert.Equal(240, px.Blue);
    }

    private static SKBitmap CropDoc(SKBitmap doc, int topY, int height)
    {
        var res = new SKBitmap(doc.Width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var c = new SKCanvas(res);
        using var img = SKImage.FromBitmap(doc);
        c.DrawImage(img, new SKRect(0, topY, doc.Width, topY + height), new SKRect(0, 0, doc.Width, height), SKSamplingOptions.Default);
        return res;
    }
}
