using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Labels.Core;
using Labels.Ui;
using Microsoft.Win32;

namespace Labels;

public sealed record RecentFile(string Path, string Name, string When, string Glyph);

public partial class MainWindow : Window
{
    static readonly string[] Supported = [".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    readonly ObservableCollection<LabelPage> _labels = new();
    readonly ObservableCollection<HistoryEntry> _history = new();
    readonly Dictionary<string, LabelPage> _historyPages = new();
    readonly AppSettings _settings = AppSettings.Load();
    IPrinterTransport _printer = new BluetoothPrinter();
    PrinterChoice? _choice;
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    FileSystemWatcher? _watcher;
    CancellationTokenSource? _printCts;
    string? _bannerPath;
    bool _busy, _printing, _showingHistory, _shellShown;
    PrinterState _state = PrinterState.Unknown;
    LabelPage? _shownPage;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        LabelList.ItemsSource = _labels;
        HistoryList.ItemsSource = _history;
        _labels.CollectionChanged += (_, _) => UpdateChrome();
        foreach (var h in LabelHistory.Load()) _history.Add(h);

        LabelFormat.Current = App.FormatOverride ?? _settings.Format;
        var choices = PrinterChoice.Discover();
        // Saved choice → legacy Bluetooth address → best guess.
        var saved = choices.FirstOrDefault(c => c.Kind == _settings.PrinterKind && c.Id == _settings.PrinterId)
                 ?? choices.FirstOrDefault(c => c.Kind == PrinterKind.Bluetooth && c.Id == _settings.DeviceAddress)
                 ?? choices.FirstOrDefault();
        UsePrinter(saved);

        DensitySlider.Value = _settings.Density;
        DensityText.Text = _settings.Density.ToString();
        LoadRecentDownloads();
        StartWatchingDownloads();
        UpdateChrome();
        StartArtFloat();

        _statusTimer.Tick += async (_, _) => { if (IsActive) await RefreshStatusAsync(); };
        Loaded += async (_, _) =>
        {
            _statusTimer.Start();
            if (args.Length > 0) await AddFilesAsync(args);
            await RefreshStatusAsync();
        };
        Activated += async (_, _) => { if (_state is PrinterState.Offline or PrinterState.Unknown) await RefreshStatusAsync(); };
        SizeChanged += (_, _) => SettingsCard.MaxHeight = Math.Max(300, ActualHeight - 70);
        Closed += (_, _) => { _settings.Save(); _printer.Dispose(); _watcher?.Dispose(); };
    }

    /// <summary>The label shown in the preview: the queue selection, or a history item.</summary>
    LabelPage? Current => _showingHistory
        ? (HistoryList.SelectedItem is HistoryEntry e ? HistoryPage(e) : null)
        : LabelList.SelectedItem as LabelPage;

    LabelPage? HistoryPage(HistoryEntry e)
    {
        if (_historyPages.TryGetValue(e.Id, out var p)) return p;
        try { p = LabelPage.FromHistory(e, LabelHistory.LoadMono(e)); }
        catch { return null; }
        p.PropertyChanged += (_, _) => UpdateChrome();
        _historyPages[e.Id] = p;
        return p;
    }

    // ================= Loading =================

    async Task<List<LabelPage>> AddFilesAsync(IEnumerable<string> paths, bool select = true)
    {
        var added = new List<LabelPage>();
        var files = paths.Where(p => File.Exists(p) && Supported.Contains(Path.GetExtension(p).ToLowerInvariant())).ToList();
        if (files.Count == 0) { ShowToast("That file type isn't supported. Use a PDF or an image.", false); return added; }
        SetBusy(true, files.Count == 1 ? "Reading label…" : $"Reading {files.Count} files…");
        try
        {
            foreach (var f in files)
            {
                try
                {
                    var pages = await LabelPage.LoadFileAsync(f);
                    foreach (var p in pages) { _labels.Add(p); added.Add(p); }
                    if (_bannerPath != null && string.Equals(_bannerPath, f, StringComparison.OrdinalIgnoreCase)) HideBanner();
                }
                catch (Exception ex) { ShowToast($"Couldn't open {Path.GetFileName(f)}. {ex.Message}", false); }
            }
        }
        finally { SetBusy(false); }
        if (added.Count > 0 && select)
        {
            SwitchList(false);
            LabelList.SelectedItem = added[0];
            int pagesWithTwo = added.Count(p => p.LabelsOnPage > 1);
            if (pagesWithTwo > 0) ShowToast($"Found {added.Count} labels — each will print on its own", true);
        }
        return added;
    }

    async Task AddBitmapAsync(System.Windows.Media.Imaging.BitmapSource bmp)
    {
        SetBusy(true, "Reading label…");
        try
        {
            var page = await LabelPage.FromBitmapAsync(bmp, "Pasted Image");
            _labels.Add(page);
            SwitchList(false);
            LabelList.SelectedItem = page;
        }
        catch (Exception ex) { ShowToast("Couldn't use the pasted image. " + ex.Message, false); }
        finally { SetBusy(false); }
    }

    void AddFile_Click(object sender, RoutedEventArgs e) => OpenFileDialog();

    async void OpenFileDialog()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose Shipping Labels",
            Filter = "Labels (PDF, images)|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
            Multiselect = true,
            InitialDirectory = DownloadsFolder(),
        };
        if (dlg.ShowDialog(this) == true) await AddFilesAsync(dlg.FileNames);
    }

    void Paste_Click(object sender, RoutedEventArgs e) => Paste();

    async void Paste()
    {
        try
        {
            if (Clipboard.ContainsFileDropList()) await AddFilesAsync(Clipboard.GetFileDropList().Cast<string>());
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img) await AddBitmapAsync(img);
            else ShowToast("There's no label on the clipboard.", false);
        }
        catch { ShowToast("Couldn't read the clipboard.", false); }
    }

    // ================= Drag and drop =================

    bool _dropShown;
    protected override void OnDragEnter(DragEventArgs e) { base.OnDragEnter(e); UpdateDrag(e); }
    protected override void OnDragOver(DragEventArgs e) { base.OnDragOver(e); UpdateDrag(e); }
    protected override void OnDragLeave(DragEventArgs e) { base.OnDragLeave(e); HideDrop(); }

    void UpdateDrag(DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (ok && !_dropShown)
        {
            _dropShown = true;
            Motion.FadeIn(DropOverlay, 160);
            Motion.PopIn(DropBubble, 0.8);
        }
    }

    void HideDrop()
    {
        if (!_dropShown) return;
        _dropShown = false;
        Motion.FadeOut(DropOverlay, 160);
    }

    protected override async void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        HideDrop();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await AddFilesAsync(files);
        else if (e.Data.GetData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource bmp) await AddBitmapAsync(bmp);
    }

    // ================= Lists =================

    void ListSwitch_SelectionChanged(object? sender, EventArgs e) => SwitchList(ListSwitch.SelectedIndex == 1, fromSwitch: true);

    void ShowHistory_Click(object sender, RoutedEventArgs e) => SwitchList(true);

    void SwitchList(bool history, bool fromSwitch = false)
    {
        if (CropStage.Visibility == Visibility.Visible) ExitCrop(false);
        _showingHistory = history;
        if (!fromSwitch) ListSwitch.SelectedIndex = history ? 1 : 0;
        LabelList.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        HistoryList.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        if (history && HistoryList.SelectedItem == null && _history.Count > 0) HistoryList.SelectedIndex = 0;
        Motion.FromTo(history ? HistoryList : LabelList, OpacityProperty, 0, 1, 260, Motion.Smooth);
        UpdateChrome();
    }

    void LabelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (LabelPage p in e.RemovedItems) p.PropertyChanged -= Selected_PropertyChanged;
        foreach (LabelPage p in e.AddedItems) p.PropertyChanged += Selected_PropertyChanged;
        UpdateChrome();
    }

    void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateChrome();

    void Selected_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateChrome();

    void LabelList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back) { RemoveSelected(); e.Handled = true; }
    }

    async void Rotate_Click(object sender, RoutedEventArgs e) { if (Current is { IsEditable: true } p) { await p.RotateAsync(); BumpPaper(); } }
    void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    void RemoveSelected()
    {
        if (_showingHistory)
        {
            if (HistoryList.SelectedItem is not HistoryEntry h) return;
            int i = _history.IndexOf(h);
            LabelHistory.Delete(h);
            _historyPages.Remove(h.Id);
            _history.Remove(h);
            if (_history.Count > 0) HistoryList.SelectedIndex = Math.Min(i, _history.Count - 1);
            UpdateChrome();
            return;
        }
        if (LabelList.SelectedItem is not LabelPage p) return;
        int idx = _labels.IndexOf(p);
        _labels.Remove(p);
        if (_labels.Count > 0) LabelList.SelectedIndex = Math.Min(idx, _labels.Count - 1);
    }

    void CopiesMinus_Click(object sender, RoutedEventArgs e) { if (Current is { } p) p.Copies--; }
    void CopiesPlus_Click(object sender, RoutedEventArgs e) { if (Current is { } p) p.Copies++; }

    void DensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)Math.Round(e.NewValue);
        if (DensityText != null) DensityText.Text = v.ToString();
        _settings.Density = v;
    }

    // ================= Presentation =================

    void UpdateChrome()
    {
        bool anyQueued = _labels.Count > 0;
        bool showShell = anyQueued || _showingHistory;
        SetShell(showShell);
        if (!_showingHistory && anyQueued && LabelList.SelectedItem == null) LabelList.SelectedIndex = 0;

        var p = Current;
        bool listEmpty = _showingHistory ? _history.Count == 0 : !anyQueued;
        ListEmpty.Visibility = listEmpty ? Visibility.Visible : Visibility.Collapsed;
        ListEmptyText.Text = _showingHistory ? "Labels you print will appear here for quick reprints." : "Your queue is empty.";
        ListEmptyIcon.Text = _showingHistory ? "" : "";

        int total = _labels.Sum(l => l.Copies);
        if (_showingHistory)
        {
            PrintButtonText.Text = p == null ? "Print Again" : p.Copies == 1 ? "Print Again" : $"Print {p.Copies} Again";
            PrintButton.IsEnabled = p != null && !_printing;
            SummaryText.Text = HistoryList.SelectedItem is HistoryEntry he ? $"Printed {he.When.ToLowerInvariant()}" : "";
            CopiesHint.Text = "For this reprint";
        }
        else
        {
            PrintButtonText.Text = total switch { 0 => "Print", 1 => "Print Label", _ => $"Print {total} Labels" };
            PrintButton.IsEnabled = anyQueued && !_printing;
            SummaryText.Text = !anyQueued ? "" : _labels.Count == 1 ? "1 label in queue" : $"{_labels.Count} labels in queue";
            CopiesHint.Text = _labels.Count > 1 ? "For the selected label" : "For this label";
        }

        RotateButton.IsEnabled = CropButton.IsEnabled = p is { IsEditable: true };
        ToolCapsule.Visibility = p == null ? Visibility.Hidden : Visibility.Visible;
        OptionsGroup.IsEnabled = p != null;
        UpdateStampSection(p);

        if (p == null)
        {
            ShowPaper(null);
            TitleText.Text = _showingHistory ? "History" : "";
            SubtitleText.Text = "";
            FooterText.Text = "";
            ShipmentSection.Visibility = Visibility.Collapsed;
            return;
        }

        ShowPaper(p);
        TitleText.Text = p.Title;
        CopiesText.Text = p.Copies.ToString();
        if (_showingHistory)
        {
            SubtitleText.Text = HistoryList.SelectedItem is HistoryEntry h ? $"Printed {h.PrintedAt:MMM d, h:mm tt}" : "";
            FooterText.Text = p.Output is { } o
                ? $"{o.Width / (double)o.Dpi:0.##} × {o.Height / (double)o.Dpi:0.##} in · Exactly as it was printed"
                : "Exactly as it was printed";
        }
        else
        {
            var bits = new List<string>();
            bits.Add(p.PageCount > 1 ? $"Page {p.PageNumber} of {p.PageCount}" : Path.GetExtension(p.SourceName).TrimStart('.').ToUpperInvariant());
            if (p.LabelsOnPage > 1) bits.Add($"Label {p.LabelOnPage} of {p.LabelsOnPage} on this page");
            SubtitleText.Text = string.Join("  ·  ", bits);
            string crop = p.IsAutoCrop ? (p.AutoCropRect == new Rect(0, 0, 1, 1) ? "Full page" : "Label found automatically") : "Custom crop";
            string orient = p.OrientationFromText ? " · Upright" : "";
            FooterText.Text = $"{p.Format.SizeText} · {crop}{orient}";
        }

        if (p.Carrier != null || p.Tracking != null)
        {
            ShipmentSection.Visibility = Visibility.Visible;
            CarrierText.Text = p.Carrier ?? p.Tracking?.Carrier ?? "";
            CarrierBadge.Visibility = string.IsNullOrEmpty(CarrierText.Text) ? Visibility.Collapsed : Visibility.Visible;
            TrackingText.Text = p.Tracking?.Pretty ?? "No tracking number found";
            TrackingText.Opacity = p.Tracking == null ? 0.6 : 1;
            CopyTrackingButton.IsEnabled = p.Tracking != null;
            TrackButton.IsEnabled = (p.Tracking?.TrackUrl ?? TrackingInfo.CarrierHome(p.Carrier)) != null;
            TrackButton.ToolTip = p.Tracking != null ? $"Track on {p.Carrier ?? p.Tracking.Carrier}.com" : $"Open {p.Carrier} tracking";
        }
        else ShipmentSection.Visibility = Visibility.Collapsed;
    }

    void ShowPaper(LabelPage? p)
    {
        var src = p?.OutputPreview;
        PaperView.Visibility = src == null ? Visibility.Hidden : Visibility.Visible;
        if (p?.Output is { } o)
        {
            // The paper card takes the label's real shape (4×6, 4×2, 2×1…).
            double w = o.Width >= o.Height ? 609 : 406, h = w * o.Height / o.Width;
            PaperCard.Width = PaperGhost.Width = w;
            PaperCard.Height = PaperGhost.Height = h;
        }
        if (ReferenceEquals(p, _shownPage) && ReferenceEquals(PaperImage.Source, src)) return;
        bool newLabel = !ReferenceEquals(p, _shownPage);
        _shownPage = p;
        PaperImage.Source = src;
        if (src != null && newLabel) BumpPaper();
    }

    /// <summary>A gentle settle each time the preview changes.</summary>
    void BumpPaper()
    {
        var (s, t) = Motion.Transforms(PaperView);
        Motion.FromTo(PaperView, OpacityProperty, 0.4, 1, 240, Motion.Smooth);
        Motion.FromTo(s, ScaleTransform.ScaleXProperty, 0.965, 1, 560, Motion.Bouncy);
        Motion.FromTo(s, ScaleTransform.ScaleYProperty, 0.965, 1, 560, Motion.Bouncy);
        Motion.FromTo(t, TranslateTransform.YProperty, 8, 0, 560, Motion.Bouncy);
    }

    void SetShell(bool show)
    {
        if (show == _shellShown) return;
        _shellShown = show;
        if (show)
        {
            Motion.PopOut(EmptyState, 1.03, 0, 220);
            Chrome.Visibility = Visibility.Visible;
            SlideIn(Sidebar, -40);
            SlideIn(Inspector, 40);
            Motion.FadeIn(Header, 300);
            Motion.FadeIn(Footer, 300);
        }
        else
        {
            Chrome.Visibility = Visibility.Collapsed;
            PaperView.Visibility = Visibility.Hidden;
            Motion.PopIn(EmptyState, 0.97);
        }
    }

    static void SlideIn(UIElement e, double fromX)
    {
        var (_, t) = Motion.Transforms(e);
        Motion.FromTo(e, OpacityProperty, 0, 1, 260, Motion.Smooth);
        Motion.FromTo(t, TranslateTransform.XProperty, fromX, 0, 620, Motion.Bouncy);
    }

    void StartArtFloat()
    {
        var a = new DoubleAnimation(-5, 5, TimeSpan.FromSeconds(2.6))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        ArtFloat.BeginAnimation(TranslateTransform.YProperty, a);
    }

    // ================= Crop editor =================

    enum Drag { None, Move, New, L, R, T, B, TL, TR, BL, BR }
    Rect _draft;
    Rect _imgRect;
    Point _dragStart;
    Rect _dragOrigin;
    Drag _drag = Drag.None;
    bool _syncCropMode;

    void Crop_Click(object sender, RoutedEventArgs e) => EnterCrop();

    void EnterCrop()
    {
        if (Current is not { IsEditable: true } p) return;
        _draft = p.Crop;
        CropImage.Source = p.SourcePreview;
        SyncCropMode(p);

        Chrome.IsHitTestVisible = false;
        Motion.To(Chrome, OpacityProperty, 0, 220, Motion.Smooth);
        CropStage.Visibility = Visibility.Visible;
        Motion.FromTo(CropStage, OpacityProperty, 0, 1, 260, Motion.Smooth);
        var (s, _) = Motion.Transforms(CropCanvas);
        Motion.FromTo(s, ScaleTransform.ScaleXProperty, 0.9, 1, 620, Motion.Bouncy);
        Motion.FromTo(s, ScaleTransform.ScaleYProperty, 0.9, 1, 620, Motion.Bouncy);
        Motion.PopIn(CropBar, 0.9, 40, 620);
        foreach (var b in CaptionButtons.Children.OfType<Button>()) b.Foreground = Brushes.White;
        Dispatcher.BeginInvoke(LayoutCrop, DispatcherPriority.Loaded);
    }

    void ExitCrop(bool animate = true)
    {
        Chrome.IsHitTestVisible = true;
        foreach (var b in CaptionButtons.Children.OfType<Button>()) b.ClearValue(ForegroundProperty);
        if (!animate)
        {
            CropStage.Visibility = CropBar.Visibility = Visibility.Collapsed;
            Chrome.BeginAnimation(OpacityProperty, null); Chrome.Opacity = 1;
            return;
        }
        Motion.To(Chrome, OpacityProperty, 1, 260, Motion.Smooth);
        Motion.FadeOut(CropStage, 220);
        Motion.PopOut(CropBar, 0.9, 30, 220);
        UpdateChrome();
        BumpPaper();
    }

    void SyncCropMode(LabelPage p)
    {
        _syncCropMode = true;
        CropModeSwitch.SelectedIndex = _draft == p.AutoCropRect ? 0 : _draft == new Rect(0, 0, 1, 1) ? 1 : 2;
        _syncCropMode = false;
    }

    void CropMode_SelectionChanged(object? sender, EventArgs e)
    {
        if (_syncCropMode || Current is not { } p) return;
        if (CropModeSwitch.SelectedIndex == 0) AnimateDraft(p.AutoCropRect);
        else if (CropModeSwitch.SelectedIndex == 1) AnimateDraft(new Rect(0, 0, 1, 1));
    }

    // Spring the crop frame between rectangles.
    Rect _animFrom, _animTo; DateTime _animStart; bool _animating;
    void AnimateDraft(Rect to)
    {
        _animFrom = _draft; _animTo = to; _animStart = DateTime.Now;
        if (!_animating) { _animating = true; CompositionTarget.Rendering += OnAnimFrame; }
    }

    void OnAnimFrame(object? sender, EventArgs e)
    {
        double t = Math.Min(1, (DateTime.Now - _animStart).TotalMilliseconds / 520);
        double k = Motion.Snappy.Ease(t);
        _draft = new Rect(
            _animFrom.X + (_animTo.X - _animFrom.X) * k, _animFrom.Y + (_animTo.Y - _animFrom.Y) * k,
            Math.Max(0.01, _animFrom.Width + (_animTo.Width - _animFrom.Width) * k),
            Math.Max(0.01, _animFrom.Height + (_animTo.Height - _animFrom.Height) * k));
        DrawCrop();
        if (t >= 1) { _draft = _animTo; DrawCrop(); _animating = false; CompositionTarget.Rendering -= OnAnimFrame; }
    }

    void CropCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutCrop();

    void LayoutCrop()
    {
        if (Current is not { IsEditable: true } p || CropCanvas.ActualWidth <= 0) return;
        double cw = CropCanvas.ActualWidth, ch = CropCanvas.ActualHeight, a = p.SourceAspect;
        double w = Math.Min(cw, ch * a), h = w / a;
        _imgRect = new Rect((cw - w) / 2, (ch - h) / 2, w, h);
        Canvas.SetLeft(CropImage, _imgRect.X); Canvas.SetTop(CropImage, _imgRect.Y);
        CropImage.Width = w; CropImage.Height = h;
        DrawCrop();
    }

    Rect DraftOnScreen() => new(_imgRect.X + _draft.X * _imgRect.Width, _imgRect.Y + _draft.Y * _imgRect.Height,
                                _draft.Width * _imgRect.Width, _draft.Height * _imgRect.Height);

    void DrawCrop()
    {
        var r = DraftOnScreen();
        Canvas.SetLeft(CropFrame, r.X); Canvas.SetTop(CropFrame, r.Y);
        CropFrame.Width = Math.Max(0, r.Width); CropFrame.Height = Math.Max(0, r.Height);

        var shade = new GeometryGroup { FillRule = FillRule.EvenOdd };
        shade.Children.Add(new RectangleGeometry(_imgRect));
        shade.Children.Add(new RectangleGeometry(r));
        CropShade.Data = shade;

        // Thirds grid
        void Line(System.Windows.Shapes.Line l, double x1, double y1, double x2, double y2) { l.X1 = x1; l.Y1 = y1; l.X2 = x2; l.Y2 = y2; }
        Line(GridV1, r.X + r.Width / 3, r.Y, r.X + r.Width / 3, r.Bottom);
        Line(GridV2, r.X + r.Width * 2 / 3, r.Y, r.X + r.Width * 2 / 3, r.Bottom);
        Line(GridH1, r.X, r.Y + r.Height / 3, r.Right, r.Y + r.Height / 3);
        Line(GridH2, r.X, r.Y + r.Height * 2 / 3, r.Right, r.Y + r.Height * 2 / 3);

        // Corner brackets + edge grabbers, drawn just outside the frame like Photos.
        double L = Math.Min(24, Math.Min(r.Width, r.Height) / 3), o = 1.5;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            void Poly(params Point[] pts) { c.BeginFigure(pts[0], false, false); c.PolyLineTo(pts.Skip(1).ToList(), true, true); }
            Poly(new(r.X - o, r.Y - o + L), new(r.X - o, r.Y - o), new(r.X - o + L, r.Y - o));
            Poly(new(r.Right + o - L, r.Y - o), new(r.Right + o, r.Y - o), new(r.Right + o, r.Y - o + L));
            Poly(new(r.X - o, r.Bottom + o - L), new(r.X - o, r.Bottom + o), new(r.X - o + L, r.Bottom + o));
            Poly(new(r.Right + o - L, r.Bottom + o), new(r.Right + o, r.Bottom + o), new(r.Right + o, r.Bottom + o - L));
            double mx = r.X + r.Width / 2, my = r.Y + r.Height / 2, e = Math.Min(10, L / 2);
            if (r.Width > 80) { Poly(new(mx - e, r.Y - o), new(mx + e, r.Y - o)); Poly(new(mx - e, r.Bottom + o), new(mx + e, r.Bottom + o)); }
            if (r.Height > 80) { Poly(new(r.X - o, my - e), new(r.X - o, my + e)); Poly(new(r.Right + o, my - e), new(r.Right + o, my + e)); }
        }
        g.Freeze();
        CropCorners.Data = g;
    }

    Drag HitTestCrop(Point p)
    {
        var r = DraftOnScreen();
        const double c = 20, e = 12;
        bool nl = Math.Abs(p.X - r.X) < c, nr = Math.Abs(p.X - r.Right) < c, nt = Math.Abs(p.Y - r.Y) < c, nb = Math.Abs(p.Y - r.Bottom) < c;
        if (nl && nt) return Drag.TL; if (nr && nt) return Drag.TR; if (nl && nb) return Drag.BL; if (nr && nb) return Drag.BR;
        bool inY = p.Y > r.Y && p.Y < r.Bottom, inX = p.X > r.X && p.X < r.Right;
        if (inY && Math.Abs(p.X - r.X) < e) return Drag.L;
        if (inY && Math.Abs(p.X - r.Right) < e) return Drag.R;
        if (inX && Math.Abs(p.Y - r.Y) < e) return Drag.T;
        if (inX && Math.Abs(p.Y - r.Bottom) < e) return Drag.B;
        if (r.Contains(p)) return Drag.Move;
        return Drag.New;
    }

    static Cursor CursorFor(Drag d) => d switch
    {
        Drag.TL or Drag.BR => Cursors.SizeNWSE,
        Drag.TR or Drag.BL => Cursors.SizeNESW,
        Drag.L or Drag.R => Cursors.SizeWE,
        Drag.T or Drag.B => Cursors.SizeNS,
        Drag.Move => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    Point ToNorm(Point p) => new(Math.Clamp((p.X - _imgRect.X) / _imgRect.Width, 0, 1), Math.Clamp((p.Y - _imgRect.Y) / _imgRect.Height, 0, 1));

    void CropCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var pt = e.GetPosition(CropCanvas);
        _drag = HitTestCrop(pt);
        _dragStart = ToNorm(pt);
        _dragOrigin = _draft;
        CropCanvas.CaptureMouse();
        Motion.To(CropGrid, OpacityProperty, 1, 160, Motion.Smooth);
    }

    void CropCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        var pt = e.GetPosition(CropCanvas);
        if (_drag == Drag.None) { CropCanvas.Cursor = CursorFor(HitTestCrop(pt)); return; }
        var n = ToNorm(pt);
        double dx = n.X - _dragStart.X, dy = n.Y - _dragStart.Y;
        var o = _dragOrigin;
        double x0 = o.X, y0 = o.Y, x1 = o.Right, y1 = o.Bottom;
        const double min = 0.04;
        switch (_drag)
        {
            case Drag.Move:
                double mx = Math.Clamp(o.X + dx, 0, 1 - o.Width), my = Math.Clamp(o.Y + dy, 0, 1 - o.Height);
                _draft = new Rect(mx, my, o.Width, o.Height); DrawCrop(); MarkCustom(); return;
            case Drag.New:
                _draft = new Rect(_dragStart, n); DrawCrop(); MarkCustom(); return;
        }
        if (_drag is Drag.L or Drag.TL or Drag.BL) x0 = Math.Clamp(o.X + dx, 0, x1 - min);
        if (_drag is Drag.R or Drag.TR or Drag.BR) x1 = Math.Clamp(o.Right + dx, x0 + min, 1);
        if (_drag is Drag.T or Drag.TL or Drag.TR) y0 = Math.Clamp(o.Y + dy, 0, y1 - min);
        if (_drag is Drag.B or Drag.BL or Drag.BR) y1 = Math.Clamp(o.Bottom + dy, y0 + min, 1);
        _draft = new Rect(x0, y0, x1 - x0, y1 - y0);
        DrawCrop();
        MarkCustom();
    }

    void MarkCustom()
    {
        if (CropModeSwitch.SelectedIndex == 2) return;
        _syncCropMode = true; CropModeSwitch.SelectedIndex = 2; _syncCropMode = false;
    }

    void CropCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        CropCanvas.ReleaseMouseCapture();
        if (_drag == Drag.New && (_draft.Width < 0.03 || _draft.Height < 0.03)) AnimateDraft(_dragOrigin);
        _drag = Drag.None;
        Motion.To(CropGrid, OpacityProperty, 0, 300, Motion.Smooth);
    }

    void CropCancel_Click(object sender, RoutedEventArgs e) => ExitCrop();
    async void CropDone_Click(object sender, RoutedEventArgs e) => await ApplyCropAsync();

    async Task ApplyCropAsync()
    {
        if (Current is { } p)
        {
            if (_draft == p.AutoCropRect) { if (!p.IsAutoCrop) await p.ResetCropAsync(); }
            else if (_draft != p.Crop) await p.SetCropAsync(_draft);
        }
        ExitCrop();
    }

    // ================= Stamps =================

    public sealed record RecentStampItem(string Path, string Name, System.Windows.Media.Imaging.BitmapSource? Thumb);

    System.Windows.Media.Imaging.BitmapSource? _fragileThumb;
    bool _syncingStamp;

    System.Windows.Media.Imaging.BitmapSource FragileThumb_()
    {
        if (_fragileThumb != null) return _fragileThumb;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) { dc.PushTransform(new ScaleTransform(2, 2)); StampArt.DrawFragile(dc); }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(600, 208, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return _fragileThumb = rtb;
    }

    static System.Windows.Media.Imaging.BitmapSource? LoadThumb(string path)
    {
        try
        {
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 200;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    void StampButton_Click(object sender, RoutedEventArgs e) => OpenStampMenu();

    void OpenStampMenu()
    {
        if (Current == null) return;
        FragileThumb.Source = FragileThumb_();
        _settings.RecentStamps.RemoveAll(p => !File.Exists(p));
        RecentStampList.ItemsSource = _settings.RecentStamps.Take(4)
            .Select(p => new RecentStampItem(p, Path.GetFileNameWithoutExtension(p), LoadThumb(p))).ToList();
        bool many = !_showingHistory && _labels.Count > 1;
        StampToAll.Visibility = StampMenuSep.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
        StampToAll.IsChecked = false;
        StampMenu.Visibility = Visibility.Visible;
        StampMenuCard.RenderTransformOrigin = new Point(0.85, 0);
        Motion.PopIn(StampMenuCard, 0.85, -12, 560);
    }

    void CloseStampMenu()
    {
        if (StampMenu.Visibility != Visibility.Visible) return;
        Motion.PopOut(StampMenuCard, 0.9, -8, 180, () => StampMenu.Visibility = Visibility.Collapsed);
    }

    void StampMenuScrim_MouseDown(object sender, MouseButtonEventArgs e) => CloseStampMenu();

    void StampFragile_Click(object sender, RoutedEventArgs e) => ApplyStamp(Stamp.Fragile());

    void StampRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) ApplyStamp(Stamp.FromImage(path));
    }

    void StampChoose_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a Stamp Image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dlg.ShowDialog(this) != true) return;
        _settings.RecentStamps.RemoveAll(p => string.Equals(p, dlg.FileName, StringComparison.OrdinalIgnoreCase));
        _settings.RecentStamps.Insert(0, dlg.FileName);
        if (_settings.RecentStamps.Count > 8) _settings.RecentStamps.RemoveRange(8, _settings.RecentStamps.Count - 8);
        _settings.Save();
        ApplyStamp(Stamp.FromImage(dlg.FileName));
    }

    void ApplyStamp(Stamp stamp)
    {
        bool toAll = StampToAll.IsChecked == true && !_showingHistory;
        CloseStampMenu();
        var targets = toAll ? _labels.ToList() : Current is { } c ? [c] : new List<LabelPage>();
        int crowded = 0;
        try
        {
            for (int i = 0; i < targets.Count; i++)
                if (!targets[i].SetStamp(i == 0 ? stamp : stamp.Clone())) crowded++;
        }
        catch (Exception ex)
        {
            foreach (var t in targets) t.SetStamp(null);
            ShowToast("Couldn't use that image. " + ex.Message, false);
            return;
        }
        UpdateChrome();
        BumpPaper();
        if (crowded > 0) ShowToast("No empty space found — drag the stamp where you want it", false);
        else ShowToast(targets.Count > 1 ? $"Stamp added to {targets.Count} labels" : "Stamp placed in an empty spot", true);
    }

    void RemoveStamp_Click(object sender, RoutedEventArgs e)
    {
        Current?.SetStamp(null);
        StampOutline.Opacity = 0;
        UpdateChrome();
    }

    void StampAll_Click(object sender, RoutedEventArgs e)
    {
        if (Current?.Stamp is not { } s) return;
        int n = 0;
        foreach (var l in _labels.Where(l => !ReferenceEquals(l, Current)))
        {
            var c = s.Clone();
            l.SetStamp(c);
            c.Faint = s.Faint;
            l.Recompose();
            n++;
        }
        ShowToast(n == 1 ? "Stamp added to 1 more label" : $"Stamp added to {n} more labels", true);
    }

    void StampSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingStamp || Current?.Stamp is not { } s) return;
        s.Width = e.NewValue;
        ClampStamp(s);
        ScheduleRecompose();
    }

    void StampStyle_SelectionChanged(object? sender, EventArgs e)
    {
        if (_syncingStamp || Current?.Stamp is not { } s) return;
        s.Faint = StampStyle.SelectedIndex == 1;
        Current.Recompose();
    }

    void UpdateStampSection(LabelPage? p)
    {
        var s = p?.Stamp;
        StampSection.Visibility = s == null ? Visibility.Collapsed : Visibility.Visible;
        StampButton.IsEnabled = p != null;
        if (s == null) return;
        _syncingStamp = true;
        StampName.Text = s.Name;
        StampThumb.Source = s.Kind == StampKind.Fragile ? FragileThumb_() : LoadThumb(s.ImagePath!);
        StampSize.Value = s.Width;
        StampStyle.SelectedIndex = s.Faint ? 1 : 0;
        _syncingStamp = false;
        StampWarning.Visibility = !s.Faint && p!.StampCoverage > 0.03 ? Visibility.Visible : Visibility.Collapsed;
        StampAllButton.Visibility = !_showingHistory && _labels.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- dragging the stamp on the preview ---

    bool _stampDrag;
    Point _stampDragStart;
    double _stampStartX, _stampStartY;
    bool _recomposeQueued;

    Point PaperNorm(MouseEventArgs e)
    {
        var p = e.GetPosition(PaperImage);
        return new Point(p.X / Math.Max(1, PaperImage.ActualWidth), p.Y / Math.Max(1, PaperImage.ActualHeight));
    }

    /// <summary>Size in dots of the label being edited.</summary>
    (int W, int H) LabelDots() => Current?.Output is { } o ? (o.Width, o.Height) : (LabelFormat.Current.WidthDots, LabelFormat.Current.HeightDots);

    Rect StampRectNorm(Stamp s)
    {
        var (W, H) = LabelDots();
        var b = s.Bounds(W, H);
        return new Rect((double)b.X / W, (double)b.Y / H, (double)b.Width / W, (double)b.Height / H);
    }

    void ShowStampOutline(Stamp s, bool on)
    {
        var r = StampRectNorm(s);
        double w = PaperImage.ActualWidth, h = PaperImage.ActualHeight;
        Canvas.SetLeft(StampOutline, r.X * w - 3); Canvas.SetTop(StampOutline, r.Y * h - 3);
        StampOutline.Width = r.Width * w + 6; StampOutline.Height = r.Height * h + 6;
        Motion.To(StampOutline, OpacityProperty, on ? 1 : 0, 160, Motion.Smooth);
    }

    void ClampStamp(Stamp s)
    {
        var (W, H) = LabelDots();
        double hw = s.Width / 2, hh = s.Width * W / s.Aspect / H / 2;
        s.CenterX = Math.Clamp(s.CenterX, hw, Math.Max(hw, 1 - hw));
        s.CenterY = Math.Clamp(s.CenterY, hh, Math.Max(hh, 1 - hh));
    }

    void ScheduleRecompose()
    {
        if (_recomposeQueued) return;
        _recomposeQueued = true;
        CompositionTarget.Rendering += RecomposeFrame;
    }

    void RecomposeFrame(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= RecomposeFrame;
        _recomposeQueued = false;
        if (Current is { Stamp: { } s } p)
        {
            p.Recompose();
            ShowStampOutline(s, _stampDrag || PaperHost.IsMouseOver);
        }
    }

    void Paper_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Current?.Stamp is not { } s) return;
        var n = PaperNorm(e);
        if (!StampRectNorm(s).Contains(n)) return;
        _stampDrag = true;
        _stampDragStart = n;
        _stampStartX = s.CenterX; _stampStartY = s.CenterY;
        PaperHost.CaptureMouse();
        ShowStampOutline(s, true);
        e.Handled = true;
    }

    void Paper_MouseMove(object sender, MouseEventArgs e)
    {
        if (Current?.Stamp is not { } s) { PaperHost.Cursor = null; return; }
        var n = PaperNorm(e);
        if (_stampDrag)
        {
            s.CenterX = _stampStartX + n.X - _stampDragStart.X;
            s.CenterY = _stampStartY + n.Y - _stampDragStart.Y;
            ClampStamp(s);
            ScheduleRecompose();
            return;
        }
        bool over = StampRectNorm(s).Contains(n);
        PaperHost.Cursor = over ? Cursors.SizeAll : null;
        ShowStampOutline(s, over);
    }

    void Paper_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_stampDrag) return;
        _stampDrag = false;
        PaperHost.ReleaseMouseCapture();
        if (Current?.Stamp is { } s) { Current.Recompose(); ShowStampOutline(s, StampRectNorm(s).Contains(PaperNorm(e))); }
        UpdateChrome();
    }

    void Paper_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_stampDrag && Current?.Stamp is { } s) ShowStampOutline(s, false);
    }

    void Paper_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Current?.Stamp is not { } s || !StampRectNorm(s).Contains(PaperNorm(e))) return;
        s.Width = Math.Clamp(s.Width * (e.Delta > 0 ? 1.06 : 1 / 1.06), 0.15, 0.95);
        ClampStamp(s);
        _syncingStamp = true; StampSize.Value = s.Width; _syncingStamp = false;
        ScheduleRecompose();
        e.Handled = true;
    }

    // ================= Printing =================

    async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_showingHistory) { if (Current is { } p) await PrintPagesAsync([p], recordHistory: false); }
        else await PrintPagesAsync(_labels.ToList(), recordHistory: true);
    }

    async Task<bool> PrintPagesAsync(List<LabelPage> pages, bool recordHistory, string? doneMessage = null)
    {
        if (_printing || pages.Count == 0) return false;
        var items = pages.Where(l => l.Output != null).Select(l => (l.Output!, l.Copies)).ToList();
        int total = items.Sum(i => i.Copies);
        bool ok = await SendJobAsync(() => Tspl.BuildJob(items, _settings.ToPrintSettings()),
                                     doneMessage ?? (total == 1 ? "Sent 1 label to the printer" : $"Sent {total} labels to the printer"));
        if (!ok) return false;
        if (recordHistory)
        {
            foreach (var p in pages)
            {
                try { _history.Insert(0, LabelHistory.Add(p)); } catch { }
            }
            if (_settings.ClearAfterPrint) foreach (var p in pages) _labels.Remove(p);
        }
        UpdateChrome();
        return true;
    }

    async Task<bool> SendJobAsync(Func<byte[]> build, string doneMessage, bool showProgress = true)
    {
        if (_printing) return false;
        _printing = true;
        _printCts = new CancellationTokenSource();
        if (showProgress) SetProgress(true, 0, "Connecting…");
        SetStatus(PrinterState.Connecting);
        try
        {
            var job = await Task.Run(build);
            var progress = new Progress<double>(v => SetProgress(true, v, v >= 1 ? "Printing…" : $"Sending… {v:P0}"));
            await _printer.SendAsync(job, showProgress ? progress : null, _printCts.Token);
            SetStatus(PrinterState.Printing);
            ShowToast(doneMessage, true);
            _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.Invoke(async () => await RefreshStatusAsync()));
            return true;
        }
        catch (OperationCanceledException) { ShowToast("Printing canceled", false); SetStatus(PrinterState.Unknown); return false; }
        catch (Exception ex)
        {
            ShowToast(ex.Message, false);
            SetStatus(PrinterState.Offline);
            return false;
        }
        finally
        {
            _printing = false;
            if (showProgress) await Task.Delay(250);
            SetProgress(false, 0, "");
            UpdateChrome();
        }
    }

    void SetProgress(bool on, double value, string text)
    {
        if (on && ProgressPanel.Visibility != Visibility.Visible)
        {
            PrintButton.Visibility = Visibility.Hidden;
            Motion.PopIn(ProgressPanel, 0.96);
            ProgressFill.Width = 0;
        }
        else if (!on && ProgressPanel.Visibility == Visibility.Visible)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            Motion.PopIn(PrintButton, 0.94);
        }
        FeedButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Text = text;
        ProgressText.Foreground = value > 0.5 ? Brushes.White : (Brush)FindResource("Text1");
        if (on) Motion.To(ProgressFill, WidthProperty, Math.Max(50, ProgressPanel.ActualWidth * value), 300, Motion.Smooth);
    }

    void CancelPrint_Click(object sender, RoutedEventArgs e) => _printCts?.Cancel();

    async void Feed_Click(object sender, RoutedEventArgs e) => await SendJobAsync(Tspl.FeedCommand, "Fed one label", false);

    async void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromSheet();
        await SendJobAsync(() => Tspl.CalibrateCommand(LabelFormat.Current), "Calibrating — the printer will feed a few labels", false);
    }

    async void TestLabel_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromSheet();
        var img = TestLabel.Create(_settings.Density, LabelFormat.Current);
        await SendJobAsync(() => Tspl.BuildJob([(img, 1)], _settings.ToPrintSettings()), "Test label sent");
    }

    // ================= Shipment details =================

    void CopyTracking_Click(object sender, RoutedEventArgs e)
    {
        if (Current?.Tracking is not { } t) return;
        try { Clipboard.SetText(t.Number); ShowToast("Tracking number copied", true); } catch { }
    }

    void Track_Click(object sender, RoutedEventArgs e)
    {
        var p = Current;
        var url = p?.Tracking?.TrackUrl ?? TrackingInfo.CarrierHome(p?.Carrier);
        Log($"Track clicked: carrier={p?.Carrier} tracking={p?.Tracking?.Number} url={url}");
        if (url == null) { ShowToast("Couldn't tell which carrier this label is from.", false); return; }
        string what = p?.Tracking != null ? $"{p.Carrier ?? p.Tracking.Carrier} tracking" : $"{p?.Carrier} tracking — paste the number there";
        var browser = OpenUrl(url);
        if (browser == "") ShowToast($"Opening {what}…", true);
        else if (browser != null) ShowToast($"Opened {what} in {browser} — your default browser didn't start", true);
        else ShowToast("Couldn't open a web browser.", false);
        if (browser != null && p?.Tracking != null) { try { Clipboard.SetText(p.Tracking.Number); } catch { } }
    }

    /// <summary>
    /// Opens a link in the default browser. If that browser fails to launch (a broken install, for example),
    /// tries the other installed browsers. Returns "" for the default browser, the fallback's name, or null.
    /// </summary>
    static string? OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Log("opened via default browser");
            return "";
        }
        catch (Exception ex) { Log("default browser failed: " + ex.Message); }

        foreach (var (name, exe) in InstalledBrowsers())
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { url } });
                Log($"opened via {name} ({exe})");
                return name;
            }
            catch (Exception ex) { Log($"{name} failed: {ex.Message}"); }
        }
        return null;
    }

    /// <summary>Registered browsers (Chrome and Edge first), skipping the default one that just failed.</summary>
    static List<(string Name, string Exe)> InstalledBrowsers()
    {
        var found = new List<(string, string)>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var root = hive.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
                foreach (var key in root?.GetSubKeyNames() ?? [])
                {
                    if (key.Contains("IEXPLORE", StringComparison.OrdinalIgnoreCase)) continue;
                    using var cmd = root!.OpenSubKey(key + @"\shell\open\command");
                    var line = cmd?.GetValue(null) as string;
                    if (string.IsNullOrEmpty(line)) continue;
                    var exe = line.StartsWith('"') ? line[1..line.IndexOf('"', 1)] : line.Split(' ')[0];
                    if (!File.Exists(exe) || found.Any(f => string.Equals(f.Item2, exe, StringComparison.OrdinalIgnoreCase))) continue;
                    using var nameKey = root.OpenSubKey(key);
                    var name = nameKey?.GetValue(null) as string ?? key;
                    found.Add((name, exe));
                }
            }
            catch { }
        }
        int Rank(string n) => n.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ? 0
                            : n.Contains("Edge", StringComparison.OrdinalIgnoreCase) ? 1
                            : n.Contains("Opera", StringComparison.OrdinalIgnoreCase) ? 9 : 5;
        return found.OrderBy(f => Rank(f.Item1)).ToList();
    }

    static void Log(string line)
    {
        try
        {
            var dir = AppPaths.DataDir;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app.log"), $"{DateTime.Now:u} {line}\n");
        }
        catch { }
    }

    // ================= Printer status =================

    void RefreshStatus_Click(object sender, RoutedEventArgs e) => _ = RefreshStatusAsync();

    async Task RefreshStatusAsync()
    {
        if (_printing) return;
        RefreshSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.8)) { RepeatBehavior = RepeatBehavior.Forever });
        if (_state is PrinterState.Unknown or PrinterState.Offline) SetStatus(PrinterState.Connecting);
        var s = await _printer.QueryStatusAsync();
        RefreshSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(1)));
        if (!_printing) SetStatus(s);
    }

    void SetStatus(PrinterState s)
    {
        _state = s;
        var (text, brush) = s switch
        {
            PrinterState.Ready => ("Ready", "Success"),
            PrinterState.Connecting => ("Connecting…", "Text3"),
            PrinterState.Printing => ("Printing", "Accent"),
            PrinterState.PaperOut => ("Out of labels", "Warning"),
            PrinterState.CoverOpen => ("Cover open", "Warning"),
            PrinterState.Paused => ("Paused", "Warning"),
            PrinterState.Error => ("Needs attention", "Danger"),
            PrinterState.Offline => ("Not connected", "Danger"),
            PrinterState.NotPaired => ("Not set up", "Warning"),
            PrinterState.LabelEdgeNotFound => ("Can't find label edge", "Warning"),
            _ => ("Unknown", "Text3"),
        };
        StatusText.Text = text;
        StatusText.ToolTip = s switch
        {
            PrinterState.Offline => _choice?.Kind == PrinterKind.WindowsPrinter
                ? (_printer.LastError ?? "Windows can't reach this printer.") + " Check the USB cable and that the printer is on."
                : (_printer.LastError ?? "") + " Make sure the printer is switched on, nearby, and not connected to your phone.",
            PrinterState.NotPaired => "Pair the printer in Windows Bluetooth settings, then choose it in Settings.",
            PrinterState.LabelEdgeNotFound => "The printer fed a whole label without finding the next one. Open Settings and run Calibrate, or set Label roll to Continuous. Open and close the lid to clear the red light.",
            _ => null,
        };
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        EmptyStatusText.Text = $"{PrinterNameText.Text} · {text}";
        EmptyStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
    }

    /// <summary>Switches to a printer (Bluetooth or Windows printer) and updates the printer card.</summary>
    void UsePrinter(PrinterChoice? choice)
    {
        _printer.Dispose();
        _choice = choice;
        _printer = choice?.CreateTransport() ?? new BluetoothPrinter();
        PrinterNameText.Text = choice?.ShortName ?? "No Printer";
        PrinterNameText.ToolTip = choice?.Display;
        UpdatePrinterDetail();
    }

    void UpdatePrinterDetail()
    {
        var f = LabelFormat.Current;
        PrinterDetailText.Text = _choice == null ? f.ToString() : $"{_choice.KindText} · {f.SizeText} · {f.Dpi} dpi";
    }

    // ----- label size & resolution -----

    public sealed record SizeItem(double W, double H, string Use, bool Custom = false)
    {
        public string Display => Custom ? "Custom size…" : $"{W:0.##} × {H:0.##} in   ·   {Use}";
    }

    void LoadSizes()
    {
        var items = LabelFormat.Presets.Select(p => new SizeItem(p.W, p.H, p.Use)).ToList();
        items.Add(new SizeItem(0, 0, "", true));
        SizeList.ItemsSource = items;
        var f = _settings.Format;
        var match = items.FirstOrDefault(i => !i.Custom && Math.Abs(i.W - f.WidthIn) < 0.01 && Math.Abs(i.H - f.HeightIn) < 0.01);
        SizeList.SelectedItem = match ?? items[^1];
        CustomW.Text = f.WidthIn.ToString("0.##");
        CustomH.Text = f.HeightIn.ToString("0.##");
        CustomSizeRow.Visibility = match == null ? Visibility.Visible : Visibility.Collapsed;
        DpiSwitch.SelectedIndex = f.Dpi == 300 ? 1 : 0;
    }

    void SizeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool custom = SizeList.SelectedItem is SizeItem { Custom: true };
        if (custom && CustomSizeRow.Visibility != Visibility.Visible) Motion.PopIn(CustomSizeRow, 0.96, -6, 420);
        else if (!custom) CustomSizeRow.Visibility = Visibility.Collapsed;
    }

    LabelFormat ReadFormatFromSheet()
    {
        double w = _settings.LabelWidthIn, h = _settings.LabelHeightIn;
        if (SizeList.SelectedItem is SizeItem { Custom: false } s) { w = s.W; h = s.H; }
        else if (SizeList.SelectedItem is SizeItem { Custom: true })
        {
            static double? Parse(string t) => double.TryParse(t.Trim().Replace("\"", "").Replace("in", ""),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
            if (Parse(CustomW.Text) is { } cw && cw >= 0.5 && cw <= 4.65) w = cw;
            if (Parse(CustomH.Text) is { } ch && ch >= 0.4 && ch <= 12) h = ch;
        }
        return new LabelFormat(w, h, DpiSwitch.SelectedIndex == 1 ? 300 : 203);
    }

    /// <summary>Applies a new label size/resolution and re-renders everything in the queue for it.</summary>
    async Task ApplyFormatAsync(LabelFormat f)
    {
        if (f == LabelFormat.Current) return;
        LabelFormat.Current = f;
        _settings.LabelWidthIn = f.WidthIn; _settings.LabelHeightIn = f.HeightIn; _settings.Dpi = f.Dpi;
        UpdatePrinterDetail();
        if (_labels.Count == 0) return;
        SetBusy(true, $"Fitting labels to {f.SizeText}…");
        try { foreach (var l in _labels.ToList()) await l.ChangeFormatAsync(f); }
        finally { SetBusy(false); }
        _shownPage = null; // force the preview to refresh its shape
        UpdateChrome();
    }

    // ================= Settings sheet =================

    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    void OpenSettings()
    {
        LoadPorts();
        LoadSizes();
        MediaSwitch.SelectedIndex = _settings.Media == MediaType.Continuous ? 1 : 0;
        SpeedSwitch.SelectedIndex = _settings.Speed <= 3 ? 0 : _settings.Speed >= 5 ? 2 : 1;
        FlipSwitch.IsChecked = _settings.Flip;
        ClearSwitch.IsChecked = _settings.ClearAfterPrint;
        WatchSwitch.IsChecked = _settings.WatchDownloads;
        AutoPrintSwitch.IsChecked = _settings.AutoPrintDownloads;
        InvertSwitch.IsChecked = _settings.InvertBits;
        SettingsCard.MaxHeight = Math.Max(300, ActualHeight - 70);
        SettingsScroll.ScrollToTop();
        SettingsSheet.Visibility = Visibility.Visible;
        Motion.FromTo(SettingsScrim, OpacityProperty, 0, 1, 220, Motion.Smooth);
        Motion.PopIn(SettingsCard, 0.9, 30, 600);
    }

    void LoadPorts()
    {
        var choices = PrinterChoice.Discover();
        PortList.ItemsSource = choices;
        PortList.SelectedItem = choices.FirstOrDefault(c => c.Kind == _choice?.Kind && c.Id == _choice?.Id);
        NoPortsText.Visibility = choices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void RefreshPorts_Click(object sender, RoutedEventArgs e) => LoadPorts();

    void ApplySettingsFromSheet()
    {
        if (SettingsSheet.Visibility != Visibility.Visible) return;
        if (PortList.SelectedItem is PrinterChoice sel && (sel.Kind != _choice?.Kind || sel.Id != _choice?.Id))
        {
            UsePrinter(sel);
            _settings.PrinterKind = sel.Kind;
            _settings.PrinterId = sel.Id;
            SetStatus(PrinterState.Unknown);
            _ = RefreshStatusAsync();
        }
        _ = ApplyFormatAsync(ReadFormatFromSheet());
        _settings.Media = MediaSwitch.SelectedIndex == 1 ? MediaType.Continuous : MediaType.Sensed;
        _settings.Speed = SpeedSwitch.SelectedIndex switch { 0 => 3, 2 => 6, _ => 4 };
        _settings.Flip = FlipSwitch.IsChecked == true;
        _settings.ClearAfterPrint = ClearSwitch.IsChecked == true;
        _settings.InvertBits = InvertSwitch.IsChecked == true;
        _settings.AutoPrintDownloads = AutoPrintSwitch.IsChecked == true;
        bool watch = WatchSwitch.IsChecked == true || _settings.AutoPrintDownloads;
        if (_settings.WatchDownloads != watch)
        {
            _settings.WatchDownloads = watch;
            StartWatchingDownloads();
        }
        _settings.Save();
    }

    void CloseSettings()
    {
        ApplySettingsFromSheet();
        Motion.FadeOut(SettingsScrim, 200);
        Motion.PopOut(SettingsCard, 0.94, 20, 220, () => SettingsSheet.Visibility = Visibility.Collapsed);
    }

    void SettingsDone_Click(object sender, RoutedEventArgs e) => CloseSettings();
    void SettingsScrim_MouseDown(object sender, MouseButtonEventArgs e) => CloseSettings();

    // ================= Downloads =================

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out string path);

    static string DownloadsFolder()
    {
        if (AppPaths.DownloadsOverride is { } over) return over;
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0 && Directory.Exists(p)) return p;
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    void LoadRecentDownloads()
    {
        var items = new List<RecentFile>();
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            items = new DirectoryInfo(DownloadsFolder()).EnumerateFiles()
                .Where(f => Supported.Contains(f.Extension.ToLowerInvariant()) && f.LastWriteTime > cutoff)
                .OrderByDescending(f => f.LastWriteTime).Take(5)
                .Select(f => new RecentFile(f.FullName, f.Name, Ago(f.LastWriteTime), f.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "" : ""))
                .ToList();
        }
        catch { }
        RecentList.ItemsSource = items;
        RecentEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "Just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (t.Date == DateTime.Today) return t.ToString("h:mm tt");
        if (t.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
        return t.ToString("ddd");
    }

    async void RecentItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) await AddFilesAsync([path]);
    }

    void OpenDownloads_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(DownloadsFolder()) { UseShellExecute = true }); } catch { }
    }

    void StartWatchingDownloads()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!_settings.WatchDownloads && !_settings.AutoPrintDownloads) return;
        try
        {
            _watcher = new FileSystemWatcher(DownloadsFolder()) { IncludeSubdirectories = false, EnableRaisingEvents = true };
            _watcher.Created += (_, a) => OnDownload(a.FullPath);
            _watcher.Renamed += (_, a) => OnDownload(a.FullPath);
        }
        catch { }
    }

    readonly HashSet<string> _autoHandled = new(StringComparer.OrdinalIgnoreCase);

    void OnDownload(string path)
    {
        if (!Supported.Contains(Path.GetExtension(path).ToLowerInvariant())) return;
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(1200); // let the browser finish writing
            if (!File.Exists(path) || !_autoHandled.Add(path)) return;
            LoadRecentDownloads();
            if (_labels.Any(l => string.Equals(l.SourcePath, path, StringComparison.OrdinalIgnoreCase))) return;

            if (_settings.AutoPrintDownloads && Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var pages = await AddFilesAsync([path]);
                if (pages.Count > 0)
                    await PrintPagesAsync(pages, recordHistory: true,
                        doneMessage: pages.Count == 1 ? $"Printed {Path.GetFileNameWithoutExtension(path)} from Downloads" : $"Printed {pages.Count} labels from Downloads");
                return;
            }
            if (!_settings.WatchDownloads) return;
            if (_labels.Count > 0)
            {
                _bannerPath = path;
                DownloadBannerName.Text = Path.GetFileName(path);
                Motion.PopIn(DownloadBanner, 0.9, 12);
            }
        });
    }

    async void DownloadBannerAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_bannerPath is { } p) { HideBanner(); await AddFilesAsync([p]); }
    }

    void HideBanner()
    {
        _bannerPath = null;
        if (DownloadBanner.Visibility == Visibility.Visible) Motion.PopOut(DownloadBanner, 0.9, 10);
    }

    // ================= Feedback =================

    void SetBusy(bool on, string text = "")
    {
        _busy = on;
        BusyText.Text = text;
        if (on)
        {
            Motion.PopIn(BusyOverlay, 0.85);
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.8)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            Motion.PopOut(BusyOverlay, 0.9, 0, 180, () => SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null));
        }
    }

    readonly DispatcherTimer _toastTimer = new();

    void ShowToast(string text, bool success)
    {
        ToastText.Text = text;
        ToastIcon.Text = success ? "" : "";
        ToastIcon.SetResourceReference(TextBlock.ForegroundProperty, success ? "Success" : "Warning");
        Motion.PopIn(Toast, 0.8, -36, 640);
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(success ? 2.6 : 4.5);
        _toastTimer.Tick -= HideToast;
        _toastTimer.Tick += HideToast;
        _toastTimer.Start();
    }

    void HideToast(object? sender, EventArgs e)
    {
        _toastTimer.Stop();
        Motion.PopOut(Toast, 0.9, -24, 260);
    }

    // ================= Keyboard & window =================

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (StampMenu.Visibility == Visibility.Visible && e.Key == Key.Escape) { CloseStampMenu(); e.Handled = true; return; }
        if (SettingsSheet.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { CloseSettings(); e.Handled = true; }
            return;
        }
        if (_busy) return;
        bool cropping = CropStage.Visibility == Visibility.Visible;
        var cur = Current;
        switch (e.Key)
        {
            case Key.O when ctrl: OpenFileDialog(); e.Handled = true; break;
            case Key.V when ctrl: Paste(); e.Handled = true; break;
            case Key.P when ctrl: Print_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.Escape when cropping: ExitCrop(); e.Handled = true; break;
            case Key.Enter when cropping: _ = ApplyCropAsync(); e.Handled = true; break;
            case Key.R when !ctrl && !cropping && cur is { IsEditable: true }: _ = cur.RotateAsync().ContinueWith(_ => Dispatcher.Invoke(BumpPaper)); e.Handled = true; break;
            case Key.C when !ctrl && !cropping && cur is { IsEditable: true }: EnterCrop(); e.Handled = true; break;
            case Key.S when !ctrl && !cropping && cur != null: OpenStampMenu(); e.Handled = true; break;
            case Key.Delete when !cropping: RemoveSelected(); e.Handled = true; break;
            case Key.OemPlus or Key.Add when cur != null: cur.Copies++; e.Handled = true; break;
            case Key.OemMinus or Key.Subtract when cur != null: cur.Copies--; e.Handled = true; break;
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ================= Diagnostics =================

    internal async Task SnapshotAsync(string outPng, string[] flags)
    {
        await Task.Delay(4000); // let files load, animations settle and the status check finish
        if (flags.FirstOrDefault(f => f.StartsWith("--select=")) is { } sel) { LabelList.SelectedIndex = int.Parse(sel["--select=".Length..]); await Task.Delay(600); }
        if (flags.Contains("--demo-history"))
        {
            foreach (var l in _labels) _history.Insert(0, LabelHistory.Add(l));
            _labels.Clear();
            SwitchList(true);
            await Task.Delay(900);
        }
        if (flags.Contains("--stamp-fragile")) { ApplyStamp(Stamp.Fragile()); await Task.Delay(1200); }
        if (flags.FirstOrDefault(f => f.StartsWith("--stamp-image=")) is { } si) { ApplyStamp(Stamp.FromImage(si["--stamp-image=".Length..])); await Task.Delay(1200); }
        if (flags.Contains("--stamp-faint") && Current?.Stamp is { } faintStamp) { faintStamp.Faint = true; Current.Recompose(); UpdateChrome(); await Task.Delay(300); }
        if (flags.Contains("--stamp-menu")) { OpenStampMenu(); await Task.Delay(900); }
        if (flags.Contains("--history")) { SwitchList(true); await Task.Delay(900); }
        if (flags.Contains("--settings")) { OpenSettings(); await Task.Delay(900); }
        if (flags.Contains("--settings-bottom")) { OpenSettings(); await Task.Delay(700); SettingsScroll.ScrollToEnd(); await Task.Delay(300); }
        if (flags.Contains("--crop")) { EnterCrop(); await Task.Delay(900); }
        if (flags.Contains("--drop")) { _dropShown = true; Motion.FadeIn(DropOverlay, 1); Motion.PopIn(DropBubble, 0.99, 0, 1); await Task.Delay(400); }
        if (flags.Contains("--toast")) { ShowToast("Sent 2 labels to the printer", true); await Task.Delay(900); }
        if (flags.Contains("--ready")) { _statusTimer.Stop(); SetStatus(PrinterState.Ready); }
        await Task.Delay(200);
        Root.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(Root.ActualWidth * dpi.DpiScaleX), (int)(Root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(Root);
        using (var fs = File.Create(outPng))
        {
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            enc.Save(fs);
        }
        Close();
    }
}
