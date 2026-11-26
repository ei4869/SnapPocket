using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SnapPocket.Core.Interop;
using SnapPocket.Core.Models;
using SnapPocket.Core.Services;

namespace SnapPocket.UI.Settings;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _configService;
    private readonly DirectoryMemoryService _directoryMemory;
    private readonly HotkeyService _hotkeyService;
    private readonly StorageService _storageService;
    private readonly StartupService _startupService;
    private readonly ThemeService _themeService;
    private readonly LaunchShortcutService _launchShortcut = new();
    private LaunchShortcutState _launchShortcutState;

    private bool _isInitializing = true;
    private HotkeyAction? _recordingAction;
    private Button? _activeRecordingButton;

    public SettingsWindow(
        ConfigService configService,
        DirectoryMemoryService directoryMemory,
        HotkeyService hotkeyService,
        StorageService storageService,
        StartupService startupService,
        ThemeService themeService)
    {
        InitializeComponent();

        _configService = configService;
        _directoryMemory = directoryMemory;
        _hotkeyService = hotkeyService;
        _storageService = storageService;
        _startupService = startupService;
        _themeService = themeService;

        SourceInitialized += (_, _) => _themeService.ApplyWindowChrome(this);
        Loaded += (_, _) => LoadAllSettings();
        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewKeyUp += OnWindowPreviewKeyUp;
        _directoryMemory.DirectoriesChanged += OnExternalDirectoriesChanged;
        _configService.ConfigChanged += OnExternalConfigChanged;
        _configService.SaveFailed += OnConfigSaveFailed;

        Closed += (_, _) =>
        {
            if (_recordingAction.HasValue)
            {
                _recordingAction = null;
                _hotkeyService.RecordPreviouslyRegistered = null;
            }
            _directoryMemory.DirectoriesChanged -= OnExternalDirectoriesChanged;
            _configService.ConfigChanged -= OnExternalConfigChanged;
            _configService.SaveFailed -= OnConfigSaveFailed;
        };
    }

    private void OnConfigSaveFailed(string _) => Dispatcher.BeginInvoke(LoadAllSettings);

    private void OnExternalDirectoriesChanged()
    {
        Dispatcher.BeginInvoke(RefreshDirectoryRulesUI);
    }

    private void OnExternalConfigChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isInitializing) return;
            _isInitializing = true;
            try
            {
                SettingsAnnotationToggle.IsChecked = _configService.Config.EnableAnnotationAfterCapture;
                SettingsRenameToggle.IsChecked = _configService.Config.EnableCustomRenameAfterCapture;
            }
            finally
            {
                _isInitializing = false;
            }
        });
    }

    private void LoadAllSettings()
    {
        _isInitializing = true;
        try
        {
            var cfg = _configService.Config;

            // General
            StartupCheckBox.IsChecked = _startupService.IsEnabled();
            DockCompleteHideCheckBox.IsChecked = cfg.DockCompletelyHideWhenIdle;
            SelectComboByTag(DockEdgeCombo, cfg.DockEdge.ToString());
            TriggerPxSlider.Value = cfg.DockEdgeTriggerPixels;
            TriggerPxText.Text = $"{cfg.DockEdgeTriggerPixels} px";
            AutoHideDelaySlider.Value = cfg.DockAutoHideDelayMs;
            AutoHideDelayText.Text = $"{cfg.DockAutoHideDelayMs} ms";
            ToastCheckBox.IsChecked = cfg.ShowToastNotification;
            SoundCheckBox.IsChecked = cfg.PlaySoundOnCapture;

            // Capture & Save
            SettingsAnnotationToggle.IsChecked = cfg.EnableAnnotationAfterCapture;
            SettingsRenameToggle.IsChecked = cfg.EnableCustomRenameAfterCapture;
            SelectComboByTag(FullscreenScopeCombo, cfg.FullscreenScope.ToString());
            SelectComboByTag(FormatCombo, cfg.ImageFormat.ToString());
            QualitySlider.Value = cfg.ImageQuality;
            QualityText.Text = $"{cfg.ImageQuality}%";
            QualityRow.IsEnabled = cfg.ImageFormat != ImageFormatType.Png;
            QualityRow.Opacity = cfg.ImageFormat != ImageFormatType.Png ? 1.0 : 0.48;
            TemplateBox.Text = cfg.FileNameTemplate;
            UpdateTemplatePreview();

            // Appearance
            SelectComboByTag(ThemeCombo, cfg.ThemeMode.ToString());
            ConfigPathText.Text = _configService.ConfigFilePath;

            // Dynamic lists
            RefreshLaunchShortcut();
            RefreshHotkeysUI();
            RefreshDirectoryRulesUI();
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void RefreshLaunchShortcut()
    {
        LaunchShortcutHint.Text =
            $"开启后，按 {LaunchShortcutService.DisplayHotkey} 打开 SnapPocket 面板。快捷方式保存在当前用户的 Windows 开始菜单。";
        try
        {
            _launchShortcutState = _launchShortcut.GetState();
            LaunchShortcutCheckBox.IsEnabled = _launchShortcutState != LaunchShortcutState.Occupied;
            LaunchShortcutCheckBox.IsChecked = _launchShortcutState is LaunchShortcutState.Enabled or LaunchShortcutState.NeedsRepair;
            SetLaunchShortcutStatus(_launchShortcutState switch
            {
                LaunchShortcutState.NeedsRepair => "启动路径或组合键已变化，关闭后重新开启可更新快捷方式。",
                LaunchShortcutState.Occupied => "开始菜单中已有同名项目，请先将其重命名。",
                _ => null
            });
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LaunchShortcutCheckBox.IsChecked = false;
            LaunchShortcutCheckBox.IsEnabled = false;
            SetLaunchShortcutStatus($"无法读取快捷启动设置。{ex.Message}");
        }
    }

    private void LaunchShortcutCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        bool enable = LaunchShortcutCheckBox.IsChecked == true;
        if (enable && _launchShortcutState == LaunchShortcutState.Disabled)
        {
            const int probeId = 0x5391;
            if (_configService.Config.Hotkeys.Any(h => h.Enabled &&
                    h.Modifiers == LaunchShortcutService.Modifiers && h.VirtualKey == LaunchShortcutService.VirtualKey) ||
                !NativeMethods.RegisterHotKey(IntPtr.Zero, probeId, LaunchShortcutService.Modifiers, LaunchShortcutService.VirtualKey))
            {
                LaunchShortcutCheckBox.IsChecked = false;
                SetLaunchShortcutStatus($"{LaunchShortcutService.DisplayHotkey} 已被占用，请释放该组合键后再开启。");
                return;
            }
            NativeMethods.UnregisterHotKey(IntPtr.Zero, probeId);
        }

        try
        {
            _launchShortcut.SetEnabled(enable);
            RefreshLaunchShortcut();
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            RefreshLaunchShortcut();
            SetLaunchShortcutStatus($"设置快捷启动失败。{ex.Message}");
        }
    }

    private void SetLaunchShortcutStatus(string? message)
    {
        LaunchShortcutStatus.Text = message ?? string.Empty;
        LaunchShortcutStatus.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void NavTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string page) return;

        if (_recordingAction.HasValue && page != "Hotkeys")
        {
            CancelRecordingHotkeys();
        }

        PageGeneral.Visibility = page == "General" ? Visibility.Visible : Visibility.Collapsed;
        PageHotkeys.Visibility = page == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        PageDirectories.Visibility = page == "Directories" ? Visibility.Visible : Visibility.Collapsed;
        PageCaptureSave.Visibility = page == "CaptureSave" ? Visibility.Visible : Visibility.Collapsed;
        PageAppearance.Visibility = page == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- Page 1: General ---
    private void StartupCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool enable = StartupCheckBox.IsChecked == true;
        if (_startupService.SetEnabled(enable))
        {
            _configService.Config.LaunchAtStartup = enable;
            _configService.Save();
        }
        else
        {
            StartupCheckBox.IsChecked = !enable;
        }
    }

    private void DockCompleteHideCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _configService.Config.DockCompletelyHideWhenIdle = DockCompleteHideCheckBox.IsChecked == true;
        _configService.Save();
    }

    private void DockEdgeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (DockEdgeCombo.SelectedItem is ComboBoxItem item && Enum.TryParse<DockEdge>(item.Tag?.ToString(), out var edge))
        {
            _configService.Config.DockEdge = edge;
            _configService.Save();
        }
    }

    private void DockSliders_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        _configService.Config.DockEdgeTriggerPixels = (int)TriggerPxSlider.Value;
        _configService.Config.DockAutoHideDelayMs = (int)AutoHideDelaySlider.Value;
        TriggerPxText.Text = $"{_configService.Config.DockEdgeTriggerPixels} px";
        AutoHideDelayText.Text = $"{_configService.Config.DockAutoHideDelayMs} ms";
        _configService.Save();
    }

    private void NotificationToggles_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _configService.Config.ShowToastNotification = ToastCheckBox.IsChecked == true;
        _configService.Config.PlaySoundOnCapture = SoundCheckBox.IsChecked == true;
        _configService.Save();
    }

    // --- Page 2: Hotkeys ---
    private void RefreshHotkeysUI()
    {
        HotkeysContainer.Children.Clear();
        _activeRecordingButton = null;
        var conflicts = new List<string>();

        foreach (var hk in _configService.Config.Hotkeys)
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labelStack.Children.Add(new TextBlock
            {
                Text = HotkeyService.GetActionDisplayName(hk.Action),
                FontSize = 13,
                Foreground = (Brush)FindResource("TextPrimaryBrush")
            });

            if (hk.HasConflict && !string.IsNullOrWhiteSpace(hk.ConflictMessage))
            {
                conflicts.Add($"{HotkeyService.GetActionDisplayName(hk.Action)}：{hk.ConflictMessage}");
                labelStack.Children.Add(new TextBlock
                {
                    Text = "⚠ " + hk.ConflictMessage,
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 0),
                    Foreground = (Brush)FindResource("DangerBrush")
                });
            }
            Grid.SetColumn(labelStack, 0);
            row.Children.Add(labelStack);

            bool isRecordingThis = _recordingAction == hk.Action;
            var keyBtn = new Button
            {
                Content = isRecordingThis
                    ? "请按下组合键... (Esc取消)"
                    : (hk.Enabled && hk.VirtualKey != 0 ? HotkeyService.FormatHotkey(hk.Modifiers, hk.VirtualKey) : "点击设置快捷键"),
                Style = (Style)FindResource("SubtleButtonStyle"),
                Margin = new Thickness(0, 0, 6, 0),
                BorderThickness = new Thickness(isRecordingThis ? 1.5 : 1),
                BorderBrush = hk.HasConflict
                    ? (Brush)FindResource("DangerBrush")
                    : (isRecordingThis ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BorderSubtleBrush")),
                Tag = hk.Action
            };
            keyBtn.Click += (_, _) => StartRecordingHotkey(hk.Action);
            Grid.SetColumn(keyBtn, 1);
            row.Children.Add(keyBtn);

            if (isRecordingThis)
            {
                _activeRecordingButton = keyBtn;
            }

            var clearBtn = new Button
            {
                Content = "清除",
                Style = (Style)FindResource("SubtleButtonStyle"),
                Padding = new Thickness(9, 5, 9, 5),
                FontSize = 11.5
            };
            clearBtn.Click += (_, _) =>
            {
                CancelRecordingHotkeys();
                ShowHotkeyResult(_hotkeyService.TrySetBinding(hk.Action, 0, 0));
            };
            Grid.SetColumn(clearBtn, 2);
            row.Children.Add(clearBtn);

            HotkeysContainer.Children.Add(row);
        }

        if (conflicts.Count > 0)
        {
            HotkeyErrorBannerText.Text = "检测到快捷键冲突：" + string.Join("；", conflicts);
            HotkeyErrorBanner.Visibility = Visibility.Visible;
        }
        else if (!_recordingAction.HasValue)
        {
            HotkeyErrorBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void StartRecordingHotkey(HotkeyAction action)
    {
        if (_recordingAction == action)
        {
            CancelRecordingHotkeys();
            return;
        }

        _recordingAction = action;
        HotkeyErrorBanner.Visibility = Visibility.Collapsed;
        _hotkeyService.RecordPreviouslyRegistered = recorded =>
        {
            var existing = _configService.Config.Hotkeys.Find(item => item.Action == recorded);
            if (existing is not null)
                CompleteRecording(existing.Modifiers, existing.VirtualKey);
        };
        RefreshHotkeysUI();
        Keyboard.Focus(this);
    }

    private void CancelRecordingHotkeys()
    {
        _recordingAction = null;
        _activeRecordingButton = null;
        _hotkeyService.RecordPreviouslyRegistered = null;
        RefreshHotkeysUI();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingAction.HasValue) return;
        e.Handled = true;

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };

        if (key == Key.Escape)
        {
            CancelRecordingHotkeys();
            return;
        }

        // Show live modifier hint while user is holding Ctrl/Alt/Shift
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            UpdateLiveModifierHint();
            return;
        }

        var (mods, vk) = HotkeyService.FromWpfKeyEvent(key, Keyboard.Modifiers);
        if (vk != 0)
            CompleteRecording(mods, vk);
    }

    private void CompleteRecording(uint modifiers, uint key)
    {
        if (_recordingAction is not { } action)
            return;
        var result = _launchShortcutState == LaunchShortcutState.Enabled &&
            modifiers == LaunchShortcutService.Modifiers && key == LaunchShortcutService.VirtualKey
            ? HotkeyValidationResult.Fail("该组合键已用于退出后快捷启动。")
            : _hotkeyService.TrySetBinding(action, modifiers, key);
        if (result.IsValid)
            CancelRecordingHotkeys();
        ShowHotkeyResult(result);
    }

    private void ShowHotkeyResult(HotkeyValidationResult result)
    {
        RefreshHotkeysUI();
        if (!result.IsValid)
        {
            HotkeyErrorBannerText.Text = result.ErrorMessage;
            HotkeyErrorBanner.Visibility = Visibility.Visible;
        }
    }

    private void OnWindowPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_recordingAction.HasValue) return;
        UpdateLiveModifierHint();
    }

    private void UpdateLiveModifierHint()
    {
        if (_activeRecordingButton is null) return;
        var mods = Keyboard.Modifiers;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        _activeRecordingButton.Content = parts.Count > 0
            ? string.Join(" + ", parts) + " + ..."
            : "请按下组合键... (Esc取消)";
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        CancelRecordingHotkeys();
        ShowHotkeyResult(_hotkeyService.TryResetBindings());
    }

    // --- Page 3: App Directory Rules ---
    private void RefreshDirectoryRulesUI()
    {
        DefaultDirBox.Text = _configService.Config.DefaultSaveDirectory;

        RulesListPanel.Children.Clear();
        string query = RuleSearchBox.Text.Trim();

        var rules = _configService.Config.AppDirectories
            .OrderByDescending(r => r.LastUsedUtc)
            .Where(r => string.IsNullOrEmpty(query) ||
                        r.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        r.DirectoryPath.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        EmptyRulesHint.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var rule in rules)
        {
            bool exists = DirectoryMemoryService.IsDirectoryCurrentlyAvailable(rule.DirectoryPath);

            var card = new Border
            {
                Margin = new Thickness(0, 4, 0, 4),
                Padding = new Thickness(12, 9, 12, 9),
                CornerRadius = new CornerRadius(7),
                Background = (Brush)FindResource("SurfaceElevatedBrush"),
                BorderBrush = exists ? (Brush)FindResource("BorderSubtleBrush") : (Brush)FindResource("WarningBrush"),
                BorderThickness = new Thickness(1)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var infoStack = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock
            {
                Text = rule.ProcessName,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush")
            });
            if (!exists)
            {
                titleRow.Children.Add(new TextBlock
                {
                    Text = "  (目录已失效，将回退默认目录)",
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("WarningBrush")
                });
            }
            infoStack.Children.Add(titleRow);
            infoStack.Children.Add(new TextBlock
            {
                Text = rule.DirectoryPath,
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = rule.DirectoryPath,
                Foreground = (Brush)FindResource("TextSecondaryBrush")
            });
            Grid.SetColumn(infoStack, 0);
            grid.Children.Add(infoStack);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var editBtn = new Button
            {
                Content = "更改",
                Style = (Style)FindResource("SubtleButtonStyle"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 6, 0)
            };
            editBtn.Click += (_, _) =>
            {
                var dlg = new OpenFolderDialog
                {
                    Title = $"为 {rule.ProcessName} 重新选择目录",
                    InitialDirectory = exists ? rule.DirectoryPath : _configService.Config.DefaultSaveDirectory
                };
                if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
                {
                    _directoryMemory.UpdateRule(rule.ProcessName, dlg.FolderName);
                }
            };
            btnRow.Children.Add(editBtn);

            var delBtn = new Button
            {
                Content = "删除",
                Style = (Style)FindResource("DangerSubtleButtonStyle"),
                Padding = new Thickness(10, 4, 10, 4)
            };
            delBtn.Click += (_, _) => _directoryMemory.RemoveRule(rule.ProcessName);
            btnRow.Children.Add(delBtn);

            Grid.SetColumn(btnRow, 1);
            grid.Children.Add(btnRow);

            card.Child = grid;
            RulesListPanel.Children.Add(card);
        }
    }

    private void ChangeDefaultDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择全局默认截图保存目录",
            InitialDirectory = Directory.Exists(_configService.Config.DefaultSaveDirectory)
                ? _configService.Config.DefaultSaveDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            _directoryMemory.SetDefaultDirectory(dlg.FolderName);
        }
    }

    private void OpenDefaultDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = DirectoryMemoryService.EnsureFallbackDefaultPath(_configService.Config.DefaultSaveDirectory);
        StorageService.OpenFolderInExplorer(dir);
    }

    private void RuleSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshDirectoryRulesUI();

    private void ClearAllRules_Click(object sender, RoutedEventArgs e)
    {
        if (_configService.Config.AppDirectories.Count == 0) return;
        var res = MessageBox.Show("确定要清空全部已记忆的应用目录规则吗？", "确认清空", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res == MessageBoxResult.Yes)
        {
            _directoryMemory.ClearAllRules();
        }
    }

    // --- Page 4: Capture & Save ---
    private void CaptureFlowToggles_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _configService.Config.EnableAnnotationAfterCapture = SettingsAnnotationToggle.IsChecked == true;
        _configService.Config.EnableCustomRenameAfterCapture = SettingsRenameToggle.IsChecked == true;
        _configService.Save();
    }

    private void CaptureFormat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (FullscreenScopeCombo.SelectedItem is ComboBoxItem scopeItem &&
            Enum.TryParse<FullscreenScope>(scopeItem.Tag?.ToString(), out var scope))
        {
            _configService.Config.FullscreenScope = scope;
        }

        if (FormatCombo.SelectedItem is ComboBoxItem fmtItem &&
            Enum.TryParse<ImageFormatType>(fmtItem.Tag?.ToString(), out var fmt))
        {
            _configService.Config.ImageFormat = fmt;
            QualityRow.IsEnabled = fmt != ImageFormatType.Png;
            QualityRow.Opacity = fmt != ImageFormatType.Png ? 1.0 : 0.48;
        }

        _configService.Save();
        UpdateTemplatePreview();
    }

    private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        _configService.Config.ImageQuality = (int)QualitySlider.Value;
        QualityText.Text = $"{_configService.Config.ImageQuality}%";
        _configService.Save();
    }

    private void TemplateBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        _configService.Config.FileNameTemplate = TemplateBox.Text;
        _configService.Save();
        UpdateTemplatePreview();
    }

    private void UpdateTemplatePreview()
    {
        var sampleCtx = new AppContextInfo
        {
            DisplayName = "EldenRing",
            ProcessName = "EldenRing.exe",
            IsDesktopContext = false
        };
        string name = _storageService.BuildDefaultFileName(sampleCtx, Core.Models.CaptureMode.Fullscreen, DateTime.Now);
        TemplatePreviewText.Text = $"示例文件名：{name}{_storageService.GetCurrentExtension()}";
    }

    // --- Page 5: Appearance ---
    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (ThemeCombo.SelectedItem is ComboBoxItem item && Enum.TryParse<Core.Models.ThemeMode>(item.Tag?.ToString(), out var mode))
        {
            _configService.Config.ThemeMode = mode;
            _configService.Save();
            _themeService.ApplyTheme(mode);
        }
    }

    private void OpenConfigDir_Click(object sender, RoutedEventArgs e)
    {
        StorageService.OpenFolderInExplorer(_configService.ConfigDirectory, _configService.ConfigFilePath);
    }
}
