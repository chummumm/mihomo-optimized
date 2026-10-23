using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mihomo.Desktop.Controls;

public sealed record RouteHop(string Name, string Brush, bool IsPolicy = false);

/// <summary>Policy marks use configured line colors; group text yields first to preserve the exit name.</summary>
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
        double excess = Math.Max(0, widths.Sum() + marks - ActualWidth);
        // CSS flex-shrink is four for group text and one for the final adapter.
        for (var pass = 0; excess > .01 && pass <= widths.Length; pass++)
        {
            var weight = widths.Select((width, index) => width > 0 ? text[index].WidthIncludingTrailingWhitespace * (index == widths.Length - 1 ? 1 : 4) : 0).Sum(); if (weight <= 0) break;
            double removed = 0; for (var index = 0; index < widths.Length; index++) { var cut = Math.Min(widths[index], excess * text[index].WidthIncludingTrailingWhitespace * (index == widths.Length - 1 ? 1 : 4) / weight); widths[index] -= cut; removed += cut; } excess -= removed;
        }
        double x = 0;
        for (var index = 0; index < Hops.Length; index++)
        {
            var hop = Hops[index]; var mark = hop.IsPolicy ? 8d : 6d; var brush = Resource(hop.Brush);
            drawing.DrawEllipse(hop.IsPolicy ? Resource("SurfaceBrush") : brush, hop.IsPolicy ? new Pen(brush, 2) : null, new Point(x + mark / 2, ActualHeight / 2), hop.IsPolicy ? 3 : 3, hop.IsPolicy ? 3 : 3); x += mark + 6;
            if (widths[index] >= 1) { text[index].MaxTextWidth = widths[index]; text[index].MaxLineCount = 1; text[index].Trimming = TextTrimming.CharacterEllipsis; drawing.DrawText(text[index], new Point(x, (ActualHeight - text[index].Height) / 2)); }
            x += widths[index] + 10;
        }
    }
}
