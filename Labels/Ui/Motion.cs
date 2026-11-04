using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Labels.Ui;

/// <summary>
/// A critically-ish damped spring, normalised to the animation's duration.
/// Bounce 0 = no overshoot; 0.3 = a gentle, Apple-like settle.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public double Bounce { get; set; } = 0.18;

    public SpringEase() { EasingMode = EasingMode.EaseIn; } // EaseIn = use EaseInCore as-is

    protected override double EaseInCore(double t)
    {
        double zeta = Math.Clamp(1 - Bounce, 0.05, 0.999);
        const double w = 11.0;
        double wd = w * Math.Sqrt(1 - zeta * zeta);
        double e = Math.Exp(-zeta * w * t);
        double v = 1 - e * (Math.Cos(wd * t) + zeta * w / wd * Math.Sin(wd * t));
        return t >= 1 ? 1 : v;
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}

/// <summary>Small helpers for the springy, layered motion used across the app.</summary>
public static class Motion
{
    public static readonly SpringEase Smooth = new() { Bounce = 0.0 };
    public static readonly SpringEase Snappy = new() { Bounce = 0.15 };
    public static readonly SpringEase Bouncy = new() { Bounce = 0.32 };

    static Motion()
    {
        Smooth.Freeze(); Snappy.Freeze(); Bouncy.Freeze();
    }

    public static void To(IAnimatable target, DependencyProperty dp, double to, int ms = 420, IEasingFunction? ease = null, Action? done = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Snappy };
        if (done != null) a.Completed += (_, _) => done();
        target.BeginAnimation(dp, a, HandoffBehavior.SnapshotAndReplace);
    }

    public static void FromTo(IAnimatable target, DependencyProperty dp, double from, double to, int ms = 420, IEasingFunction? ease = null, Action? done = null)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Snappy };
        if (done != null) a.Completed += (_, _) => done();
        target.BeginAnimation(dp, a, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Ensures the element has a centred ScaleTransform (plus a TranslateTransform) we can animate.</summary>
    public static (ScaleTransform Scale, TranslateTransform Shift) Transforms(UIElement e)
    {
        if (e.RenderTransform is TransformGroup g && g.Children.Count == 2 &&
            g.Children[0] is ScaleTransform s && g.Children[1] is TranslateTransform t)
            return (s, t);
        s = new ScaleTransform(1, 1); t = new TranslateTransform();
        e.RenderTransform = new TransformGroup { Children = { s, t } };
        e.RenderTransformOrigin = new Point(0.5, 0.5);
        return (s, t);
    }

    public static void Scale(UIElement e, double to, int ms = 420, IEasingFunction? ease = null)
    {
        var (s, _) = Transforms(e);
        To(s, ScaleTransform.ScaleXProperty, to, ms, ease);
        To(s, ScaleTransform.ScaleYProperty, to, ms, ease);
    }

    /// <summary>Fade + scale in from slightly smaller, the way sheets and popovers arrive.</summary>
    public static void PopIn(UIElement e, double fromScale = 0.92, double fromY = 0, int ms = 520)
    {
        e.Visibility = Visibility.Visible;
        var (s, t) = Transforms(e);
        FromTo(e, UIElement.OpacityProperty, 0, 1, 220, Smooth);
        FromTo(s, ScaleTransform.ScaleXProperty, fromScale, 1, ms, Bouncy);
        FromTo(s, ScaleTransform.ScaleYProperty, fromScale, 1, ms, Bouncy);
        if (fromY != 0) FromTo(t, TranslateTransform.YProperty, fromY, 0, ms, Bouncy);
    }

    public static void PopOut(UIElement e, double toScale = 0.94, double toY = 0, int ms = 260, Action? done = null)
    {
        var (s, t) = Transforms(e);
        To(s, ScaleTransform.ScaleXProperty, toScale, ms, Smooth);
        To(s, ScaleTransform.ScaleYProperty, toScale, ms, Smooth);
        if (toY != 0) To(t, TranslateTransform.YProperty, toY, ms, Smooth);
        To(e, UIElement.OpacityProperty, 0, ms, Smooth, () =>
        {
            e.Visibility = Visibility.Collapsed;
            done?.Invoke();
        });
    }

    public static void FadeIn(UIElement e, int ms = 240)
    {
        e.Visibility = Visibility.Visible;
        FromTo(e, UIElement.OpacityProperty, e.Opacity >= 0.99 ? 0 : e.Opacity, 1, ms, Smooth);
    }

    public static void FadeOut(UIElement e, int ms = 200, Action? done = null)
        => To(e, UIElement.OpacityProperty, 0, ms, Smooth, () => { e.Visibility = Visibility.Collapsed; done?.Invoke(); });

    /// <summary>Press feedback: a quick squish that springs back.</summary>
    public static void AttachPress(UIElement e, double pressed = 0.95)
    {
        e.PreviewMouseLeftButtonDown += (_, _) => Scale(e, pressed, 160, Smooth);
        e.PreviewMouseLeftButtonUp += (_, _) => Scale(e, 1, 520, Bouncy);
        e.MouseLeave += (_, _) => Scale(e, 1, 520, Bouncy);
    }
}
