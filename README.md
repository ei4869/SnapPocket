# SnapPocket

[English](README.en.md)

SnapPocket 是一款便携的 Windows 截图工具。用快捷键截取区域、全屏或窗口，也能把滚动页面拼成长图，或把截图贴在屏幕最上方。它会记住每个应用或游戏的截图保存位置，方便把图片分开整理。

## 下载和使用

1. 在 [GitHub Releases](https://github.com/ei4869/SnapPocket/releases/latest) 下载 `SnapPocket-win-x64.zip`。压缩包适用于 64 位 Windows 10 和 Windows 11，自带运行时。
2. 解压到一个有写入权限的文件夹，双击 `SnapPocket.exe`。启动后可以在系统托盘找到它。
3. 按 `Ctrl + Shift + 1`，拖动鼠标选取截图区域。普通截图默认保存到「图片」下的 `SnapPocket` 文件夹，并复制到剪贴板。

按 `Ctrl + Shift + 6` 打开悬浮面板。想让某个应用的截图保存到另一个文件夹，先切到该应用，再在面板里选择目录。面板也能开启「截屏后快速标注」和「截屏后自定义命名」。快速标注支持箭头、文字、步骤编号、马赛克和模糊等工具。

## 默认快捷键

| 功能 | 快捷键 |
| --- | --- |
| 区域截图 | `Ctrl + Shift + 1` |
| 全屏截图 | `Ctrl + Shift + 2` |
| 活动窗口截图 | `Ctrl + Shift + 3` |
| 截图并贴图置顶 | `Ctrl + Shift + 4` |
| 滚动长截图 | `Ctrl + Shift + 5` |
| 打开或收起悬浮面板 | `Ctrl + Shift + 6` |

滚动截图时，先框选内容，把鼠标留在蓝框内向下滚动，确认预览后点「完成拼接」。在托盘菜单的「详细设置」里可以修改快捷键、保存目录、图片格式和其他选项。

## 从源码构建

在 Windows 上使用 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```powershell
dotnet test SnapPocket.sln -c Release
.\scripts\publish-portable.ps1 -SelfContained
```

自包含程序输出到 `outputs/SnapPocket-win-x64-standalone/`。
