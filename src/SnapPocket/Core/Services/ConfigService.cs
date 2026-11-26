using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;
using SnapPocket.Annotation.Models;
using SnapPocket.Core.Models;

namespace SnapPocket.Core.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        IgnoreReadOnlyProperties = true,
        Converters = { new AnnotationColorConverter() }
    };

    private readonly object _lock = new();
    private string _savedConfig;
    public string ConfigDirectory { get; }
    public string ConfigFilePath { get; }
    public AppConfig Config { get; private set; }

    public event Action? ConfigChanged;
    public event Action<string>? SaveFailed;

    public ConfigService() : this(ResolveConfigDirectory()) { }

    internal ConfigService(string directory)
    {
        ConfigDirectory = directory;
        ConfigFilePath = Path.Combine(directory, "config.json");
        Config = LoadInternal();
        EnsureDefaultsPopulated(Config);
        _savedConfig = JsonSerializer.Serialize(Config, JsonOptions);
    }

    private static string ResolveConfigDirectory()
    {
        var exeDir = AppContext.BaseDirectory;
        var portableConfig = Path.Combine(exeDir, "config.json");
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SnapPocket");

        // Prefer portable config if writable & either already exists or portable marker exists;
        // default to exe directory if writable (true portable experience), otherwise AppData.
        return File.Exists(portableConfig) || IsDirectoryWritable(exeDir) ? exeDir : appDataDir;
    }

    private static bool IsDirectoryWritable(string dir)
    {
        try
        {
            var testPath = Path.Combine(dir, $".sp_write_test_{Guid.NewGuid():N}");
            using (File.Create(testPath, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private AppConfig LoadInternal()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Corrupted config falls back cleanly to defaults
        }

        return new AppConfig();
    }

    private static void EnsureDefaultsPopulated(AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.DefaultSaveDirectory))
        {
            cfg.DefaultSaveDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "SnapPocket");
        }

        if (string.IsNullOrWhiteSpace(cfg.FileNameTemplate))
            cfg.FileNameTemplate = "{app}_{yyyyMMdd_HHmmss}";

        cfg.ImageQuality = Math.Clamp(cfg.ImageQuality, 10, 100);
        cfg.DockVerticalRatio = Math.Clamp(cfg.DockVerticalRatio, 0.08, 0.92);
        cfg.DockEdgeTriggerPixels = Math.Clamp(cfg.DockEdgeTriggerPixels, 4, 30);
        cfg.DockAutoHideDelayMs = Math.Clamp(cfg.DockAutoHideDelayMs, 200, 3000);

        cfg.AppDirectories ??= [];
        cfg.RecentDirectories ??= [];
        cfg.Hotkeys ??= [];
        cfg.AnnotationPresets ??= [];
        cfg.AppDirectories.RemoveAll(rule => rule is null || string.IsNullOrWhiteSpace(rule.ProcessName));
        cfg.Hotkeys.RemoveAll(hotkey => hotkey is null);
        foreach (var (tool, style) in cfg.AnnotationPresets.ToArray())
        {
            if (style is null || !Enum.IsDefined(tool))
                cfg.AnnotationPresets.Remove(tool);
            else
                cfg.AnnotationPresets[tool] = style.Normalize();
        }

        var defaults = AppConfig.CreateDefaultHotkeys();
        foreach (var def in defaults)
        {
            if (cfg.Hotkeys.All(h => h.Action != def.Action))
            {
                cfg.Hotkeys.Add(def);
            }
        }
    }

    public bool Save()
    {
        string? error = null;
        lock (_lock)
        {
            var tempPath = Path.Combine(ConfigDirectory, $".config-{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(ConfigDirectory);
                var json = JsonSerializer.Serialize(Config, JsonOptions);
                File.WriteAllText(tempPath, json, new System.Text.UTF8Encoding(false));
                File.Move(tempPath, ConfigFilePath, overwrite: true);
                _savedConfig = json;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                var attempted = Config;
                Config = JsonSerializer.Deserialize<AppConfig>(_savedConfig, JsonOptions)!;
                foreach (var hotkey in Config.Hotkeys)
                {
                    var previous = attempted.Hotkeys.Find(h => h.Action == hotkey.Action &&
                        h.Modifiers == hotkey.Modifiers && h.VirtualKey == hotkey.VirtualKey && h.Enabled == hotkey.Enabled);
                    hotkey.HasConflict = previous?.HasConflict == true;
                    hotkey.ConflictMessage = previous?.ConflictMessage;
                }
                try { File.Delete(tempPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                error = ex.Message;
            }
        }

        if (error is not null)
        {
            SaveFailed?.Invoke(error);
            return false;
        }

        ConfigChanged?.Invoke();
        return true;
    }

    private sealed class AnnotationColorConverter : JsonConverter<SKColor>
    {
        public override SKColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(reader.GetUInt32());

        public override void Write(Utf8JsonWriter writer, SKColor value, JsonSerializerOptions options)
            => writer.WriteNumberValue((uint)value);
    }
}
