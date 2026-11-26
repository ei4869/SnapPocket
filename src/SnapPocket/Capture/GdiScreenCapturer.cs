using System.Drawing;
using System.Drawing.Imaging;
using SkiaSharp;
using SnapPocket.Core.Interop;

namespace SnapPocket.Capture;

public sealed class GdiScreenCapturer : IScreenCapturer
{
    public SKBitmap? CaptureRect(Rectangle physicalBounds)
    {
        if (physicalBounds.Width <= 0 || physicalBounds.Height <= 0)
            return null;

        try
        {
            using var gdiBmp = new Bitmap(physicalBounds.Width, physicalBounds.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(gdiBmp))
                CopyScreen(g, physicalBounds);

            return ConvertGdiToSkia(gdiBmp);
        }
        catch
        {
            return null;
        }
    }

    public SKBitmap? CaptureMonitorForPoint(Point physicalPoint, out Rectangle bounds)
    {
        bounds = NativeMethods.GetMonitorBoundsForPoint(new NativeMethods.POINT { X = physicalPoint.X, Y = physicalPoint.Y });
        return CaptureRect(bounds);
    }

    public SKBitmap? CaptureVirtualScreen(out Rectangle bounds)
    {
        bounds = NativeMethods.GetVirtualScreenBounds();
        return CaptureRect(bounds);
    }

    public SKBitmap? CaptureWindow(IntPtr hWnd, out Rectangle bounds)
    {
        bounds = NativeMethods.GetExtendedWindowBounds(hWnd);
        var virtualBounds = NativeMethods.GetVirtualScreenBounds();
        bounds = Rectangle.Intersect(bounds, virtualBounds);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        return CaptureRect(bounds);
    }

    internal static unsafe SKBitmap ConvertGdiToSkia(Bitmap gdiBmp)
    {
        var skBmp = new SKBitmap(gdiBmp.Width, gdiBmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        try
        {
            CopyPixels(gdiBmp, skBmp);
            return skBmp;
        }
        catch
        {
            skBmp.Dispose();
            throw;
        }
    }

    private static void CopyScreen(Graphics graphics, Rectangle bounds)
        => graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);

    private static unsafe void CopyPixels(Bitmap gdiBmp, SKBitmap skBmp)
    {
        var rect = new Rectangle(0, 0, gdiBmp.Width, gdiBmp.Height);
        var bmpData = gdiBmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            byte* src = (byte*)bmpData.Scan0;
            byte* dst = (byte*)skBmp.GetPixels();
            int copyWidth = gdiBmp.Width * 4;
            int srcStride = bmpData.Stride;
            int dstStride = skBmp.RowBytes;

            if (copyWidth == srcStride && srcStride == dstStride)
            {
                long length = (long)srcStride * gdiBmp.Height;
                Buffer.MemoryCopy(src, dst, length, length);
            }
            else
                for (int y = 0; y < gdiBmp.Height; y++)
                    Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * dstStride, dstStride, copyWidth);
        }
        finally
        {
            gdiBmp.UnlockBits(bmpData);
        }

        skBmp.NotifyPixelsChanged();
    }

    internal sealed class RegionCaptureSession : IDisposable
    {
        private readonly Rectangle _bounds;
        private readonly Bitmap _bitmap;
        private readonly Graphics _graphics;

        private RegionCaptureSession(Rectangle bounds)
        {
            _bounds = bounds;
            _bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            try { _graphics = Graphics.FromImage(_bitmap); }
            catch
            {
                _bitmap.Dispose();
                throw;
            }
        }

        public static RegionCaptureSession? TryCreate(Rectangle bounds)
        {
            try { return new RegionCaptureSession(bounds); }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException)
            {
                return null;
            }
        }

        public bool TryCapture(SKBitmap target)
        {
            try
            {
                CopyScreen(_graphics, _bounds);
                CopyPixels(_bitmap, target);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or InvalidOperationException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            _graphics.Dispose();
            _bitmap.Dispose();
        }
    }
}
