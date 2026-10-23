using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Mihomo.Desktop.Controls;

// A visual hint leaves Text and its native ValuePattern completely unchanged.
internal sealed class InputHint : Adorner
{
    private readonly Grid content = new() { Margin = new Thickness(10, 0, 10, 0), IsHitTestVisible = false };
    private readonly TextBlock hint = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    private InputHint(TextBox input, string? icon) : base(input)
    {
        IsHitTestVisible = false; IsClipEnabled = true;
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        hint.SetBinding(TextBlock.TextProperty, new Binding(nameof(input.ToolTip)) { Source = input });
        hint.SetBinding(TextBlock.FontFamilyProperty, new Binding(nameof(input.FontFamily)) { Source = input });
        hint.SetBinding(TextBlock.FontSizeProperty, new Binding(nameof(input.FontSize)) { Source = input });
        if (icon is not null)
        {
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            var glyph = new FluentIcon(icon) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
            content.Children.Add(glyph); Grid.SetColumn(hint, 1);
        }
        content.Children.Add(hint); AddVisualChild(content); AddLogicalChild(content);
        Refresh(input);
    }

    internal static void Attach(TextBox input, string? icon)
    {
        input.Padding = new Thickness(icon is null ? 10 : 34, 0, 10, 0);
        AdornerLayer? layer = null;
        InputHint? adorner = null;
        input.Loaded += (_, _) =>
        {
            layer = AdornerLayer.GetAdornerLayer(input);
            if (layer is null || adorner is not null) return;
            adorner = new InputHint(input, icon); layer.Add(adorner);
        };
        input.Unloaded += (_, _) =>
        {
            if (adorner is not null) layer?.Remove(adorner);
            adorner = null; layer = null;
        };
        input.TextChanged += (_, _) => adorner?.Refresh(input);
    }

    private void Refresh(TextBox input) => hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? content : throw new ArgumentOutOfRangeException(nameof(index));
    protected override Size MeasureOverride(Size constraint)
    {
        content.Measure(AdornedElement.RenderSize); return AdornedElement.RenderSize;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        content.Arrange(new Rect(finalSize)); return finalSize;
    }
}
