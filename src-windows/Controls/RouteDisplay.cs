using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mihomo.Desktop.Controls;

public sealed record RouteHop(string Name, string Brush);

/// <summary>Compact route marks share the configured policy order with the topology.</summary>
public sealed class RouteDisplay : Control
{
    public static readonly DependencyProperty HopsProperty = DependencyProperty.Register(nameof(Hops), typeof(RouteHop[]), typeof(RouteDisplay), new FrameworkPropertyMetadata(Array.Empty<RouteHop>(), FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public RouteHop[] Hops { get => (RouteHop[])GetValue(HopsProperty); set => SetValue(HopsProperty, value); }
    public RouteDisplay() { Height = 36; ClipToBounds = true; Focusable = false; }
    private static void Changed(DependencyObject source, DependencyPropertyChangedEventArgs e) => AutomationProperties.SetName(source, string.Join(" → ", ((RouteDisplay)source).Hops.Select(hop => hop.Name)));
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    protected override void OnRender(DrawingContext drawing)
    {
        double x = 4;
        foreach (var hop in Hops)
        {
            var brush = TryFindResource(hop.Brush) as Brush ?? Foreground;
            drawing.DrawEllipse(brush, null, new Point(x + 3, ActualHeight / 2), 2.5, 2.5);
            x += 12;
            var text = new FormattedText(hop.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.DrawText(text, new Point(x, (ActualHeight - text.Height) / 2)); x += text.Width + 14;
        }
        if (x > ActualWidth) ToolTip = string.Join(" → ", Hops.Select(hop => hop.Name));
    }
}
