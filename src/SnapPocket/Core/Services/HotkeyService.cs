using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using SnapPocket.Core.Interop;
using SnapPocket.Core.Models;

namespace SnapPocket.Core.Services;

public sealed class HotkeyValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }

    public static HotkeyValidationResult Ok() => new() { IsValid = true };
    public static HotkeyValidationResult Fail(string msg) => new() { IsValid = false, ErrorMessage = msg };
}

public sealed class HotkeyService : IDisposable
{
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly ConfigService _configService;
    private readonly Dictionary<int, (HotkeyAction Action, uint Modifiers, uint Key)> _registeredIds = new();
    private readonly Func<int, uint, uint, int>? _registerForTest;
    private readonly Action<int>? _unregisterForTest;
    private HwndSource? _messageSource;
    private int _nextId = 1000;
    internal Action<HotkeyAction>? RecordPreviouslyRegistered { get; set; }

    public event Action<HotkeyAction>? HotkeyTriggered;
    public event Action<List<HotkeyConfig>>? ConflictsDetected;

    public HotkeyService(ConfigService configService)
    {
        _configService = configService;
    }

    internal HotkeyService(ConfigService configService, Func<int, uint, uint, int> register, Action<int> unregister)
        : this(configService)
    {
        _registerForTest = register;
        _unregisterForTest = unregister;
    }

    private bool CanRegister => _messageSource is not null || _registerForTest is not null;

    public void Initialize()
    {
        if (_messageSource is not null) return;

        var parameters = new HwndSourceParameters("SnapPocketHotkeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };
        _messageSource = new HwndSource(parameters);
        _messageSource.AddHook(WndProc);

        ReloadAndRegisterAll();
    }

    public List<HotkeyConfig> ReloadAndRegisterAll()
    {
        UnregisterAll();

        var conflicts = new List<HotkeyConfig>();
        if (!CanRegister) return conflicts;

        var seenCombos = new HashSet<(uint Mod, uint Vk)>();

        foreach (var hk in _configService.Config.Hotkeys)
        {
            hk.HasConflict = false;
            hk.ConflictMessage = null;

            if (!hk.Enabled || hk.VirtualKey == 0)
                continue;

            var validation = ValidateCombination(hk.Modifiers, hk.VirtualKey);
            if (!validation.IsValid)
            {
                hk.HasConflict = true;
                hk.ConflictMessage = validation.ErrorMessage;
                conflicts.Add(hk);
                continue;
            }

            if (!seenCombos.Add((hk.Modifiers, hk.VirtualKey)))
            {
                hk.HasConflict = true;
                hk.ConflictMessage = "与本工具内其他快捷键重复";
                conflicts.Add(hk);
                continue;
            }

            int id = _nextId++;
            int error = Register(id, hk.Modifiers, hk.VirtualKey);
            if (error == 0)
            {
                _registeredIds[id] = (hk.Action, hk.Modifiers, hk.VirtualKey);
            }
            else
            {
                hk.HasConflict = true;
                hk.ConflictMessage = GetRegistrationError(error);
                conflicts.Add(hk);
            }
        }

        if (conflicts.Count > 0)
        {
            ConflictsDetected?.Invoke(conflicts);
        }

        return conflicts;
    }

    public void UnregisterAll()
    {
        foreach (int id in _registeredIds.Keys)
        {
            Unregister(id);
        }
        _registeredIds.Clear();
    }

    internal HotkeyValidationResult TrySetBinding(HotkeyAction action, uint modifiers, uint key)
    {
        if (key != 0 && _configService.Config.Hotkeys.Any(item => item.Action != action && item.Enabled &&
            item.Modifiers == modifiers && item.VirtualKey == key))
            return HotkeyValidationResult.Fail("该组合键已被 SnapPocket 的其他操作使用");

        var bindings = _configService.Config.Hotkeys.Select(hotkey => hotkey.Action == action
            ? new HotkeyConfig { Action = action, Modifiers = modifiers, VirtualKey = key, Enabled = key != 0 }
            : hotkey).ToList();
        return TryReplaceBindings(bindings, action);
    }

    internal HotkeyValidationResult TryResetBindings() => TryReplaceBindings(AppConfig.CreateDefaultHotkeys(), null);

    private HotkeyValidationResult TryReplaceBindings(List<HotkeyConfig> bindings, HotkeyAction? singleChange)
    {
        if (!CanRegister)
            return HotkeyValidationResult.Fail("快捷键服务未就绪");

        var currentIds = _registeredIds.ToDictionary(entry => (entry.Value.Modifiers, entry.Value.Key), entry => entry.Key);
        var nextIds = new Dictionary<int, (HotkeyAction, uint, uint)>();
        var newlyRegistered = new List<int>();
        var seen = new HashSet<(uint, uint)>();
        var previousSettings = _configService.Config.Hotkeys;
        try
        {
            foreach (var hotkey in bindings.Where(item => item.Enabled && item.VirtualKey != 0))
            {
                if (singleChange.HasValue && hotkey.Action != singleChange.Value && hotkey.HasConflict)
                    continue;

                var validation = ValidateCombination(hotkey.Modifiers, hotkey.VirtualKey);
                if (!validation.IsValid)
                    return validation;
                var keys = (hotkey.Modifiers, hotkey.VirtualKey);
                if (!seen.Add(keys))
                    return HotkeyValidationResult.Fail("该组合键已被 SnapPocket 的其他操作使用");

                if (!currentIds.TryGetValue(keys, out int id))
                {
                    id = _nextId++;
                    int error = Register(id, hotkey.Modifiers, hotkey.VirtualKey);
                    if (error != 0)
                        return HotkeyValidationResult.Fail($"{GetActionDisplayName(hotkey.Action)}：{GetRegistrationError(error)}");
                    newlyRegistered.Add(id);
                }
                nextIds[id] = (hotkey.Action, hotkey.Modifiers, hotkey.VirtualKey);
            }

            _configService.Config.Hotkeys = bindings;
            if (!_configService.Save())
            {
                _configService.Config.Hotkeys = previousSettings;
                return HotkeyValidationResult.Fail("无法写入配置，已保留原快捷键");
            }

            foreach (int id in _registeredIds.Keys.Where(id => !nextIds.ContainsKey(id)).ToArray())
                Unregister(id);
            _registeredIds.Clear();
            foreach (var (id, binding) in nextIds)
                _registeredIds[id] = binding;
            newlyRegistered.Clear();
            return HotkeyValidationResult.Ok();
        }
        finally
        {
            foreach (int id in newlyRegistered)
                Unregister(id);
        }
    }

    private int Register(int id, uint modifiers, uint key)
    {
        if (_registerForTest is not null)
            return _registerForTest(id, modifiers, key);
        return NativeMethods.RegisterHotKey(_messageSource!.Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, key)
            ? 0 : Marshal.GetLastWin32Error();
    }

    private void Unregister(int id)
    {
        if (_unregisterForTest is not null)
            _unregisterForTest(id);
        else if (_messageSource is not null)
            NativeMethods.UnregisterHotKey(_messageSource.Handle, id);
    }

    private static string GetRegistrationError(int error)
        => error == ErrorHotkeyAlreadyRegistered ? "已被系统或其他软件占用" : $"注册失败 (错误码 {error})";

    public static HotkeyValidationResult ValidateCombination(uint modifiers, uint vk)
    {
        if (vk == 0)
            return HotkeyValidationResult.Ok();

        // Block modifier-only keys
        if (vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C)
            return HotkeyValidationResult.Fail("请按下修饰键加普通按键的组合");

        // Block Esc / Enter / Tab without modifiers, and bare letters/numbers/space
        bool hasMod = (modifiers & (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_WIN)) != 0;
        bool isFunctionKey = vk >= 0x70 && vk <= 0x87; // F1-F24
        bool isPrintScreen = vk == 0x2C;

        if (!hasMod && !isFunctionKey && !isPrintScreen)
            return HotkeyValidationResult.Fail("为避免打字与游戏误触，非功能键必须配合 Ctrl/Alt/Shift 使用");

        // Dangerous single-modifier system/common combos
        if (modifiers == NativeMethods.MOD_CONTROL && vk is (uint)'C' or (uint)'V' or (uint)'X' or (uint)'Z' or (uint)'Y' or (uint)'A' or (uint)'S' or (uint)'W' or (uint)'F')
            return HotkeyValidationResult.Fail("该组合键属于高频编辑快捷键，已拦截以防冲突");

        if (modifiers == NativeMethods.MOD_ALT && (vk == 0x09 || vk == 0x73)) // Alt+Tab, Alt+F4
            return HotkeyValidationResult.Fail("系统保留快捷键，不可绑定");

        if ((modifiers & NativeMethods.MOD_WIN) != 0 && vk is (uint)'L' or (uint)'D' or (uint)'E' or (uint)'R' or 0x09)
            return HotkeyValidationResult.Fail("Windows 系统保留快捷键，不可绑定");

        if (!hasMod && vk == 0x7B) // bare F12
            return HotkeyValidationResult.Fail("单键 F12 与 Steam 截图及开发者工具冲突，请加修饰键");

        return HotkeyValidationResult.Ok();
    }

    public static string FormatHotkey(uint modifiers, uint vk)
    {
        if (vk == 0) return "未设置";

        var parts = new List<string>();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");

        parts.Add(FormatVirtualKey(vk));
        return string.Join(" + ", parts);
    }

    public static string GetActionDisplayName(HotkeyAction action) => action switch
    {
        HotkeyAction.RegionCapture => "区域框选截图",
        HotkeyAction.FullscreenCapture => "全屏快速截图",
        HotkeyAction.ActiveWindowCapture => "当前活动窗口截图",
        HotkeyAction.PinCapture => "截屏贴图置顶",
        HotkeyAction.ScrollingCapture => "滚动长截图",
        HotkeyAction.ToggleDockPanel => "呼出 / 收起悬浮面板",
        HotkeyAction.ToggleAnnotationMode => "切换「快速标注」开关",
        HotkeyAction.ToggleCustomRename => "切换「截屏后命名」开关",
        _ => action.ToString()
    };

    public static (uint Modifiers, uint VirtualKey) FromWpfKeyEvent(Key key, ModifierKeys mods)
    {
        if (key == Key.None || key == Key.System) return (0, 0);

        uint modFlags = 0;
        if (mods.HasFlag(ModifierKeys.Control)) modFlags |= NativeMethods.MOD_CONTROL;
        if (mods.HasFlag(ModifierKeys.Alt)) modFlags |= NativeMethods.MOD_ALT;
        if (mods.HasFlag(ModifierKeys.Shift)) modFlags |= NativeMethods.MOD_SHIFT;
        if (mods.HasFlag(ModifierKeys.Windows)) modFlags |= NativeMethods.MOD_WIN;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return (modFlags, vk);
    }

    private static string FormatVirtualKey(uint vk)
    {
        if (vk >= (uint)'0' && vk <= (uint)'9') return ((char)vk).ToString();
        if (vk >= (uint)'A' && vk <= (uint)'Z') return ((char)vk).ToString();
        if (vk >= 0x60 && vk <= 0x69) return $"Num{vk - 0x60}";
        if (vk >= 0x70 && vk <= 0x87) return $"F{vk - 0x6F}";
        return vk switch
        {
            0x2C => "PrtSc",
            0x20 => "Space",
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0xC0 => "`",
            0xBD => "-",
            0xBB => "=",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xBA => ";",
            0xDE => "'",
            0xBC => ",",
            0xBE => ".",
            0xBF => "/",
            _ => KeyInterop.KeyFromVirtualKey((int)vk).ToString()
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_registeredIds.TryGetValue(id, out var binding))
            {
                handled = true;
                if (RecordPreviouslyRegistered is { } record)
                    record(binding.Action);
                else
                    HotkeyTriggered?.Invoke(binding.Action);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        if (_messageSource is not null)
        {
            _messageSource.RemoveHook(WndProc);
            _messageSource.Dispose();
            _messageSource = null;
        }
    }
}
