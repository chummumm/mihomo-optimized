using System.Windows;
using System.Windows.Controls;

namespace Mihomo.Desktop.Controls;

/// <summary>The reference header's flex-wrap: wrap only when the measured title and commands do not fit.</summary>
internal sealed class FluentPageHeader : Panel
{
    private readonly FrameworkElement heading;
    private readonly WrapPanel tools;
    private bool stacked;
    private double firstHeight, secondHeight;
    internal bool Narrow { get; set; }
    internal FluentPageHeader(FrameworkElement heading, WrapPanel tools)
    {
        this.heading = heading; this.tools = tools;
        Children.Add(heading); Children.Add(tools);
    }
    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsFinite(available.Width) ? available.Width : 1280;
        heading.Measure(new Size(width, double.PositiveInfinity));
        tools.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        stacked = tools.Children.Count > 0 && heading.DesiredSize.Width + 16 + tools.DesiredSize.Width > width;
        if (stacked) tools.Measure(new Size(width, double.PositiveInfinity));
        firstHeight = heading.DesiredSize.Height;
        secondHeight = tools.Children.Count > 0 ? tools.DesiredSize.Height : 0;
        return new Size(width, stacked ? firstHeight + 12 + secondHeight : Math.Max(firstHeight, secondHeight));
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (stacked)
        {
            heading.Arrange(new Rect(0, 0, finalSize.Width, firstHeight));
            tools.HorizontalAlignment = HorizontalAlignment.Left;
            tools.Arrange(new Rect(0, firstHeight + 12, finalSize.Width, secondHeight));
        }
        else
        {
            double commands = tools.Children.Count > 0 ? tools.DesiredSize.Width : 0;
            heading.Arrange(new Rect(0, Math.Max(0, (finalSize.Height - firstHeight) / 2), Math.Max(0, finalSize.Width - commands - (commands > 0 ? 16 : 0)), firstHeight));
            tools.HorizontalAlignment = HorizontalAlignment.Right;
            tools.Arrange(new Rect(Math.Max(0, finalSize.Width - commands), Math.Max(0, (finalSize.Height - secondHeight) / 2), commands, secondHeight));
        }
        return finalSize;
    }
}
