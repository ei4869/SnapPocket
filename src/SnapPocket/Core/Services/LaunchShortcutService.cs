using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using SnapPocket.Core.Interop;

namespace SnapPocket.Core.Services;

internal enum LaunchShortcutState
{
    Disabled,
    Enabled,
    NeedsRepair,
    Occupied
}

internal sealed class LaunchShortcutService
{
    internal const string OpenPanelArgument = "--open-panel";
    internal const string DisplayHotkey = "Ctrl + Alt + P";
    internal const uint Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
    internal const uint VirtualKey = 'P';

    private const string ShortcutFileName = "SnapPocket - 快捷启动.lnk";
    private const string LegacyDescription = "SnapPocket 快捷启动";
    private const string Description = "打开 SnapPocket 悬浮面板";
    private const ushort ShellHotkey = (0x02 | 0x04) << 8 | (ushort)VirtualKey;
    private const int ShellCreate = 0x00000002;
    private const int ShellDelete = 0x00000004;
    private const uint NotifyUnicodePathAndFlush = 0x0005 | 0x1000;

    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");
    private readonly string _programsFolder;
    private readonly string? _executablePath;

    public LaunchShortcutService()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.ProcessPath)
    {
    }

    internal LaunchShortcutService(string programsFolder, string? executablePath)
    {
        _programsFolder = programsFolder;
        _executablePath = executablePath;
    }

    internal string ShortcutPath => Path.Combine(_programsFolder, ShortcutFileName);

    public LaunchShortcutState GetState() => ReadState().State;

    private (LaunchShortcutState State, bool RepairLegacy) ReadState()
    {
        if (string.IsNullOrWhiteSpace(_programsFolder))
            throw new InvalidOperationException("Windows 开始菜单的程序目录不可用。");
        if (!File.Exists(ShortcutPath) && !Directory.Exists(ShortcutPath))
            return (LaunchShortcutState.Disabled, false);
        if (Directory.Exists(ShortcutPath))
            return (LaunchShortcutState.Occupied, false);

        return WithShellLink(link =>
        {
            ((IPersistFile)link).Load(ShortcutPath, 0);
            var buffer = new StringBuilder(32768);
            link.GetDescription(buffer, buffer.Capacity);
            string description = buffer.ToString();
            bool legacy = string.Equals(description, LegacyDescription, StringComparison.Ordinal);
            if (!legacy && !string.Equals(description, Description, StringComparison.Ordinal))
                return (LaunchShortcutState.Occupied, false);

            buffer.Clear();
            link.GetArguments(buffer, buffer.Capacity);
            if (!string.Equals(buffer.ToString(), OpenPanelArgument, StringComparison.Ordinal))
                return (LaunchShortcutState.Occupied, false);

            buffer.Clear();
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0x4); // SLGP_RAWPATH
            link.GetHotkey(out ushort hotkey);
            var target = Environment.ExpandEnvironmentVariables(buffer.ToString());
            bool currentTarget = string.Equals(target, _executablePath, StringComparison.OrdinalIgnoreCase);
            var state = !legacy && hotkey == ShellHotkey && currentTarget
                ? LaunchShortcutState.Enabled : LaunchShortcutState.NeedsRepair;
            return (state, legacy && currentTarget && hotkey == ShellHotkey);
        });
    }

    internal void RepairLegacyShortcut()
    {
        if (ReadState().RepairLegacy)
            SetEnabled(true);
    }

    public void SetEnabled(bool enabled)
    {
        var state = GetState();
        if (state == LaunchShortcutState.Occupied)
            throw new InvalidOperationException("开始菜单中已有同名项目。请先重命名该项目。");

        if (!enabled)
        {
            if (state == LaunchShortcutState.Disabled)
                return;
            File.Delete(ShortcutPath);
            NotifyShell(ShellDelete);
            return;
        }

        if (state == LaunchShortcutState.Enabled)
            return;
        if (string.IsNullOrWhiteSpace(_executablePath) || !File.Exists(_executablePath))
            throw new InvalidOperationException("找不到 SnapPocket 的程序文件。");

        Directory.CreateDirectory(_programsFolder);
        byte[]? previousShortcut = state == LaunchShortcutState.NeedsRepair
            ? File.ReadAllBytes(ShortcutPath) : null;

        WithShellLink(link =>
        {
            link.SetPath(_executablePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(_executablePath)!);
            link.SetArguments(OpenPanelArgument);
            link.SetDescription(Description);
            link.SetHotkey(ShellHotkey);

            if (previousShortcut is not null)
            {
                // Release the previous binding before registering the replacement.
                File.Delete(ShortcutPath);
                NotifyShell(ShellDelete);
            }

            try
            {
                // Explorer can register the hotkey on save, so save under its final name.
                ((IPersistFile)link).Save(ShortcutPath, true);
            }
            catch
            {
                if (previousShortcut is not null)
                {
                    File.WriteAllBytes(ShortcutPath, previousShortcut);
                    NotifyShell(ShellCreate);
                }
                throw;
            }

            return true;
        });
        NotifyShell(ShellCreate);
    }

    private static T WithShellLink<T>(Func<IShellLink, T> action)
    {
        var type = Type.GetTypeFromCLSID(ShellLinkClassId, throwOnError: true)!;
        var shellLink = (IShellLink)Activator.CreateInstance(type)!;
        try
        {
            return action(shellLink);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellLink);
        }
    }

    private void NotifyShell(int change) => SHChangeNotify(change, NotifyUnicodePathAndFlush, ShortcutPath, IntPtr.Zero);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, string path, IntPtr reserved);

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, IntPtr data, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
