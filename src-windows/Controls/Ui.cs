using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

internal static class JsonData
{
    public static JsonElement Get(this JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var result) ? result : default;
    public static string Text(this JsonElement item, string key = "", string fallback = "")
    {
        var value = key.Length == 0 ? item : item.Get(key);
        return value.ValueKind == JsonValueKind.String ? string.IsNullOrEmpty(value.GetString()) ? fallback : value.GetString()! : value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? value.ToString() : fallback;
    }
    public static double Number(this JsonElement item, string key = "", double fallback = 0)
    {
        var value = key.Length == 0 ? item : item.Get(key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : fallback;
    }
    public static bool Bool(this JsonElement item, string key = "") => (key.Length == 0 ? item : item.Get(key)).ValueKind == JsonValueKind.True;
    public static IEnumerable<JsonElement> Items(this JsonElement item) => item.ValueKind == JsonValueKind.Array ? item.EnumerateArray() : [];
    public static IEnumerable<JsonProperty> Fields(this JsonElement item) => item.ValueKind == JsonValueKind.Object ? item.EnumerateObject() : [];
}

internal static class Ui
{
    public static T Id<T>(this T element, string id) where T : DependencyObject { AutomationProperties.SetAutomationId(element, id); return element; }
    /// <summary>Live readings (latency, rates, ports, versions) use the UI face with fixed-width figures instead of a code font.</summary>
    public static T Tabular<T>(this T element) where T : DependencyObject
    {
        if (element is TextBlock or Control) element.ClearValue(element is TextBlock ? TextBlock.FontFamilyProperty : Control.FontFamilyProperty);
        System.Windows.Documents.Typography.SetNumeralAlignment(element, FontNumeralAlignment.Tabular); return element;
    }
    public static TextBlock Text(string value, double size = 14, bool strong = false) => new()
    {
        Text = value, FontSize = size, LineHeight = size == 12 ? 16 : size == 28 ? 36 : Math.Round(size * 1.43), FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
    };
    public static TextBlock Muted(string value, double size = 12)
    {
        var text = Text(value, size); text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); return text;
    }
    public static TextBlock Label(string value, double size = 14, bool strong = false) => L.Bind(Text(value, size, strong), TextBlock.TextProperty, value);
    public static TextBlock Hint(string value, double size = 12) => L.Bind(Muted(value, size), TextBlock.TextProperty, value);
    public static StackPanel Column(params UIElement[] children)
    {
        var panel = new StackPanel(); foreach (var child in children) panel.Children.Add(child); return panel;
    }
    public static WrapPanel Row(params UIElement[] children)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children) { if (child is FrameworkElement item) item.Margin = new Thickness(0, 0, 8, 8); panel.Children.Add(child); }
        return panel;
    }
    public static Border Card(UIElement content, double padding = 20)
    {
        var border = new Border { Child = content, Padding = new Thickness(padding), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 16) };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); return border;
    }
    public static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0) };
    public static DataGrid Table(params (string Header, string Path, double Width)[] columns)
    {
        var table = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow, EnableRowVirtualization = true,
            EnableColumnVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            RowHeight = 40, MinRowHeight = 32, BorderThickness = new Thickness(1), FontSize = 14 };
        table.SetResourceReference(Control.BackgroundProperty, "SolidCardBrush");
        table.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "DividerBrush");
        table.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        table.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        VirtualizingPanel.SetIsVirtualizing(table, true); VirtualizingPanel.SetVirtualizationMode(table, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(table, true);
        foreach (var (header, path, width) in columns)
        {
            var column = new DataGridTextColumn { Binding = new Binding(path), Width = width < 0 ? new DataGridLength(-width, DataGridLengthUnitType.Star) : new DataGridLength(width), MinWidth = 60 };
            L.Bind(column, DataGridColumn.HeaderProperty, header);
            table.Columns.Add(column);
        }
        return table;
    }
    // Call after configuring the columns. The native rows presenter reports no
    // horizontal extent without rows, although the separate headers still have
    // their full width. Physical scrolling of that real width fixes the empty
    // case; populated tables keep the original virtualizing scroll presenter.
    public static void KeepHeadersScrollableWhenEmpty(DataGrid table)
    {
        var width = new MultiBinding { Converter = new EmptyHeaderWidthConverter() };
        width.Bindings.Add(new Binding(nameof(ItemsControl.HasItems)) { Source = table });
        foreach (var column in table.Columns)
        {
            width.Bindings.Add(new Binding(nameof(DataGridColumn.ActualWidth)) { Source = column });
            width.Bindings.Add(new Binding(nameof(DataGridColumn.Visibility)) { Source = column });
        }
        var panel = new FrameworkElementFactory(typeof(DataGridRowsPresenter));
        panel.SetBinding(FrameworkElement.MinWidthProperty, width);
        table.ItemsPanel = new ItemsPanelTemplate(panel);
        bool hasItems = table.Items.Count > 0;
        long revision = 0;
        ScrollViewer.SetCanContentScroll(table, hasItems);
        ScrollViewer? Scroll() => table.Template?.FindName("DG_ScrollViewer", table) as ScrollViewer;
        table.ItemContainerGenerator.ItemsChanged += (_, _) =>
        {
            bool next = table.Items.Count > 0;
            if (next == hasItems) return;
            hasItems = next;
            var offset = Scroll()?.HorizontalOffset ?? 0;
            var current = ++revision;
            ScrollViewer.SetCanContentScroll(table, next);
            // Switching between the physical and virtualizing IScrollInfo can
            // replace its offset. Restore only after the new extent is measured.
            _ = table.Dispatcher.InvokeAsync(() =>
            {
                if (current == revision && table.IsLoaded) Scroll()?.ScrollToHorizontalOffset(offset);
            }, DispatcherPriority.Loaded);
        };
        table.Loaded += (_, _) =>
        {
            if (Scroll() is not { } viewer || viewer.Template?.FindName("PART_HorizontalScrollBar", viewer) is not ScrollBar bar) return;
            // Keep the real native scrollbar, including its range and commands,
            // across the whole table rather than indenting it by frozen columns.
            if (bar.Parent is Grid grid && grid.ColumnDefinitions.Count > 1)
            {
                Grid.SetColumn(bar, 0); Grid.SetColumnSpan(bar, grid.ColumnDefinitions.Count);
            }
        };
    }
    private sealed class EmptyHeaderWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length == 0 || values[0] is true) return 0d;
            double width = 0;
            for (int index = 1; index + 1 < values.Length; index += 2)
                if (values[index] is double value && double.IsFinite(value) && values[index + 1] is Visibility.Visible) width += Math.Max(0, value);
            return width;
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
    public static TextBox SearchBox(string label, string id, string? icon = null, bool rightIcon = false)
    {
        var input = new TextBox { MinWidth = 180, Height = 32, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = label }.Id(id);
        L.Bind(input, FrameworkElement.ToolTipProperty, label);
        L.Bind(input, AutomationProperties.NameProperty, label);
        InputHint.Attach(input, icon, rightIcon); return input;
    }
    public static string Bytes(double value)
    {
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB" }; var unit = 0; value = Math.Max(0, value);
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value.ToString(unit == 0 ? "0" : "0.##", CultureInfo.CurrentCulture)} {units[unit]}";
    }
    public static string Time(string value) => DateTimeOffset.TryParse(value, out var date) ? date.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) : value;
    public static string Delay(double value) => value > 0 ? $"{value:0.#} ms" : L.T("未测量");
    public static void Copy(string content) { if (!string.IsNullOrEmpty(content)) Clipboard.SetText(content); }
}
