using SkiaSharp;

namespace SnapPocket.Capture;

public interface IScreenCapturer
{
    SKBitmap? CaptureRect(System.Drawing.Rectangle physicalBounds);
    SKBitmap? CaptureMonitorForPoint(System.Drawing.Point physicalPoint, out System.Drawing.Rectangle bounds);
    SKBitmap? CaptureVirtualScreen(out System.Drawing.Rectangle bounds);
    SKBitmap? CaptureWindow(IntPtr hWnd, out System.Drawing.Rectangle bounds);
}
