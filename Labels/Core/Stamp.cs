using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Labels.Core;

public enum StampKind { Fragile, Image }

/// <summary>An image or preset stamped onto a label. Position and size are fractions of the 4×6 label.</summary>
public sealed class Stamp
{
    public StampKind Kind { get; init; }
    public string? ImagePath { get; init; }
    public string Name => Kind == StampKind.Fragile ? "Fragile — Handle With Care" : Path.GetFileNameWithoutExtension(ImagePath ?? "Image");

    /// <summary>Centre of the stamp, 0..1 across/down the label.</summary>
    public double CenterX { get; set; } = 0.5;
    public double CenterY { get; set; } = 0.8;
    /// <summary>Stamp width as a fraction of the label width.</summary>
    public double Width { get; set; } = 0.6;
    /// <summary>Faint = a light dotted watermark that lets the label show through.</summary>
    public bool Faint { get; set; }

    /// <summary>Width ÷ height of the artwork.</summary>
    public double Aspect => Art.Aspect;

    StampArt? _art;
    StampArt Art => _art ??= StampArt.Get(this);

    public Stamp Clone() => new() { Kind = Kind, ImagePath = ImagePath, CenterX = CenterX, CenterY = CenterY, Width = Width, Faint = Faint, _art = _art };

    /// <summary>The stamp's rectangle in label dots.</summary>
    public Int32Rect Bounds(int labelW, int labelH)
    {
        int w = Math.Max(8, (int)Math.Round(Width * labelW));
        int h = Math.Max(8, (int)Math.Round(w / Aspect));
        int x = (int)Math.Round(CenterX * labelW - w / 2.0);
        int y = (int)Math.Round(CenterY * labelH - h / 2.0);
        return new Int32Rect(x, y, w, h);
    }

    /// <summary>Draws the stamp onto a copy of the label.</summary>
    public MonoBitmap ApplyTo(MonoBitmap label)
    {
        var result = new MonoBitmap(label.Width, label.Height);
        Array.Copy(label.Dots, result.Dots, label.Dots.Length);
        var b = Bounds(label.Width, label.Height);
        var layer = Art.Render(b.Width, b.Height); // sbyte: 1 = black, -1 = white (knockout), 0 = transparent
        for (int y = 0; y < b.Height; y++)
        {
            int ly = b.Y + y;
            if (ly < 0 || ly >= label.Height) continue;
            for (int x = 0; x < b.Width; x++)
            {
                int lx = b.X + x;
                if (lx < 0 || lx >= label.Width) continue;
                sbyte v = layer[y * b.Width + x];
                if (v == 0) continue;
                int i = ly * label.Width + lx;
                if (Faint)
                {
                    // Light watermark: a sparse dot screen, never erasing what's printed underneath.
                    if (v > 0 && (lx % 3 == 0) && (ly % 3 == 0 || ly % 3 == 1 && lx % 6 == 0)) result.Dots[i] = true;
                }
                else result.Dots[i] = v > 0;
            }
        }
        return result;
    }

    /// <summary>How much existing ink the stamp covers (0..1). Used to warn before covering text or a barcode.</summary>
    public double Coverage(MonoBitmap label)
    {
        var b = Bounds(label.Width, label.Height);
        long ink = 0, total = 0;
        for (int y = Math.Max(0, b.Y); y < Math.Min(label.Height, b.Y + b.Height); y++)
            for (int x = Math.Max(0, b.X); x < Math.Min(label.Width, b.X + b.Width); x++)
            {
                total++;
                if (label.Dots[y * label.Width + x]) ink++;
            }
        return total == 0 ? 0 : (double)ink / total;
    }

    /// <summary>
    /// Finds the biggest empty area of the label that fits the stamp, preferring the lower half,
    /// and moves/sizes the stamp there. Returns false if nothing empty was found.
    /// </summary>
    public bool AutoPlace(MonoBitmap label)
    {
        const int cell = 6;
        int gw = label.Width / cell, gh = label.Height / cell;
        // Integral image of "cells containing ink".
        var sum = new int[(gw + 1) * (gh + 1)];
        for (int cy = 0; cy < gh; cy++)
        {
            int rowSum = 0;
            for (int cx = 0; cx < gw; cx++)
            {
                bool ink = false;
                for (int y = cy * cell; y < cy * cell + cell && !ink; y++)
                    for (int x = cx * cell; x < cx * cell + cell; x++)
                        if (label.Dots[y * label.Width + x]) { ink = true; break; }
                rowSum += ink ? 1 : 0;
                sum[(cy + 1) * (gw + 1) + cx + 1] = sum[cy * (gw + 1) + cx + 1] + rowSum;
            }
        }
        int Ink(int x, int y, int w, int h) =>
            sum[(y + h) * (gw + 1) + x + w] - sum[y * (gw + 1) + x + w] - sum[(y + h) * (gw + 1) + x] + sum[y * (gw + 1) + x];

        const int pad = 2; // keep a little white around the stamp
        for (double frac = 0.8; frac >= 0.22; frac -= 0.04)
        {
            int wc = (int)Math.Round(frac * gw) + 2 * pad;
            int hc = (int)Math.Round(frac * gw / Aspect) + 2 * pad;
            if (wc >= gw - 2 || hc >= gh - 2) continue;
            int bestX = -1, bestY = -1; double bestScore = double.MinValue;
            for (int y = 2; y + hc <= gh - 2; y++)
                for (int x = 2; x + wc <= gw - 2; x++)
                {
                    if (Ink(x, y, wc, hc) > 0) continue;
                    // Prefer lower on the label, and centred.
                    double score = y - Math.Abs(x + wc / 2.0 - gw / 2.0) * 0.5;
                    if (score > bestScore) { bestScore = score; bestX = x; bestY = y; }
                }
            if (bestX >= 0)
            {
                Width = frac;
                CenterX = (bestX + wc / 2.0) * cell / label.Width;
                CenterY = (bestY + hc / 2.0) * cell / label.Height;
                return true;
            }
        }
        Width = 0.55; CenterX = 0.5; CenterY = 0.88;
        return false;
    }

    public static Stamp Fragile() => new() { Kind = StampKind.Fragile };
    public static Stamp FromImage(string path) => new() { Kind = StampKind.Image, ImagePath = path };
}

/// <summary>Cached artwork for a stamp, rendered to 1-bit at the requested size.</summary>
sealed class StampArt
{
    static readonly Dictionary<string, StampArt> Cache = new();
    readonly Func<int, int, sbyte[]> _render;
    readonly Dictionary<(int, int), sbyte[]> _sizes = new();
    public double Aspect { get; }

    StampArt(double aspect, Func<int, int, sbyte[]> render) { Aspect = aspect; _render = render; }

    public sbyte[] Render(int w, int h)
    {
        if (_sizes.TryGetValue((w, h), out var r)) return r;
        if (_sizes.Count > 8) _sizes.Clear();
        return _sizes[(w, h)] = _render(w, h);
    }

    public static StampArt Get(Stamp s)
    {
        string key = s.Kind == StampKind.Fragile ? "fragile" : "img:" + s.ImagePath;
        if (Cache.TryGetValue(key, out var a)) return a;
        a = s.Kind == StampKind.Fragile ? FragileArt() : ImageArt(s.ImagePath!);
        return Cache[key] = a;
    }

    // ---------- "Fragile — Handle With Care" ----------

    const double FragileW = 300, FragileH = 104;

    static StampArt FragileArt() => new(FragileW / FragileH, (w, h) => Rasterize(w, h, FragileW, FragileH, DrawFragile, dither: false));

    public static void DrawFragile(DrawingContext dc)
    {
        var black = Brushes.Black;
        // White card with a heavy rounded border (knocks out anything underneath).
        dc.DrawRoundedRectangle(Brushes.White, new Pen(black, 7), new Rect(3.5, 3.5, FragileW - 7, FragileH - 7), 14, 14);
        // Wine glass icon.
        var glass = Geometry.Parse("M 22,18 L 66,18 C 66,44 58,56 48,58 L 48,78 L 60,82 L 60,88 L 28,88 L 28,82 L 40,78 L 40,58 C 30,56 22,44 22,18 Z");
        dc.DrawGeometry(black, null, glass);
        // Crack in the glass.
        dc.DrawGeometry(null, new Pen(Brushes.White, 3) { LineJoin = PenLineJoin.Miter },
            Geometry.Parse("M 47,20 L 41,30 L 50,36 L 43,46"));
        // Text.
        var bold = new Typeface(new FontFamily("Arial Black, Segoe UI Black, Arial"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);
        var semi = new Typeface(new FontFamily("Arial, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var t1 = new FormattedText("FRAGILE", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold, 46, black, 1.0);
        var t2 = new FormattedText("HANDLE WITH CARE", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, semi, 17, black, 1.0);
        double left = 78, room = FragileW - left - 16;
        double s1 = Math.Min(1, room / t1.Width), s2 = Math.Min(1, room / t2.Width);
        dc.PushTransform(new TranslateTransform(left + (room - t1.Width * s1) / 2, 12));
        dc.PushTransform(new ScaleTransform(s1, s1));
        dc.DrawText(t1, new Point(0, 0));
        dc.Pop(); dc.Pop();
        dc.DrawRectangle(black, null, new Rect(left + 4, 66, room - 8, 3));
        dc.PushTransform(new TranslateTransform(left + (room - t2.Width * s2) / 2, 72));
        dc.PushTransform(new ScaleTransform(s2, s2));
        dc.DrawText(t2, new Point(0, 0));
        dc.Pop(); dc.Pop();
    }

    // ---------- Custom images ----------

    static StampArt ImageArt(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.UriSource = new Uri(Path.GetFullPath(path));
        bi.EndInit();
        bi.Freeze();
        double iw = bi.PixelWidth, ih = bi.PixelHeight;
        return new StampArt(iw / ih, (w, h) => Rasterize(w, h, iw, ih,
            dc => dc.DrawImage(bi, new Rect(0, 0, iw, ih)), dither: true));
    }

    /// <summary>
    /// Renders artwork at w×h dots. Opaque dark pixels become black, opaque light pixels become white (knockout),
    /// transparent pixels leave the label alone. Photos and gradients are dithered so they read on thermal paper.
    /// </summary>
    static sbyte[] Rasterize(int w, int h, double srcW, double srcH, Action<DrawingContext> draw, bool dither)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(w / srcW, h / srcH));
            draw(dc);
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);

        var lum = new float[w * h];
        var alpha = new byte[w * h];
        for (int i = 0, p = 0; i < lum.Length; i++, p += 4)
        {
            alpha[i] = px[p + 3];
            // Un-premultiply over white so anti-aliased edges and transparent areas read as light.
            float a = px[p + 3] / 255f;
            float l = (px[p] * 0.114f + px[p + 1] * 0.587f + px[p + 2] * 0.299f);
            lum[i] = l + (1 - a) * 255f;
        }

        var outp = new sbyte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (alpha[i] < 100) { outp[i] = 0; continue; }
                float old = lum[i];
                bool black = old < 128;
                outp[i] = (sbyte)(black ? 1 : -1);
                if (!dither) continue;
                // Atkinson dithering: keeps logos crisp and gives photos texture.
                float err = (old - (black ? 0 : 255)) / 8f;
                void Spread(int dx, int dy) { int xx = x + dx, yy = y + dy; if (xx >= 0 && xx < w && yy < h) lum[yy * w + xx] += err; }
                Spread(1, 0); Spread(2, 0); Spread(-1, 1); Spread(0, 1); Spread(1, 1); Spread(0, 2);
            }
        return outp;
    }
}
