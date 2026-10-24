using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Mihomo.Desktop.Controls;

/// <summary>
/// Keeps textarea padding inside the scrolling document. The ordinary TextBox
/// template puts it outside the viewport and clips a partly visible last line.
/// Text editing, line virtualization, selection, undo and automation remain WPF's.
/// </summary>
internal sealed class TextAreaTextBox : TextBox
{
    private ScrollViewer? viewer;
    private TextAreaViewport? viewport;

    public TextAreaTextBox() => SetResourceReference(StyleProperty, typeof(TextBox));

    public override void OnApplyTemplate()
    {
        viewer = null; viewport = null;
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_ContentHost") is not ScrollViewer host) return;
        if (host.Content is TextAreaViewport current)
        {
            viewer = host; viewport = current; ApplyPadding(); return;
        }
        if (host.Content is not FrameworkElement content || content is not IScrollInfo scrolling) return;

        // TextBox has just supplied its own TextBoxView. Wrap that same live
        // view; never copy its text or replace its native editor/automation peer.
        host.Content = null;
        viewport = new TextAreaViewport(content, scrolling);
        host.Content = viewport;
        viewer = host;
        ApplyPadding();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == PaddingProperty) ApplyPadding();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (viewport is null) { base.OnMouseWheel(e); return; }
        if (e.Handled) return;
        // TextBoxBase calls its TextBoxView directly. Use the whole document
        // instead, so the last wheel step can reveal trailing padding too.
        // Leave an event at either edge unhandled for the outer scroll viewer.
        var before = viewport.VerticalOffset;
        if (e.Delta > 0) viewport.MouseWheelUp();
        else if (e.Delta < 0) viewport.MouseWheelDown();
        if (Math.Abs(viewport.VerticalOffset - before) > .001) e.Handled = true;
    }

    internal bool TryGetTextViewport(out double offset, out Rect bounds)
    {
        offset = 0; bounds = Rect.Empty;
        return viewport is not null && viewport.TryGetTextViewport(this, out offset, out bounds);
    }

    private void ApplyPadding()
    {
        if (viewer is null || viewport is null) return;
        // Keep WPF's horizontal bidi-caret margins and horizontal scrolling
        // unchanged. Only the vertical padding moves into the document.
        viewer.Padding = new Thickness(Padding.Left, 0, Padding.Right, 0);
        viewport.SetPadding(Padding.Top, Padding.Bottom);
    }
}

internal sealed class TextAreaViewport : Decorator, IScrollInfo
{
    private readonly FrameworkElement textView;
    private readonly IScrollInfo scrolling;
    private double top, bottom, viewportHeight, extentHeight, verticalOffset, appliedTextOffset;
    private ScrollViewer? scrollOwner;
    private bool arranging, observing;

    internal TextAreaViewport(FrameworkElement textView, IScrollInfo scrolling)
    {
        this.textView = textView; this.scrolling = scrolling;
        Child = textView;
        ClipToBounds = true;
        Loaded += (_, _) =>
        {
            if (observing) return;
            observing = true; textView.LayoutUpdated += NativeLayoutUpdated;
            NativeLayoutUpdated(null, EventArgs.Empty);
        };
        Unloaded += (_, _) =>
        {
            if (!observing) return;
            observing = false; textView.LayoutUpdated -= NativeLayoutUpdated;
        };
    }

    internal void SetPadding(double top, double bottom)
    {
        if (this.top == top && this.bottom == bottom) return;
        this.top = top; this.bottom = bottom;
        InvalidateMeasure();
    }

    internal bool TryGetTextViewport(Visual ancestor, out double offset, out Rect bounds)
    {
        offset = scrolling.VerticalOffset; bounds = Rect.Empty;
        if (!textView.IsArrangeValid || !ancestor.IsAncestorOf(textView)) return false;
        bounds = textView.TransformToAncestor(ancestor).TransformBounds(new Rect(textView.RenderSize));
        return !bounds.IsEmpty && bounds.Height > 0;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        textView.Measure(new Size(constraint.Width, Math.Max(0, constraint.Height - top - bottom)));
        return new Size(Math.Min(constraint.Width, textView.DesiredSize.Width),
            Math.Min(constraint.Height, textView.DesiredSize.Height + top + bottom));
    }

    protected override Size ArrangeOverride(Size size)
    {
        SynchronizeNativeOffset();
        var before = (viewportHeight, extentHeight, verticalOffset, scrolling.ViewportWidth, scrolling.ExtentWidth);
        viewportHeight = size.Height;

        // The first arrange publishes TextBoxView's extent. The next pass then
        // uses that real extent to reveal padding only at the document edges.
        // The range is identical to the old view:
        // text + top + bottom - viewport == text - (viewport - top - bottom).
        arranging = true;
        try
        {
            for (var pass = 0; pass < 3; pass++)
            {
                var textHeight = scrolling.ExtentHeight;
                extentHeight = textHeight + top + bottom;
                verticalOffset = Math.Clamp(verticalOffset, 0, Math.Max(0, extentHeight - viewportHeight));
                var leading = Math.Min(top, Math.Max(0, top - verticalOffset));
                var trailing = Math.Min(bottom, Math.Max(0, verticalOffset + viewportHeight - top - textHeight));
                var bounds = new Rect(0, leading, size.Width, Math.Max(0, viewportHeight - leading - trailing));
                textView.Arrange(bounds);

                // SetVerticalOffset clamps against the just-arranged native view.
                // Arrange again only when the requested text offset changed.
                var target = Math.Max(0, verticalOffset - top);
                if (Math.Abs(scrolling.VerticalOffset - target) > .001)
                {
                    scrolling.SetVerticalOffset(target);
                    textView.Arrange(bounds);
                }
                if (Math.Abs(scrolling.ExtentHeight - textHeight) <= .001) break;
            }
            extentHeight = scrolling.ExtentHeight + top + bottom;
            appliedTextOffset = scrolling.VerticalOffset;
        }
        finally { arranging = false; }
        if (before != (viewportHeight, extentHeight, verticalOffset, scrolling.ViewportWidth, scrolling.ExtentWidth))
            scrollOwner?.InvalidateScrollInfo();
        return size;
    }

    private void NativeLayoutUpdated(object? sender, EventArgs e)
    {
        if (arranging) return;
        // CaretElement's minimal-scroll path can call TextBoxView's IScrollInfo
        // directly. Adopt that real offset before another arrange, rather than
        // snapping a keyboard/selection scroll back to our previous position.
        var changed = SynchronizeNativeOffset();
        if (changed || Math.Abs(extentHeight - (scrolling.ExtentHeight + top + bottom)) > .001)
        {
            InvalidateArrange();
            scrollOwner?.InvalidateScrollInfo();
        }
    }

    private bool SynchronizeNativeOffset()
    {
        var current = scrolling.VerticalOffset;
        if (Math.Abs(current - appliedTextOffset) <= .001) return false;
        appliedTextOffset = current;
        verticalOffset = Math.Clamp(current <= 0 ? 0 : current + top, 0,
            Math.Max(0, scrolling.ExtentHeight + top + bottom - viewportHeight));
        return true;
    }

    public bool CanHorizontallyScroll { get => scrolling.CanHorizontallyScroll; set => scrolling.CanHorizontallyScroll = value; }
    public bool CanVerticallyScroll { get => scrolling.CanVerticallyScroll; set => scrolling.CanVerticallyScroll = value; }
    public double ExtentWidth => scrolling.ExtentWidth;
    public double ExtentHeight => extentHeight;
    public double ViewportWidth => scrolling.ViewportWidth;
    public double ViewportHeight => viewportHeight;
    public double HorizontalOffset => scrolling.HorizontalOffset;
    public double VerticalOffset => verticalOffset;
    public ScrollViewer ScrollOwner
    {
        get => scrollOwner!;
        set
        {
            if (ReferenceEquals(scrollOwner, value)) return;
            scrollOwner = value; scrolling.ScrollOwner = value;
            appliedTextOffset = scrolling.VerticalOffset;
            InvalidateMeasure();
        }
    }

    public void SetHorizontalOffset(double offset) => scrolling.SetHorizontalOffset(offset);
    public void SetVerticalOffset(double offset)
    {
        if (!CanVerticallyScroll || double.IsNaN(offset)) return;
        offset = Math.Clamp(offset, 0, Math.Max(0, extentHeight - viewportHeight));
        if (Math.Abs(verticalOffset - offset) <= .001) return;
        verticalOffset = offset;
        appliedTextOffset = scrolling.VerticalOffset;
        InvalidateArrange();
        scrollOwner?.InvalidateScrollInfo();
    }

    // Match the native TextBoxView/ScrollData's DIP increments.
    public void LineUp() => SetVerticalOffset(VerticalOffset - 16);
    public void LineDown() => SetVerticalOffset(VerticalOffset + 16);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - 48);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + 48);
    public void LineLeft() => scrolling.LineLeft();
    public void LineRight() => scrolling.LineRight();
    public void PageLeft() => scrolling.PageLeft();
    public void PageRight() => scrolling.PageRight();
    public void MouseWheelLeft() => scrolling.MouseWheelLeft();
    public void MouseWheelRight() => scrolling.MouseWheelRight();

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (rectangle.IsEmpty || visual is null || visual != this && !IsAncestorOf(visual)) return Rect.Empty;
        rectangle = visual.TransformToAncestor(this).TransformBounds(rectangle);
        var oldX = HorizontalOffset; var oldY = VerticalOffset;
        SetHorizontalOffset(VisibleOffset(oldX, ViewportWidth, oldX + rectangle.Left, oldX + rectangle.Right));
        SetVerticalOffset(VisibleOffset(oldY, ViewportHeight, oldY + rectangle.Top, oldY + rectangle.Bottom));
        rectangle.Offset(oldX - HorizontalOffset, oldY - VerticalOffset);
        rectangle.Intersect(new Rect(0, 0, ViewportWidth, ViewportHeight));
        return rectangle;
    }

    private static double VisibleOffset(double start, double length, double first, double last)
    {
        var before = first < start; var after = last > start + length; var fits = last - first <= length;
        if (before && !after && fits || after && !before && !fits) return first;
        if (before && !after && !fits || after && !before && fits) return last - length;
        return start;
    }
}
