using System.IO;
using System.Text;

namespace Labels.Core;

/// <summary>A 1-bit label image: true = black dot. Row-major, Width x Height dots.</summary>
public sealed class MonoBitmap
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Dots per inch the image was rendered for.</summary>
    public int Dpi { get; }
    public bool[] Dots { get; }
    public MonoBitmap(int w, int h, int dpi = 203) { Width = w; Height = h; Dpi = dpi; Dots = new bool[w * h]; }
    public bool this[int x, int y] => Dots[y * Width + x];
    public double WidthMm => Width * 25.4 / Dpi;
    public double HeightMm => Height * 25.4 / Dpi;
}

public sealed class PrintSettings
{
    public int Density { get; set; } = 8;     // 0..15
    public int Speed { get; set; } = 4;       // inches per second
    public bool Flip { get; set; }            // rotate output 180°
    public bool InvertBits { get; set; }      // some firmwares treat 1 as black
    public MediaType Media { get; set; } = MediaType.Sensed;
}

public enum MediaType
{
    /// <summary>Let the printer find each label's edge with its sensor (as taught by Calibrate). We send no GAP command.</summary>
    Sensed,
    /// <summary>No gaps to sense: print exactly one label length and stop. For continuous, perforated rolls.</summary>
    Continuous,
}

/// <summary>Builds TSPL jobs. Only rows/columns that contain ink are sent, which matters over Bluetooth.</summary>
public static class Tspl
{
    static string Mm(double v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " mm";

    public static byte[] BuildJob(IReadOnlyList<(MonoBitmap Image, int Copies)> labels, PrintSettings s)
    {
        using var ms = new MemoryStream();
        void Cmd(string c) { var b = Encoding.ASCII.GetBytes(c + "\r\n"); ms.Write(b); }

        string? lastSize = null;
        void Size(MonoBitmap img)
        {
            // Each label carries its own size, so mixed sizes in one job still print correctly.
            var size = $"SIZE {Mm(img.WidthMm)},{Mm(img.HeightMm)}";
            if (size != lastSize) { Cmd(size); lastSize = size; }
        }

        if (labels.Count > 0) Size(labels[0].Image);
        // Sensed: don't override what calibration taught the printer. Continuous: no gap at all.
        if (s.Media == MediaType.Continuous) Cmd("GAP 0 mm,0 mm");
        // DIRECTION 0 makes the top of the label come out of the printer first.
        Cmd($"DIRECTION {(s.Flip ? 1 : 0)},0");
        Cmd("REFERENCE 0,0");
        Cmd("OFFSET 0 mm");
        Cmd("SET TEAR ON");
        Cmd($"DENSITY {Math.Clamp(s.Density, 0, 15)}");
        Cmd($"SPEED {Math.Clamp(s.Speed, 1, 6)}");

        foreach (var (img, copies) in labels)
        {
            if (copies <= 0) continue;
            Size(img);
            Cmd("CLS");
            foreach (var band in Bands(img))
            {
                var head = Encoding.ASCII.GetBytes($"BITMAP {band.ByteX * 8},{band.Y},{band.ByteW},{band.H},0,");
                ms.Write(head);
                ms.Write(Pack(img, band, s.InvertBits));
                ms.Write("\r\n"u8);
            }
            Cmd($"PRINT 1,{copies}");
        }
        return ms.ToArray();
    }

    public static byte[] FeedCommand() => Encoding.ASCII.GetBytes("FORMFEED\r\n");
    /// <summary>Teaches the printer where each label starts by feeding a few labels past its gap sensor.</summary>
    public static byte[] CalibrateCommand(LabelFormat f) =>
        Encoding.ASCII.GetBytes($"SIZE {Mm(f.WidthMm)},{Mm(f.HeightMm)}\r\nGAPDETECT\r\n");
    record struct Band(int Y, int H, int ByteX, int ByteW);

    static IEnumerable<Band> Bands(MonoBitmap img)
    {
        int bytesPerRow = (img.Width + 7) / 8;
        // Per-row ink extents in bytes (-1 = blank).
        var lo = new int[img.Height]; var hi = new int[img.Height];
        for (int y = 0; y < img.Height; y++)
        {
            lo[y] = -1; hi[y] = -1;
            int row = y * img.Width;
            for (int x = 0; x < img.Width; x++)
                if (img.Dots[row + x]) { if (lo[y] < 0) lo[y] = x / 8; hi[y] = x / 8; }
        }

        const int mergeGap = 24; // blank rows tolerated inside a band (command overhead vs. bytes)
        int yStart = -1, last = -1, bl = int.MaxValue, bh = -1;
        for (int y = 0; y <= img.Height; y++)
        {
            bool ink = y < img.Height && lo[y] >= 0;
            if (ink)
            {
                if (yStart < 0) { yStart = y; bl = lo[y]; bh = hi[y]; }
                else if (y - last > mergeGap)
                {
                    yield return new Band(yStart, last - yStart + 1, bl, bh - bl + 1);
                    yStart = y; bl = lo[y]; bh = hi[y];
                }
                bl = Math.Min(bl, lo[y]); bh = Math.Max(bh, hi[y]);
                last = y;
            }
        }
        if (yStart >= 0) yield return new Band(yStart, last - yStart + 1, bl, Math.Min(bh, bytesPerRow - 1) - bl + 1);
    }

    static byte[] Pack(MonoBitmap img, Band b, bool invert)
    {
        var data = new byte[b.ByteW * b.H];
        int i = 0;
        for (int y = b.Y; y < b.Y + b.H; y++)
        {
            for (int bx = b.ByteX; bx < b.ByteX + b.ByteW; bx++)
            {
                int v = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    int x = bx * 8 + bit;
                    bool black = x < img.Width && img.Dots[y * img.Width + x];
                    if (black) v |= 0x80 >> bit;
                }
                // TSPL BITMAP: a 0 bit prints a black dot.
                data[i++] = (byte)(invert ? v : ~v);
            }
        }
        return data;
    }
}
