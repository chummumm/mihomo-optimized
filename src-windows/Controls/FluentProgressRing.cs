using System.Windows;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mihomo.Desktop.Controls;

public sealed class FluentProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(FluentProgressRing), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush? Foreground { get => (Brush?)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(FluentProgressRing), new PropertyMetadata(false, (d, _) => ((FluentProgressRing)d).UpdateAnimation()));
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    private readonly RotateTransform rotation = new();
    public FluentProgressRing()
    {
        IsHitTestVisible = false; Visibility = Visibility.Collapsed; Width = Height = 24; RenderTransformOrigin = new Point(.5, .5); RenderTransform = rotation;
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Loaded += (_, _) => { SystemParameters.StaticPropertyChanged += SystemChanged; UpdateAnimation(); };
        Unloaded += (_, _) => { SystemParameters.StaticPropertyChanged -= SystemChanged; rotation.BeginAnimation(RotateTransform.AngleProperty, null); };
    }
    private void SystemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast)) Dispatcher.InvokeAsync(UpdateAnimation);
    }
    private void UpdateAnimation()
    {
        Visibility = IsActive ? Visibility.Visible : Visibility.Collapsed;
        rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        if (IsActive && IsVisible && FluentMotion.Enabled)
            rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever });
    }
    protected override void OnRender(DrawingContext drawing)
    {
        double diameter = Math.Max(1, Math.Min(ActualWidth, ActualHeight) - 4), radius = diameter / 2;
        var path = new StreamGeometry();
        using (var figure = path.Open())
        {
            figure.BeginFigure(new Point(radius + 2, 2), false, false);
            figure.ArcTo(new Point(2 + radius - radius * .866, 2 + radius * .5), new Size(radius, radius), 0, true, SweepDirection.Clockwise, true, false);
        }
        drawing.DrawGeometry(null, new Pen(Foreground ?? TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, path);
    }
}
