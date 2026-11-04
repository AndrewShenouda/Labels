using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Labels.Core;

/// <summary>Draws a calibration label for any size: border, corner marks, orientation text and bar widths.</summary>
public static class TestLabel
{
    public static MonoBitmap Create(int density, LabelFormat f)
    {
        int W = f.WidthDots, H = f.HeightDots;
        double u = Math.Min(W, H) / 812.0; // 1.0 on a 4-inch-wide label at 203 dpi
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
            double inset = Math.Max(4, 10 * u);
            dc.DrawRectangle(null, new Pen(Brushes.Black, Math.Max(2, 4 * u)), new Rect(inset, inset, W - 2 * inset, H - 2 * inset));
            var bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var reg = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            // Fit each line to the label width.
            double y = inset + 14 * u;
            void Line(string s, double size, Typeface tf)
            {
                var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, Brushes.Black, 1.0);
                double k = Math.Min(1, (W - 2 * inset - 24 * u) / ft.Width);
                dc.PushTransform(new TranslateTransform((W - ft.Width * k) / 2, y));
                dc.PushTransform(new ScaleTransform(k, k));
                dc.DrawText(ft, new Point(0, 0));
                dc.Pop(); dc.Pop();
                y += ft.Height * k + 8 * u;
            }
            Line("▲ TOP", 54 * u, bold);
            Line("Labels", 100 * u, bold);
            Line($"Test Label · {f.SizeText} · {f.Dpi} dpi", 30 * u, reg);
            Line($"Darkness {density}", 30 * u, reg);

            // Bar widths 1..6 dots, like barcode modules (scaled to the label).
            double barH = Math.Min(160 * u, H - y - 140 * u);
            if (barH > 20)
            {
                double x = inset + 30 * u, top = y + 10 * u;
                for (int w = 1; w <= 6; w++)
                {
                    for (int i = 0; i < 6 && x < W - inset - 20 * u; i++) { dc.DrawRectangle(Brushes.Black, null, new Rect(x, top, w, barH)); x += w * 2; }
                    x += 20 * u;
                }
            }
            // Corner ticks to check the label is fully on the paper.
            double c = inset + 20 * u, len = 20 * u, t = Math.Max(2, 4 * u);
            foreach (var (cx, cy) in new[] { (c, c), (W - c, c), (c, H - c), (W - c, H - c) })
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(cx - len, cy - t / 2, 2 * len, t));
                dc.DrawRectangle(Brushes.Black, null, new Rect(cx - t / 2, cy - len, t, 2 * len));
            }
            var bottom = new FormattedText("BOTTOM ▼", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold, 40 * u, Brushes.Black, 1.0);
            dc.DrawText(bottom, new Point((W - bottom.Width) / 2, H - inset - bottom.Height - 30 * u));
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var px = new byte[W * H * 4];
        rtb.CopyPixels(px, W * 4, 0);
        var m = new MonoBitmap(W, H, f.Dpi);
        for (int i = 0, p = 0; i < m.Dots.Length; i++, p += 4) m.Dots[i] = px[p + 1] < 128;
        return m;
    }
}
