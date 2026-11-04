using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Labels.Ui;

/// <summary>A capsule segmented control whose glass thumb springs to the selected option.</summary>
public class Segmented : Border
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(string), typeof(Segmented), new PropertyMetadata("", (d, _) => ((Segmented)d).Build()));
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(Segmented),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((Segmented)d).OnSelected((int)e.OldValue)));

    /// <summary>Options separated by '|'.</summary>
    public string Items { get => (string)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public event EventHandler? SelectionChanged;

    readonly Grid _grid = new();
    readonly Border _thumb = new() { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2) };
    readonly UniformGrid _labels = new() { Rows = 1 };
    readonly TranslateTransform _shift = new();
    readonly ScaleTransform _squish = new(1, 1);

    public Segmented()
    {
        Height = 32;
        Padding = new Thickness(0);
        SetResourceReference(BackgroundProperty, "SegmentTrack");
        _thumb.SetResourceReference(Border.BackgroundProperty, "SegmentThumb");
        _thumb.SetResourceReference(Border.BorderBrushProperty, "GlassRim");
        _thumb.BorderThickness = new Thickness(0.75);
        _thumb.Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1.5, Direction = 270, Opacity = 0.16 };
        _thumb.RenderTransform = new TransformGroup { Children = { _squish, _shift } };
        _thumb.RenderTransformOrigin = new Point(0.5, 0.5);
        _grid.Children.Add(_thumb);
        _grid.Children.Add(_labels);
        Child = _grid;
        SizeChanged += (_, _) => Layout(false);
        Loaded += (_, _) => { Build(); Layout(false); };
    }

    string[] Options => Items.Split('|', StringSplitOptions.RemoveEmptyEntries);

    void Build()
    {
        _labels.Children.Clear();
        var opts = Options;
        _labels.Columns = Math.Max(1, opts.Length);
        for (int i = 0; i < opts.Length; i++)
        {
            int idx = i;
            var t = new TextBlock
            {
                Text = opts[i], FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0),
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Text1");
            var hit = new Border { Background = Brushes.Transparent, Child = t, Cursor = Cursors.Hand };
            hit.MouseLeftButtonDown += (_, e) =>
            {
                Motion.To(_squish, ScaleTransform.ScaleXProperty, 0.94, 140, Motion.Smooth);
                Motion.To(_squish, ScaleTransform.ScaleYProperty, 0.94, 140, Motion.Smooth);
                e.Handled = true;
            };
            hit.MouseLeftButtonUp += (_, e) =>
            {
                Motion.To(_squish, ScaleTransform.ScaleXProperty, 1, 520, Motion.Bouncy);
                Motion.To(_squish, ScaleTransform.ScaleYProperty, 1, 520, Motion.Bouncy);
                SelectedIndex = idx;
                e.Handled = true;
            };
            _labels.Children.Add(hit);
        }
        Layout(false);
    }

    void OnSelected(int old)
    {
        Layout(true);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    void Layout(bool animate)
    {
        int n = Math.Max(1, Options.Length);
        double w = ActualWidth > 0 ? ActualWidth : 0;
        double seg = w / n;
        _thumb.Width = Math.Max(0, seg - 4);
        CornerRadius = new CornerRadius(ActualHeight / 2);
        _thumb.CornerRadius = new CornerRadius(Math.Max(0, ActualHeight / 2 - 2));
        double x = seg * Math.Clamp(SelectedIndex, 0, n - 1);
        if (animate) Motion.To(_shift, TranslateTransform.XProperty, x, 560, Motion.Bouncy);
        else { _shift.BeginAnimation(TranslateTransform.XProperty, null); _shift.X = x; }
        for (int i = 0; i < _labels.Children.Count; i++)
            if (_labels.Children[i] is Border { Child: TextBlock tb })
                tb.FontWeight = i == SelectedIndex ? FontWeights.SemiBold : FontWeights.Normal;
    }
}
