using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Labels.Core;

public sealed record TrackingInfo(string Carrier, string Number)
{
    public string Pretty => Carrier == "UPS" && Number.Length == 18
        ? $"{Number[..2]} {Number[2..5]} {Number[5..8]} {Number[8..10]} {Number[10..14]} {Number[14..]}"
        : string.Join(" ", Enumerable.Range(0, (Number.Length + 3) / 4).Select(i => Number.Substring(i * 4, Math.Min(4, Number.Length - i * 4))));

    public string? TrackUrl => Carrier switch
    {
        "UPS" => $"https://www.ups.com/track?loc=en_US&tracknum={Number}",
        "USPS" => $"https://tools.usps.com/go/TrackConfirmAction?tLabels={Number}",
        "FedEx" => $"https://www.fedex.com/fedextrack/?trknbr={Number}",
        "DHL" => $"https://www.dhl.com/us-en/home/tracking/tracking-express.html?submit=1&tracking-id={Number}",
        "Amazon" => $"https://track.amazon.com/tracking/{Number}",
        "OnTrac" => $"https://www.ontrac.com/tracking/?number={Number}",
        _ => null,
    };

    /// <summary>The carrier's own tracking page, for when the number couldn't be read.</summary>
    public static string? CarrierHome(string? carrier) => carrier switch
    {
        "UPS" => "https://www.ups.com/track?loc=en_US",
        "USPS" => "https://tools.usps.com/go/TrackConfirmAction_input",
        "FedEx" => "https://www.fedex.com/fedextrack/",
        "DHL" => "https://www.dhl.com/us-en/home/tracking.html",
        "Amazon" => "https://track.amazon.com/",
        "OnTrac" => "https://www.ontrac.com/tracking/",
        _ => null,
    };
}

/// <summary>
/// Reads the label with Windows' on-device text recognition to decide which way up it is,
/// and to find the carrier and tracking number.
/// </summary>
public static class LabelInsights
{
    static OcrEngine? _engine;
    static bool _tried;

    static OcrEngine? Engine
    {
        get
        {
            if (_tried) return _engine;
            _tried = true;
            try
            {
                _engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                       ?? OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch { _engine = null; }
            return _engine;
        }
    }

    public static bool Available => Engine != null;

    static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "SHIP", "TO", "FROM", "USPS", "UPS", "FEDEX", "DHL", "PRIORITY", "MAIL", "GROUND", "EXPRESS", "TRACKING",
        "POSTAGE", "PAID", "US", "ZONE", "DATE", "WEIGHT", "LBS", "LB", "OZ", "REF", "INVOICE", "BILLING", "RETURN",
        "SERVICE", "ADVANTAGE", "STANDARD", "HOME", "DELIVERY", "SIGNATURE", "PO", "ST", "AVE", "RD", "APT", "SUITE",
        "STREET", "ROAD", "DRIVE", "LANE", "BLVD", "USA", "UNITED", "STATES", "PACKAGE", "FIRST", "CLASS", "ELECTRONIC",
        "RATE", "APPROVED", "TRK", "SHIPPED", "ORDER", "NY", "CA", "TX", "FL", "IL", "NJ", "PA", "OH", "GA", "WA",
    };

    /// <summary>Higher = more of the text reads as real, upright words.</summary>
    static int Score(OcrResult r)
    {
        int s = 0;
        foreach (var line in r.Lines)
            foreach (var w in line.Words)
            {
                var t = w.Text.Trim(',', '.', ':', ';', '#', '(', ')');
                if (t.Length == 0) continue;
                if (Words.Contains(t)) s += 12;
                else if (Regex.IsMatch(t, @"^\d{5}(-\d{4})?$")) s += 8;            // ZIP code
                else if (Regex.IsMatch(t, @"^[A-Za-z]{3,}$")) s += t.Length;      // plausible word
                else if (Regex.IsMatch(t, @"^\d{3,}$")) s += t.Length / 2;
            }
        return s;
    }

    static async Task<OcrResult?> RecognizeAsync(BitmapSource src)
    {
        var engine = Engine;
        if (engine == null) return null;
        // Keep within the engine's limits and fast: ~1600 px on the long side.
        double k = Math.Min(1.0, 1600.0 / Math.Max(src.PixelWidth, src.PixelHeight));
        BitmapSource b = k < 1 ? new TransformedBitmap(src, new ScaleTransform(k, k)) : src;
        var gray = new FormatConvertedBitmap(b, PixelFormats.Gray8, null, 0);
        int w = gray.PixelWidth, h = gray.PixelHeight;
        var px = new byte[w * h];
        gray.CopyPixels(px, w, 0);
        using var sb = SoftwareBitmap.CreateCopyFromBuffer(px.AsBuffer(), BitmapPixelFormat.Gray8, w, h);
        return await engine.RecognizeAsync(sb);
    }

    /// <summary>
    /// Tries each candidate quarter-turn of the cropped label and returns the one whose text reads best,
    /// along with the recognised text in that orientation. Returns null turns when recognition isn't available
    /// or nothing readable was found.
    /// </summary>
    public static async Task<(int? Turns, string Text)> DetectAsync(BitmapSource piece, IEnumerable<int> candidates)
    {
        if (Engine == null) return (null, "");
        int? best = null; int bestScore = 0; string bestText = "";
        foreach (int turns in candidates)
        {
            BitmapSource rotated = turns == 0 ? piece : new TransformedBitmap(piece, new RotateTransform(turns * 90));
            OcrResult? r;
            try { r = await RecognizeAsync(rotated); }
            catch { continue; }
            if (r == null) continue;
            int s = Score(r);
            if (s > bestScore) { bestScore = s; best = turns; bestText = r.Text; }
        }
        return bestScore >= 20 ? (best, bestText) : (null, bestText);
    }

    /// <summary>Finds the tracking number, trying the detected carrier's format first.</summary>
    public static TrackingInfo? FindTracking(string text, string? carrier = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Text recognition sometimes reads O for 0 inside numbers; fix only when surrounded by digits.
        var upper = Regex.Replace(text.ToUpperInvariant(), @"(?<=\d)O(?=\d)", "0");
        var compact = Regex.Replace(upper, @"\s+", "");

        TrackingInfo? Ups() => Regex.Match(compact, @"1Z[0-9A-Z]{16}") is { Success: true } m ? new("UPS", m.Value) : null;
        // USPS IMpb: 20–22 digits starting 9x, sometimes preceded by the 420+ZIP routing code.
        TrackingInfo? Usps() => Regex.Match(compact, @"(?<!\d)(9[1-5]\d{18,24})(?!\d)") is { Success: true } m ? new("USPS", m.Groups[1].Value) : null;
        TrackingInfo? Amazon() => Regex.Match(compact, @"TBA\d{12}") is { Success: true } m ? new("Amazon", m.Value) : null;
        TrackingInfo? OnTrac() => Regex.Match(compact, @"(?<![A-Z0-9])[CD]\d{14}(?!\d)") is { Success: true } m ? new("OnTrac", m.Value) : null;
        TrackingInfo? Dhl()
        {
            var w = Regex.Match(upper, @"WAYBILL[^\d]{0,12}([\d ]{10,14})");
            var n = w.Success ? w.Groups[1].Value.Replace(" ", "") : null;
            if (n is { Length: 10 }) return new("DHL", n);
            return Regex.Match(compact, @"(?<!\d)(\d{10})(?!\d)") is { Success: true } m ? new("DHL", m.Groups[1].Value) : null;
        }

        var ordered = carrier switch
        {
            "FedEx" => new Func<TrackingInfo?>[] { () => FedEx(upper), Ups, Usps },
            "UPS" => [Ups, () => FedEx(upper), Usps],
            "USPS" => [Usps, Ups, () => FedEx(upper)],
            "DHL" => [Dhl, Ups],
            "Amazon" => [Amazon],
            "OnTrac" => [OnTrac],
            _ => [Ups, Usps, () => FedEx(upper), Amazon],
        };
        foreach (var f in ordered) if (f() is { } t) return t;
        return null;
    }

    /// <summary>
    /// FedEx prints "TRK# 7968 2452 4554" next to a small form-code box (e.g. 0221). Read the 4-digit groups
    /// after TRK# and take the 12- (Express) or 15-digit (Ground) run that isn't the form code.
    /// </summary>
    static TrackingInfo? FedEx(string upper)
    {
        var candidates = new List<(string Number, int Score)>();
        foreach (Match trk in Regex.Matches(upper, @"TRK\s*#?"))
        {
            var after = upper.Substring(trk.Index + trk.Length, Math.Min(70, upper.Length - trk.Index - trk.Length));
            var tokens = Regex.Matches(after, @"\d+").Select(m => m.Value).ToList();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Length is 12 or 15) { candidates.Add((tokens[i], 10 - i)); continue; }
                var sb = new System.Text.StringBuilder();
                for (int j = i; j < tokens.Count && sb.Length < 15; j++)
                {
                    if (tokens[j].Length > 4) break;
                    sb.Append(tokens[j]);
                    if (sb.Length is 12 or 15 && j > i)
                    {
                        // A leading 4-digit group starting with 0 is the form code, not the tracking number.
                        int score = 10 - i - (tokens[i].Length == 4 && tokens[i][0] == '0' ? 8 : 0) + (sb.Length == 12 ? 1 : 0);
                        candidates.Add((sb.ToString(), score));
                    }
                }
            }
        }
        if (candidates.Count == 0)
        {
            // No TRK# marker read: a "#### #### ####" group anywhere on a FedEx label.
            var m = Regex.Match(upper, @"(?<![\d ])([1-9]\d{3}) (\d{4}) (\d{4})(?![ ]?\d)");
            if (m.Success) return new("FedEx", m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value);
            return null;
        }
        return new("FedEx", candidates.OrderByDescending(c => c.Score).First().Number);
    }

    public static string? FindCarrier(string text)
    {
        var u = text.ToUpperInvariant();
        if (Regex.IsMatch(u, @"FEDEX|FED EX|\bTRK\s*#")) return "FedEx";
        if (Regex.IsMatch(u, @"\bUSPS\b|POSTAL SERVICE|PRIORITY MAIL|GROUND ADVANTAGE|FIRST-CLASS")) return "USPS";
        if (Regex.IsMatch(u, @"\bUPS\b|1Z[0-9A-Z]{6}")) return "UPS";
        if (u.Contains("DHL")) return "DHL";
        if (u.Contains("AMAZON") || u.Contains("TBA")) return "Amazon";
        if (u.Contains("ONTRAC")) return "OnTrac";
        return null;
    }
}
