using System.IO;
using System.Text.Json;

namespace Labels.Core;

public sealed class AppSettings
{
    /// <summary>Bluetooth address of the printer, 12 hex digits (older settings files).</summary>
    public string? DeviceAddress { get; set; }
    /// <summary>The chosen printer: Bluetooth (Id = address) or a Windows printer (Id = queue name).</summary>
    public PrinterKind? PrinterKind { get; set; }
    public string? PrinterId { get; set; }

    public double LabelWidthIn { get; set; } = 4;
    public double LabelHeightIn { get; set; } = 6;
    public int Dpi { get; set; } = 203;

    public LabelFormat Format => new(
        Math.Clamp(LabelWidthIn, 0.5, 4.65), Math.Clamp(LabelHeightIn, 0.4, 12),
        LabelFormat.Resolutions.Contains(Dpi) ? Dpi : 203);
    public int Density { get; set; } = 8;
    public int Speed { get; set; } = 4;
    public bool Flip { get; set; }
    public bool InvertBits { get; set; }
    public MediaType Media { get; set; } = MediaType.Sensed;
    public bool WatchDownloads { get; set; } = true;
    /// <summary>Print shipping-label PDFs that land in Downloads without asking.</summary>
    public bool AutoPrintDownloads { get; set; }
    /// <summary>Custom stamp images used recently, newest first.</summary>
    public List<string> RecentStamps { get; set; } = new();
    public bool ClearAfterPrint { get; set; } = true;

    static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public PrintSettings ToPrintSettings() => new()
    {
        Density = Density, Speed = Speed, Flip = Flip, InvertBits = InvertBits, Media = Media,
    };
}
