using SkiaSharp;

namespace SnapPocket.Core.Models;

public sealed class AppContextInfo
{
    public IntPtr WindowHandle { get; init; }
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = "桌面 / 默认";
    public string WindowTitle { get; init; } = string.Empty;
    public bool IsDesktopContext { get; init; } = true;

    public static AppContextInfo Desktop => new()
    {
        WindowHandle = IntPtr.Zero,
        ProcessId = 0,
        ProcessName = string.Empty,
        DisplayName = "桌面 / 默认",
        WindowTitle = string.Empty,
        IsDesktopContext = true
    };
}

public sealed class ResolvedDirectoryInfo
{
    public string DirectoryPath { get; init; } = string.Empty;
    public bool IsAppSpecific { get; init; }
    public bool FellBackFromMissingDirectory { get; init; }
    public string? MissingDirectoryPath { get; init; }
}

public sealed class CaptureResult : IDisposable
{
    public required SKBitmap Bitmap { get; set; }
    public CaptureMode Mode { get; init; }
    public AppContextInfo SourceContext { get; init; } = AppContextInfo.Desktop;
    public DateTime CapturedAt { get; init; } = DateTime.Now;
    public System.Drawing.Rectangle ScreenBoundsPixels { get; init; }

    public void Dispose()
    {
        Bitmap.Dispose();
    }
}

public sealed class SaveOutcome
{
    public bool Success { get; init; }
    public bool ClipboardCopied { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string FileName => string.IsNullOrEmpty(FilePath) ? string.Empty : Path.GetFileName(FilePath);
    public string DirectoryPath => string.IsNullOrEmpty(FilePath) ? string.Empty : Path.GetDirectoryName(FilePath) ?? string.Empty;
    public bool FellBackFromMissingDirectory { get; init; }
    public string? WarningMessage { get; init; }
    public string? ErrorMessage { get; init; }
}
