using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using SnapPocket.Core.Models;

namespace SnapPocket.Core.Services;

public sealed partial class StorageService
{
    private readonly ConfigService _configService;
    private readonly DirectoryMemoryService _directoryMemory;
    private readonly Func<SKBitmap, Task<bool>> _copyImageAsync;

    public StorageService(ConfigService configService, DirectoryMemoryService directoryMemory)
        : this(configService, directoryMemory, CopyToClipboardAsync)
    {
    }

    internal StorageService(ConfigService configService, DirectoryMemoryService directoryMemory, Func<SKBitmap, Task<bool>> copyImageAsync)
    {
        _configService = configService;
        _directoryMemory = directoryMemory;
        _copyImageAsync = copyImageAsync;
    }

    public string BuildDefaultFileName(AppContextInfo context, CaptureMode mode, DateTime timestamp)
    {
        var cfg = _configService.Config;
        var template = string.IsNullOrWhiteSpace(cfg.FileNameTemplate)
            ? "{app}_{yyyyMMdd_HHmmss}"
            : cfg.FileNameTemplate;

        string appToken = context.IsDesktopContext || string.IsNullOrWhiteSpace(context.DisplayName)
            ? "Desktop"
            : SanitizeFileNameSegment(context.DisplayName);

        string modeToken = mode switch
        {
            CaptureMode.Region => "Region",
            CaptureMode.Fullscreen => "Screen",
            CaptureMode.ActiveWindow => "Window",
            CaptureMode.Pin => "Pin",
            CaptureMode.Scrolling => "Scroll",
            _ => "Snap"
        };

        string result = template
            .Replace("{app}", appToken, StringComparison.OrdinalIgnoreCase)
            .Replace("{mode}", modeToken, StringComparison.OrdinalIgnoreCase)
            .Replace("{yyyyMMdd_HHmmss}", timestamp.ToString("yyyyMMdd_HHmmss"), StringComparison.Ordinal)
            .Replace("{yyyy}", timestamp.ToString("yyyy"), StringComparison.Ordinal)
            .Replace("{MM}", timestamp.ToString("MM"), StringComparison.Ordinal)
            .Replace("{dd}", timestamp.ToString("dd"), StringComparison.Ordinal)
            .Replace("{HH}", timestamp.ToString("HH"), StringComparison.Ordinal)
            .Replace("{mm}", timestamp.ToString("mm"), StringComparison.Ordinal)
            .Replace("{ss}", timestamp.ToString("ss"), StringComparison.Ordinal);

        result = SanitizeFileNameSegment(result);
        return string.IsNullOrWhiteSpace(result) ? $"Snap_{timestamp:yyyyMMdd_HHmmss}" : result;
    }

    public string GetCurrentExtension() => _configService.Config.ImageFormat switch
    {
        ImageFormatType.Jpg => ".jpg",
        ImageFormatType.WebP => ".webp",
        _ => ".png"
    };

    public async Task<SaveOutcome> SaveAndCopyAsync(SKBitmap bitmap, AppContextInfo context, CaptureMode mode,
        DateTime timestamp, string? customBaseName = null, string? directoryOverride = null)
    {
        var (preferredDirectory, defaultDirectory) = _directoryMemory.GetSaveDirectories(context);
        var config = _configService.Config;
        var format = config.ImageFormat;
        int quality = config.ImageQuality;
        bool playSound = config.PlaySoundOnCapture;
        string extension = GetCurrentExtension();
        string baseName = string.IsNullOrWhiteSpace(customBaseName)
            ? BuildDefaultFileName(context, mode, timestamp)
            : SanitizeFileNameSegment(customBaseName);
        if (baseName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            baseName = SanitizeFileNameSegment(baseName[..^extension.Length]);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = BuildDefaultFileName(context, mode, timestamp);

        string filePath;
        string? warning = null;
        try
        {
            (filePath, warning) = await Task.Run(() =>
                SaveFile(bitmap, baseName, extension, format, quality, directoryOverride, preferredDirectory, defaultDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return new SaveOutcome { ErrorMessage = ex.Message };
        }

        bool clipboardCopied = await TryCopyAsync(bitmap);
        if (playSound)
            SystemSounds.Asterisk.Play();

        return new SaveOutcome
        {
            Success = true,
            FilePath = filePath,
            ClipboardCopied = clipboardCopied,
            FellBackFromMissingDirectory = warning is not null,
            WarningMessage = warning
        };
    }

    private static (string Path, string? Warning) SaveFile(SKBitmap bitmap, string baseName, string extension,
        ImageFormatType format, int quality, string? directoryOverride, string? preferredDirectory, string defaultDirectory)
    {
        using var data = EncodeBitmap(bitmap, format, quality);
        if (directoryOverride is not null)
            return (WriteUniqueFile(data, directoryOverride, baseName, extension), null);

        if (preferredDirectory is not null && DirectoryMemoryService.IsDirectoryCurrentlyAvailable(preferredDirectory))
        {
            try
            {
                return (WriteUniqueFile(data, preferredDirectory, baseName, extension), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A mapped directory can disappear or lose write access between checks.
            }
        }

        string destination = DirectoryMemoryService.EnsureFallbackDefaultPath(defaultDirectory);
        var warning = preferredDirectory is not null ? "应用目录无法写入，已保存到默认目录。"
            : !string.Equals(defaultDirectory, destination, StringComparison.OrdinalIgnoreCase)
                ? "默认目录不可用，已保存到应用回退目录。"
                : null;
        return (WriteUniqueFile(data, destination, baseName, extension), warning);
    }

    private static string WriteUniqueFile(SKData data, string directory, string baseName, string extension)
    {
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $".snap-{Guid.NewGuid():N}.tmp");
        bool ownsTemp = false;
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
            {
                ownsTemp = true;
                data.SaveTo(stream);
            }

            for (int i = 0; ; i++)
            {
                string suffix = i == 0 ? "" : i < 10000 ? $"_{i}" : $"_{Guid.NewGuid():N}";
                string candidate = Path.Combine(directory, baseName + suffix + extension);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    continue;
                try
                {
                    File.Move(tempPath, candidate);
                    return candidate;
                }
                catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    // Retry a name taken by another writer.
                }
            }
        }
        finally
        {
            if (ownsTemp)
            {
                try { File.Delete(tempPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static SKData EncodeBitmap(SKBitmap bitmap, ImageFormatType format, int quality)
    {
        var skFormat = format switch
        {
            ImageFormatType.Jpg => SKEncodedImageFormat.Jpeg,
            ImageFormatType.WebP => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png
        };

        using var image = SKImage.FromBitmap(bitmap);
        int clampedQuality = format == ImageFormatType.Png ? 100 : Math.Clamp(quality, 10, 100);
        return image.Encode(skFormat, clampedQuality)
            ?? throw new InvalidOperationException("图像编码失败");
    }

    public static void EncodeToFile(SKBitmap bitmap, string filePath, ImageFormatType format, int quality)
    {
        using var data = EncodeBitmap(bitmap, format, quality);
        using var stream = File.Create(filePath);
        data.SaveTo(stream);
    }

    public async Task<bool> TryCopyAsync(SKBitmap bitmap)
    {
        try
        {
            return await _copyImageAsync(bitmap);
        }
        catch (Exception ex) when (ex is ExternalException or IOException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> CopyToClipboardAsync(SKBitmap bitmap)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return false;

        var source = await Task.Run(() => ToBitmapSource(bitmap));
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await dispatcher.InvokeAsync(() => Clipboard.SetImage(source));
                return true;
            }
            catch (ExternalException)
            {
                if (attempt < 2)
                    await Task.Delay(40);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return false;
    }

    public static BitmapSource ToBitmapSource(SKBitmap skBitmap)
    {
        SKBitmap converted = skBitmap;
        bool ownsConverted = false;

        if (skBitmap.ColorType != SKColorType.Bgra8888)
        {
            converted = new SKBitmap(skBitmap.Width, skBitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(converted);
            using var img = SKImage.FromBitmap(skBitmap);
            canvas.DrawImage(img, 0, 0, SKSamplingOptions.Default);
            ownsConverted = true;
        }

        try
        {
            int stride = converted.RowBytes;
            int byteCount = stride * converted.Height;
            var wb = BitmapSource.Create(
                converted.Width,
                converted.Height,
                96,
                96,
                PixelFormats.Pbgra32,
                null,
                converted.GetPixels(),
                byteCount,
                stride);
            wb.Freeze();
            return wb;
        }
        finally
        {
            if (ownsConverted)
                converted.Dispose();
        }
    }

    public static SKBitmap? FromClipboardImage()
    {
        SKBitmap? result = null;
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                if (!Clipboard.ContainsImage()) return;
                var src = Clipboard.GetImage();
                if (src is null) return;
                result = FromBitmapSource(src);
            }
            catch
            {
                result = null;
            }
        });
        return result;
    }

    public static SKBitmap FromBitmapSource(BitmapSource source)
    {
        var formatted = source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);

        int width = formatted.PixelWidth;
        int height = formatted.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        formatted.CopyPixels(pixels, stride, 0);

        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return bitmap;
    }

    public static string SanitizeFileNameSegment(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(input.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        cleaned = MultiUnderscoreRegex().Replace(cleaned, "_").Trim(' ', '.', '_');
        if (cleaned.Length > 120) cleaned = cleaned[..120];
        var deviceName = cleaned.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        if (deviceName is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (deviceName.Length == 4 && (deviceName.StartsWith("COM") || deviceName.StartsWith("LPT")) &&
                "123456789¹²³".Contains(deviceName[3])))
        {
            cleaned = "截图_" + cleaned;
        }
        return cleaned;
    }

    public static void OpenFolderInExplorer(string directoryPath, string? selectFilePath = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(selectFilePath) && File.Exists(selectFilePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{selectFilePath}\"")
                {
                    UseShellExecute = true
                });
                return;
            }

            if (Directory.Exists(directoryPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directoryPath}\"")
                {
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Ignore shell open failures
        }
    }

    [GeneratedRegex("_{2,}")]
    private static partial Regex MultiUnderscoreRegex();
}
