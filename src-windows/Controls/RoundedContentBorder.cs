using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mihomo.Desktop.Controls;

/// <summary>
/// Clips the child to the inside of a rounded border without clipping the
/// border's own shadow. The child clip belongs to this container.
/// </summary>
public sealed class RoundedContentBorder : Border
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        Size arranged = base.ArrangeOverride(finalSize);
        if (Child is not { } child) return arranged;

        Thickness stroke = BorderThickness;
        Vector offset = VisualTreeHelper.GetOffset(child);
        double left = stroke.Left - offset.X, top = stroke.Top - offset.Y;
        double width = Math.Max(0, finalSize.Width - stroke.Left - stroke.Right);
        double height = Math.Max(0, finalSize.Height - stroke.Top - stroke.Bottom);
        CornerRadius corners = CornerRadius;
        var tl = new Size(Math.Max(0, corners.TopLeft - stroke.Left), Math.Max(0, corners.TopLeft - stroke.Top));
        var tr = new Size(Math.Max(0, corners.TopRight - stroke.Right), Math.Max(0, corners.TopRight - stroke.Top));
        var br = new Size(Math.Max(0, corners.BottomRight - stroke.Right), Math.Max(0, corners.BottomRight - stroke.Bottom));
        var bl = new Size(Math.Max(0, corners.BottomLeft - stroke.Left), Math.Max(0, corners.BottomLeft - stroke.Bottom));
        double factor = 1;
        foreach (var (sum, edge) in new[] { (tl.Width + tr.Width, width), (bl.Width + br.Width, width),
            (tl.Height + bl.Height, height), (tr.Height + br.Height, height) })
            if (sum > 0) factor = Math.Min(factor, edge / sum);
        tl = Scale(tl, factor); tr = Scale(tr, factor); br = Scale(br, factor); bl = Scale(bl, factor);

        var clip = new StreamGeometry();
        using (var drawing = clip.Open())
        {
            void Corner(Point end, Size radius)
            {
                if (radius.Width <= 0 || radius.Height <= 0) drawing.LineTo(end, true, false);
                else drawing.ArcTo(end, radius, 0, false, SweepDirection.Clockwise, true, false);
            }
            double right = left + width, bottom = top + height;
            drawing.BeginFigure(new Point(left + tl.Width, top), true, true);
            drawing.LineTo(new Point(right - tr.Width, top), true, false); Corner(new Point(right, top + tr.Height), tr);
            drawing.LineTo(new Point(right, bottom - br.Height), true, false); Corner(new Point(right - br.Width, bottom), br);
            drawing.LineTo(new Point(left + bl.Width, bottom), true, false); Corner(new Point(left, bottom - bl.Height), bl);
            drawing.LineTo(new Point(left, top + tl.Height), true, false); Corner(new Point(left + tl.Width, top), tl);
        }
        clip.Freeze();
        child.Clip = clip;
        return arranged;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == CornerRadiusProperty) InvalidateArrange();
    }

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        if (visualRemoved is UIElement removed) removed.ClearValue(ClipProperty);
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
    }

    private static Size Scale(Size radius, double factor) => new(radius.Width * factor, radius.Height * factor);
}
