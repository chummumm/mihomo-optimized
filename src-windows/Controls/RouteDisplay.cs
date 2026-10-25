using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mihomo.Desktop.Controls;

public sealed record RouteHop(string Name, string Brush, bool IsPolicy = false);

/// <summary>Policy marks use configured line colors; when space runs out the group names collapse to their marks so the exit name stays readable.</summary>
public sealed class RouteDisplay : Control
{
    public static readonly DependencyProperty HopsProperty = DependencyProperty.Register(nameof(Hops), typeof(RouteHop[]), typeof(RouteDisplay), new FrameworkPropertyMetadata(Array.Empty<RouteHop>(), FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public static readonly DependencyProperty ClosedProperty = DependencyProperty.Register(nameof(Closed), typeof(bool), typeof(RouteDisplay), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public RouteHop[] Hops { get => (RouteHop[])GetValue(HopsProperty); set => SetValue(HopsProperty, value); }
    public bool Closed { get => (bool)GetValue(ClosedProperty); set => SetValue(ClosedProperty, value); }
    public RouteDisplay() { Height = 39; ClipToBounds = true; Focusable = false; SetResourceReference(FontFamilyProperty, "BodyFont"); Loaded += (_, _) => DesignTokens.Changed += Refresh; Unloaded += (_, _) => DesignTokens.Changed -= Refresh; }
    private void Refresh() => InvalidateVisual();
    private static void Changed(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var control = (RouteDisplay)source; var text = string.Join(" / ", control.Hops.Select(hop => hop.Name)); AutomationProperties.SetName(source, text); control.ToolTip = text;
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    protected override void OnRender(DrawingContext drawing)
    {
        if (ActualWidth <= 0 || Hops.Length == 0) return;
        Brush Resource(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        var text = Hops.Select((hop, index) => new FormattedText(hop.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 13,
            Resource(index == Hops.Length - 1 ? Closed ? "TextTertiaryBrush" : "TextPrimaryBrush" : "TextSecondaryBrush"), VisualTreeHelper.GetDpi(this).PixelsPerDip)).ToArray();
        var widths = text.Select(item => item.WidthIncludingTrailingWhitespace).ToArray();
        double marks = Hops.Sum(hop => (hop.IsPolicy ? 8 : 6) + 6) + Math.Max(0, Hops.Length - 1) * 10;
        double centre = ActualHeight / 2;
        void Mark(RouteHop hop, double left)
        {
            var brush = Resource(hop.Brush);
            drawing.DrawEllipse(hop.IsPolicy ? Resource("SurfaceBrush") : brush, hop.IsPolicy ? new Pen(brush, 2) : null, new Point(left + (hop.IsPolicy ? 4 : 3), centre), 3, 3);
        }
        void Label(int index, double left, double width)
        {
            if (width < 1) return;
            text[index].MaxTextWidth = width; text[index].MaxLineCount = 1; text[index].Trimming = TextTrimming.CharacterEllipsis;
            drawing.DrawText(text[index], new Point(left, (ActualHeight - text[index].Height) / 2));
        }
        if (widths.Sum() + marks <= ActualWidth || Hops.Length == 1)
        {
            double x = 0;
            for (var index = 0; index < Hops.Length; index++)
            {
                Mark(Hops[index], x); x += (Hops[index].IsPolicy ? 8 : 6) + 6;
                Label(index, x, Math.Min(widths[index], ActualWidth - x)); x += widths[index] + 10;
            }
            return;
        }
        // Too narrow for every hop: never cut group names down to one glyph. The policy hops collapse into
        // overlapping rings ahead of the exit, which keeps its full name; the tooltip still lists the chain.
        double cursor = 0;
        for (var index = 0; index < Hops.Length - 1; index++) { Mark(Hops[index], cursor); cursor += 6; }
        cursor += 6;
        var exit = Hops[^1]; Mark(exit, cursor); cursor += (exit.IsPolicy ? 8 : 6) + 6;
        Label(Hops.Length - 1, cursor, Math.Max(0, ActualWidth - cursor));
    }
}
