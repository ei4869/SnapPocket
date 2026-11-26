using SnapPocket.Core.Models;

namespace SnapPocket.Core.Services;

public sealed class DirectoryMemoryService
{
    private const int MaxRecentDirectories = 8;
    private readonly ConfigService _configService;

    public event Action? DirectoriesChanged;

    public DirectoryMemoryService(ConfigService configService)
    {
        _configService = configService;
    }

    internal (string? PreferredDirectory, string DefaultDirectory) GetSaveDirectories(AppContextInfo context)
    {
        var config = _configService.Config;
        var rule = context.IsDesktopContext || string.IsNullOrWhiteSpace(context.ProcessName) ? null :
            config.AppDirectories.FirstOrDefault(item => string.Equals(item.ProcessName, context.ProcessName,
                StringComparison.OrdinalIgnoreCase));
        return (string.IsNullOrWhiteSpace(rule?.DirectoryPath) ? null : rule.DirectoryPath, config.DefaultSaveDirectory);
    }

    public ResolvedDirectoryInfo ResolveDirectory(AppContextInfo context, bool verifyAccessibility = false)
    {
        var (preferred, configuredDefault) = GetSaveDirectories(context);
        if (preferred is not null)
        {
            if (!verifyAccessibility || IsDirectoryAccessibleOrCreatable(preferred))
            {
                return new ResolvedDirectoryInfo
                {
                    DirectoryPath = preferred,
                    IsAppSpecific = true
                };
            }
        }

        return new ResolvedDirectoryInfo
        {
            DirectoryPath = verifyAccessibility ? EnsureFallbackDefaultPath(configuredDefault) : configuredDefault,
            FellBackFromMissingDirectory = preferred is not null,
            MissingDirectoryPath = preferred
        };
    }

    public void AssignDirectoryForContext(AppContextInfo context, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return;
        directoryPath = Path.GetFullPath(directoryPath.Trim());

        var cfg = _configService.Config;
        if (context.IsDesktopContext || string.IsNullOrWhiteSpace(context.ProcessName))
        {
            cfg.DefaultSaveDirectory = directoryPath;
        }
        else
        {
            var existing = cfg.AppDirectories.FirstOrDefault(r =>
                r.ProcessName.Equals(context.ProcessName, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.DirectoryPath = directoryPath;
                existing.DisplayName = context.DisplayName;
                existing.LastUsedUtc = DateTime.UtcNow;
            }
            else
            {
                cfg.AppDirectories.Add(new AppDirectoryRule
                {
                    ProcessName = context.ProcessName,
                    DisplayName = context.DisplayName,
                    DirectoryPath = directoryPath,
                    LastUsedUtc = DateTime.UtcNow
                });
            }
        }

        AddRecentDirectoryInternal(cfg, directoryPath);
        if (_configService.Save())
            DirectoriesChanged?.Invoke();
    }

    public void SetDefaultDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return;
        directoryPath = Path.GetFullPath(directoryPath.Trim());
        var cfg = _configService.Config;
        cfg.DefaultSaveDirectory = directoryPath;
        AddRecentDirectoryInternal(cfg, directoryPath);
        if (_configService.Save())
            DirectoriesChanged?.Invoke();
    }

    public void UpdateRule(string processName, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(directoryPath)) return;
        directoryPath = Path.GetFullPath(directoryPath.Trim());
        var cfg = _configService.Config;
        var existing = cfg.AppDirectories.FirstOrDefault(r =>
            r.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.DirectoryPath = directoryPath;
            existing.LastUsedUtc = DateTime.UtcNow;
            AddRecentDirectoryInternal(cfg, directoryPath);
            if (_configService.Save())
                DirectoriesChanged?.Invoke();
        }
    }

    public void RemoveRule(string processName)
    {
        var cfg = _configService.Config;
        int removed = cfg.AppDirectories.RemoveAll(r =>
            r.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            if (_configService.Save())
                DirectoriesChanged?.Invoke();
        }
    }

    public void ClearAllRules()
    {
        var cfg = _configService.Config;
        if (cfg.AppDirectories.Count == 0) return;
        cfg.AppDirectories.Clear();
        if (_configService.Save())
            DirectoriesChanged?.Invoke();
    }

    public IReadOnlyList<string> GetRecentDirectories()
    {
        var cfg = _configService.Config;
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(cfg.DefaultSaveDirectory))
            list.Add(cfg.DefaultSaveDirectory);

        foreach (var dir in cfg.RecentDirectories)
        {
            if (!string.IsNullOrWhiteSpace(dir) &&
                !list.Contains(dir, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(dir);
            }
        }

        return list.Take(MaxRecentDirectories).ToList();
    }

    public static bool IsDirectoryCurrentlyAvailable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsDirectoryAccessibleOrCreatable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (Directory.Exists(path)) return true;

            // Check root drive exists (prevents hanging/creating on unplugged USB drives)
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return false;

            // Only consider missing leaf inaccessible when parent doesn't exist either
            // If user deleted the exact target folder, treat as missing so fallback warns clearly.
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static string EnsureFallbackDefaultPath(string configuredDefault)
    {
        var target = string.IsNullOrWhiteSpace(configuredDefault)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnapPocket")
            : configuredDefault;

        try
        {
            var root = Path.GetPathRoot(target);
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                Directory.CreateDirectory(target);
                return target;
            }
        }
        catch
        {
            // Fall through to Pictures\SnapPocket
        }

        var safeFallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnapPocket");
        Directory.CreateDirectory(safeFallback);
        return safeFallback;
    }

    private static void AddRecentDirectoryInternal(AppConfig cfg, string path)
    {
        cfg.RecentDirectories.RemoveAll(d => d.Equals(path, StringComparison.OrdinalIgnoreCase));
        cfg.RecentDirectories.Insert(0, path);
        if (cfg.RecentDirectories.Count > MaxRecentDirectories)
            cfg.RecentDirectories.RemoveRange(MaxRecentDirectories, cfg.RecentDirectories.Count - MaxRecentDirectories);
    }
}
