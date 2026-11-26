using System.Diagnostics;
using System.Drawing;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace SnapPocket.Capture;

public sealed class ScrollingCaptureProgress
{
    public int FramesCaptured { get; init; }
    public int TotalHeightPixels { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public bool IsWarning { get; init; }
    public bool RequiresPartialConfirmation { get; init; }
    public BitmapSource? PreviewThumbnail { get; init; }
    internal ScrollingPreviewUpdate? PreviewUpdate { get; init; }
}

internal readonly record struct ScrollingPreviewUpdate(BitmapSource Pixels, int Top, int Height, int HeightLimit);

internal sealed class ScrollingFinishRequest
{
    private int _request;
    public void Finish(bool allowPartial) => Interlocked.Exchange(ref _request, allowPartial ? 2 : 1);
    public int Take() => Interlocked.Exchange(ref _request, 0);
}

public sealed class ScrollingCaptureEngine
{
    private const int MaxTotalHeight = 18000;
    private const int PreviewTargetWidth = 216;
    private const string HeightLimitStatus = "已达到长图高度上限，可点击「完成拼接」";
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan IdleFrameInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FinishSettleTime = TimeSpan.FromMilliseconds(55);
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(64);
    private static readonly TimeSpan LostOverlapDelay = TimeSpan.FromMilliseconds(400);

    private readonly IScreenCapturer _fastCapturer;

    public ScrollingCaptureEngine(IScreenCapturer? capturer = null)
    {
        _fastCapturer = capturer ?? new GdiScreenCapturer();
    }

    public Task<SKBitmap?> CaptureManualAsync(
        Rectangle region,
        IProgress<ScrollingCaptureProgress>? progress,
        CancellationToken stopToken,
        SKBitmap? initialFrame = null)
        => Task.Run(() => CaptureOnWorkerAsync(region, progress, stopToken, initialFrame, null));

    internal Task<SKBitmap?> CaptureWithFinishRequestAsync(Rectangle region, IProgress<ScrollingCaptureProgress>? progress,
        CancellationToken cancelToken, SKBitmap? initialFrame, ScrollingFinishRequest finishRequest)
        => Task.Run(() => CaptureOnWorkerAsync(region, progress, cancelToken, initialFrame, finishRequest));

    private async Task<SKBitmap?> CaptureOnWorkerAsync(
        Rectangle region,
        IProgress<ScrollingCaptureProgress>? progress,
        CancellationToken stopToken,
        SKBitmap? initialFrame,
        ScrollingFinishRequest? finishRequest)
    {
        if (region.Width < 30 || region.Height < 40 || (finishRequest is not null && stopToken.IsCancellationRequested))
            return null;

        using var gdiSession = _fastCapturer is GdiScreenCapturer ? GdiScreenCapturer.RegionCaptureSession.TryCreate(region) : null;
        using var firstBuffer = gdiSession is null ? null : new SKBitmap(region.Width, region.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var secondBuffer = gdiSession is null ? null : new SKBitmap(region.Width, region.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

        SKBitmap? CaptureFrame(SKBitmap? previous)
        {
            if (gdiSession is null)
                return _fastCapturer.CaptureRect(region);

            var target = ReferenceEquals(previous, firstBuffer) ? secondBuffer! : firstBuffer!;
            return gdiSession.TryCapture(target) ? target : null;
        }

        void ReleaseFrame(SKBitmap? frame)
        {
            if (gdiSession is null && !ReferenceEquals(frame, initialFrame))
                frame?.Dispose();
        }

        SKBitmap? reference = initialFrame ?? CaptureFrame(null);
        if (reference is null)
            return null;

        try
        {
            int seamY = ComputeSeamY(region.Height);
            int heightLimit = Math.Max(MaxTotalHeight, region.Height);
            using var stitched = new ScrollingImageBuffer(region.Width, heightLimit);
            int bodyHeight = seamY;
            int totalHeight = region.Height;
            int frameCount = 1;
            bool heightLimitReached = totalHeight >= heightLimit;
            string? overlapWarning = null;
            bool previewPending = false;
            long? firstUnmatched = null;
            long? finishStarted = null;
            int stableFinishFrames = 0;
            bool offerPartial = false;
            long lastMovement = Stopwatch.GetTimestamp();

            int previewWidth = Math.Min(PreviewTargetWidth, region.Width);
            double scale = (double)previewWidth / region.Width;
            int previewHeightLimit = Math.Max(1, (int)Math.Round(heightLimit * scale));
            using var preview = progress is null ? null : new SKBitmap(previewWidth, previewHeightLimit,
                SKColorType.Bgra8888, SKAlphaType.Premul);
            using var previewCanvas = preview is null ? null : new SKCanvas(preview);
            var matcher = new ScrollingFrameMatcher(reference, seamY);
            int reportedPrefix = 0;

            void Report(string status, bool warning, bool includePreview)
            {
                if (progress is null)
                    return;

                BitmapSource? thumbnail = null;
                ScrollingPreviewUpdate? update = null;
                if (includePreview && preview is not null)
                {
                    DrawPreviewStrip(previewCanvas!, reference, seamY, totalHeight - bodyHeight, bodyHeight, scale, previewWidth);
                    int height = Math.Max(1, (int)Math.Round(totalHeight * scale));
                    int top = finishRequest is null ? 0 : Math.Min(reportedPrefix, height - 1);
                    int rows = height - top;
                    var pixels = BitmapSource.Create(previewWidth, rows, 96, 96, PixelFormats.Pbgra32, null,
                        preview.GetPixels() + top * preview.RowBytes, preview.RowBytes * rows, preview.RowBytes);
                    pixels.Freeze();
                    if (finishRequest is null)
                        thumbnail = pixels;
                    else
                        update = new ScrollingPreviewUpdate(pixels, top, height, previewHeightLimit);
                    reportedPrefix = (int)Math.Round(bodyHeight * scale);
                }
                progress.Report(new ScrollingCaptureProgress
                {
                    FramesCaptured = frameCount,
                    TotalHeightPixels = totalHeight,
                    StatusText = status,
                    IsWarning = warning,
                    RequiresPartialConfirmation = offerPartial,
                    PreviewThumbnail = thumbnail,
                    PreviewUpdate = update
                });
            }

            stitched.Append(reference, 0, seamY);
            if (previewCanvas is not null)
                DrawPreviewStrip(previewCanvas, reference, 0, seamY, 0, scale, previewWidth);
            Report(heightLimitReached ? HeightLimitStatus :
                "在蓝框内向下滚动，完成后点击「完成拼接」", heightLimitReached, true);
            long lastPreview = Stopwatch.GetTimestamp();

            using var timer = new PeriodicTimer(FrameInterval);
            try
            {
                while ((!heightLimitReached || finishRequest is not null) && await timer.WaitForNextTickAsync(stopToken))
                {
                    int request = finishRequest?.Take() ?? 0;
                    if (request == 2)
                        break;
                    if (request == 1 && finishStarted is null)
                    {
                        finishStarted = Stopwatch.GetTimestamp();
                        stableFinishFrames = 0;
                    }

                    SKBitmap? current = heightLimitReached ? null : CaptureFrame(reference);
                    if (current is not null)
                    {
                        try
                        {
                            var match = matcher.Match(current);
                            stableFinishFrames = finishStarted is not null && match.Unchanged ? stableFinishFrames + 1 : 0;
                            if (!match.Unchanged)
                                lastMovement = Stopwatch.GetTimestamp();

                            if (match.Shift > 0)
                            {
                                int sliceTop = seamY - match.Shift;
                                int sliceHeight = Math.Min(match.Shift, heightLimit - bodyHeight);
                                stitched.Append(current, sliceTop, sliceHeight);
                                if (previewCanvas is not null)
                                    DrawPreviewStrip(previewCanvas, current, sliceTop, sliceHeight, bodyHeight, scale, previewWidth);
                                bodyHeight += sliceHeight;
                                totalHeight = Math.Min(totalHeight + match.Shift, heightLimit);
                                frameCount++;

                                ReleaseFrame(reference);
                                reference = current;
                                current = null;
                                matcher.Accept();
                                firstUnmatched = null;
                                overlapWarning = null;
                                offerPartial = false;
                                previewPending = true;
                                heightLimitReached = totalHeight >= heightLimit;
                            }
                            else if (match.Unchanged)
                            {
                                bool recovered = overlapWarning is not null || offerPartial;
                                offerPartial = false;
                                if (recovered)
                                    Report("已回到拼接位置，可以继续向下滚动", false, false);
                                firstUnmatched = null;
                                overlapWarning = null;
                            }
                            else
                            {
                                firstUnmatched ??= Stopwatch.GetTimestamp();
                                if (overlapWarning is null && Stopwatch.GetElapsedTime(firstUnmatched.Value) >= LostOverlapDelay)
                                {
                                    overlapWarning = match.Shift < 0
                                        ? "已向上回看，滚回原来的拼接位置后可继续"
                                        : "画面暂时接不上，向上滚回刚才的位置后可继续";
                                    Report(overlapWarning, true, false);
                                }
                            }
                        }
                        finally
                        {
                            ReleaseFrame(current);
                        }
                    }
                    else if (!heightLimitReached)
                    {
                        stableFinishFrames = 0;
                        firstUnmatched ??= Stopwatch.GetTimestamp();
                        if (overlapWarning is null && Stopwatch.GetElapsedTime(firstUnmatched.Value) >= LostOverlapDelay)
                        {
                            overlapWarning = "暂时无法读取画面，恢复后可继续拼接";
                            Report(overlapWarning, true, false);
                        }
                    }

                    if (previewPending && (heightLimitReached || Stopwatch.GetElapsedTime(lastPreview) >= PreviewInterval))
                    {
                        Report(heightLimitReached ? HeightLimitStatus : overlapWarning ?? "正在拼接，继续向下滚动",
                            heightLimitReached || overlapWarning is not null, true);
                        previewPending = false;
                        lastPreview = Stopwatch.GetTimestamp();
                    }

                    if (finishStarted is { } started)
                    {
                        var elapsed = Stopwatch.GetElapsedTime(started);
                        if (heightLimitReached || (stableFinishFrames >= 2 && elapsed >= FinishSettleTime &&
                            Stopwatch.GetElapsedTime(lastMovement) >= FinishSettleTime))
                            break;
                        if (elapsed >= FinishTimeout)
                        {
                            if (firstUnmatched is null)
                                break;

                            finishStarted = null;
                            offerPartial = true;
                            overlapWarning ??= "当前画面没有接上。可以滚回上次位置，或保存已拼接部分";
                            Report(overlapWarning, true, false);
                        }
                    }

                    var period = finishStarted is null && Stopwatch.GetElapsedTime(lastMovement) >= IdleAfter
                        ? IdleFrameInterval : FrameInterval;
                    if (timer.Period != period)
                        timer.Period = period;
                }

                if (heightLimitReached && finishRequest is null)
                    await Task.Delay(Timeout.Infinite, stopToken);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                if (finishRequest is not null)
                    return null;
            }

            stitched.Append(reference, seamY, totalHeight - bodyHeight);
            return stitched.Complete();
        }
        finally
        {
            ReleaseFrame(reference);
        }
    }

    private static int ComputeSeamY(int frameHeight)
        => Math.Clamp((int)(frameHeight * 0.86), frameHeight / 2, frameHeight - 6);

    internal static int EstimateVerticalShift(SKBitmap previous, SKBitmap next)
        => Math.Max(0, new ScrollingFrameMatcher(previous, ComputeSeamY(previous.Height)).Match(next).Shift);

    private static void DrawPreviewStrip(
        SKCanvas canvas, SKBitmap source, int sourceTop, int height, int targetTop, double scale, int width)
    {
        int top = (int)Math.Round(targetTop * scale);
        int bottom = (int)Math.Round((targetTop + height) * scale);
        if (top < bottom)
        {
            canvas.DrawBitmap(source, new SKRect(0, sourceTop, source.Width, sourceTop + height),
                new SKRect(0, top, width, bottom), new SKSamplingOptions(SKFilterMode.Linear));
        }
    }
}
