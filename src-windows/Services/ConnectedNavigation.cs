using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mihomo.Desktop.Controls;

namespace Mihomo.Desktop.Services;

public static class ConnectedNavigation
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached("Target", typeof(string), typeof(ConnectedNavigation), new PropertyMetadata(null));
    public static string? GetTarget(DependencyObject element) => (string?)element.GetValue(TargetProperty);
    public static void SetTarget(DependencyObject element, string value) => element.SetValue(TargetProperty, value);
    private sealed record Capture(Window Owner, string Target, BitmapSource Image, Rect Rectangle,
        BitmapSource? RootImage, Rect RootRectangle, DateTime Time);
    private static Capture? pending;
    private static Action? finish;
    private static Window? animationOwner;
    public static bool HasPending(Window owner, string? target) => pending is { } value && value.Owner == owner && value.Target == target && DateTime.UtcNow - value.Time < TimeSpan.FromSeconds(5) && FluentMotion.Enabled;
    public static void Prepare(FrameworkElement element, string target)
    {
        pending = null;
        finish?.Invoke();
        if (!FluentMotion.Enabled || Window.GetWindow(element) is not { } window || !element.IsVisible || element.ActualWidth < 1 || element.ActualHeight < 1) return;
        var image = Snapshot(element); if (image is null) return;
        var root = window.Content as FrameworkElement;
        pending = new Capture(window, target, image, new Rect(element.TranslatePoint(new Point(), window), element.RenderSize),
            root is null ? null : Snapshot(root, wholePage: true),
            root is null ? Rect.Empty : new Rect(root.TranslatePoint(new Point(), window), root.RenderSize), DateTime.UtcNow);
    }

    public static void Cancel(Window window)
    {
        if (pending?.Owner == window) pending = null;
        if (animationOwner == window) finish?.Invoke();
    }

    public static void Hold(Window window, Canvas overlay, FrameworkElement page, string? target)
    {
        if (!HasPending(window, target) || pending is not { RootImage: not null } capture) return;
        finish?.Invoke();
        var image = RootImage(capture, window, overlay, excludeIdentity: false);
        overlay.Children.Add(image);
        var restoreInput = SuspendInput(page);
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        bool finished = false;
        void Complete()
        {
            if (finished) return; finished = true;
            timeout.Stop(); overlay.Children.Remove(image);
            restoreInput();
            if (ReferenceEquals(pending, capture)) pending = null;
            window.Closed -= Closed; page.Unloaded -= Unloaded;
            finish = null; animationOwner = null;
        }
        void Closed(object? sender, EventArgs args) => Complete();
        void Unloaded(object sender, RoutedEventArgs args) => Complete();
        timeout.Tick += (_, _) => Complete();
        finish = Complete; animationOwner = window;
        window.Closed += Closed; page.Unloaded += Unloaded; timeout.Start();
    }

    public static void Play(Window window, Canvas overlay, FrameworkElement page, string? target, FrameworkElement? transitionSurface = null)
    {
        var capture = pending;
        if (!FluentMotion.Enabled || capture is null || capture.Owner != window || capture.Target != target || DateTime.UtcNow - capture.Time > TimeSpan.FromSeconds(5)) return;
        pending = null;
        if (animationOwner == window) finish?.Invoke();
        page.UpdateLayout();
        var destination = Find(page, target!);
        if (destination is null || !destination.IsVisible || destination.ActualWidth < 1) return;
        var from = window.TranslatePoint(capture.Rectangle.TopLeft, overlay);
        var to = destination.TranslatePoint(new Point(), overlay);
        // The route's live 28px entrance continues beneath the cross-fade.
        // Move the shared identity to its settled position, not that transient
        // transform, so restoring the real header cannot jump at completion.
        if (transitionSurface?.RenderTransform is TranslateTransform translation)
            to.Offset(-translation.X, -translation.Y);
        var arrival = Snapshot(destination); if (arrival is null) return;
        finish?.Invoke();
        Image? root = capture.RootImage is null ? null : RootImage(capture, window, overlay, excludeIdentity: true);
        if (root is not null) overlay.Children.Add(root);
        var images = new Grid { Width = capture.Rectangle.Width, Height = capture.Rectangle.Height, IsHitTestVisible = false };
        var oldImage = new Image { Source = capture.Image, Stretch = Stretch.Fill };
        var newImage = new Image { Source = arrival, Stretch = Stretch.Fill, Opacity = 0 };
        images.Children.Add(oldImage); images.Children.Add(newImage);
        Canvas.SetLeft(images, from.X); Canvas.SetTop(images, from.Y); overlay.Children.Add(images);
        double opacity = destination.Opacity; destination.Opacity = 0;
        var restoreInput = SuspendInput(page);
        bool finished = false;
        void Complete()
        {
            if (finished) return; finished = true;
            destination.Opacity = opacity; overlay.Children.Remove(images);
            if (root is not null) overlay.Children.Remove(root);
            restoreInput();
            window.Closed -= Closed; page.Unloaded -= Unloaded;
            finish = null; animationOwner = null;
        }
        void Closed(object? sender, EventArgs args) => Complete();
        void Unloaded(object sender, RoutedEventArgs args) => Complete();
        finish = Complete; animationOwner = window; window.Closed += Closed; page.Unloaded += Unloaded;
        // The original customizes only exit-group's old/new image fades to
        // 300ms ease-out. Its group geometry and root retain the browser's
        // 250ms ease defaults (CSS View Transitions, user-agent stylesheet).
        var movementDuration = TimeSpan.FromMilliseconds(250);
        var imageDuration = TimeSpan.FromMilliseconds(300);
        void Animate(DependencyProperty property, double start, double end) => images.BeginAnimation(property, new DoubleAnimation(start, end, movementDuration) { EasingFunction = new CubicBezierEase(.25, .1, .25, 1) });
        Animate(Canvas.LeftProperty, from.X, to.X); Animate(Canvas.TopProperty, from.Y, to.Y);
        Animate(FrameworkElement.WidthProperty, capture.Rectangle.Width, destination.ActualWidth);
        Animate(FrameworkElement.HeightProperty, capture.Rectangle.Height, destination.ActualHeight);
        if (root is not null)
            root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, movementDuration) { EasingFunction = new CubicBezierEase(.25, .1, .25, 1) });
        var fade = new DoubleAnimation(0, 1, imageDuration) { EasingFunction = FluentMotion.Decelerate };
        fade.Completed += (_, _) => Complete();
        oldImage.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, imageDuration) { EasingFunction = FluentMotion.Decelerate });
        newImage.BeginAnimation(UIElement.OpacityProperty, fade);
    }
    private static Action SuspendInput(FrameworkElement page)
    {
        // Keep the real page's appearance while a previous visual state covers
        // it. Pointer input and focus must not reach controls the user cannot
        // yet see. Shell navigation remains available and cancels the capture.
        bool hitTestVisible = page.IsHitTestVisible;
        page.SetCurrentValue(UIElement.IsHitTestVisibleProperty, false);
        if (page.IsKeyboardFocusWithin) Keyboard.ClearFocus();
        KeyboardFocusChangedEventHandler focus = (_, e) => e.Handled = true;
        KeyEventHandler key = (_, e) => e.Handled = true;
        TextCompositionEventHandler text = (_, e) => e.Handled = true;
        page.PreviewGotKeyboardFocus += focus;
        page.PreviewKeyDown += key;
        page.PreviewTextInput += text;
        return () =>
        {
            page.PreviewGotKeyboardFocus -= focus;
            page.PreviewKeyDown -= key;
            page.PreviewTextInput -= text;
            page.SetCurrentValue(UIElement.IsHitTestVisibleProperty, hitTestVisible);
        };
    }
    private static Image RootImage(Capture capture, Window window, Canvas overlay, bool excludeIdentity)
    {
        var image = new Image { Source = capture.RootImage, Width = capture.RootRectangle.Width,
            Height = capture.RootRectangle.Height, Stretch = Stretch.Fill, IsHitTestVisible = false };
        var origin = window.TranslatePoint(capture.RootRectangle.TopLeft, overlay);
        Canvas.SetLeft(image, origin.X); Canvas.SetTop(image, origin.Y);
        if (excludeIdentity)
        {
            var identity = capture.Rectangle;
            identity.Offset(-capture.RootRectangle.X, -capture.RootRectangle.Y);
            image.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(capture.RootRectangle.Size)), new RectangleGeometry(identity));
        }
        return image;
    }
    private static BitmapSource? Snapshot(FrameworkElement element, bool wholePage = false)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        if (element.ActualWidth < 1 || element.ActualHeight < 1 || element.ActualWidth * dpi.DpiScaleX > (wholePage ? 4000 : 2000) || element.ActualHeight * dpi.DpiScaleY > (wholePage ? 3000 : 400)) return null;
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
