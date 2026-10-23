using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
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
    public static TextBlock Text(string value, double size = 14, bool strong = false) => new()
    {
        Text = value, FontSize = size, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
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
    public static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 12, 12) };
    public static DataGrid Table(params (string Header, string Path, double Width)[] columns)
    {
        var table = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow, EnableRowVirtualization = true,
            EnableColumnVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            RowHeight = 38, MinRowHeight = 32, BorderThickness = new Thickness(1), FontSize = 13 };
        table.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
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
    public static TextBox SearchBox(string label, string id)
    {
        var input = new TextBox { MinWidth = 180, Height = 34, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = label }.Id(id);
        L.Bind(input, FrameworkElement.ToolTipProperty, label);
        L.Bind(input, AutomationProperties.NameProperty, label); return input;
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
