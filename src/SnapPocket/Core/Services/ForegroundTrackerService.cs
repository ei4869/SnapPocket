using System.Diagnostics;
using SnapPocket.Core.Interop;
using SnapPocket.Core.Models;

namespace SnapPocket.Core.Services;

public sealed class ForegroundTrackerService : IDisposable
{
    private static readonly HashSet<string> DesktopClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TrayNotifyWnd"
    };

    private static readonly HashSet<string> ShellProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SearchHost.exe",
        "StartMenuExperienceHost.exe",
        "ShellExperienceHost.exe",
        "LockApp.exe"
    };

    private readonly uint _ownProcessId;
    private readonly NativeMethods.WinEventDelegate _winEventCallback;
    private IntPtr _hookHandle;
    private AppContextInfo _currentContext = AppContextInfo.Desktop;

    public AppContextInfo CurrentContext => _currentContext;
    public event Action<AppContextInfo>? ContextChanged;

    public ForegroundTrackerService()
    {
        _ownProcessId = (uint)Environment.ProcessId;
        _winEventCallback = OnWinEvent;
    }

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero) return;

        // Capture initial foreground window
        RefreshFromHandle(NativeMethods.GetForegroundWindow());

        _hookHandle = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventCallback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    public AppContextInfo CaptureFreshContext()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            _ = NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
            if (pid != 0 && pid != _ownProcessId)
            {
                RefreshFromHandle(fg);
            }
        }
        return _currentContext;
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;
        RefreshFromHandle(hwnd);
    }

    private void RefreshFromHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == _ownProcessId) return;

        var className = NativeMethods.GetWindowClassName(hwnd);
        if (DesktopClassNames.Contains(className))
        {
            UpdateContext(AppContextInfo.Desktop);
            return;
        }

        string? exePath = NativeMethods.TryGetProcessExePath(pid);
        string processName = string.Empty;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            processName = Path.GetFileName(exePath);
        }
        else
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                processName = proc.ProcessName + ".exe";
            }
            catch
            {
                UpdateContext(AppContextInfo.Desktop);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(processName) || ShellProcessNames.Contains(processName))
        {
            UpdateContext(AppContextInfo.Desktop);
            return;
        }

        // Distinguish Explorer desktop shell vs real File Explorer window
        if (processName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) &&
            !className.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase))
        {
            UpdateContext(AppContextInfo.Desktop);
            return;
        }

        var title = NativeMethods.GetWindowTitleText(hwnd);
        var displayName = Path.GetFileNameWithoutExtension(processName);
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = processName;

        var ctx = new AppContextInfo
        {
            WindowHandle = hwnd,
            ProcessId = pid,
            ProcessName = processName,
            DisplayName = displayName,
            WindowTitle = title,
            IsDesktopContext = false
        };

        UpdateContext(ctx);
    }

    private void UpdateContext(AppContextInfo next)
    {
        bool changed =
            _currentContext.IsDesktopContext != next.IsDesktopContext ||
            !string.Equals(_currentContext.ProcessName, next.ProcessName, StringComparison.OrdinalIgnoreCase) ||
            _currentContext.WindowHandle != next.WindowHandle;

        _currentContext = next;
        if (changed)
        {
            ContextChanged?.Invoke(next);
        }
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
