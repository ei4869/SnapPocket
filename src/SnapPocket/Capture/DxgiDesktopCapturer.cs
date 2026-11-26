using System.Drawing;
using SkiaSharp;
using SnapPocket.Core.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SnapPocket.Capture;

/// <summary>
/// Captures desktop & exclusive fullscreen games via DXGI Desktop Duplication (zero DLL injection).
/// Automatically falls back to GDI if DXGI output duplication is unavailable or spans multiple outputs.
/// </summary>
public sealed class DxgiDesktopCapturer : IScreenCapturer
{
    private readonly GdiScreenCapturer _gdiFallback = new();

    public SKBitmap? CaptureRect(Rectangle physicalBounds)
    {
        if (physicalBounds.Width <= 0 || physicalBounds.Height <= 0)
            return null;

        // Try DXGI when the rectangle resides on a single DXGI output
        var dxgi = TryCaptureSingleOutputDxgi(physicalBounds);
        if (dxgi is not null)
            return dxgi;

        return _gdiFallback.CaptureRect(physicalBounds);
    }

    public SKBitmap? CaptureMonitorForPoint(Point physicalPoint, out Rectangle bounds)
    {
        bounds = NativeMethods.GetMonitorBoundsForPoint(new NativeMethods.POINT { X = physicalPoint.X, Y = physicalPoint.Y });
        return CaptureRect(bounds);
    }

    public SKBitmap? CaptureVirtualScreen(out Rectangle bounds)
    {
        bounds = NativeMethods.GetVirtualScreenBounds();
        // Virtual screen often spans multiple GPUs/monitors; try DXGI first if single monitor, else GDI
        var dxgi = TryCaptureSingleOutputDxgi(bounds);
        return dxgi ?? _gdiFallback.CaptureRect(bounds);
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

    private static unsafe SKBitmap? TryCaptureSingleOutputDxgi(Rectangle targetRect)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
            {
                using (adapter)
                {
                    for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
                    {
                        using (output)
                        {
                            var desc = output.Description;
                            var outputRect = new Rectangle(
                                desc.DesktopCoordinates.Left,
                                desc.DesktopCoordinates.Top,
                                desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                                desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top);

                            if (!outputRect.Contains(targetRect))
                                continue;

                            using var output1 = output.QueryInterface<IDXGIOutput1>();
                            var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
                            var hr = D3D11.D3D11CreateDevice(
                                adapter,
                                DriverType.Unknown,
                                DeviceCreationFlags.BgraSupport,
                                featureLevels,
                                out ID3D11Device? device,
                                out ID3D11DeviceContext? context);

                            if (hr.Failure || device is null || context is null)
                                continue;

                            using (device)
                            using (context)
                            using (var duplication = output1.DuplicateOutput(device))
                            {
                                // Acquire frame; if first frame is an unpopulated init frame, retry briefly
                                SKBitmap? fallbackFirstFrame = null;
                                for (int attempt = 0; attempt < 3; attempt++)
                                {
                                    var acquireResult = duplication.AcquireNextFrame(100, out var frameInfo, out var desktopResource);
                                    if (acquireResult.Failure)
                                    {
                                        if (fallbackFirstFrame is not null)
                                            return fallbackFirstFrame;
                                        Thread.Sleep(20);
                                        continue;
                                    }

                                    using (desktopResource)
                                    {
                                        try
                                        {
                                            using var gpuTex = desktopResource.QueryInterface<ID3D11Texture2D>();
                                            var texDesc = gpuTex.Description;
                                            var stagingDesc = new Texture2DDescription
                                            {
                                                Width = texDesc.Width,
                                                Height = texDesc.Height,
                                                MipLevels = 1,
                                                ArraySize = 1,
                                                Format = texDesc.Format,
                                                SampleDescription = new SampleDescription(1, 0),
                                                Usage = ResourceUsage.Staging,
                                                BindFlags = BindFlags.None,
                                                CPUAccessFlags = CpuAccessFlags.Read,
                                                MiscFlags = ResourceOptionFlags.None
                                            };

                                            using var stagingTex = device.CreateTexture2D(stagingDesc);
                                            context.CopyResource(stagingTex, gpuTex);

                                            var mapped = context.Map(stagingTex, 0, MapMode.Read);
                                            try
                                            {
                                                int offsetX = targetRect.Left - outputRect.Left;
                                                int offsetY = targetRect.Top - outputRect.Top;
                                                int copyW = Math.Min(targetRect.Width, (int)texDesc.Width - offsetX);
                                                int copyH = Math.Min(targetRect.Height, (int)texDesc.Height - offsetY);

                                                if (copyW <= 0 || copyH <= 0)
                                                    return null;

                                                var result = new SKBitmap(copyW, copyH, SKColorType.Bgra8888, SKAlphaType.Opaque);
                                                byte* srcBase = (byte*)mapped.DataPointer;
                                                byte* dstBase = (byte*)result.GetPixels();
                                                int srcStride = (int)mapped.RowPitch;
                                                int dstStride = result.RowBytes;
                                                int bytesPerRow = copyW * 4;

                                                for (int y = 0; y < copyH; y++)
                                                {
                                                    byte* srcRow = srcBase + (long)(offsetY + y) * srcStride + (long)offsetX * 4;
                                                    byte* dstRow = dstBase + (long)y * dstStride;
                                                    Buffer.MemoryCopy(srcRow, dstRow, dstStride, bytesPerRow);
                                                    // Ensure alpha channel is 255 (DXGI desktop surface often leaves alpha 0)
                                                    for (int x = 3; x < bytesPerRow; x += 4)
                                                    {
                                                        dstRow[x] = 255;
                                                    }
                                                }

                                                if (attempt == 0 && frameInfo.LastPresentTime == 0 && frameInfo.AccumulatedFrames == 0)
                                                {
                                                    // Keep this frame in case desktop is static and next acquire times out
                                                    fallbackFirstFrame?.Dispose();
                                                    fallbackFirstFrame = result;
                                                    continue;
                                                }

                                                fallbackFirstFrame?.Dispose();
                                                return result;
                                            }
                                            finally
                                            {
                                                context.Unmap(stagingTex, 0);
                                            }
                                        }
                                        finally
                                        {
                                            duplication.ReleaseFrame();
                                        }
                                    }
                                }

                                if (fallbackFirstFrame is not null)
                                    return fallbackFirstFrame;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall back cleanly to GDI
        }

        return null;
    }
}
