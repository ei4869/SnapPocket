using System.ComponentModel;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace SnapPocket.Capture;

internal sealed class ScrollingImageBuffer : IDisposable
{
    private const uint Commit = 0x1000;
    private const uint Reserve = 0x2000;
    private const uint Release = 0x8000;
    private const uint ReadWrite = 0x04;

    private readonly int _width;
    private readonly int _limit;
    private readonly int _stride;
    private IntPtr _pixels;
    private long _committed;
    private bool _transferred;

    public ScrollingImageBuffer(int width, int heightLimit)
    {
        _width = width;
        _limit = heightLimit;
        _stride = checked(width * 4);
        // Reserve address space once and only commit the pages that contain accepted rows.
        _pixels = VirtualAlloc(IntPtr.Zero, checked((nuint)((long)_stride * heightLimit)), Reserve, ReadWrite);
        if (_pixels == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法为长截图预留像素缓冲。");
    }

    public int Height { get; private set; }

    public void Append(SKBitmap source, int top, int rows)
    {
        if (rows == 0)
            return;

        int nextHeight = checked(Height + rows);
        if (_transferred || rows < 0 || nextHeight > _limit || source.Width != _width || top < 0 || rows > source.Height - top)
            throw new InvalidOperationException("长截图区段尺寸无效。");

        long bytes = (long)nextHeight * _stride;
        if (bytes > _committed)
        {
            long pageSize = Environment.SystemPageSize;
            long end = (bytes + pageSize - 1) / pageSize * pageSize;
            if (VirtualAlloc(_pixels + (nint)_committed, (nuint)(end - _committed), Commit, ReadWrite) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法为长截图分配像素缓冲。");
            _committed = end;
        }

        using var pixels = source.PeekPixels();
        var info = new SKImageInfo(_width, rows, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (!pixels.ReadPixels(info, _pixels + (nint)((long)Height * _stride), _stride, 0, top))
            throw new InvalidOperationException("无法追加长截图像素。");
        Height = nextHeight;
    }

    public SKBitmap Complete()
    {
        if (_transferred || Height == 0)
            throw new InvalidOperationException("长截图缓冲无法生成图像。");

        var result = new SKBitmap();
        var info = new SKImageInfo(_width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        try
        {
            if (!result.InstallPixels(info, _pixels, _stride, static (_, state) => ((ScrollingImageBuffer)state!).ReleasePixels(), this))
                throw new InvalidOperationException("无法生成长截图。");
            _transferred = true;
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private void ReleasePixels()
    {
        var pixels = Interlocked.Exchange(ref _pixels, IntPtr.Zero);
        if (pixels != IntPtr.Zero)
            VirtualFree(pixels, 0, Release);
    }

    public void Dispose()
    {
        if (!_transferred)
            ReleasePixels();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint allocationType, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(IntPtr address, nuint size, uint freeType);
}
