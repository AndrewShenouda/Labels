using System.Windows;
using Microsoft.Win32;

namespace Labels;

public partial class App : Application
{
    public static bool IsDark { get; private set; }
    /// <summary>Diagnostics only: a label format forced from the command line.</summary>
    public static Core.LabelFormat? FormatOverride { get; private set; }
    public static event Action? ThemeChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) =>
        {
            try
            {
                var dir = Core.AppPaths.DataDir;
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"), $"{DateTime.Now:u} {a.Exception}\n\n");
            }
            catch { }
            a.Handled = true; // keep the app alive; most errors here are recoverable UI glitches
        };
        if (e.Args.Length >= 2 && e.Args[0] is "--probe" or "--test-print" or "--calibrate") { _ = ProbeAsync(e.Args[1], e.Args[0], e.Args.Length > 2 ? e.Args[2] : null); return; }
        // Diagnostics: --format=WxH@DPI overrides the label stock for --render and --snapshot.
        if (e.Args.FirstOrDefault(a => a.StartsWith("--format=")) is { } fmt)
        {
            var m = System.Text.RegularExpressions.Regex.Match(fmt, @"=([\d.]+)x([\d.]+)(?:@(\d+))?");
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (m.Success) Core.LabelFormat.Current = FormatOverride = new(double.Parse(m.Groups[1].Value, inv), double.Parse(m.Groups[2].Value, inv),
                                                          m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 203);
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--render") { _ = RenderToFilesAsync(e.Args[1], e.Args.Length > 2 && !e.Args[2].StartsWith("--") ? e.Args[2] : "."); return; }
        // Diagnostics: --snapshot <out.png> [--dark] [--settings] [files...]
        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
        {
            bool dark = e.Args.Contains("--dark");
            Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative) };
            var files = e.Args.Skip(2).Where(a => !a.StartsWith("--")).ToArray();
            var w = new MainWindow(files);
            MainWindow = w;
            w.Show();
            _ = w.SnapshotAsync(e.Args[1], e.Args.Where(a => a.StartsWith("--")).ToArray());
            return;
        }
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += (_, a) =>
        {
            if (a.Category == UserPreferenceCategory.General) Dispatcher.Invoke(ApplyTheme);
        };
        var win = new MainWindow(e.Args);
        MainWindow = win;
        win.Show();
    }

    /// <summary>Diagnostics: lists paired devices, connects to the printer and asks its status; optionally prints the test label.</summary>
    /// <param name="queue">Optional Windows printer name; otherwise the saved/first Bluetooth printer.</param>
    async Task ProbeAsync(string logPath, string mode, string? queue = null)
    {
        bool print = mode == "--test-print";
        var log = new System.Text.StringBuilder();
        try
        {
            var choices = Core.PrinterChoice.Discover();
            foreach (var c in choices) log.AppendLine($"printer: {c.Display}");
            var settings = Core.AppSettings.Load();
            var f = settings.Format;
            Core.IPrinterTransport printer = queue != null
                ? new Core.WindowsPrinter(queue)
                : new Core.BluetoothPrinter { Address = settings.PrinterKind == Core.PrinterKind.Bluetooth ? settings.PrinterId : settings.DeviceAddress ?? Core.BluetoothPrinter.PairedDevices().FirstOrDefault()?.Address };
            using var _ = printer;
            log.AppendLine($"using {(queue != null ? "Windows printer " + queue : "Bluetooth")} · format {f}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var state = await printer.QueryStatusAsync();
            byte? statusByte = (printer as Core.BluetoothPrinter)?.LastStatusByte;
            log.AppendLine($"status={state} byte={(statusByte is byte b ? "0x" + b.ToString("X2") : "none")} err={printer.LastError} in {sw.ElapsedMilliseconds} ms");
            if (mode == "--calibrate" && state != Core.PrinterState.Offline)
            {
                await printer.SendAsync(Core.Tspl.CalibrateCommand(f), null);
                log.AppendLine("sent GAPDETECT");
                for (int i = 0; i < 8; i++)
                {
                    await Task.Delay(1500);
                    log.AppendLine($"  +{(i + 1) * 1.5:0.0}s status={await printer.QueryStatusAsync()}");
                }
            }
            if (print && state != Core.PrinterState.Offline)
            {
                var job = Core.Tspl.BuildJob([(Core.TestLabel.Create(settings.Density, f), 1)], settings.ToPrintSettings());
                sw.Restart();
                await printer.SendAsync(job, null);
                log.AppendLine($"sent test label: {job.Length} bytes in {sw.ElapsedMilliseconds} ms");
                await Task.Delay(1500);
                log.AppendLine($"status after: {await printer.QueryStatusAsync()}");
            }
        }
        catch (Exception ex) { log.AppendLine("ERROR " + ex); }
        System.IO.File.WriteAllText(logPath, log.ToString());
        Shutdown();
    }

    /// <summary>Diagnostics: renders a file the way it would print and writes PNG previews plus the TSPL job.</summary>
    async Task RenderToFilesAsync(string input, string outDir)
    {
        var log = new System.Text.StringBuilder();
        try
        {
            System.IO.Directory.CreateDirectory(outDir);
            var name = System.IO.Path.GetFileNameWithoutExtension(input);
            var pages = await Core.LabelPage.LoadFileAsync(input);
            var items = new List<(Core.MonoBitmap, int)>();
            foreach (var p in pages)
            {
                var file = System.IO.Path.Combine(outDir, $"{name}_p{p.PageNumber}_{p.LabelOnPage}.png");
                log.AppendLine($"  ocr={Core.LabelInsights.Available} upright-from-text={p.OrientationFromText} carrier={p.Carrier} tracking={p.Tracking?.Number} url={p.Tracking?.TrackUrl}");
                log.AppendLine("  text: " + p.LastOcrText.Replace("\r", " ").Replace("\n", " | "));
                using (var fs = System.IO.File.Create(file))
                {
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(p.OutputPreview!));
                    enc.Save(fs);
                }
                items.Add((p.Output!, 1));
                log.AppendLine($"page {p.PageNumber}: crop={p.Crop} auto={p.IsAutoCrop} -> {file}");
            }
            var job = Core.Tspl.BuildJob(items, new Core.PrintSettings());
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, name + ".tspl"), job);
            log.AppendLine($"job bytes={job.Length} format={Core.LabelFormat.Current}");
        }
        catch (Exception ex) { log.AppendLine("ERROR " + ex); }
        System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "render.log"), log.ToString());
        Shutdown();
    }

    static bool SystemPrefersDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    void ApplyTheme()
    {
        bool dark = SystemPrefersDark();
        if (dark == IsDark && Resources.MergedDictionaries[0].Source != null && ThemeLoaded) return;
        IsDark = dark;
        ThemeLoaded = true;
        Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        };
        ThemeChanged?.Invoke();
    }

    static bool ThemeLoaded;
}
