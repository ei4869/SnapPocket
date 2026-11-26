using System.Text.Json.Serialization;
using SnapPocket.Annotation.Models;

namespace SnapPocket.Core.Models;

public enum DockEdge
{
    Right,
    Left
}

public enum ThemeMode
{
    FollowSystem,
    Dark,
    Light
}

public enum ImageFormatType
{
    Png,
    Jpg,
    WebP
}

public enum FullscreenScope
{
    CurrentMonitor,
    AllMonitors
}

public enum CaptureMode
{
    Region,
    Fullscreen,
    ActiveWindow,
    Pin,
    Scrolling
}

public enum HotkeyAction
{
    RegionCapture,
    FullscreenCapture,
    ActiveWindowCapture,
    PinCapture,
    ScrollingCapture,
    ToggleDockPanel,
    ToggleAnnotationMode,
    ToggleCustomRename
}

public sealed class HotkeyConfig
{
    public HotkeyAction Action { get; set; }
    public uint Modifiers { get; set; }
    public uint VirtualKey { get; set; }
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public bool HasConflict { get; set; }

    [JsonIgnore]
    public string? ConflictMessage { get; set; }
}

public sealed class AppDirectoryRule
{
    public string ProcessName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AppConfig
{
    public string DefaultSaveDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "SnapPocket");

    public List<AppDirectoryRule> AppDirectories { get; set; } = [];
    public List<string> RecentDirectories { get; set; } = [];

    public bool EnableAnnotationAfterCapture { get; set; } = false;
    public bool EnableCustomRenameAfterCapture { get; set; } = false;
    public Dictionary<AnnotationTool, AnnotationStyle> AnnotationPresets { get; set; } = [];

    public ImageFormatType ImageFormat { get; set; } = ImageFormatType.Png;
    public int ImageQuality { get; set; } = 92;
    public string FileNameTemplate { get; set; } = "{app}_{yyyyMMdd_HHmmss}";
    public FullscreenScope FullscreenScope { get; set; } = FullscreenScope.CurrentMonitor;

    public DockEdge DockEdge { get; set; } = DockEdge.Right;
    public double DockVerticalRatio { get; set; } = 0.42;
    public int DockEdgeTriggerPixels { get; set; } = 10;
    public int DockAutoHideDelayMs { get; set; } = 650;
    public bool DockCompletelyHideWhenIdle { get; set; } = true;

    public bool ShowToastNotification { get; set; } = true;
    public bool PlaySoundOnCapture { get; set; } = false;
    public bool LaunchAtStartup { get; set; } = false;
    public ThemeMode ThemeMode { get; set; } = ThemeMode.FollowSystem;

    public List<HotkeyConfig> Hotkeys { get; set; } = CreateDefaultHotkeys();

    public static List<HotkeyConfig> CreateDefaultHotkeys()
    {
        // MOD_CONTROL (0x0002) | MOD_SHIFT (0x0004) = 0x0006
        const uint ctrlShift = 0x0006;
        return
        [
            new HotkeyConfig { Action = HotkeyAction.RegionCapture, Modifiers = ctrlShift, VirtualKey = (uint)'1', Enabled = true },
            new HotkeyConfig { Action = HotkeyAction.FullscreenCapture, Modifiers = ctrlShift, VirtualKey = (uint)'2', Enabled = true },
            new HotkeyConfig { Action = HotkeyAction.ActiveWindowCapture, Modifiers = ctrlShift, VirtualKey = (uint)'3', Enabled = true },
            new HotkeyConfig { Action = HotkeyAction.PinCapture, Modifiers = ctrlShift, VirtualKey = (uint)'4', Enabled = true },
            new HotkeyConfig { Action = HotkeyAction.ScrollingCapture, Modifiers = ctrlShift, VirtualKey = (uint)'5', Enabled = true },
            new HotkeyConfig { Action = HotkeyAction.ToggleDockPanel, Modifiers = ctrlShift, VirtualKey = (uint)'6', Enabled = true },
            new HotkeyConfig { Action = HotkeyAction.ToggleAnnotationMode, Modifiers = 0, VirtualKey = 0, Enabled = false },
            new HotkeyConfig { Action = HotkeyAction.ToggleCustomRename, Modifiers = 0, VirtualKey = 0, Enabled = false }
        ];
    }
}
