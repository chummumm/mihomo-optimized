using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mihomo.Desktop.Controls;

/// <summary>The provider card's transparent, three-pixel indeterminate progress overlay.</summary>
internal sealed class ProxyProviderLoading : Grid
{
    private readonly Border train = new() { Height = 3, CornerRadius = new CornerRadius(99), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TranslateTransform travel = new();
    private bool subscribed;

    internal ProxyProviderLoading()
    {
        Background = Brushes.Transparent; Cursor = Cursors.AppStarting;
        var track = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true }; train.RenderTransform = travel; train.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); track.Children.Add(train); Children.Add(track);
        Loaded += (_, _) =>
        {
            if (!subscribed) { SystemParameters.StaticPropertyChanged += PreferenceChanged; subscribed = true; }
            if (ProxyVisuals.Animate) BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(167)) { FillBehavior = FillBehavior.Stop });
            UpdateAnimation();
        };
        Unloaded += (_, _) =>
        {
            if (subscribed) SystemParameters.StaticPropertyChanged -= PreferenceChanged; subscribed = false;
            travel.BeginAnimation(TranslateTransform.XProperty, null); BeginAnimation(OpacityProperty, null);
        };
        IsVisibleChanged += (_, _) => UpdateAnimation();
        SizeChanged += (_, _) => { Clip = new RectangleGeometry(new Rect(RenderSize), 4, 4); UpdateAnimation(); };
    }
    private void PreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast)) Dispatcher.InvokeAsync(UpdateAnimation);
    }
    private void UpdateAnimation()
    {
        travel.BeginAnimation(TranslateTransform.XProperty, null); travel.X = 0; train.Width = Math.Max(0, ActualWidth * .4);
        if (!IsLoaded || !IsVisible || !ProxyVisuals.Animate || train.Width <= 0) return;
        travel.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-train.Width, train.Width * 2.6, TimeSpan.FromMilliseconds(1600)) { EasingFunction = new CubicBezierEase(.8, 0, .2, 1), RepeatBehavior = RepeatBehavior.Forever });
    }
}
