using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace Labels.Core;

public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? Carrier { get; set; }
    public string? TrackingNumber { get; set; }
    public DateTime PrintedAt { get; set; } = DateTime.Now;
    public int Copies { get; set; } = 1;
    /// <summary>Resolution the stored image was rendered at (older entries: 203).</summary>
    public int Dpi { get; set; } = 203;

    // UI helpers
    [JsonIgnore] public BitmapSource? Thumbnail { get; set; }
    [JsonIgnore] public string When
    {
        get
        {
            var d = DateTime.Now - PrintedAt;
            if (d.TotalMinutes < 1) return "Just now";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
            if (PrintedAt.Date == DateTime.Today) return PrintedAt.ToString("h:mm tt");
            if (PrintedAt.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
            if (d.TotalDays < 7) return PrintedAt.ToString("dddd");
            return PrintedAt.ToString("MMM d");
        }
    }
    [JsonIgnore] public string Subtitle => Carrier is { } c
        ? (TrackingNumber is { Length: > 4 } t ? $"{c} · …{t[^4..]} · {When}" : $"{c} · {When}")
        : When;
}

/// <summary>Keeps the last labels printed (image + details) in %AppData%\Labels\History for one-click reprints.</summary>
public static class LabelHistory
{
    const int Keep = 150;
    static string Dir => Path.Combine(AppPaths.DataDir, "History");

    public static List<HistoryEntry> Load()
    {
        var list = new List<HistoryEntry>();
        try
        {
            if (!Directory.Exists(Dir)) return list;
            foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
            {
                try
                {
                    var e = JsonSerializer.Deserialize<HistoryEntry>(File.ReadAllText(f));
                    if (e == null) continue;
                    e.Thumbnail = LoadImage(ImagePath(e));
                    if (e.Thumbnail != null) list.Add(e);
                }
                catch { }
            }
        }
        catch { }
        return list.OrderByDescending(e => e.PrintedAt).ToList();
    }

    static string ImagePath(HistoryEntry e) => Path.Combine(Dir, e.Id + ".png");

    static BitmapSource? LoadImage(string path)
    {
        if (!File.Exists(path)) return null;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.UriSource = new Uri(path);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    public static HistoryEntry Add(LabelPage page)
    {
        Directory.CreateDirectory(Dir);
        var e = new HistoryEntry
        {
            Name = page.Title,
            Carrier = page.Carrier,
            TrackingNumber = page.Tracking?.Number,
            Copies = page.Copies,
            Dpi = page.Output?.Dpi ?? 203,
        };
        var bmp = page.OutputPreview ?? LabelPage.ToPreview(page.Output!);
        using (var fs = File.Create(ImagePath(e)))
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            enc.Save(fs);
        }
        File.WriteAllText(Path.Combine(Dir, e.Id + ".json"), JsonSerializer.Serialize(e));
        e.Thumbnail = bmp;
        Prune();
        return e;
    }

    public static MonoBitmap LoadMono(HistoryEntry e) => LabelPage.FromPreview(LoadImage(ImagePath(e))!, e.Dpi);

    public static void Delete(HistoryEntry e)
    {
        try { File.Delete(ImagePath(e)); File.Delete(Path.Combine(Dir, e.Id + ".json")); } catch { }
    }

    static void Prune()
    {
        try
        {
            var all = Load();
            foreach (var old in all.Skip(Keep)) Delete(old);
        }
        catch { }
    }
}
