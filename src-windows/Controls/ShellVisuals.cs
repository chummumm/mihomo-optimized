using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mihomo.Desktop.Controls;

/// <summary>CSS-equivalent color transitions without replacing dynamic theme resources.</summary>
public static class ShellVisuals
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.RegisterAttached(
        "Background", typeof(Brush), typeof(ShellVisuals), new PropertyMetadata(Brushes.Transparent, BackgroundChanged));
    public static readonly DependencyProperty DurationProperty = DependencyProperty.RegisterAttached(
        "Duration", typeof(double), typeof(ShellVisuals), new PropertyMetadata(83d));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.RegisterAttached(
        "Foreground", typeof(Brush), typeof(ShellVisuals), new PropertyMetadata(Brushes.Black, ForegroundChanged));
    public static readonly DependencyProperty PressedProperty = DependencyProperty.RegisterAttached("Pressed", typeof(bool), typeof(ShellVisuals), new PropertyMetadata(false));
    public static readonly DependencyProperty TrackPressProperty = DependencyProperty.RegisterAttached("TrackPress", typeof(bool), typeof(ShellVisuals), new PropertyMetadata(false, TrackPressChanged));
    public static Brush GetBackground(DependencyObject target) => (Brush)target.GetValue(BackgroundProperty);
    public static void SetBackground(DependencyObject target, Brush value) => target.SetValue(BackgroundProperty, value);
    public static double GetDuration(DependencyObject target) => (double)target.GetValue(DurationProperty);
    public static void SetDuration(DependencyObject target, double value) => target.SetValue(DurationProperty, value);
    public static Brush GetForeground(DependencyObject target) => (Brush)target.GetValue(ForegroundProperty);
    public static void SetForeground(DependencyObject target, Brush value) => target.SetValue(ForegroundProperty, value);
    public static bool GetPressed(DependencyObject target) => (bool)target.GetValue(PressedProperty);
    public static void SetPressed(DependencyObject target, bool value) => target.SetValue(PressedProperty, value);
    public static bool GetTrackPress(DependencyObject target) => (bool)target.GetValue(TrackPressProperty);
    public static void SetTrackPress(DependencyObject target, bool value) => target.SetValue(TrackPressProperty, value);
    private static void TrackPressChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is not FrameworkElement element) return;
        if ((bool)change.NewValue)
        {
            element.PreviewMouseLeftButtonDown += Press; element.PreviewMouseLeftButtonUp += Release;
            element.MouseLeave += Release; element.LostMouseCapture += Release; element.Unloaded += Release;
        }
        else
        {
            element.PreviewMouseLeftButtonDown -= Press; element.PreviewMouseLeftButtonUp -= Release;
            element.MouseLeave -= Release; element.LostMouseCapture -= Release; element.Unloaded -= Release;
            SetPressed(element, false);
        }
    }
    private static void Press(object sender, RoutedEventArgs args) => SetPressed((DependencyObject)sender, true);
    private static void Release(object sender, RoutedEventArgs args) => SetPressed((DependencyObject)sender, false);

    private static void BackgroundChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is not Border border) return;
        if (change.NewValue is not SolidColorBrush next || !border.IsLoaded || !FluentMotion.Enabled)
        {
            border.Background = (Brush?)change.NewValue;
            return;
        }
        Color from = (border.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
        // CSS keeps the target hue while fading a transparent color. This avoids
        // an unintended black flash at the first frame of a colored hover.
        if (from.A == 0) from = Color.FromArgb(0, next.Color.R, next.Color.G, next.Color.B);
        Color to = next.Color;
        if (to.A == 0) to = Color.FromArgb(0, from.R, from.G, from.B);
        var rendered = new SolidColorBrush(to);
        border.Background = rendered;
        if (from != to) rendered.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(from, to, TimeSpan.FromMilliseconds(Math.Max(0, GetDuration(border)))));
    }

    private static void ForegroundChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is not FrameworkElement element) return;
        DependencyProperty? property = target is TextBlock ? TextBlock.ForegroundProperty : target is Control ? Control.ForegroundProperty : null;
        if (property is null) return;
        if (change.NewValue is not SolidColorBrush next || !element.IsLoaded || !FluentMotion.Enabled)
        {
            element.SetValue(property, change.NewValue);
            return;
        }
        Color from = (element.GetValue(property) as SolidColorBrush)?.Color ?? next.Color;
        var rendered = new SolidColorBrush(next.Color); element.SetValue(property, rendered);
        if (from != next.Color) rendered.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(from, next.Color, TimeSpan.FromMilliseconds(Math.Max(0, GetDuration(element)))));
    }
}
