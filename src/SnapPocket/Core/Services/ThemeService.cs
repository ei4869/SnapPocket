using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using SnapPocket.Core.Interop;
using SnapPocket.Core.Models;

namespace SnapPocket.Core.Services;

public sealed class ThemeService
{
    public bool IsDarkActive { get; private set; } = true;
    public event Action<bool>? ThemeChanged;

    public void ApplyTheme(Core.Models.ThemeMode mode)
    {
        bool useDark = mode switch
        {
            Core.Models.ThemeMode.Dark => true,
            Core.Models.ThemeMode.Light => false,
            _ => IsWindowsDarkThemeEnabled()
        };

        IsDarkActive = useDark;
        var app = Application.Current;
        if (app is null) return;

        var dictUri = new Uri(
            useDark ? "Resources/Themes/Dark.xaml" : "Resources/Themes/Light.xaml",
            UriKind.Relative);

        var merged = app.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source?.OriginalString ?? string.Empty;
            if (src.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase))
            {
                merged.RemoveAt(i);
            }
        }

        merged.Insert(0, new ResourceDictionary { Source = dictUri });

        foreach (Window win in app.Windows)
        {
            ApplyWindowChrome(win, useDark);
        }

        ThemeChanged?.Invoke(useDark);
    }

    public void ApplyWindowChrome(Window window, bool? darkOverride = null)
    {
        bool useDark = darkOverride ?? IsDarkActive;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int darkVal = useDark ? 1 : 0;
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkVal, Marshal.SizeOf<int>());

            int corner = NativeMethods.DWMWCP_ROUND;
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, Marshal.SizeOf<int>());
        }
        catch
        {
            // Older Windows 10 builds may ignore corner preference
        }
    }

    private static bool IsWindowsDarkThemeEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int val)
            {
                return val == 0;
            }
        }
        catch
        {
            // Default dark
        }
        return true;
    }
}
