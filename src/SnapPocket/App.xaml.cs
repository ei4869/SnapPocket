using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using SnapPocket.Capture;
using SnapPocket.Core.Services;
using SnapPocket.UI.Capture;
using SnapPocket.UI.Dock;
using SnapPocket.UI.Settings;
using SnapPocket.UI.Tray;

namespace SnapPocket;

public partial class App : Application
{
    private const string OpenPanelSignalName = @"Local\SnapPocket_OpenPanel";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _openPanelSignal;
    private RegisteredWaitHandle? _openPanelWait;
    private ConfigService? _configService;
    private ThemeService? _themeService;
    private StartupService? _startupService;
    private ForegroundTrackerService? _foregroundTracker;
    private DirectoryMemoryService? _directoryMemory;
    private StorageService? _storageService;
    private HotkeyService? _hotkeyService;
    private TrayIconManager? _trayManager;
    private FloatingDockWindow? _dockWindow;
    private ToastNotificationWindow? _toastWindow;
    private CaptureCoordinator? _captureCoordinator;
    private SettingsWindow? _settingsWindow;
    private bool _isShuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool openPanel = e.Args.Contains(LaunchShortcutService.OpenPanelArgument, StringComparer.OrdinalIgnoreCase);
        _openPanelSignal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenPanelSignalName);
        _singleInstanceMutex = new Mutex(true, @"Local\SnapPocket_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            if (openPanel)
                _openPanelSignal.Set();
            else
                MessageBox.Show("SnapPocket 已在后台运行，请查看系统托盘或屏幕边缘。", "SnapPocket", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        _configService = new ConfigService();
        _themeService = new ThemeService();
        _themeService.ApplyTheme(_configService.Config.ThemeMode);

        _startupService = new StartupService();
        _foregroundTracker = new ForegroundTrackerService();
        _directoryMemory = new DirectoryMemoryService(_configService);
        _storageService = new StorageService(_configService, _directoryMemory);
        _hotkeyService = new HotkeyService(_configService);

        _configService.SaveFailed += detail => GetToastWindow().ShowToast("配置保存失败", detail, isError: true);
        try
        {
            new LaunchShortcutService().RepairLegacyShortcut();
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            GetToastWindow().ShowToast("快捷启动修复失败", ex.Message, isError: true);
        }
        _dockWindow = new FloatingDockWindow(_configService, _foregroundTracker, _directoryMemory);
        _dockWindow.OpenSettingsRequested += OpenSettingsWindow;
        _dockWindow.ExitRequested += ShutdownApplication;

        IScreenCapturer capturer = new DxgiDesktopCapturer();
        _captureCoordinator = new CaptureCoordinator(
            _configService,
            _foregroundTracker,
            _directoryMemory,
            _storageService,
            capturer,
            _dockWindow,
            GetToastWindow,
            _themeService);

        _hotkeyService.HotkeyTriggered += action =>
            Dispatcher.BeginInvoke(() => _captureCoordinator.HandleHotkeyAction(action));

        _trayManager = new TrayIconManager(_configService);
        _trayManager.TogglePanelRequested += () => Dispatcher.BeginInvoke(() => _dockWindow.ToggleExpanded());
        _trayManager.OpenSettingsRequested += () => Dispatcher.BeginInvoke(OpenSettingsWindow);
        _trayManager.OpenDefaultFolderRequested += () =>
        {
            var dir = DirectoryMemoryService.EnsureFallbackDefaultPath(_configService.Config.DefaultSaveDirectory);
            StorageService.OpenFolderInExplorer(dir);
        };
        _trayManager.ExitRequested += () => Dispatcher.BeginInvoke(ShutdownApplication);

        _foregroundTracker.Start();
        _hotkeyService.Initialize();
        _dockWindow.Show();
        _openPanelWait = ThreadPool.RegisterWaitForSingleObject(_openPanelSignal, (_, _) =>
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, OpenPanel);
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        if (openPanel)
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, OpenPanel);

        var conflicts = _configService.Config.Hotkeys.Where(h => h.HasConflict).ToList();
        if (conflicts.Count > 0)
        {
            var names = string.Join("、", conflicts.Select(c => HotkeyService.GetActionDisplayName(c.Action)));
            GetToastWindow().ShowToast(
                "部分快捷键存在占用冲突",
                $"{names} 注册失败，请在「详细设置 → 快捷键」中修改",
                isError: true);
        }
    }

    private ToastNotificationWindow GetToastWindow() => _toastWindow ??= new ToastNotificationWindow();

    private void OpenPanel()
    {
        if (!_isShuttingDown)
            _dockWindow?.OpenExpanded();
    }

    private void OpenSettingsWindow()
    {
        if (_configService is null || _directoryMemory is null || _hotkeyService is null ||
            _storageService is null || _startupService is null || _themeService is null)
            return;

        if (_settingsWindow is not null && _settingsWindow.IsLoaded)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _configService,
            _directoryMemory,
            _hotkeyService,
            _storageService,
            _startupService,
            _themeService);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ShutdownApplication()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;
        _openPanelWait?.Unregister(null);
        _openPanelWait = null;

        try
        {
            _captureCoordinator?.CloseAllPins();
            _settingsWindow?.Close();
            _dockWindow?.Close();
            _toastWindow?.Close();
            _hotkeyService?.Dispose();
            _foregroundTracker?.Dispose();
            _trayManager?.Dispose();
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch
        {
            // Ensure clean exit regardless of UI shutdown ordering
        }

        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _openPanelWait?.Unregister(null);
        _openPanelSignal?.Dispose();
        if (!_isShuttingDown)
        {
            _hotkeyService?.Dispose();
            _foregroundTracker?.Dispose();
            _trayManager?.Dispose();
            _singleInstanceMutex?.Dispose();
        }
        base.OnExit(e);
    }
}
