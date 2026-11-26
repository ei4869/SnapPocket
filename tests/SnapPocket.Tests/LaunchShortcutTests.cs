using System.IO;
using System.Runtime.InteropServices;
using SnapPocket.Core.Services;

namespace SnapPocket.Tests;

public sealed class LaunchShortcutTests
{
    [Fact]
    public void ManagedShortcutCanBeCreatedUpdatedForANewPathAndRemoved()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "shortcut-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string executable = Environment.ProcessPath!;
            var shortcut = new LaunchShortcutService(directory, executable);
            Assert.Equal(LaunchShortcutState.Disabled, shortcut.GetState());
            shortcut.SetEnabled(true);
            Assert.Equal(LaunchShortcutState.Enabled, shortcut.GetState());
            Assert.True(File.Exists(shortcut.ShortcutPath));

            string moved = Path.Combine(directory, "moved.exe");
            File.Copy(executable, moved);
            var updated = new LaunchShortcutService(directory, moved);
            Assert.Equal(LaunchShortcutState.NeedsRepair, updated.GetState());
            updated.SetEnabled(true);
            Assert.Equal(LaunchShortcutState.Enabled, updated.GetState());
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

            updated.SetEnabled(false);
            Assert.Equal(LaunchShortcutState.Disabled, updated.GetState());
            Assert.False(File.Exists(updated.ShortcutPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LegacyShortcutIsRepairedOnlyByItsTargetExecutable()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "shortcut-test-" + Guid.NewGuid().ToString("N"));
        string executable = Environment.ProcessPath!;
        var shortcut = new LaunchShortcutService(directory, executable);
        shortcut.SetEnabled(true);
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic link = shell.CreateShortcut(shortcut.ShortcutPath);
                try
                {
                    link.Description = "SnapPocket 快捷启动";
                    link.Save();
                }
                finally
                {
                    Marshal.FinalReleaseComObject((object)link);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject((object)shell);
            }

            Assert.Equal(LaunchShortcutState.NeedsRepair, shortcut.GetState());
            var otherCopy = new LaunchShortcutService(directory, Path.Combine(directory, "other.exe"));
            otherCopy.RepairLegacyShortcut();
            Assert.Equal(LaunchShortcutState.NeedsRepair, shortcut.GetState());

            shortcut.RepairLegacyShortcut();
            Assert.Equal(LaunchShortcutState.Enabled, shortcut.GetState());
            Assert.Equal(new[] { shortcut.ShortcutPath }, Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExistingUnmanagedPathSurvivesToggling()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "shortcut-test-" + Guid.NewGuid().ToString("N"));
        var shortcut = new LaunchShortcutService(directory, Environment.ProcessPath);
        Directory.CreateDirectory(shortcut.ShortcutPath);
        try
        {
            Assert.Equal(LaunchShortcutState.Occupied, shortcut.GetState());
            Assert.Throws<InvalidOperationException>(() => shortcut.SetEnabled(true));
            Assert.Throws<InvalidOperationException>(() => shortcut.SetEnabled(false));
            Assert.True(Directory.Exists(shortcut.ShortcutPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
