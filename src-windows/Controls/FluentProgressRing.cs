using System.Windows;
using System.ComponentModel;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mihomo.Desktop.Controls;

public sealed class FluentProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(FluentProgressRing), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush? Foreground { get => (Brush?)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(FluentProgressRing), new PropertyMetadata(false, (d, _) => ((FluentProgressRing)d).UpdateAnimation()));
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public static readonly DependencyProperty IsButtonSpinnerProperty = DependencyProperty.Register(nameof(IsButtonSpinner), typeof(bool), typeof(FluentProgressRing), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((FluentProgressRing)d).UpdateAnimation()));
    /// <summary>The Button CSS spinner: a 2px border with its right quarter missing, rotating in 900ms.</summary>
    public bool IsButtonSpinner { get => (bool)GetValue(IsButtonSpinnerProperty); set => SetValue(IsButtonSpinnerProperty, value); }
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double?), typeof(FluentProgressRing), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((FluentProgressRing)d).ChangeValue((double?)e.OldValue, (double?)e.NewValue)), value => value is null || value is double number && double.IsFinite(number));
    /// <summary>Null selects the animated ProgressRing; a value selects a determinate arc, clamped to 0..1.</summary>
    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    private static readonly DependencyProperty DashLengthProperty = DependencyProperty.Register("DashLength", typeof(double), typeof(FluentProgressRing), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty DashOffsetProperty = DependencyProperty.Register("DashOffset", typeof(double), typeof(FluentProgressRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty DisplayedValueProperty = DependencyProperty.Register("DisplayedValue", typeof(double), typeof(FluentProgressRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private readonly RotateTransform rotation = new();
    private bool updating;
    public FluentProgressRing()
    {
        IsHitTestVisible = false; Visibility = Visibility.Collapsed; Width = Height = 16; RenderTransformOrigin = new Point(.5, .5); RenderTransform = rotation;
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Loaded += (_, _) => { SystemParameters.StaticPropertyChanged += SystemChanged; DesignTokens.Changed += InvalidateVisual; UpdateAnimation(); };
        Unloaded += (_, _) => { SystemParameters.StaticPropertyChanged -= SystemChanged; DesignTokens.Changed -= InvalidateVisual; StopAnimations(); };
    }
    private void SystemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))
            Dispatcher.InvokeAsync(() => { if (IsButtonSpinner || Value.HasValue) UpdateAnimation(); else InvalidateVisual(); });
    }
    private void UpdateAnimation()
    {
        if (updating) return;
        updating = true;
        try
        {
            Visibility = IsActive ? Visibility.Visible : Visibility.Collapsed;
            StopAnimations();
            bool determinate = !IsButtonSpinner && Value.HasValue;
            rotation.Angle = determinate ? -90 : 0;
            SetCurrentValue(DisplayedValueProperty, Math.Clamp(Value ?? 0, 0, 1));
            InvalidateVisual();
            if (!IsActive || !IsLoaded || !IsVisible || determinate) return;
            if (IsButtonSpinner)
            {
                if (FluentMotion.Enabled) Spin(900);
                return;
            }
            // base.css explicitly exempts ProgressRing and its circle from reduced motion.
            Spin(2000);
            BeginAnimation(DashLengthProperty, DashAnimation(1, 62, 62));
            BeginAnimation(DashOffsetProperty, DashAnimation(0, -14, -99));
        }
        finally { updating = false; }
    }
    private void Spin(double milliseconds) => rotation.BeginAnimation(RotateTransform.AngleProperty,
        new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(milliseconds)) { RepeatBehavior = RepeatBehavior.Forever });
    private static DoubleAnimationUsingKeyFrames DashAnimation(double start, double middle, double end)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1500), RepeatBehavior = RepeatBehavior.Forever };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(start, TimeSpan.Zero));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(middle, TimeSpan.FromMilliseconds(750), new CubicBezierEase(.4, 0, .2, 1)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(end, TimeSpan.FromMilliseconds(1500), new CubicBezierEase(.4, 0, .2, 1)));
        return animation;
    }
    private void StopAnimations()
    {
        rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        BeginAnimation(DashLengthProperty, null); BeginAnimation(DashOffsetProperty, null); BeginAnimation(DisplayedValueProperty, null);
    }
    private void ChangeValue(double? oldValue, double? newValue)
    {
        if (!oldValue.HasValue || !newValue.HasValue || IsButtonSpinner || !IsActive || !IsLoaded || !IsVisible)
        {
            UpdateAnimation(); return;
        }
        double from = (double)GetValue(DisplayedValueProperty), to = Math.Clamp(newValue.Value, 0, 1);
        BeginAnimation(DisplayedValueProperty, null); SetCurrentValue(DisplayedValueProperty, to);
        if (FluentMotion.Enabled && Math.Abs(from - to) > .000001)
            BeginAnimation(DisplayedValueProperty, new DoubleAnimation(from, to, FluentMotion.Normal) { EasingFunction = FluentMotion.Decelerate, FillBehavior = FillBehavior.Stop });
    }
    protected override void OnRender(DrawingContext drawing)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        double stroke = IsButtonSpinner ? 2 : size >= 40 ? 3 : size >= 24 ? 2.5 : 2;
        double radius = size / 2 - (IsButtonSpinner ? stroke / 2 : stroke);
        if (radius <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        Brush brush = Foreground ?? (IsButtonSpinner ? TextElement.GetForeground(this) : TryFindResource("AccentBrush") as Brush) ?? Brushes.DodgerBlue;
        double start, sweep;
        if (IsButtonSpinner) { start = Math.PI / 4; sweep = 3 * Math.PI / 2; }
        else if (Value.HasValue) { start = 0; sweep = Math.Clamp((double)GetValue(DisplayedValueProperty), 0, 1) * Math.Tau; }
        else
        {
            // SVG's circle path starts at 3 o'clock. The 100-unit gap keeps the
            // dash from wrapping, so its visible tail shrinks as offset approaches -99.
            double offset = Math.Clamp(-(double)GetValue(DashOffsetProperty), 0, 100);
            start = offset * Math.Tau / 100;
            sweep = Math.Min((double)GetValue(DashLengthProperty), 100 - offset) * Math.Tau / 100;
        }
        var pen = new Pen(brush, stroke) { StartLineCap = IsButtonSpinner ? PenLineCap.Flat : PenLineCap.Round, EndLineCap = IsButtonSpinner ? PenLineCap.Flat : PenLineCap.Round };
        if (sweep >= Math.Tau - .000001) { drawing.DrawEllipse(null, pen, center, radius, radius); return; }
        var first = new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start));
        if (sweep <= 0) { drawing.DrawEllipse(brush, null, first, stroke / 2, stroke / 2); return; }
        var last = new Point(center.X + radius * Math.Cos(start + sweep), center.Y + radius * Math.Sin(start + sweep));
        var path = new StreamGeometry();
        using (var figure = path.Open())
        {
            figure.BeginFigure(first, false, false);
            figure.ArcTo(last, new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise, true, false);
        }
        path.Freeze(); drawing.DrawGeometry(null, pen, path);
    }
}
