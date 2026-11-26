# SnapPocket

[简体中文](README.md)

SnapPocket is a portable Windows screenshot tool. Use keyboard shortcuts to capture a region, the full screen, or the active window. You can also stitch a scrolling page into one image or pin a screenshot above other windows. SnapPocket remembers a separate save folder for each app or game.

## Download and get started

1. Download `SnapPocket-win-x64.zip` from [GitHub Releases](https://github.com/ei4869/SnapPocket/releases/latest). The ZIP includes the runtime and runs on 64-bit Windows 10 and Windows 11.
2. Extract the ZIP to a folder you can write to, then run `SnapPocket.exe`. The app appears in the system tray. The interface is in Simplified Chinese.
3. Press `Ctrl + Shift + 1` and drag to select an area. Regular screenshots are saved under **Pictures → SnapPocket** by default and copied to the clipboard.

Press `Ctrl + Shift + 6` to open the floating panel. To set a save folder for an app, switch to that app first and then choose a folder in the panel. You can also enable annotation after capture (**截屏后快速标注**) or custom naming (**截屏后自定义命名**). The editor includes arrows, text, step numbers, pixelation, and blur.

## Default shortcuts

| Action | Shortcut |
| --- | --- |
| Capture a region | `Ctrl + Shift + 1` |
| Capture the full screen | `Ctrl + Shift + 2` |
| Capture the active window | `Ctrl + Shift + 3` |
| Capture and pin an image on top | `Ctrl + Shift + 4` |
| Capture a scrolling area | `Ctrl + Shift + 5` |
| Open or hide the floating panel | `Ctrl + Shift + 6` |

For a scrolling capture, select the content, keep the pointer inside the blue frame, and scroll down. Check the preview, then select **完成拼接** to finish. Open **详细设置** from the tray menu to change shortcuts, save folders, image formats, and other options.

## Build from source

Use the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows.

```powershell
dotnet test SnapPocket.sln -c Release
.\scripts\publish-portable.ps1 -SelfContained
```

The self-contained executable is built under `outputs/SnapPocket-win-x64-standalone/`.
