using System.Windows;
using System.Windows.Interop;
using SnapPocket.Core.Interop;

namespace SnapPocket.UI.Capture;

/// <summary>
/// Click-through highlight frame rendered strictly outside the scrolling capture region
/// so the user can freely scroll with mouse wheel inside the box without polluting captured pixels.
/// </summary>
public partial class ScrollingRegionFrameWindow : Window
{
    private const int BorderPadPx = 4;
    private readonly System.Drawing.Rectangle _region;

    public ScrollingRegionFrameWindow(System.Drawing.Rectangle captureRegion)
    {
        InitializeComponent();
        _region = captureRegion;
        DpiChanged += (_, e) => SetBorderThickness(e.NewDpi.DpiScaleX, e.NewDpi.DpiScaleY);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        SetBorderThickness(transform?.M11 ?? 1, transform?.M22 ?? 1);

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            _region.Left - BorderPadPx,
            _region.Top - BorderPadPx,
            _region.Width + BorderPadPx * 2,
            _region.Height + BorderPadPx * 2,
            NativeMethods.SWP_NOACTIVATE);
    }

    private void SetBorderThickness(double scaleX, double scaleY)
        => RegionBorder.BorderThickness = new Thickness(3 / scaleX, 3 / scaleY, 3 / scaleX, 3 / scaleY);
}
