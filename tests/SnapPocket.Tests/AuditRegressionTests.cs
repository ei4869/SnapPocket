using System.IO;
using SkiaSharp;
using SnapPocket.Annotation;
using SnapPocket.Annotation.Commands;
using SnapPocket.Annotation.Models;
using SnapPocket.Capture;
using SnapPocket.Core.Models;
using SnapPocket.Core.Services;
using Rectangle = System.Drawing.Rectangle;

namespace SnapPocket.Tests;

public sealed class AuditRegressionTests : IDisposable
{
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "audit-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task SaveCreatesRealFileForReservedNameAndReportsClipboardFailureSeparately()
    {
        using var bitmap = Pattern(16, 16);
        var config = new ConfigService(_folder);
        config.Config.DefaultSaveDirectory = _folder;
        config.Config.PlaySoundOnCapture = false;
        int clipboardCalls = 0;
        var storage = new StorageService(config, new DirectoryMemoryService(config), _ =>
        {
            Interlocked.Increment(ref clipboardCalls);
            return Task.FromResult(false);
        });

        Directory.CreateDirectory(Path.Combine(_folder, "截图_NUL.png"));
        string existing = Path.Combine(_folder, "截图_NUL_1.png");
        await File.WriteAllTextAsync(existing, "existing file");
        var result = await storage.SaveAndCopyAsync(bitmap, AppContextInfo.Desktop, CaptureMode.Region, DateTime.Now, "NUL.png");

        Assert.True(result.Success);
        Assert.False(result.ClipboardCopied);
        Assert.Equal(1, clipboardCalls);
        Assert.Equal("截图_NUL_2.png", result.FileName);
        Assert.Equal("existing file", await File.ReadAllTextAsync(existing));
        using var decoded = SKBitmap.Decode(result.FilePath);
        Assert.NotNull(decoded);
        Assert.Equal(16, decoded.Width);
        Assert.Empty(Directory.GetFiles(_folder, ".snap-*.tmp"));

        var failed = await storage.SaveAndCopyAsync(bitmap, AppContextInfo.Desktop, CaptureMode.Region, DateTime.Now,
            "file", directoryOverride: existing);
        Assert.False(failed.Success);
        Assert.NotEmpty(failed.ErrorMessage!);
        Assert.Equal(1, clipboardCalls);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("COM1.backup")]
    [InlineData("LPT².png")]
    [InlineData("AUX .jpg")]
    [InlineData("CONOUT$")]
    public void ReservedDeviceNamesArePrefixed(string name)
        => Assert.StartsWith("截图_", StorageService.SanitizeFileNameSegment(name));

    [Fact]
    public async Task ConcurrentWritersKeepBothImages()
    {
        using var first = Pattern(20, 20);
        using var second = Pattern(28, 28);
        var config = new ConfigService(_folder);
        config.Config.DefaultSaveDirectory = _folder;
        config.Config.PlaySoundOnCapture = false;
        var storage = new StorageService(config, new DirectoryMemoryService(config), _ => Task.FromResult(true));
        var now = DateTime.Now;
        var results = await Task.WhenAll(
            storage.SaveAndCopyAsync(first, AppContextInfo.Desktop, CaptureMode.Region, now, "same-name"),
            storage.SaveAndCopyAsync(second, AppContextInfo.Desktop, CaptureMode.Region, now, "same-name"));
        Assert.All(results, result => Assert.True(result.Success && result.ClipboardCopied));
        Assert.NotEqual(results[0].FilePath, results[1].FilePath);
        using var savedFirst = SKBitmap.Decode(results[0].FilePath);
        using var savedSecond = SKBitmap.Decode(results[1].FilePath);
        Assert.Equal(20, savedFirst.Width);
        Assert.Equal(28, savedSecond.Width);
    }

    [Fact]
    public void ConfigRollsBackWithoutChangeEventAndKeepsSavedAnnotationPresets()
    {
        var config = new ConfigService(_folder);
        var style = new AnnotationStyle { Color = new SKColor(12, 34, 56, 128), StrokeWidth = 7, ArrowPlacement = ArrowPlacement.Both };
        config.Config.AnnotationPresets[AnnotationTool.Arrow] = style;
        config.Config.FileNameTemplate = "saved";
        Assert.True(config.Save());

        int changes = 0;
        int failures = 0;
        config.ConfigChanged += () => changes++;
        config.SaveFailed += _ => failures++;
        using (File.Open(config.ConfigFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            config.Config.FileNameTemplate = "unsaved";
            config.Config.AnnotationPresets.Clear();
            Assert.False(config.Save());
        }

        Assert.Equal(0, changes);
        Assert.Equal(1, failures);
        Assert.Equal("saved", config.Config.FileNameTemplate);
        Assert.Equal(style, config.Config.AnnotationPresets[AnnotationTool.Arrow]);
        Assert.Equal(style, new ConfigService(_folder).Config.AnnotationPresets[AnnotationTool.Arrow]);
        Assert.Empty(Directory.GetFiles(_folder, ".config-*.tmp"));
    }

    [Fact]
    public void HotkeyConflictsAndConfigErrorsKeepThePreviousRegistration()
    {
        var config = new ConfigService(_folder);
        Assert.True(config.Save());
        var active = new Dictionary<int, (uint Modifiers, uint Key)>();
        using var service = new HotkeyService(config, (id, mods, key) =>
        {
            if (key == 0x79 || active.ContainsValue((mods, key)))
                return 1409;
            active[id] = (mods, key);
            return 0;
        }, id => active.Remove(id));
        Assert.Empty(service.ReloadAndRegisterAll());

        var original = config.Config.Hotkeys.Single(item => item.Action == HotkeyAction.RegionCapture);
        var before = active.ToArray();
        Assert.False(service.TrySetBinding(HotkeyAction.RegionCapture, 0, 0x79).IsValid);
        Assert.Equal(before, active.ToArray());

        using (File.Open(config.ConfigFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(service.TrySetBinding(HotkeyAction.RegionCapture, 0, 0x7A).IsValid);

        Assert.Equal(before, active.ToArray());
        Assert.Equal(original.VirtualKey, config.Config.Hotkeys.Single(item => item.Action == HotkeyAction.RegionCapture).VirtualKey);
        Assert.True(service.TrySetBinding(HotkeyAction.RegionCapture, 0, 0x7A).IsValid);
        Assert.Contains((0u, 0x7Au), active.Values);
        Assert.DoesNotContain((original.Modifiers, original.VirtualKey), active.Values);
    }

    [Fact]
    public void VectorHistoryKeepsNoBitmapCopiesAndRestoresAcrossImageEdits()
    {
        using var original = Pattern(240, 240);
        using var cropped = Pattern(100, 100);
        using var history = new UndoRedoStack(256L * 1024);
        var item = new AnnotationItem { Tool = AnnotationTool.Rectangle, StrokeWidth = 2 };
        history.PushVectorUndo([item], 1);
        Assert.Equal(0, history.RetainedBitmapBytes);
        item.StrokeWidth = 3;

        history.PushUndo(original, [item], 1);
        history.PushVectorUndo([item], 1);
        item.StrokeWidth = 5;
        Assert.Equal((long)original.RowBytes * original.Height, history.RetainedBitmapBytes);

        using var lastVector = history.Undo(cropped, [item], 1)!;
        Assert.Null(lastVector.BaseBitmap);
        Assert.Equal(3f, lastVector.Items[0].StrokeWidth);

        using var imageEdit = history.Undo(cropped, lastVector.Items, 1)!;
        Assert.Equal(original.Width, imageEdit.BaseBitmap!.Width);
        using var firstVector = history.Undo(original, imageEdit.Items, 1)!;
        Assert.Equal(2f, firstVector.Items[0].StrokeWidth);
        Assert.Null(firstVector.BaseBitmap);

        using var redoVector = history.Redo(original, firstVector.Items, 1)!;
        using var redoImage = history.Redo(original, redoVector.Items, 1)!;
        Assert.Equal(cropped.Width, redoImage.BaseBitmap!.Width);
        using var finalVector = history.Redo(cropped, redoImage.Items, 1)!;
        Assert.Null(finalVector.BaseBitmap);
        Assert.Equal(5f, finalVector.Items[0].StrokeWidth);

        history.PushUndo(original, [item], 1);
        history.PushUndo(original, [item], 1);
        Assert.InRange(history.RetainedBitmapBytes, 1, 256L * 1024);
    }

    [Fact]
    public void BlurAboveMosaicDoesNotResampleOriginalImage()
    {
        using var source = Pattern(180, 160);
        var mosaic = new AnnotationItem
        {
            Tool = AnnotationTool.Mosaic, Start = new(10, 10), End = new(170, 150),
            Style = new AnnotationStyle { MosaicBlockSize = 60 }
        };
        var blur = new AnnotationItem
        {
            Tool = AnnotationTool.Blur, Start = mosaic.Start, End = mosaic.End,
            Style = new AnnotationStyle { BlurStrength = 2 }
        };
        using var masked = AnnotationRenderer.RenderFlattened(source, [mosaic]);
        using var overlapping = AnnotationRenderer.RenderFlattened(source, [mosaic, blur]);
        using var onlyBlur = AnnotationRenderer.RenderFlattened(source, [blur]);
        Assert.Equal(masked.GetPixel(38, 40), overlapping.GetPixel(38, 40));
        Assert.NotEqual(onlyBlur.GetPixel(38, 40), overlapping.GetPixel(38, 40));

        using var preview = Pattern(180, 160);
        AnnotationRenderer.RenderInteractive(preview, source, [mosaic, blur], null, null, 1);
        Assert.Equal(overlapping.Bytes, preview.Bytes);

        var cover = new AnnotationItem
        {
            Tool = AnnotationTool.Rectangle, Start = new(10, 10), End = new(140, 120),
            Style = new AnnotationStyle { ShapeMode = ShapeMode.SolidCover, CoverColor = new SKColor(22, 24, 26, 70), CornerRadius = 40 }
        };
        using var covered = AnnotationRenderer.RenderFlattened(source, [cover]);
        Assert.Equal(new SKColor(22, 24, 26), covered.GetPixel(12, 12));
    }

    [Fact]
    public void WindowCaptureNeverSubstitutesAMonitor()
    {
        var helper = new FixtureCapturer(null);
        var context = new AppContextInfo { IsDesktopContext = false, WindowHandle = new IntPtr(1) };
        Assert.Null(CaptureCoordinator.TryCaptureActiveWindow(helper, context));
        Assert.Equal(1, helper.WindowCalls);
        Assert.Null(CaptureCoordinator.TryCaptureActiveWindow(helper, AppContextInfo.Desktop));
        Assert.Equal(1, helper.WindowCalls);
        Assert.Equal(0, helper.MonitorCalls);
    }

    [Fact]
    public void ScrollMatcherRecognizesUniformHorizontalBands()
    {
        using var image = new SKBitmap(400, 650);
        using (var canvas = new SKCanvas(image))
        using (var paint = new SKPaint())
        {
            var random = new Random(591);
            for (int y = 0; y < image.Height; y += 12)
            {
                paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                canvas.DrawRect(0, y, image.Width, 12, paint);
            }
        }
        using var start = Slice(image, 0, 220);
        using var next = Slice(image, 45, 220);
        Assert.Equal(45, ScrollingCaptureEngine.EstimateVerticalShift(start, next));
    }

    [Fact]
    public void ScrollMatcherAcceptsAdjacentHalfPixelCandidatesWithoutAccumulatingDrift()
    {
        const int width = 580;
        const int height = 444;
        using var article = Pattern(width, 1100);
        article.SetImmutable();
        using var first = Slice(article, 0, height);
        var matcher = new ScrollingFrameMatcher(first, (int)(height * 0.86));
        int total = 0;
        for (int step = 1; step <= 7; step++)
        {
            using var frame = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(frame))
                canvas.DrawBitmap(article, 0, -step * 82.5f, new SKSamplingOptions(SKFilterMode.Linear));
            var (shift, unchanged) = matcher.Match(frame);
            Assert.False(unchanged);
            Assert.InRange(shift, 82, 83);
            matcher.Accept();
            total += shift;
        }
        Assert.InRange(total, 576, 579);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScrollFinishRequiresExplicitPartialSaveOrCanResume(bool savePartial)
    {
        using var doc = Pattern(400, 780);
        using var initial = Slice(doc, 0, 220);
        var request = new ScrollingFinishRequest();
        var capturer = new FixtureCapturer(doc, request);
        var engine = new ScrollingCaptureEngine(capturer);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var prompt = new TaskCompletionSource<ScrollingCaptureProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress(update =>
        {
            if (update.RequiresPartialConfirmation)
                prompt.TrySetResult(update);
            else if (update.TotalHeightPixels == 324)
                recovered.TrySetResult();
        });

        var capture = engine.CaptureWithFinishRequestAsync(new Rectangle(0, 0, 400, 220), progress, cancel.Token, initial, request);
        var warning = await prompt.Task.WaitAsync(cancel.Token);
        Assert.False(capture.IsCompleted);
        Assert.Equal(284, warning.TotalHeightPixels);

        if (!savePartial)
        {
            capturer.Recover();
            await recovered.Task.WaitAsync(cancel.Token);
        }
        request.Finish(allowPartial: savePartial);
        using var result = await capture;
        Assert.NotNull(result);
        Assert.Equal(savePartial ? 284 : 324, result.Height);
    }

    [Fact]
    public async Task CancellingControlledScrollDiscardsTheResult()
    {
        using var doc = Pattern(400, 780);
        using var initial = Slice(doc, 0, 220);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var engine = new ScrollingCaptureEngine(new FixtureCapturer(doc));
        using var result = await engine.CaptureWithFinishRequestAsync(new Rectangle(0, 0, 400, 220),
            new InlineProgress(_ => cancel.Cancel()), cancel.Token, initial, new ScrollingFinishRequest());
        Assert.Null(result);
    }

    private sealed class InlineProgress(Action<ScrollingCaptureProgress> report) : IProgress<ScrollingCaptureProgress>
    {
        public void Report(ScrollingCaptureProgress update) => report(update);
    }

    private sealed class FixtureCapturer(SKBitmap? document, ScrollingFinishRequest? finish = null) : IScreenCapturer
    {
        private int _calls;
        private volatile bool _recovered;
        public int MonitorCalls { get; private set; }
        public int WindowCalls { get; private set; }

        public void Recover() => _recovered = true;

        public SKBitmap? CaptureRect(Rectangle physicalBounds)
        {
            int call = ++_calls;
            if (call == 3)
                finish?.Finish(allowPartial: false);
            int offset = _recovered ? 104 : call == 1 ? 32 : call == 2 ? 64 : 420;
            return document is null ? null : Slice(document, offset, physicalBounds.Height);
        }

        public SKBitmap? CaptureMonitorForPoint(System.Drawing.Point point, out Rectangle bounds)
        {
            MonitorCalls++;
            bounds = new Rectangle(0, 0, 16, 16);
            return Pattern(bounds.Width, bounds.Height);
        }

        public SKBitmap? CaptureVirtualScreen(out Rectangle bounds) { bounds = default; return null; }
        public SKBitmap? CaptureWindow(IntPtr hwnd, out Rectangle bounds) { WindowCalls++; bounds = default; return null; }
    }

    private static SKBitmap Pattern(int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint();
        var random = new Random(591);
        for (int y = 0; y < height; y += 12)
        {
            for (int x = 0; x < width; x += 48)
            {
                paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                canvas.DrawRect(x, y + random.Next(4), random.Next(25, 45), random.Next(4, 12), paint);
            }
        }
        return bitmap;
    }

    private static SKBitmap Slice(SKBitmap document, int y, int height)
    {
        var image = new SKBitmap(document.Width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(image);
        canvas.DrawBitmap(document, 0, -y, SKSamplingOptions.Default);
        return image;
    }
}
