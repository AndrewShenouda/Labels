using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Labels.Core;

/// <summary>One printable label: a region of a PDF page or image, plus orientation, crop and copies.</summary>
public sealed class LabelPage : INotifyPropertyChanged
{
    const double AnalysisDpi = 100;

    public string SourceName { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public int PageNumber { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    /// <summary>Which label on the page (1-based) when a page holds several.</summary>
    public int LabelOnPage { get; init; } = 1;
    public int LabelsOnPage { get; init; } = 1;

    PdfPage? _pdfPage;
    BitmapSource? _image;
    /// <summary>For reprints from history: the stored 1-bit label, not editable.</summary>
    MonoBitmap? _fixed;

    public bool IsEditable => _fixed == null;
    public BitmapSource SourcePreview { get; private set; } = null!;
    public double SourceAspect => SourcePreview.PixelWidth / (double)SourcePreview.PixelHeight;

    Rect _autoCrop = new(0, 0, 1, 1);
    Rect _crop = new(0, 0, 1, 1);
    public Rect Crop { get => _crop; private set { _crop = value; OnChanged(); OnChanged(nameof(IsAutoCrop)); } }
    public bool IsAutoCrop => _crop == _autoCrop;
    public Rect AutoCropRect => _autoCrop;

    int? _autoTurns;          // from text recognition; null = unknown
    bool _orientationStale = true;
    int _turns;
    /// <summary>Extra quarter turns the user asked for, on top of automatic orientation.</summary>
    public int Turns { get => _turns; set { _turns = ((value % 4) + 4) % 4; OnChanged(); } }
    public bool OrientationFromText => _autoTurns != null;

    int _copies = 1;
    public int Copies { get => _copies; set { _copies = Math.Clamp(value, 1, 99); OnChanged(); OnChanged(nameof(CopiesText)); } }
    public string CopiesText => _copies == 1 ? "1 copy" : $"{_copies} copies";

    /// <summary>Text read from the label (diagnostics).</summary>
    public string LastOcrText { get; private set; } = "";

    TrackingInfo? _tracking;
    public TrackingInfo? Tracking { get => _tracking; private set { _tracking = value; OnChanged(); OnChanged(nameof(Subtitle)); } }
    string? _carrier;
    public string? Carrier { get => _carrier ?? _tracking?.Carrier; private set { _carrier = value; OnChanged(); OnChanged(nameof(Subtitle)); } }

    MonoBitmap? _output;
    /// <summary>What gets printed: the rendered label with any stamp applied.</summary>
    public MonoBitmap? Output { get => _output; private set { _output = value; OnChanged(); } }

    /// <summary>The rendered label before a stamp is applied.</summary>
    MonoBitmap? _base;

    Stamp? _stamp;
    public Stamp? Stamp => _stamp;

    /// <summary>Adds, replaces or removes the stamp. New stamps are placed in the label's biggest empty area.</summary>
    public bool SetStamp(Stamp? stamp, bool autoPlace = true)
    {
        _stamp = stamp;
        bool placed = true;
        if (stamp != null && autoPlace && _base != null) placed = stamp.AutoPlace(_base);
        Recompose();
        OnChanged(nameof(Stamp));
        return placed;
    }

    /// <summary>Call after moving or resizing the stamp.</summary>
    public void Recompose()
    {
        if (_base == null) return;
        Output = _stamp?.ApplyTo(_base) ?? _base;
        OutputPreview = ToPreview(Output);
        OnChanged(nameof(StampCoverage));
    }

    /// <summary>Share of the stamp's area that lands on existing print (text, barcodes).</summary>
    public double StampCoverage => _stamp != null && _base != null ? _stamp.Coverage(_base) : 0;

    BitmapSource? _outputPreview;
    public BitmapSource? OutputPreview { get => _outputPreview; private set { _outputPreview = value; OnChanged(); } }

    public string Title
    {
        get
        {
            var n = Path.GetFileNameWithoutExtension(SourceName);
            if (PageCount > 1) n += $" · p{PageNumber}";
            if (LabelsOnPage > 1) n += $" · {LabelOnPage} of {LabelsOnPage}";
            return n;
        }
    }

    public string Subtitle => Carrier is { } c
        ? (Tracking is { } t ? $"{c} · …{t.Number[^4..]}" : c)
        : (IsEditable ? "4 × 6" : "Reprint");

    // ---------- loading ----------

    public static async Task<List<LabelPage>> LoadFileAsync(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf") return await LoadPdfAsync(path);
        var img = LoadImage(path);
        var crops = Analyze(img, 0);
        var page = new LabelPage { SourceName = Path.GetFileName(path), SourcePath = path, _image = img, SourcePreview = img };
        page._autoCrop = page._crop = crops[0];
        await page.RenderAsync();
        return [page];
    }

    public static async Task<LabelPage> FromBitmapAsync(BitmapSource bmp, string name)
    {
        var img = ToBgra(bmp);
        var page = new LabelPage { SourceName = name, _image = img, SourcePreview = img };
        page._autoCrop = page._crop = Analyze(img, 0)[0];
        await page.RenderAsync();
        return page;
    }

    public static LabelPage FromHistory(HistoryEntry e, MonoBitmap mono)
    {
        var p = new LabelPage { SourceName = e.Name, _fixed = mono };
        p._tracking = e.TrackingNumber is { } n ? new TrackingInfo(e.Carrier ?? "", n) : null;
        p._carrier = e.Carrier;
        p._base = mono;
        p.Output = mono;
        p.OutputPreview = ToPreview(mono);
        p.SourcePreview = p.OutputPreview;
        return p;
    }

    static async Task<List<LabelPage>> LoadPdfAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        PdfDocument doc;
        try { doc = await PdfDocument.LoadFromFileAsync(file); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B))
        { throw new InvalidOperationException("This PDF is password-protected."); }

        var pages = new List<LabelPage>();
        for (uint i = 0; i < doc.PageCount; i++)
        {
            var pdfPage = doc.GetPage(i);
            var size = pdfPage.Size; // DIPs, 96 per inch
            uint w = (uint)Math.Max(1, size.Width / 96 * AnalysisDpi), h = (uint)Math.Max(1, size.Height / 96 * AnalysisDpi);
            var preview = await RenderPdfAsync(pdfPage, new Rect(0, 0, size.Width, size.Height), w, h);
            if (IsBlank(preview)) continue; // skip empty trailing pages
            var crops = Analyze(preview, AnalysisDpi);
            for (int k = 0; k < crops.Count; k++)
            {
                var page = new LabelPage
                {
                    SourceName = Path.GetFileName(path), SourcePath = path,
                    PageNumber = (int)i + 1, PageCount = (int)doc.PageCount,
                    LabelOnPage = k + 1, LabelsOnPage = crops.Count,
                    _pdfPage = pdfPage, SourcePreview = preview,
                };
                page._autoCrop = page._crop = crops[k];
                await page.RenderAsync();
                pages.Add(page);
            }
        }
        if (pages.Count == 0) throw new InvalidOperationException("This PDF doesn't contain anything to print.");
        return pages;
    }

    static BitmapSource LoadImage(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.UriSource = new Uri(Path.GetFullPath(path));
        bi.EndInit();
        return ToBgra(bi);
    }

    static BitmapSource ToBgra(BitmapSource src)
    {
        var conv = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var r = new Rect(0, 0, conv.PixelWidth, conv.PixelHeight);
            dc.DrawRectangle(Brushes.White, null, r);
            dc.DrawImage(conv, r);
        }
        var rtb = new RenderTargetBitmap(conv.PixelWidth, conv.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    static async Task<BitmapSource> RenderPdfAsync(PdfPage page, Rect source, uint w, uint h)
    {
        using var stream = new InMemoryRandomAccessStream();
        var opts = new PdfPageRenderOptions
        {
            SourceRect = new Windows.Foundation.Rect(source.X, source.Y, source.Width, source.Height),
            DestinationWidth = w,
            DestinationHeight = h,
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
        };
        await page.RenderToStreamAsync(stream, opts);
        stream.Seek(0);
        var dec = BitmapDecoder.Create(stream.AsStreamForRead(), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Pbgra32, null, 0);
        frame.Freeze();
        return frame;
    }

    // ---------- analysis ----------

    static List<Rect> Analyze(BitmapSource full, double dpi)
    {
        double k = Math.Min(1.0, 1200.0 / Math.Max(full.PixelWidth, full.PixelHeight));
        BitmapSource a = k < 1 ? new TransformedBitmap(full, new ScaleTransform(k, k)) : full;
        a = new FormatConvertedBitmap(a, PixelFormats.Gray8, null, 0);
        int w = a.PixelWidth, h = a.PixelHeight;
        var px = new byte[w * h];
        a.CopyPixels(px, w, 0);
        var ink = new bool[w * h];
        for (int i = 0; i < px.Length; i++) ink[i] = px[i] < 190;
        var f = LabelFormat.Current;
        return AutoCrop.FindAll(ink, w, h, dpi * k, f.WidthIn, f.HeightIn);
    }

    static bool IsBlank(BitmapSource bmp)
    {
        var g = new FormatConvertedBitmap(bmp, PixelFormats.Gray8, null, 0);
        var px = new byte[g.PixelWidth * g.PixelHeight];
        g.CopyPixels(px, g.PixelWidth, 0);
        int dark = 0;
        foreach (var p in px) if (p < 190 && ++dark > 20) return false;
        return true;
    }

    // ---------- editing ----------

    public async Task SetCropAsync(Rect normalized)
    {
        if (!IsEditable) return;
        normalized.Intersect(new Rect(0, 0, 1, 1));
        if (normalized.IsEmpty || normalized.Width < 0.02 || normalized.Height < 0.02) return;
        Crop = normalized;
        _orientationStale = true; _turns = 0;
        await RenderAsync();
    }

    public async Task ResetCropAsync() { if (!IsEditable) return; Crop = _autoCrop; _orientationStale = true; _turns = 0; await RenderAsync(); }
    public async Task UseWholePageAsync() => await SetCropAsync(new Rect(0, 0, 1, 1));
    public async Task RotateAsync() { if (!IsEditable) return; Turns++; await RenderAsync(); }

    // ---------- rendering ----------

    (double W, double H) CropSize() => _pdfPage != null
        ? (_pdfPage.Size.Width * _crop.Width, _pdfPage.Size.Height * _crop.Height)
        : (_image!.PixelWidth * _crop.Width, _image.PixelHeight * _crop.Height);

    async Task<BitmapSource> RenderPieceAsync(int dw, int dh)
    {
        var (srcW, srcH) = CropSize();
        if (_pdfPage != null)
        {
            var s = _pdfPage.Size;
            return await RenderPdfAsync(_pdfPage, new Rect(_crop.X * s.Width, _crop.Y * s.Height, srcW, srcH), (uint)dw, (uint)dh);
        }
        var r = new Int32Rect((int)(_crop.X * _image!.PixelWidth), (int)(_crop.Y * _image.PixelHeight),
                              Math.Max(1, (int)srcW), Math.Max(1, (int)srcH));
        r.Width = Math.Min(r.Width, _image.PixelWidth - r.X); r.Height = Math.Min(r.Height, _image.PixelHeight - r.Y);
        return new CroppedBitmap(_image, r);
    }

    /// <summary>Works out which way up the label is by reading it, and finds the tracking number.</summary>
    async Task DetectOrientationAsync()
    {
        _orientationStale = false;
        var (srcW, srcH) = CropSize();
        double k = 1400.0 / Math.Max(srcW, srcH);
        var piece = await RenderPieceAsync(Math.Max(1, (int)(srcW * k)), Math.Max(1, (int)(srcH * k)));
        // If the crop's shape doesn't match the label stock's, it most likely needs a quarter turn.
        bool mismatch = (srcW > srcH * 1.05) == LabelFormat.Current.IsPortrait && Math.Abs(srcW - srcH) > 0.05 * Math.Max(srcW, srcH);
        // Try the two orientations that match the stock's shape first; fall back to the other two.
        var (turns, text) = await LabelInsights.DetectAsync(piece, mismatch ? [1, 3] : [0, 2]);
        if (turns == null)
        {
            var (t2, text2) = await LabelInsights.DetectAsync(piece, mismatch ? [0, 2] : [1, 3]);
            if (t2 != null) { turns = t2; text = text2; }
        }
        _autoTurns = turns ?? (mismatch ? 1 : 0);
        var carrier = LabelInsights.FindCarrier(text);
        Tracking = LabelInsights.FindTracking(text, carrier);
        Carrier = carrier ?? Tracking?.Carrier;
        LastOcrText = text;
        OnChanged(nameof(OrientationFromText));
    }

    /// <summary>The label stock this page was last rendered for.</summary>
    public LabelFormat Format { get; private set; } = LabelFormat.Current;

    /// <summary>Re-renders for a different label size or resolution.</summary>
    public async Task ChangeFormatAsync(LabelFormat f)
    {
        if (_fixed != null || f == Format) return;
        bool shapeChanged = f.IsPortrait != Format.IsPortrait;
        Format = f;
        if (shapeChanged) { _orientationStale = true; _turns = 0; }
        await RenderAsync();
    }

    /// <summary>Renders the crop into a 1-bit label at the current label size and resolution.</summary>
    public async Task RenderAsync()
    {
        if (_fixed != null) return;
        Format = LabelFormat.Current;
        if (_orientationStale) await DetectOrientationAsync();

        int W = Format.WidthDots, H = Format.HeightDots, margin = Format.MarginDots;
        var (srcW, srcH) = CropSize();
        int turns = ((_autoTurns ?? 0) + _turns) % 4;
        bool sideways = turns % 2 == 1;
        double fitW = W - 2 * margin, fitH = H - 2 * margin;
        double rotW = sideways ? srcH : srcW, rotH = sideways ? srcW : srcH;
        double scale = Math.Min(fitW / rotW, fitH / rotH);
        int dw = Math.Max(1, (int)Math.Round(srcW * scale)), dh = Math.Max(1, (int)Math.Round(srcH * scale));

        BitmapSource piece = await RenderPieceAsync(dw, dh);

        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
            dc.PushTransform(new TranslateTransform(W / 2.0, H / 2.0));
            dc.PushTransform(new RotateTransform(turns * 90));
            dc.DrawImage(piece, new Rect(-dw / 2.0, -dh / 2.0, dw, dh));
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        var px = new byte[W * H * 4];
        rtb.CopyPixels(px, W * 4, 0);
        var mono = new MonoBitmap(W, H, Format.Dpi);
        for (int i = 0, p = 0; i < mono.Dots.Length; i++, p += 4)
        {
            int lum = (px[p] * 29 + px[p + 1] * 150 + px[p + 2] * 77) >> 8;
            mono.Dots[i] = lum < 165;
        }
        _base = mono;
        // Re-cropping or rotating changes where the empty space is, so re-place the stamp.
        if (_stamp != null) _stamp.AutoPlace(mono);
        Recompose();
    }

    public static BitmapSource ToPreview(MonoBitmap m)
    {
        int stride = (m.Width + 7) / 8;
        var data = new byte[stride * m.Height];
        for (int y = 0; y < m.Height; y++)
            for (int x = 0; x < m.Width; x++)
                if (!m.Dots[y * m.Width + x]) data[y * stride + x / 8] |= (byte)(0x80 >> (x % 8)); // BlackWhite: 1 = white
        var bmp = BitmapSource.Create(m.Width, m.Height, m.Dpi, m.Dpi, PixelFormats.BlackWhite, null, data, stride);
        bmp.Freeze();
        return bmp;
    }

    public static MonoBitmap FromPreview(BitmapSource bmp, int dpi = 203)
    {
        var g = new FormatConvertedBitmap(bmp, PixelFormats.Gray8, null, 0);
        int w = g.PixelWidth, h = g.PixelHeight;
        var px = new byte[w * h];
        g.CopyPixels(px, w, 0);
        var m = new MonoBitmap(w, h, dpi);
        for (int i = 0; i < px.Length; i++) m.Dots[i] = px[i] < 128;
        return m;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
