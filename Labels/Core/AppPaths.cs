using System.IO;

namespace Labels.Core;

/// <summary>
/// Where Labels keeps its settings, history and logs. LABELS_DATA_DIR overrides it (used for demos and
/// screenshots so real data never appears); LABELS_DOWNLOADS_DIR overrides the watched Downloads folder.
/// </summary>
public static class AppPaths
{
    public static string DataDir =>
        Environment.GetEnvironmentVariable("LABELS_DATA_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Labels");

    public static string? DownloadsOverride =>
        Environment.GetEnvironmentVariable("LABELS_DOWNLOADS_DIR") is { Length: > 0 } d ? d : null;
}
