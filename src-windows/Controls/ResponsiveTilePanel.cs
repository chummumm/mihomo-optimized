using System.Windows;
using System.Windows.Controls;

namespace Mihomo.Desktop.Controls;

/// <summary>Equal-width quick-setting tiles, wrapping at the design's minimum tile width into balanced rows.</summary>
public sealed class ResponsiveTilePanel : Panel
{
    public double MinimumTileWidth { get; set; } = 200;
    public double Gap { get; set; } = 8;
    public int MaximumColumns { get; set; } = 4;
    private int Columns(double width)
    {
        int fit = Math.Clamp(double.IsInfinity(width) ? MaximumColumns : (int)Math.Floor((Math.Max(0, width) + Gap) / (MinimumTileWidth + Gap)), 1, Math.Max(1, MaximumColumns));
        // Balance the rows instead of leaving one orphan tile: four tiles flow 4 → 2 × 2 → 1, never 3 + 1.
        int count = InternalChildren.Cast<UIElement>().Count(child => child.Visibility != Visibility.Collapsed);
        if (count <= 1) return 1;
        int rows = (int)Math.Ceiling(count / (double)fit);
        return Math.Max(1, (int)Math.Ceiling(count / (double)rows));
    }
    protected override Size MeasureOverride(Size available)
    {
        int count = Columns(available.Width);
        double width = double.IsInfinity(available.Width) ? MinimumTileWidth : Math.Max(0, (available.Width - Gap * (count - 1)) / count);
        double height = 0, rowHeight = 0; int column = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(width, double.PositiveInfinity)); rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++column == count) { height += rowHeight + Gap; rowHeight = 0; column = 0; }
        }
        height += column > 0 ? rowHeight : -Gap;
        return new Size(double.IsInfinity(available.Width) ? width * count + Gap * (count - 1) : available.Width, Math.Max(0, height));
    }
    protected override Size ArrangeOverride(Size final)
    {
        var children = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToArray();
        int columns = Columns(final.Width); double width = Math.Max(0, (final.Width - Gap * (columns - 1)) / columns), y = 0;
        for (int index = 0; index < children.Length; index += columns)
        {
            double height = children.Skip(index).Take(columns).Max(child => child.DesiredSize.Height);
            for (int column = 0; column < columns && index + column < children.Length; column++) children[index + column].Arrange(new Rect(column * (width + Gap), y, width, height));
            y += height + Gap;
        }
        return final;
    }
}
