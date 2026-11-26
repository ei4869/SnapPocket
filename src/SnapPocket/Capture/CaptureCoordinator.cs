using System.Windows;
using SkiaSharp;
using SnapPocket.Core.Interop;
using SnapPocket.Core.Models;
using SnapPocket.Core.Services;
using SnapPocket.UI.Annotation;
using SnapPocket.UI.Capture;
using SnapPocket.UI.Dock;

namespace SnapPocket.Capture;

public sealed class CaptureCoordinator
{
    private readonly ConfigService _configService;
    private readonly ForegroundTrackerService _foregroundTracker;
    private readonly DirectoryMemoryService _directoryMemory;
    private readonly StorageService _storageService;
    private readonly IScreenCapturer _capturer;
    private readonly ScrollingCaptureEngine _scrollingEngine;
    private readonly FloatingDockWindow _dockWindow;
    private readonly Func<ToastNotificationWindow> _getToastWindow;
    private readonly ThemeService _themeService;
    private readonly List<PinWindow> _openPins = [];

    private bool _isBusy;

    public CaptureCoordinator(
        ConfigService configService,
        ForegroundTrackerService foregroundTracker,
        DirectoryMemoryService directoryMemory,
        StorageService storageService,
        IScreenCapturer capturer,
        FloatingDockWindow dockWindow,
        Func<ToastNotificationWindow> getToastWindow,
        ThemeService themeService)
    {
        _configService = configService;
        _foregroundTracker = foregroundTracker;
        _directoryMemory = directoryMemory;
        _storageService = storageService;
        _capturer = capturer;
        _scrollingEngine = new ScrollingCaptureEngine();
        _dockWindow = dockWindow;
        _getToastWindow = getToastWindow;
        _themeService = themeService;
    }

    public async void HandleHotkeyAction(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleDockPanel:
                _dockWindow.ToggleExpanded();
                return;

            case HotkeyAction.ToggleAnnotationMode:
                _configService.Config.EnableAnnotationAfterCapture = !_configService.Config.EnableAnnotationAfterCapture;
                if (!_configService.Save())
                    return;
                ShowInfoToast("快速标注已" + (_configService.Config.EnableAnnotationAfterCapture ? "开启" : "关闭"),
                    _configService.Config.EnableAnnotationAfterCapture ? "截屏后将进入矢量快速标注界面" : "截屏后将直接静默保存");
                return;

            case HotkeyAction.ToggleCustomRename:
                _configService.Config.EnableCustomRenameAfterCapture = !_configService.Config.EnableCustomRenameAfterCapture;
                if (!_configService.Save())
                    return;
                ShowInfoToast("截屏后命名已" + (_configService.Config.EnableCustomRenameAfterCapture ? "开启" : "关闭"),
                    _configService.Config.EnableCustomRenameAfterCapture ? "截屏保存前将在上方弹出命名框" : "截屏将按模板自动命名");
                return;
        }

        if (_isBusy) return;

        try
        {
            switch (action)
            {
                case HotkeyAction.RegionCapture: await ExecuteRegionCaptureAsync(forPin: false); break;
                case HotkeyAction.FullscreenCapture: await ExecuteFullscreenCaptureAsync(); break;
                case HotkeyAction.ActiveWindowCapture: await ExecuteActiveWindowCaptureAsync(); break;
                case HotkeyAction.PinCapture: await ExecuteRegionCaptureAsync(forPin: true); break;
                case HotkeyAction.ScrollingCapture: await ExecuteScrollingCaptureAsync(); break;
            }
        }
        catch (Exception ex)
        {
            _dockWindow.ResumeAfterCapture();
            ShowErrorToast("截图未完成", ex.Message);
        }
    }

    private async Task ExecuteFullscreenCaptureAsync()
    {
        _isBusy = true;
        try
        {
            var context = _foregroundTracker.CaptureFreshContext();
            _dockWindow.SuspendForCapture();
            await Task.Delay(35);

            SKBitmap? bitmap;
            if (_configService.Config.FullscreenScope == FullscreenScope.AllMonitors)
            {
                bitmap = await Task.Run(() => _capturer.CaptureVirtualScreen(out _));
            }
            else
            {
                System.Drawing.Point targetPoint;
                if (!context.IsDesktopContext && context.WindowHandle != IntPtr.Zero)
                {
                    var winRect = NativeMethods.GetExtendedWindowBounds(context.WindowHandle);
                    targetPoint = new System.Drawing.Point(winRect.Left + winRect.Width / 2, winRect.Top + winRect.Height / 2);
                }
                else if (NativeMethods.GetCursorPos(out var cursor))
                {
                    targetPoint = new System.Drawing.Point(cursor.X, cursor.Y);
                }
                else
                {
                    targetPoint = new System.Drawing.Point(0, 0);
                }

                bitmap = await Task.Run(() => _capturer.CaptureMonitorForPoint(targetPoint, out _));
            }

            _dockWindow.ResumeAfterCapture();

            if (bitmap is null)
            {
                ShowErrorToast("全屏截屏失败", "无法读取当前屏幕图像");
                return;
            }

            using (bitmap)
            {
                await ProcessCapturedBitmapAsync(bitmap, context, CaptureMode.Fullscreen);
            }
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task ExecuteActiveWindowCaptureAsync()
    {
        _isBusy = true;
        try
        {
            var context = _foregroundTracker.CaptureFreshContext();
            _dockWindow.SuspendForCapture();
            await Task.Delay(35);

            var bitmap = await Task.Run(() => TryCaptureActiveWindow(_capturer, context));

            _dockWindow.ResumeAfterCapture();

            if (bitmap is null)
            {
                ShowErrorToast("窗口截屏失败", context.IsDesktopContext
                    ? "请先切换到目标窗口，截取桌面可使用全屏快捷键"
                    : "未能获取当前活动窗口画面，请切换到目标窗口后重试");
                return;
            }

            using (bitmap)
            {
                await ProcessCapturedBitmapAsync(bitmap, context, CaptureMode.ActiveWindow);
            }
        }
        finally
        {
            _isBusy = false;
        }
    }

    internal static SKBitmap? TryCaptureActiveWindow(IScreenCapturer capturer, AppContextInfo context)
        => context.IsDesktopContext || context.WindowHandle == IntPtr.Zero
            ? null : capturer.CaptureWindow(context.WindowHandle, out _);

    private async Task ExecuteRegionCaptureAsync(bool forPin)
    {
        _isBusy = true;
        try
        {
            var context = _foregroundTracker.CaptureFreshContext();
            var candidates = WindowSnapHelper.EnumerateVisibleWindows((uint)Environment.ProcessId);

            _dockWindow.SuspendForCapture();
            await Task.Delay(35);

            var (captured, virtualBounds) = await CaptureDesktopAsync();
            using var snapshot = captured;
            if (snapshot is null)
            {
                _dockWindow.ResumeAfterCapture();
                ShowErrorToast("区域截屏失败", "无法捕获当前屏幕画面");
                return;
            }

            var hint = forPin
                ? "拖拽框选或单击窗口以生成置顶贴图 · 右键/Esc 取消"
                : "拖拽框选区域 · 单击吸附窗口 · 右键/Esc 取消";

            var selector = new RegionCaptureWindow(snapshot, virtualBounds, candidates, hint);
            bool? ok = selector.ShowDialog();

            _dockWindow.ResumeAfterCapture();

            if (ok != true || selector.CroppedBitmap is null)
                return;

            using var cropped = selector.CroppedBitmap;
            if (forPin)
            {
                SpawnPinWindow(cropped, context, selector.SelectedPhysicalRect);
            }
            else
            {
                await ProcessCapturedBitmapAsync(cropped, context, CaptureMode.Region, selector.SelectedPhysicalRect);
            }
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task ExecuteScrollingCaptureAsync()
    {
        _isBusy = true;
        try
        {
            var context = _foregroundTracker.CaptureFreshContext();
            var candidates = WindowSnapHelper.EnumerateVisibleWindows((uint)Environment.ProcessId);

            _dockWindow.SuspendForCapture();
            await Task.Delay(35);

            System.Drawing.Rectangle region;
            SKBitmap? selectedBitmap;
            var (captured, virtualBounds) = await CaptureDesktopAsync();
            using (var snapshot = captured)
            {
                if (snapshot is null)
                {
                    _dockWindow.ResumeAfterCapture();
                    ShowErrorToast("滚动长截图失败", "无法初始化屏幕画面");
                    return;
                }

                var selector = new RegionCaptureWindow(
                    snapshot,
                    virtualBounds,
                    candidates,
                    "框选滚动区域 · 在蓝框内向下滚动 · 右键/Esc 取消框选")
                {
                    Title = "SnapPocket 滚动截图选区"
                };

                bool? ok = selector.ShowDialog();
                region = selector.SelectedPhysicalRect;
                selectedBitmap = selector.CroppedBitmap;
                if (ok != true || region.Width < 30 || region.Height < 40)
                {
                    selectedBitmap?.Dispose();
                    _dockWindow.ResumeAfterCapture();
                    if (ok == true)
                        ShowErrorToast("滚动选区太小", "请框选至少 30 × 40 像素的区域");
                    return;
                }
            }

            using var initialFrame = selectedBitmap;
            using var cts = new CancellationTokenSource();
            var finishRequest = new ScrollingFinishRequest();
            var frameOverlay = new ScrollingRegionFrameWindow(region);
            var previewHud = new ScrollingPreviewWindow(region);
            bool previewClosed = false;
            previewHud.FinishRequested += finishRequest.Finish;
            previewHud.CancelRequested += cts.Cancel;
            previewHud.Closed += (_, _) =>
            {
                previewClosed = true;
                cts.Cancel();
            };

            var progress = new Progress<ScrollingCaptureProgress>(p => previewHud.UpdateProgress(p));
            SKBitmap? stitched = null;
            try
            {
                frameOverlay.Show();
                previewHud.Show();
                stitched = await _scrollingEngine.CaptureWithFinishRequestAsync(region, progress, cts.Token, initialFrame, finishRequest);
                if (cts.IsCancellationRequested)
                {
                    stitched?.Dispose();
                    stitched = null;
                }
                if (stitched is null && !cts.IsCancellationRequested)
                    ShowErrorToast("滚动截图失败", "无法读取选区画面");
            }
            finally
            {
                frameOverlay.Close();
                if (!previewClosed)
                    previewHud.Close();
                _dockWindow.ResumeAfterCapture();
            }

            if (stitched is null)
                return;

            using (stitched)
            {
                await ProcessCapturedBitmapAsync(stitched, context, CaptureMode.Scrolling, region);
            }
        }
        finally
        {
            _isBusy = false;
        }
    }

    private Task<(SKBitmap? Bitmap, System.Drawing.Rectangle Bounds)> CaptureDesktopAsync() => Task.Run(() =>
    {
        var bitmap = _capturer.CaptureVirtualScreen(out var bounds);
        return (bitmap, bounds);
    });

    private async Task ProcessCapturedBitmapAsync(
        SKBitmap initialBitmap,
        AppContextInfo context,
        CaptureMode mode,
        System.Drawing.Rectangle? sourceRect = null)
    {
        SKBitmap workingBitmap = initialBitmap;
        try
        {
            var (appDirectory, defaultDirectory) = _directoryMemory.GetSaveDirectories(context);
            string targetDirectory = appDirectory ?? defaultDirectory;

            // Step 1: Quick Annotation (unified global toggle)
            if (_configService.Config.EnableAnnotationAfterCapture)
            {
                var editor = new AnnotationWindow(workingBitmap, targetDirectory, _configService);
                editor.SourceInitialized += (_, _) => _themeService.ApplyWindowChrome(editor);
                bool? editorOk = editor.ShowDialog();

                if (editorOk != true || editor.FinalFlattenedBitmap is null || editor.ResultAction == AnnotationResultAction.Cancel)
                {
                    editor.FinalFlattenedBitmap?.Dispose();
                    return;
                }

                workingBitmap = editor.FinalFlattenedBitmap;

                if (editor.ResultAction == AnnotationResultAction.Pin)
                {
                    SpawnPinWindow(workingBitmap, context, sourceRect);
                    return;
                }
            }

            // Step 2: Optional Top-of-Screen Custom Rename Popup
            DateTime now = DateTime.Now;
            string? customName = null;
            if (_configService.Config.EnableCustomRenameAfterCapture)
            {
                string defaultBase = _storageService.BuildDefaultFileName(context, mode, now);
                string ext = _storageService.GetCurrentExtension();
                var renamePopup = new QuickRenamePopup(defaultBase, ext, targetDirectory);
                bool? renameOk = renamePopup.ShowDialog();
                if (renameOk != true)
                    return;

                customName = renamePopup.EnteredFileName;
            }

            string? retryDirectory = null;
            while (true)
            {
                var outcome = await _storageService.SaveAndCopyAsync(workingBitmap, context, mode, now, customName, retryDirectory);
                if (outcome.Success)
                {
                    NotifySaveOutcome(outcome);
                    break;
                }

                var choice = MessageBox.Show(
                    $"{outcome.ErrorMessage ?? "无法写入目标目录"}\n\n是否换一个文件夹重试？",
                    "截图保存失败", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (choice != MessageBoxResult.Yes)
                    break;
                using var picker = new System.Windows.Forms.FolderBrowserDialog { Description = "选择截图保存目录" };
                if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    break;
                retryDirectory = picker.SelectedPath;
            }
        }
        finally
        {
            if (workingBitmap != initialBitmap)
                workingBitmap.Dispose();
        }
    }

    private void SpawnPinWindow(SKBitmap bitmap, AppContextInfo context, System.Drawing.Rectangle? rect)
    {
        var pin = new PinWindow(bitmap, context, _storageService, NotifySaveOutcome, rect, _configService);
        pin.Closed += (_, _) => _openPins.Remove(pin);
        _openPins.Add(pin);
        pin.Show();
    }

    private void NotifySaveOutcome(SaveOutcome outcome)
    {
        if (!_configService.Config.ShowToastNotification && outcome.Success && outcome.ClipboardCopied &&
            outcome.WarningMessage is null)
            return;

        if (outcome.Success)
        {
            _getToastWindow().ShowToast(
                outcome.ClipboardCopied ? "截图已保存并复制" : "截图已保存，复制失败",
                $"{outcome.FileName}  ({outcome.DirectoryPath})",
                warning: outcome.WarningMessage,
                directory: outcome.DirectoryPath,
                filePath: outcome.FilePath);
        }
        else
        {
            ShowErrorToast("截图保存失败", outcome.ErrorMessage ?? "未知写入错误");
        }
    }

    public void ShowInfoToast(string title, string detail)
    {
        if (_configService.Config.ShowToastNotification)
        _getToastWindow().ShowToast(title, detail);
    }

    public void ShowErrorToast(string title, string detail)
    {
        _getToastWindow().ShowToast(title, detail, isError: true);
    }

    public void CloseAllPins()
    {
        foreach (var pin in _openPins.ToList())
        {
            pin.Close();
        }
        _openPins.Clear();
    }
}
