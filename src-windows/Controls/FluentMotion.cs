using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mihomo.Desktop.Controls;

public static class FluentMotion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    public static Duration Fast => TimeSpan.FromMilliseconds(167);
    public static Duration Normal => TimeSpan.FromMilliseconds(250);
    public static IEasingFunction Decelerate => new CubicEase { EasingMode = EasingMode.EaseOut };

    public static void Enter(FrameworkElement element, bool back = false)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        element.RenderTransform = Transform.Identity;
        if (!Enabled) return;
        var movement = new TranslateTransform(back ? -28 : 0, back ? 0 : 20);
        element.RenderTransform = movement;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(167)));
        var slide = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = Decelerate };
        movement.BeginAnimation(back ? TranslateTransform.XProperty : TranslateTransform.YProperty, slide);
    }

    public static void Flash(FrameworkElement element)
    {
        if (!Enabled) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.55, 1, TimeSpan.FromMilliseconds(333)) { EasingFunction = Decelerate });
    }
}
