using System.Globalization;

namespace Labels.Core;

/// <summary>The label stock in the printer: its size and the print head's resolution.</summary>
public sealed record LabelFormat(double WidthIn, double HeightIn, int Dpi)
{
    /// <summary>The format new labels are rendered for. Set from Settings.</summary>
    public static LabelFormat Current { get; set; } = new(4, 6, 203);

    public static readonly int[] Resolutions = [203, 300];

    public static readonly (double W, double H, string Use)[] Presets =
    [
        (4, 6, "Shipping"),
        (4, 4, "Square"),
        (4, 3, "Large address"),
        (4, 2, "Address"),
        (3, 2, "Product"),
        (2.25, 1.25, "Barcode"),
        (2, 1, "Small"),
    ];

    public int WidthDots => (int)Math.Round(WidthIn * Dpi);
    public int HeightDots => (int)Math.Round(HeightIn * Dpi);
    public double WidthMm => WidthIn * 25.4;
    public double HeightMm => HeightIn * 25.4;
    public bool IsPortrait => HeightIn >= WidthIn;
    /// <summary>About 1 mm kept clear on each edge.</summary>
    public int MarginDots => (int)Math.Round(Dpi / 25.4);

    public string SizeText => $"{Num(WidthIn)} × {Num(HeightIn)} in";
    public override string ToString() => $"{SizeText} · {Dpi} dpi";

    static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
