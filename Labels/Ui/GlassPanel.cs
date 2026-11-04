using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Labels.Ui;

/// <summary>
/// Liquid-glass material. Samples the element named by <see cref="Backdrop"/> right behind the panel,
/// blurs and brightens it, then layers a translucent tint, a specular rim and a soft shadow on top.
/// </summary>
public class GlassPanel : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(GlassPanel), new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((GlassPanel)d).UpdateClip()));
    public static readonly DependencyProperty BackdropProperty = DependencyProperty.Register(
        nameof(Backdrop), typeof(UIElement), typeof(GlassPanel), new PropertyMetadata(null, (d, _) => ((GlassPanel)d).Rebuild()));
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(GlassPanel), new PropertyMetadata(null));
    public static readonly DependencyProperty BlurRadiusProperty = DependencyProperty.Register(
        nameof(BlurRadius), typeof(double), typeof(GlassPanel), new PropertyMetadata(36.0, (d, _) => ((GlassPanel)d).Rebuild()));
    public static readonly DependencyProperty ShadowProperty = DependencyProperty.Register(
        nameof(Shadow), typeof(bool), typeof(GlassPanel), new PropertyMetadata(true, (d, _) => ((GlassPanel)d).Rebuild()));

    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public UIElement? Backdrop { get => (UIElement?)GetValue(BackdropProperty); set => SetValue(BackdropProperty, value); }
    public Brush? Tint { get => (Brush?)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public double BlurRadius { get => (double)GetValue(BlurRadiusProperty); set => SetValue(BlurRadiusProperty, value); }
    public bool Shadow { get => (bool)GetValue(ShadowProperty); set => SetValue(ShadowProperty, value); }

    readonly Grid _root = new();
    readonly Border _shadow = new();
    readonly Grid _clipHost = new();
    readonly Rectangle _sample = new() { IsHitTestVisible = false };
    readonly Rectangle _tint = new() { IsHitTestVisible = false };
    readonly Rectangle _sheen = new() { IsHitTestVisible = false };
    readonly Border _rim = new() { IsHitTestVisible = false };
    readonly ContentPresenter _content = new();
    VisualBrush? _brush;

    static GlassPanel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(GlassPanel), new FrameworkPropertyMetadata(typeof(GlassPanel)));
        FocusableProperty.OverrideMetadata(typeof(GlassPanel), new FrameworkPropertyMetadata(false));
    }

    public GlassPanel()
    {
        Template = null;
        _clipHost.Children.Add(_sample);
        _clipHost.Children.Add(_tint);
        _clipHost.Children.Add(_sheen);
        _root.Children.Add(_shadow);
        _root.Children.Add(_clipHost);
        _root.Children.Add(_rim);
        _root.Children.Add(_content);
        AddVisualChild(_root);

        _tint.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding(nameof(Tint)) { Source = this });
        _content.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding(nameof(Content)) { Source = this });
        _content.SetBinding(MarginProperty, new System.Windows.Data.Binding(nameof(Padding)) { Source = this });

        _shadow.SetResourceReference(Border.BackgroundProperty, "GlassShadowFill");
        _sheen.SetResourceReference(Shape.FillProperty, "GlassSheen");
        _rim.SetResourceReference(Border.BorderBrushProperty, "GlassRim");
        _rim.BorderThickness = new Thickness(1);

        SizeChanged += (_, _) => { UpdateClip(); UpdateViewbox(); };
        LayoutUpdated += (_, _) => UpdateViewbox();
        Loaded += (_, _) => Rebuild();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _root;

    protected override Size MeasureOverride(Size constraint) { _root.Measure(constraint); return _root.DesiredSize; }
    protected override Size ArrangeOverride(Size arrangeBounds) { _root.Arrange(new Rect(arrangeBounds)); return arrangeBounds; }

    void Rebuild()
    {
        if (Shadow)
        {
            _shadow.Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 10, Direction = 270, Opacity = 0.16, Color = Colors.Black };
            _shadow.Visibility = Visibility.Visible;
        }
        else _shadow.Visibility = Visibility.Collapsed;

        if (Backdrop != null)
        {
            _brush = new VisualBrush(Backdrop)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            _sample.Fill = _brush;
            _sample.Effect = BlurRadius > 0 ? new BlurEffect { Radius = BlurRadius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance } : null;
            // Oversample so the blur has pixels to pull in at the panel's edges.
            _sample.Margin = new Thickness(-BlurRadius);
        }
        UpdateViewbox();
    }

    Rect _lastBox;
    void UpdateViewbox()
    {
        if (_brush == null || Backdrop == null || !IsVisible || ActualWidth <= 0) return;
        try
        {
            if (PresentationSource.FromVisual(this) is null) return;
            var gt = TransformToVisual(Backdrop);
            var r = gt.TransformBounds(new Rect(-BlurRadius, -BlurRadius, ActualWidth + 2 * BlurRadius, ActualHeight + 2 * BlurRadius));
            if (r != _lastBox) { _brush.Viewbox = r; _lastBox = r; }
        }
        catch (InvalidOperationException) { /* not in the same tree yet */ }
    }

    void UpdateClip()
    {
        double r = Math.Min(CornerRadius, Math.Min(ActualWidth, ActualHeight) / 2);
        var geo = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), r, r);
        geo.Freeze();
        _clipHost.Clip = geo;
        _shadow.CornerRadius = new CornerRadius(r);
        _rim.CornerRadius = new CornerRadius(r);
    }
}
