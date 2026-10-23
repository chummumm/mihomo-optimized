using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mihomo.Desktop.Controls;

/// <summary>CSS cubic-bezier semantics: solve x(t) before evaluating y(t).</summary>
public sealed class CubicBezierEase : EasingFunctionBase
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; } = 1;
    public CubicBezierEase() { EasingMode = EasingMode.EaseIn; }
    public CubicBezierEase(double x1, double y1, double x2, double y2) : this() { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; }
    protected override double EaseInCore(double x)
    {
        if (x <= 0 || x >= 1) return x;
        double low = 0, high = 1, t = x;
        for (int i = 0; i < 26; i++)
        {
            double u = 1 - t, value = 3 * u * u * t * X1 + 3 * u * t * t * X2 + t * t * t;
            if (Math.Abs(value - x) < .0000001) break;
            if (value < x) low = t; else high = t;
            t = (low + high) / 2;
        }
        double rest = 1 - t;
        return 3 * rest * rest * t * Y1 + 3 * rest * t * t * Y2 + t * t * t;
    }
    protected override Freezable CreateInstanceCore() => new CubicBezierEase(X1, Y1, X2, Y2);
}

public static class FluentMotion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    public static Duration Faster => TimeSpan.FromMilliseconds(83);
    public static Duration Fast => TimeSpan.FromMilliseconds(167);
    public static Duration Normal => TimeSpan.FromMilliseconds(250);
    public static IEasingFunction Decelerate => new CubicBezierEase(0, 0, 0, 1);
    public static IEasingFunction Accelerate => new CubicBezierEase(.7, 0, 1, .5);
    public static IEasingFunction PointToPoint => new CubicBezierEase(.55, .55, 0, 1);

    public static void Enter(FrameworkElement element, bool back = false)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        var movement = new TranslateTransform();
        element.RenderTransform = movement;
        if (!Enabled) return;
        var duration = TimeSpan.FromMilliseconds(300);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = Decelerate, FillBehavior = FillBehavior.Stop });
        movement.BeginAnimation(back ? TranslateTransform.XProperty : TranslateTransform.YProperty,
            new DoubleAnimation(back ? -40 : 28, 0, duration) { EasingFunction = Decelerate, FillBehavior = FillBehavior.Stop });
    }

    public static void Flash(FrameworkElement element)
    {
        if (!Enabled || AdornerLayer.GetAdornerLayer(element) is not { } layer) return;
        foreach (var previous in layer.GetAdorners(element)?.OfType<AttentionAdorner>() ?? []) layer.Remove(previous);
        var attention = new AttentionAdorner(element);
        layer.Add(attention);
        attention.Play(() => layer.Remove(attention));
    }

    private sealed class AttentionAdorner : Adorner
    {
        private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register("Progress", typeof(double), typeof(AttentionAdorner), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
        internal AttentionAdorner(UIElement element) : base(element) { IsHitTestVisible = false; }
        internal void Play(Action finished)
        {
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1400)) { EasingFunction = Decelerate, RepeatBehavior = new RepeatBehavior(2), FillBehavior = FillBehavior.Stop };
            animation.Completed += (_, _) => finished(); BeginAnimation(ProgressProperty, animation);
        }
        protected override void OnRender(DrawingContext dc)
        {
            double p = (double)GetValue(ProgressProperty), spread = p * 10;
            var size = AdornedElement.RenderSize;
            if (size.Width <= 2 || size.Height <= 2 || TryFindResource("AccentBrush") is not Brush accent) return;
            var stroke = accent.CloneCurrentValue(); stroke.Opacity *= 1 - p;
            dc.DrawRoundedRectangle(null, new Pen(stroke, 2), new Rect(1, 1, size.Width - 2, size.Height - 2), 3, 3);
            if (spread <= 0) return;
            var halo = accent.CloneCurrentValue(); halo.Opacity *= .7 * (1 - p);
            var outer = new RectangleGeometry(new Rect(-spread, -spread, size.Width + 2 * spread, size.Height + 2 * spread), 4 + spread, 4 + spread);
            dc.DrawGeometry(halo, null, new CombinedGeometry(GeometryCombineMode.Exclude, outer, new RectangleGeometry(new Rect(size), 4, 4)));
        }
    }
}
