using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mihomo.Desktop.Controls;

/// <summary>Local acrylic for a separate popup HWND, sampling only its owning window's client content.</summary>
internal sealed class PopupBackdrop : IDisposable
{
    private readonly Border frame;
    private readonly UIElement content;
    private readonly Thickness padding;
    private readonly Border foreground;
    private readonly Grid layers = new();
    private readonly FluentBackdropFilter filter = new() { Sigma = 30, Saturation = 1.5 };
    private bool disposed;

    private PopupBackdrop(Border frame, FrameworkElement source, string id)
    {
        this.frame = frame; content = frame.Child ?? throw new InvalidOperationException("The popup frame has no content."); padding = frame.Padding;
        filter.Id(id); filter.SetResourceReference(FluentBackdropFilter.TintBrushProperty, "AcrylicBrush");
        // Popup.Child lives in another visual tree. Sampling the owner excludes
        // this popup and its filter; no screenshot or copied control is involved.
        filter.SetSources(source, source);
        frame.Child = null; foreground = new Border { Padding = padding, Child = content }; layers.Children.Add(filter); layers.Children.Add(foreground); frame.Padding = new Thickness(0); frame.Child = layers;
        frame.Background = Brushes.Transparent; UpdateShape();
        frame.SizeChanged += Resized; frame.Unloaded += Unloaded;
    }
    internal static PopupBackdrop? Attach(Border frame, FrameworkElement anchor, string id)
    {
        if (frame.Child is null || Owner(anchor)?.Content is not FrameworkElement source || source.IsAncestorOf(frame) || frame.IsAncestorOf(source)) return null;
        return new PopupBackdrop(frame, source, id);
    }
    internal static Window? Owner(FrameworkElement anchor)
    {
        DependencyObject? item = anchor; var visited = new HashSet<DependencyObject>();
        while (item is not null && visited.Add(item))
        {
            if (Window.GetWindow(item) is { } owner) return owner;
            item = item switch
            {
                ContextMenu menu => menu.PlacementTarget,
                MenuItem menuItem => ItemsControl.ItemsControlFromItemContainer(menuItem),
                _ => LogicalTreeHelper.GetParent(item),
            };
        }
        return null;
    }
    private void Resized(object sender, SizeChangedEventArgs args) => UpdateShape();
    private void UpdateShape()
    {
        var c = frame.CornerRadius; var b = frame.BorderThickness;
        filter.CornerRadius = new CornerRadius(Math.Max(0, c.TopLeft - Math.Max(b.Top, b.Left)), Math.Max(0, c.TopRight - Math.Max(b.Top, b.Right)), Math.Max(0, c.BottomRight - Math.Max(b.Bottom, b.Right)), Math.Max(0, c.BottomLeft - Math.Max(b.Bottom, b.Left)));
    }
    private void Unloaded(object sender, RoutedEventArgs args) => Dispose();
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        frame.SizeChanged -= Resized; frame.Unloaded -= Unloaded;
        filter.Dispose(); foreground.Child = null; layers.Children.Clear();
        if (ReferenceEquals(frame.Child, layers)) { frame.Child = null; frame.Padding = padding; frame.Child = content; frame.SetResourceReference(Border.BackgroundProperty, "AcrylicBrush"); }
    }
}
