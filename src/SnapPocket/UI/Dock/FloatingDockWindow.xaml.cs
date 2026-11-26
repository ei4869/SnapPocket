using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SnapPocket.Core.Interop;
using SnapPocket.Core.Models;
using SnapPocket.Core.Services;

namespace SnapPocket.UI.Dock;

public partial class FloatingDockWindow : Window
{
    private enum DockVisualState
    {
        Hidden,
        Peek,
        Expanded
    }

    private readonly ConfigService _configService;
    private readonly ForegroundTrackerService _foregroundTracker;
    private readonly DirectoryMemoryService _directoryMemory;
    private readonly DispatcherTimer _edgePollTimer;

    private DockVisualState _state = DockVisualState.Hidden;
    private AppContextInfo _displayedContext = AppContextInfo.Desktop;
    private string _currentResolvedDir = string.Empty;

    private bool _isDialogOpen;
    private bool _isSuspendedForCapture;
    private bool _isDraggingHandle;
    private bool _dragMoved;
    private Point _dragStartScreen;
    private double _dragStartTop;
    private double _cachedDpiX = 1.0;
    private double _cachedDpiY = 1.0;
    private DateTime _lastMouseInsideUtc = DateTime.UtcNow;

    public event Action? OpenSettingsRequested;
    public event Action? ExitRequested;

    public FloatingDockWindow(
        ConfigService configService,
        ForegroundTrackerService foregroundTracker,
        DirectoryMemoryService directoryMemory)
    {
        InitializeComponent();

        _configService = configService;
        _foregroundTracker = foregroundTracker;
        _directoryMemory = directoryMemory;

        _edgePollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _edgePollTimer.Tick += OnEdgePollTick;
        DpiChanged += (_, _) => RefreshCachedDpi();
        if (_configService.Config.DockCompletelyHideWhenIdle)
            Opacity = 0;

        _foregroundTracker.ContextChanged += OnForegroundContextChanged;
        _directoryMemory.DirectoriesChanged += OnDirectoriesChanged;
        _configService.ConfigChanged += OnConfigChanged;
        _configService.SaveFailed += OnConfigSaveFailed;

        Loaded += (_, _) =>
        {
            RefreshCachedDpi();
            ApplyDockEdgeLayout();
            SyncFromConfig();
            UpdateDisplayedContext(_foregroundTracker.CurrentContext);
            SetState(DockVisualState.Hidden);
            _edgePollTimer.Start();
        };

        Closed += (_, _) =>
        {
            _edgePollTimer.Stop();
            _foregroundTracker.ContextChanged -= OnForegroundContextChanged;
            _directoryMemory.DirectoriesChanged -= OnDirectoriesChanged;
            _configService.ConfigChanged -= OnConfigChanged;
            _configService.SaveFailed -= OnConfigSaveFailed;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    public void SuspendForCapture()
    {
        _isSuspendedForCapture = true;
        _edgePollTimer.Stop();
        if (IsVisible) Hide();
    }

    public void ResumeAfterCapture()
    {
        _isSuspendedForCapture = false;
        if (IsLoaded)
            _edgePollTimer.Start();
        if (_state == DockVisualState.Hidden && _configService.Config.DockCompletelyHideWhenIdle)
        {
            if (IsVisible) Hide();
        }
        else if (!IsVisible)
        {
            Show();
            RepositionToEdge();
        }
    }

    private void RefreshCachedDpi()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            _cachedDpiX = source.CompositionTarget.TransformToDevice.M11;
            _cachedDpiY = source.CompositionTarget.TransformToDevice.M22;
        }
    }

    public void ToggleExpanded()
    {
        if (_state == DockVisualState.Expanded)
        {
            RecentDirsDrawer.Visibility = Visibility.Collapsed;
            SetState(DockVisualState.Hidden);
        }
        else
        {
            OpenExpanded();
        }
    }

    public void OpenExpanded()
    {
        if (_isSuspendedForCapture)
            return;
        UpdateDisplayedContext(_foregroundTracker.CaptureFreshContext());
        SyncFromConfig();
        SetState(DockVisualState.Expanded);
        _lastMouseInsideUtc = DateTime.UtcNow.AddSeconds(2);
    }

    public void CollapseToHidden()
    {
        RecentDirsDrawer.Visibility = Visibility.Collapsed;
        SetState(DockVisualState.Hidden);
    }

    public void ApplyDockEdgeLayout()
    {
        bool isLeft = _configService.Config.DockEdge == DockEdge.Left;
        if (isLeft)
        {
            Grid.SetColumn(HandleBorder, 0);
            Grid.SetColumn(PanelBorder, 1);
            HandleBorder.CornerRadius = new CornerRadius(0, 8, 8, 0);
            PanelBorder.Margin = new Thickness(6, 8, 8, 8);
            HandleChevron.Text = _state == DockVisualState.Expanded ? "‹" : "›";
        }
        else
        {
            Grid.SetColumn(HandleBorder, 1);
            Grid.SetColumn(PanelBorder, 0);
            HandleBorder.CornerRadius = new CornerRadius(8, 0, 0, 8);
            PanelBorder.Margin = new Thickness(8, 8, 6, 8);
            HandleChevron.Text = _state == DockVisualState.Expanded ? "›" : "‹";
        }
        RepositionToEdge();
    }

    private void OnForegroundContextChanged(AppContextInfo ctx)
    {
        Dispatcher.BeginInvoke(() => UpdateDisplayedContext(ctx));
    }

    private void OnDirectoriesChanged()
    {
        Dispatcher.BeginInvoke(() => UpdateDisplayedContext(_displayedContext));
    }

    private void OnConfigChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            SyncFromConfig();
            ApplyDockEdgeLayout();
            UpdateDisplayedContext(_displayedContext);
            if (_state == DockVisualState.Hidden)
            {
                SetState(DockVisualState.Hidden);
            }
        });
    }

    private void OnConfigSaveFailed(string _) => OnConfigChanged();

    private void SyncFromConfig()
    {
        var cfg = _configService.Config;
        AnnotationToggle.IsChecked = cfg.EnableAnnotationAfterCapture;
        RenameToggle.IsChecked = cfg.EnableCustomRenameAfterCapture;
        RecentDirsList.ItemsSource = _directoryMemory.GetRecentDirectories();
    }

    private void UpdateDisplayedContext(AppContextInfo ctx)
    {
        _displayedContext = ctx;
        var resolved = _directoryMemory.ResolveDirectory(ctx, verifyAccessibility: false);
        _currentResolvedDir = resolved.DirectoryPath;

        if (ctx.IsDesktopContext)
        {
            ContextBadgeText.Text = "默认";
            ContextNameText.Text = "桌面 / 默认";
            if (TryFindResource("SurfaceElevatedBrush") is Brush mutedBg)
                ContextBadgeBorder.Background = mutedBg;
            if (TryFindResource("TextSecondaryBrush") is Brush mutedFg)
                ContextBadgeText.Foreground = mutedFg;
        }
        else
        {
            ContextBadgeText.Text = resolved.IsAppSpecific ? "已记忆" : "应用";
            ContextNameText.Text = ctx.ProcessName;
            if (TryFindResource(resolved.IsAppSpecific ? "SuccessBrush" : "AccentBrush") is Brush bg)
                ContextBadgeBorder.Background = bg;
            if (TryFindResource("AccentTextBrush") is Brush fg)
                ContextBadgeText.Foreground = fg;
        }

        string folderName = Path.GetFileName(_currentResolvedDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName))
            folderName = _currentResolvedDir;

        DirFolderNameText.Text = folderName;
        DirFullPathText.Text = _currentResolvedDir;
        DirectorySelectorCard.ToolTip = $"当前保存目录：{_currentResolvedDir}\n点击快速切换目录";
    }

    private void SetState(DockVisualState next)
    {
        _state = next;
        bool isLeft = _configService.Config.DockEdge == DockEdge.Left;

        switch (next)
        {
            case DockVisualState.Hidden:
                PanelBorder.Visibility = Visibility.Collapsed;
                RecentDirsDrawer.Visibility = Visibility.Collapsed;
                if (_configService.Config.DockCompletelyHideWhenIdle)
                {
                    Opacity = 0;
                    if (IsVisible) Hide();
                    return;
                }

                Opacity = 1;
                HandleBorder.Width = 7;
                HandleBorder.Height = 72;
                HandleBorder.Opacity = 0.65;
                HiddenAccentBar.Visibility = Visibility.Visible;
                HandleContent.Visibility = Visibility.Collapsed;
                if (!IsVisible && !_isSuspendedForCapture) Show();
                break;

            case DockVisualState.Peek:
                Opacity = 1;
                PanelBorder.Visibility = Visibility.Collapsed;
                RecentDirsDrawer.Visibility = Visibility.Collapsed;
                HandleBorder.Width = 34;
                HandleBorder.Height = 76;
                HandleBorder.Opacity = 0.96;
                HiddenAccentBar.Visibility = Visibility.Collapsed;
                HandleContent.Visibility = Visibility.Visible;
                HandleChevron.Text = isLeft ? "›" : "‹";
                if (!IsVisible && !_isSuspendedForCapture) Show();
                break;

            case DockVisualState.Expanded:
                Opacity = 1;
                PanelBorder.Visibility = Visibility.Visible;
                HandleBorder.Width = 30;
                HandleBorder.Height = 76;
                HandleBorder.Opacity = 0.96;
                HiddenAccentBar.Visibility = Visibility.Collapsed;
                HandleContent.Visibility = Visibility.Visible;
                HandleChevron.Text = isLeft ? "‹" : "›";
                if (!IsVisible && !_isSuspendedForCapture) Show();
                break;
        }

        RefreshCachedDpi();
        UpdateLayout();
        RepositionToEdge();
    }

    private void RepositionToEdge()
    {
        if (!IsLoaded) return;

        var workArea = SystemParameters.WorkArea;
        double width = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
        double height = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;

        Left = _configService.Config.DockEdge == DockEdge.Left
            ? workArea.Left
            : workArea.Right - width;

        double targetCenterY = workArea.Top + workArea.Height * _configService.Config.DockVerticalRatio;
        double clampedTop = Math.Clamp(targetCenterY - height / 2.0, workArea.Top + 8, Math.Max(workArea.Top + 8, workArea.Bottom - height - 8));
        Top = clampedTop;
    }

    private void OnEdgePollTick(object? sender, EventArgs e)
    {
        if (_isDialogOpen || _isDraggingHandle || _isSuspendedForCapture)
            return;

        if (!NativeMethods.GetCursorPos(out var pt))
            return;

        double cursorX = pt.X / _cachedDpiX;
        double cursorY = pt.Y / _cachedDpiY;

        var workArea = SystemParameters.WorkArea;
        bool isLeft = _configService.Config.DockEdge == DockEdge.Left;
        int triggerPx = Math.Max(6, _configService.Config.DockEdgeTriggerPixels);

        bool insideWindow = IsVisible &&
            cursorX >= Left - 6 && cursorX <= Left + ActualWidth + 6 &&
            cursorY >= Top - 6 && cursorY <= Top + ActualHeight + 6;

        // Generous vertical edge trigger band around dock anchor so completely hidden dock wakes effortlessly
        double anchorCenterY = workArea.Top + workArea.Height * _configService.Config.DockVerticalRatio;
        double halfTriggerBand = _configService.Config.DockCompletelyHideWhenIdle ? 150.0 : 75.0;
        bool inVerticalBand = cursorY >= anchorCenterY - halfTriggerBand && cursorY <= anchorCenterY + halfTriggerBand;

        bool nearScreenEdge = isLeft
            ? cursorX >= workArea.Left - 2 && cursorX <= workArea.Left + triggerPx
            : cursorX >= workArea.Right - triggerPx && cursorX <= workArea.Right + 2;

        if (insideWindow || (nearScreenEdge && inVerticalBand))
        {
            _lastMouseInsideUtc = DateTime.UtcNow;
            if (_state == DockVisualState.Hidden)
            {
                SetState(DockVisualState.Peek);
            }
            return;
        }

        double elapsedMs = (DateTime.UtcNow - _lastMouseInsideUtc).TotalMilliseconds;
        int hideDelay = _state == DockVisualState.Expanded
            ? Math.Max(_configService.Config.DockAutoHideDelayMs, 900)
            : _configService.Config.DockAutoHideDelayMs;

        if (_state != DockVisualState.Hidden && elapsedMs >= hideDelay)
        {
            SetState(DockVisualState.Hidden);
        }
    }

    private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingHandle = true;
        _dragMoved = false;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _dragStartTop = Top;
        HandleBorder.CaptureMouse();
        e.Handled = true;
    }

    private void Handle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingHandle) return;

        var currentScreen = PointToScreen(e.GetPosition(this));
        var source = PresentationSource.FromVisual(this);
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        double deltaY = (currentScreen.Y - _dragStartScreen.Y) / dpiY;

        if (Math.Abs(deltaY) > 5)
        {
            _dragMoved = true;
            var workArea = SystemParameters.WorkArea;
            double nextTop = Math.Clamp(_dragStartTop + deltaY, workArea.Top + 8, Math.Max(workArea.Top + 8, workArea.Bottom - ActualHeight - 8));
            Top = nextTop;
        }
    }

    private void Handle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingHandle) return;
        _isDraggingHandle = false;
        HandleBorder.ReleaseMouseCapture();

        if (_dragMoved)
        {
            var workArea = SystemParameters.WorkArea;
            double centerY = Top + ActualHeight / 2.0;
            _configService.Config.DockVerticalRatio = Math.Clamp((centerY - workArea.Top) / workArea.Height, 0.08, 0.92);
            _configService.Save();
        }
        else
        {
            ToggleExpanded();
        }
        e.Handled = true;
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        CollapseToHidden();
    }

    private void DirectoryCard_Click(object sender, MouseButtonEventArgs e)
    {
        RecentDirsList.ItemsSource = _directoryMemory.GetRecentDirectories();
        bool nextOpen = RecentDirsDrawer.Visibility != Visibility.Visible;
        RecentDirsDrawer.Visibility = nextOpen ? Visibility.Visible : Visibility.Collapsed;
        DirDropdownChevron.Text = nextOpen ? "▴" : "▾";
        UpdateLayout();
        RepositionToEdge();
    }

    private void RecentDirItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string dir && !string.IsNullOrWhiteSpace(dir))
        {
            _directoryMemory.AssignDirectoryForContext(_displayedContext, dir);
            RecentDirsDrawer.Visibility = Visibility.Collapsed;
            DirDropdownChevron.Text = "▾";
            UpdateLayout();
            RepositionToEdge();
        }
    }

    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        _isDialogOpen = true;
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = _displayedContext.IsDesktopContext
                    ? "选择默认截图保存目录"
                    : $"为 {_displayedContext.ProcessName} 选择截图保存目录",
                InitialDirectory = Directory.Exists(_currentResolvedDir)
                    ? _currentResolvedDir
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                _directoryMemory.AssignDirectoryForContext(_displayedContext, dialog.FolderName);
                RecentDirsDrawer.Visibility = Visibility.Collapsed;
                DirDropdownChevron.Text = "▾";
            }
        }
        finally
        {
            _isDialogOpen = false;
            _lastMouseInsideUtc = DateTime.UtcNow;
            UpdateLayout();
            RepositionToEdge();
        }
    }

    private void OpenCurrentDir_Click(object sender, RoutedEventArgs e)
    {
        var resolved = _directoryMemory.ResolveDirectory(_displayedContext, verifyAccessibility: true);
        Directory.CreateDirectory(resolved.DirectoryPath);
        StorageService.OpenFolderInExplorer(resolved.DirectoryPath);
    }

    private void AnnotationToggle_Click(object sender, RoutedEventArgs e)
    {
        _configService.Config.EnableAnnotationAfterCapture = AnnotationToggle.IsChecked == true;
        _configService.Save();
    }

    private void RenameToggle_Click(object sender, RoutedEventArgs e)
    {
        _configService.Config.EnableCustomRenameAfterCapture = RenameToggle.IsChecked == true;
        _configService.Save();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        CollapseToHidden();
        OpenSettingsRequested?.Invoke();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke();
    }
}
