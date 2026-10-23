using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mihomo.Desktop.Controls;

/// <summary>The reference live lamp: an 8 px dot and a 1.8 s expanding, fading copy.</summary>
internal sealed class LivePing : FrameworkElement
{
    internal static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(LivePing), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register("Progress", typeof(double), typeof(LivePing), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    internal Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    private bool subscribed, playing;

    internal LivePing()
    {
        Width = Height = 8; IsHitTestVisible = false;
        SetResourceReference(FillProperty, "SuccessBrush");
        Loaded += (_, _) =>
        {
            if (!subscribed) { SystemParameters.StaticPropertyChanged += PreferenceChanged; subscribed = true; }
            UpdateAnimation();
        };
        Unloaded += (_, _) =>
        {
            if (subscribed) SystemParameters.StaticPropertyChanged -= PreferenceChanged;
            subscribed = false; Stop();
        };
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    private void PreferenceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))
            Dispatcher.InvokeAsync(UpdateAnimation);
    }
    private void UpdateAnimation()
    {
        bool active = IsLoaded && IsVisible && FluentMotion.Enabled;
        if (active == playing) return;
        Stop();
        if (!active) return;
        playing = true;
        BeginAnimation(ProgressProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1800))
        {
            EasingFunction = FluentMotion.Decelerate,
            RepeatBehavior = RepeatBehavior.Forever
        });
    }
    private void Stop()
    {
        playing = false; BeginAnimation(ProgressProperty, null); SetValue(ProgressProperty, 1d);
    }
    protected override void OnRender(DrawingContext drawing)
    {
        var radius = Math.Min(ActualWidth, ActualHeight) / 2;
        if (Fill is null || radius <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        drawing.DrawEllipse(Fill, null, center, radius, radius);
        double progress = Math.Clamp((double)GetValue(ProgressProperty), 0, 1);
        if (!playing || progress >= 1) return;
        drawing.PushOpacity(.6 * (1 - progress));
        double halo = radius * (1 + 1.6 * progress);
        drawing.DrawEllipse(Fill, null, center, halo, halo);
        drawing.Pop();
    }
}
