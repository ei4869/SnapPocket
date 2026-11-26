using System.Runtime.InteropServices;

namespace SnapPocket.Core.Interop;

public sealed record SnapCandidate(IntPtr Handle, System.Drawing.Rectangle Bounds, string Title);

public static class WindowSnapHelper
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow",
        "ApplicationFrameInputSinkWindow"
    };

    public static List<SnapCandidate> EnumerateVisibleWindows(uint excludeProcessId)
    {
        var results = new List<SnapCandidate>();
        var virtualBounds = NativeMethods.GetVirtualScreenBounds();

        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd))
                return true;

            _ = NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == excludeProcessId)
                return true;

            int cloaked = 0;
            if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out cloaked, Marshal.SizeOf<int>()) == 0 && cloaked != 0)
                return true;

            var cls = NativeMethods.GetWindowClassName(hWnd);
            if (IgnoredClasses.Contains(cls))
                return true;

            var rect = NativeMethods.GetExtendedWindowBounds(hWnd);
            if (rect.Width < 24 || rect.Height < 24)
                return true;

            rect = System.Drawing.Rectangle.Intersect(rect, virtualBounds);
            if (rect.Width < 24 || rect.Height < 24)
                return true;

            var title = NativeMethods.GetWindowTitleText(hWnd);
            results.Add(new SnapCandidate(hWnd, rect, title));
            return true;
        }, IntPtr.Zero);

        return results;
    }

    public static SnapCandidate? FindTopmostCandidateAt(List<SnapCandidate> candidates, int screenX, int screenY)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Bounds.Contains(screenX, screenY))
                return candidate;
        }
        return null;
    }
}
