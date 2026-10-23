using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Mihomo.Desktop.Controls;

namespace Mihomo.Desktop.Services;

public static class ConnectedNavigation
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached("Target", typeof(string), typeof(ConnectedNavigation), new PropertyMetadata(null));
    public static string? GetTarget(DependencyObject element) => (string?)element.GetValue(TargetProperty);
    public static void SetTarget(DependencyObject element, string value) => element.SetValue(TargetProperty, value);
    private sealed record Capture(Window Owner, string Target, BitmapSource Image, Rect Rectangle, DateTime Time);
    private static Capture? pending;
    private static Action? finish;
    public static bool HasPending(Window owner, string? target) => pending is { } value && value.Owner == owner && value.Target == target && DateTime.UtcNow - value.Time < TimeSpan.FromSeconds(5) && FluentMotion.Enabled;
    public static void Prepare(FrameworkElement element, string target)
    {
        pending = null;
        finish?.Invoke();
        if (!FluentMotion.Enabled || Window.GetWindow(element) is not { } window || !element.IsVisible || element.ActualWidth < 1 || element.ActualHeight < 1) return;
        var image = Snapshot(element); if (image is null) return;
        pending = new Capture(window, target, image, new Rect(element.TranslatePoint(new Point(), window), element.RenderSize), DateTime.UtcNow);
    }
    public static void Play(Window window, Canvas overlay, FrameworkElement page, string? target)
    {
        var capture = pending; pending = null;
        if (!FluentMotion.Enabled || capture is null || capture.Owner != window || capture.Target != target || DateTime.UtcNow - capture.Time > TimeSpan.FromSeconds(5)) return;
        page.UpdateLayout();
        var destination = Find(page, target!);
        if (destination is null || !destination.IsVisible || destination.ActualWidth < 1) return;
        var from = window.TranslatePoint(capture.Rectangle.TopLeft, overlay);
        var to = destination.TranslatePoint(new Point(), overlay);
        var arrival = Snapshot(destination); if (arrival is null) return;
        finish?.Invoke();
        var images = new Grid { Width = capture.Rectangle.Width, Height = capture.Rectangle.Height, IsHitTestVisible = false };
        var oldImage = new Image { Source = capture.Image, Stretch = Stretch.Fill };
        var newImage = new Image { Source = arrival, Stretch = Stretch.Fill, Opacity = 0 };
        images.Children.Add(oldImage); images.Children.Add(newImage);
        Canvas.SetLeft(images, from.X); Canvas.SetTop(images, from.Y); overlay.Children.Add(images);
        double opacity = destination.Opacity; destination.Opacity = 0;
        bool finished = false;
        void Complete()
        {
            if (finished) return; finished = true;
            destination.Opacity = opacity; overlay.Children.Remove(images);
            window.Closed -= Closed; page.Unloaded -= Unloaded;
            finish = null;
        }
        void Closed(object? sender, EventArgs args) => Complete();
        void Unloaded(object sender, RoutedEventArgs args) => Complete();
        finish = Complete; window.Closed += Closed; page.Unloaded += Unloaded;
        var duration = TimeSpan.FromMilliseconds(300);
        void Animate(DependencyProperty property, double start, double end) => images.BeginAnimation(property, new DoubleAnimation(start, end, duration) { EasingFunction = FluentMotion.Decelerate });
        Animate(Canvas.LeftProperty, from.X, to.X); Animate(Canvas.TopProperty, from.Y, to.Y);
        Animate(FrameworkElement.WidthProperty, capture.Rectangle.Width, destination.ActualWidth);
        Animate(FrameworkElement.HeightProperty, capture.Rectangle.Height, destination.ActualHeight);
        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = FluentMotion.Decelerate };
        fade.Completed += (_, _) => Complete();
        oldImage.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, duration) { EasingFunction = FluentMotion.Decelerate });
        newImage.BeginAnimation(UIElement.OpacityProperty, fade);
    }
    private static BitmapSource? Snapshot(FrameworkElement element)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        if (element.ActualWidth < 1 || element.ActualHeight < 1 || element.ActualWidth * dpi.DpiScaleX > 2000 || element.ActualHeight * dpi.DpiScaleY > 400) return null;
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(element); image.Freeze(); return image;
    }
    private static FrameworkElement? Find(DependencyObject root, string target)
    {
        if (root is FrameworkElement element && GetTarget(element) == target) return element;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (Find(VisualTreeHelper.GetChild(root, index), target) is { } found) return found;
        return null;
    }
}
