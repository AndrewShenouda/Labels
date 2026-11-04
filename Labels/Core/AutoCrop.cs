using System.Windows;

namespace Labels.Core;

/// <summary>
/// Finds the shipping label on a page. Pages from carriers and marketplaces are often letter-size with the
/// 4×6 label in one corner and instructions or a receipt elsewhere. We split the page on wide whitespace
/// (recursive XY-cut) and pick the block that best matches a 4×6 label in shape and size.
/// </summary>
public static class AutoCrop
{
    /// <param name="ink">Grayscale page, true where dark.</param>
    /// <param name="dpi">Pixels per inch of the analysis image, or 0 when unknown (plain images).</param>
    /// <returns>Crop rectangle in normalized page coordinates (0..1).</returns>
    public static Rect Find(bool[] ink, int w, int h, double dpi, double labelW = 4, double labelH = 6) => FindAll(ink, w, h, dpi, labelW, labelH)[0];

    /// <summary>
    /// All labels on the page, in reading order. Sheets with two labels side by side (or stacked)
    /// come back as two rectangles.
    /// </summary>
    public static List<Rect> FindAll(bool[] ink, int w, int h, double dpi, double labelW = 4, double labelH = 6)
    {
        var all = Bounds(ink, w, new Int32Rect(0, 0, w, h));
        if (all.Width <= 0) return [new Rect(0, 0, 1, 1)];

        if (dpi > 0)
        {
            double pw = w / dpi, ph = h / dpi;
            // Already a 4×6 page (either orientation): keep the whole page so the layout is untouched.
            if (Math.Max(pw, ph) <= Math.Max(labelW, labelH) * 1.1 && Math.Min(pw, ph) <= Math.Min(labelW, labelH) * 1.1 && AspectScore(pw, ph, Ratio(labelW, labelH)) > 0.85)
                return [new Rect(0, 0, 1, 1)];
        }

        var scored = Score(ink, w, h, dpi, all, labelW, labelH);
        var best = scored[0];
        var chosen = new List<Int32Rect> { best.Rect };
        // Only page-size-aware (PDF) analysis can tell a second label from a block of text.
        if (dpi > 0 && best.Score > 0.5)
        {
            foreach (var c in scored.Skip(1))
            {
                if (c.Score < best.Score * 0.7) break;
                if (chosen.Any(r => Overlap(r, c.Rect) > 0.08)) continue;
                chosen.Add(c.Rect);
            }
        }
        // Reading order: rows top to bottom, then left to right.
        chosen = chosen.OrderBy(r => r.Y / (h / 4)).ThenBy(r => r.X).ToList();

        int pad = dpi > 0 ? (int)(dpi * 0.04) : 4;
        return chosen.Select(b =>
        {
            int x0 = Math.Max(0, b.X - pad), y0 = Math.Max(0, b.Y - pad);
            int x1 = Math.Min(w, b.X + b.Width + pad), y1 = Math.Min(h, b.Y + b.Height + pad);
            return new Rect((double)x0 / w, (double)y0 / h, (double)(x1 - x0) / w, (double)(y1 - y0) / h);
        }).ToList();
    }

    static double Overlap(Int32Rect a, Int32Rect b)
    {
        int x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
        int x1 = Math.Min(a.X + a.Width, b.X + b.Width), y1 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (x1 <= x0 || y1 <= y0) return 0;
        double inter = (double)(x1 - x0) * (y1 - y0);
        return inter / Math.Min((double)a.Width * a.Height, (double)b.Width * b.Height);
    }

    static List<(Int32Rect Rect, double Score)> Score(bool[] ink, int w, int h, double dpi, Int32Rect all, double labelW, double labelH)
    {

        var leaves = new List<Int32Rect>();
        int gap = dpi > 0 ? (int)(dpi * 0.2) : Math.Max(12, Math.Max(w, h) / 30);
        Cut(ink, w, all, gap, leaves, 0);

        var candidates = new List<Int32Rect>(leaves) { all };
        // Merge pairs of neighbouring leaves too: some labels have a wide internal gap.
        for (int i = 0; i < leaves.Count; i++)
            for (int j = i + 1; j < leaves.Count; j++)
                candidates.Add(Union(leaves[i], leaves[j]));

        var result = new List<(Int32Rect, double)>();
        foreach (var c in candidates.Distinct())
        {
            if (c.Width < w * 0.15 && c.Height < h * 0.15) continue;
            double score = AspectScore(c.Width, c.Height, Ratio(labelW, labelH));
            if (dpi > 0)
            {
                double area = c.Width / dpi * (c.Height / dpi);
                score *= Math.Exp(-Math.Abs(Math.Log(area / (labelW * labelH))) * 1.2); // label area in sq in
            }
            else
            {
                score *= Math.Sqrt((double)c.Width * c.Height / ((double)all.Width * all.Height));
            }
            result.Add((c, score));
        }
        if (result.Count == 0) result.Add((all, 0));
        return result.OrderByDescending(r => r.Item2).ToList();
    }

    /// <summary>Long side ÷ short side of the label (orientation doesn't matter: labels get rotated to fit).</summary>
    static double Ratio(double w, double h) => Math.Max(w, h) / Math.Max(1e-6, Math.Min(w, h));

    static double AspectScore(double a, double b, double target)
    {
        double r = Math.Max(a, b) / Math.Max(1e-6, Math.Min(a, b));
        return Math.Exp(-Math.Abs(Math.Log(r / target)) * 3);
    }

    static Int32Rect Union(Int32Rect a, Int32Rect b)
    {
        int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
        int x1 = Math.Max(a.X + a.Width, b.X + b.Width), y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }

    static Int32Rect Bounds(bool[] ink, int w, Int32Rect r)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = r.Y; y < r.Y + r.Height; y++)
            for (int x = r.X; x < r.X + r.Width; x++)
                if (ink[y * w + x]) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        return x1 < 0 ? new Int32Rect(r.X, r.Y, 0, 0) : new Int32Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    static void Cut(bool[] ink, int w, Int32Rect r, int gap, List<Int32Rect> leaves, int depth)
    {
        if (depth > 6 || r.Width <= 0 || r.Height <= 0) { if (r.Width > 0) leaves.Add(r); return; }

        var rows = new bool[r.Height]; var cols = new bool[r.Width];
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
                if (ink[(r.Y + y) * w + r.X + x]) { rows[y] = true; cols[x] = true; }

        var rowSegs = Segments(rows, gap);
        var colSegs = Segments(cols, gap);
        if (rowSegs.Count <= 1 && colSegs.Count <= 1) { leaves.Add(r); return; }

        // Split along the axis with the larger gap structure first.
        bool byRows = rowSegs.Count > 1 && (colSegs.Count <= 1 || r.Height >= r.Width);
        foreach (var (a, b) in byRows ? rowSegs : colSegs)
        {
            var sub = byRows ? new Int32Rect(r.X, r.Y + a, r.Width, b - a) : new Int32Rect(r.X + a, r.Y, b - a, r.Height);
            Cut(ink, w, Bounds(ink, w, sub), gap, leaves, depth + 1);
        }
    }

    static List<(int, int)> Segments(bool[] filled, int gap)
    {
        var segs = new List<(int, int)>();
        int start = -1, lastInk = -1;
        for (int i = 0; i < filled.Length; i++)
        {
            if (!filled[i]) continue;
            if (start < 0) start = i;
            else if (i - lastInk > gap) { segs.Add((start, lastInk + 1)); start = i; }
            lastInk = i;
        }
        if (start >= 0) segs.Add((start, lastInk + 1));
        return segs;
    }
}
