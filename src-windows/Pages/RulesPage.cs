using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class RulesPage : NativePage
{
    private readonly DataGrid table = Ui.Table(("顺序", nameof(RuleRow.Index), 78), ("类型", nameof(RuleRow.Type), 180), ("内容", nameof(RuleRow.DisplayPayload), -2), ("策略", nameof(RuleRow.Proxy), 242));
    private readonly TextBox search = Ui.SearchBox("筛选规则", "RuleSearch", "filter", true);
    private readonly StackPanel providers = new();
    private readonly TextBlock count = Ui.Muted("");
    private readonly TextBlock empty = Ui.Hint("没有符合筛选条件的规则。", 14);
    private readonly TextBlock providerCount = Ui.Text("", 13);
    private readonly StackPanel providerSection = new() { Margin = new Thickness(0, 0, 0, 28) };
    private readonly Dictionary<string, string> policyColors = new(StringComparer.Ordinal);
    private List<RuleRow> rows = [];
    private JsonElement providerData;
    private readonly HashSet<string> updating = new(StringComparer.Ordinal);
    private bool compactProviders;

    public RulesPage(AppSession session) : base(session, "rules", "规则", "")
    {
        count.FontSize = 13; count.LineHeight = 18.59; count.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; SetHeaderMeta(count);
        search.Width = 220; search.MinWidth = 0; search.Margin = new Thickness(0); Tools.Children.Add(search);
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition());
        search.TextChanged += (_, _) => ApplyFilter(); search.KeyDown += (_, e) => { if (e.Key == Key.Escape) { search.Clear(); e.Handled = true; } };
        var providerHeading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 0, 0, 8) }; providerHeading.Children.Add(Ui.Label("规则集", 14, true)); providerCount.Margin = new Thickness(10, 0, 0, 0); providerCount.VerticalAlignment = VerticalAlignment.Center; providerHeading.Children.Add(providerCount);
        providerSection.Children.Add(providerHeading);
        var providerCard = Ui.Card(providers, 0); providerCard.Margin = new Thickness(0); providerCard.SetResourceReference(Border.BackgroundProperty, "SolidCardBrush");
        providerSection.Children.Add(providerCard); providerSection.Id("RuleProviders"); layout.Children.Add(providerSection);
        var surface = new Grid(); surface.SizeChanged += (_, _) => surface.Clip = new RectangleGeometry(new Rect(surface.RenderSize), 7, 7); table.BorderThickness = new Thickness(0); surface.Children.Add(table); table.Id("RuleTable"); table.RowHeight = 40;
        table.RowStyle = RuleVisuals.Style("Row"); table.CanUserResizeColumns = table.CanUserSortColumns = table.CanUserReorderColumns = false;
        foreach (var column in table.Columns) column.MinWidth = 0;
        table.Columns[0].Header = "#";
        var indexStyle = new Style(typeof(TextBlock)); indexStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12d)); indexStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("TextTertiaryBrush")));
        ((DataGridTextColumn)table.Columns[0]).ElementStyle = indexStyle;
        var payloadStyle = new Style(typeof(TextBlock)); payloadStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12d)); payloadStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new DynamicResourceExtension("MonoFont"))); payloadStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis)); payloadStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(RuleRow.Payload))));
        ((DataGridTextColumn)table.Columns[2]).ElementStyle = payloadStyle;
        var badge = new FrameworkElementFactory(typeof(Border)); badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(4)); badge.SetValue(Border.PaddingProperty, new Thickness(6, 1, 6, 1)); badge.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left); badge.SetResourceReference(Border.BackgroundProperty, "ControlAltBrush");
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding(nameof(RuleRow.Type))); label.SetValue(TextBlock.FontSizeProperty, 12d); label.SetValue(TextBlock.LineHeightProperty, 18d); label.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight); label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); badge.AppendChild(label);
        var typeColumn = new DataGridTemplateColumn { CellTemplate = new DataTemplate { VisualTree = badge }, Width = 180 };
        L.Bind(typeColumn, DataGridColumn.HeaderProperty, "类型"); table.Columns[1] = typeColumn;
        var route = new FrameworkElementFactory(typeof(RulePolicyLabel)); route.SetBinding(RulePolicyLabel.LabelProperty, new Binding(nameof(RuleRow.Proxy))); route.SetBinding(RulePolicyLabel.BrushKeyProperty, new Binding(nameof(RuleRow.BrushKey))); route.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(RuleRow.Proxy)));
        var routeColumn = new DataGridTemplateColumn { CellTemplate = new DataTemplate { VisualTree = route }, Width = 242, MinWidth = 0 };
        L.Bind(routeColumn, DataGridColumn.HeaderProperty, "策略"); table.Columns[3] = routeColumn;
        empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Top; empty.Margin = new Thickness(0, 58, 0, 0); empty.Visibility = Visibility.Collapsed; surface.Children.Add(empty);
        var frame = Ui.Card(surface, 0); frame.Margin = new Thickness(0); frame.MinHeight = 360; frame.ClipToBounds = true; frame.SetResourceReference(Border.BackgroundProperty, "SolidCardBrush"); Grid.SetRow(frame, 1); layout.Children.Add(frame); SetBody(layout);
        SizeChanged += (_, _) => UpdateMetrics(); Body.SizeChanged += (_, _) => UpdateMetrics(); UpdateMetrics();
    }

    private void UpdateMetrics()
    {
        bool narrow = PageContainerWidth <= 640;
        search.Width = narrow ? Math.Max(1, Body.ActualWidth) : 220;
        double inset = narrow ? 12 : 16, gap = narrow ? 8 : 12;
        table.Columns[0].Width = inset + (narrow ? 36 : 56) + gap / 2;
        table.Columns[1].Width = (narrow ? 112 : 168) + gap;
        table.Columns[3].Width = gap / 2 + (narrow ? 96 : 220) + inset;
        for (int index = 0; index < table.Columns.Count; index++)
        {
            var padding = new Thickness(index == 0 ? inset : gap / 2, 0, index == table.Columns.Count - 1 ? inset : gap / 2, 0);
            var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), RuleVisuals.Style("Header")); header.Setters.Add(new Setter(Control.PaddingProperty, padding));
            if (index == 0) header.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextTertiaryBrush")));
            table.Columns[index].HeaderStyle = header;
            var cell = new Style(typeof(DataGridCell), RuleVisuals.Style("Cell")); cell.Setters.Add(new Setter(Control.PaddingProperty, padding)); table.Columns[index].CellStyle = cell;
        }
        var compact = (Window.GetWindow(this)?.ActualWidth ?? ActualWidth) <= 768;
        if (compactProviders != compact) { compactProviders = compact; RenderProviders(); }
    }

    public override async Task RefreshAsync()
    {
        if (!IsRunning) { Message(InactiveMessage); return; }
        var controller = ControllerIdentity;
        var rulesTask = Get("rules"); var providerTask = Get("providers/rules"); var proxiesTask = Get("proxies"); await Task.WhenAll(rulesTask, providerTask, proxiesTask);
        if (!IsActive || !IsRunning || controller != ControllerIdentity) return;
        var proxyData = (await proxiesTask).Get("proxies"); policyColors.Clear();
        foreach (var name in proxyData.Get("GLOBAL").Get("all").Items().Select(item => item.Text()).Where(name => name != "GLOBAL" && proxyData.Get(name).Get("all").ValueKind == JsonValueKind.Array))
            policyColors.TryAdd(name, "Line" + policyColors.Count % 9 + "Brush");
        rows = (await rulesTask).Get("rules").Items().Select((item, index) => new RuleRow(index + 1, item.Text("type"), item.Text("payload"), item.Text("proxy"), PolicyBrush(item.Text("proxy")))).ToList(); ApplyFilter();
        providerData = (await providerTask).Get("providers"); RenderProviders(); Message("");
    }
    private void RenderProviders()
    {
        providers.Children.Clear();
        foreach (var provider in providerData.Fields())
        {
            var name = provider.Name; var value = provider.Value;
            var update = Action("更新", "UpdateRuleProvider_" + name, () => UpdateProvider(name));
            update.Style = FluentControls.Style("Ghost"); update.Height = 28; update.FontSize = 13; update.Padding = new Thickness(10, 0, 10, 0); update.Margin = new Thickness(0);
            var busy = updating.Contains(name); var buttonContent = new StackPanel { Orientation = Orientation.Horizontal };
            buttonContent.Children.Add(busy ? new FluentProgressRing { Width = 16, Height = 16, IsActive = true, Margin = new Thickness(0, 0, 8, 0) } : new FluentIcon("update", 16) { Margin = new Thickness(0, 0, 8, 0) }); buttonContent.Children.Add(Ui.Label("更新", 13)); update.Content = buttonContent; update.IsEnabled = !busy && IsRunning;
            var row = new RuleProviderLayout { Margin = new Thickness(16, 10, 16, 10) };
            var nameText = Ui.Text(name, 13, true); nameText.MaxWidth = 240; nameText.TextWrapping = TextWrapping.NoWrap; nameText.TextTrimming = TextTrimming.CharacterEllipsis; nameText.ToolTip = name; nameText.Opacity = busy ? .6 : 1;
            var nameRow = new RuleProviderHeader(); foreach (var item in new FrameworkElement[] { nameText, ProviderTag(value.Text("vehicleType")), ProviderTag(value.Text("behavior"), true) }) nameRow.Children.Add(item); row.Children.Add(nameRow);
            Panel meta = compactProviders ? new WrapPanel() : new StackPanel { Orientation = Orientation.Horizontal };
            var ruleCount = Ui.Text("", 12); ruleCount.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); ruleCount.Inlines.Add(new Run(L.T("规则条数") + ": "));
            var numeric = new Run(value.Number("ruleCount").ToString("0", System.Globalization.CultureInfo.InvariantCulture)) { FontWeight = FontWeight.FromOpenTypeWeight(560) }; numeric.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush"); ruleCount.Inlines.Add(numeric); meta.Children.Add(ruleCount);
            if (DateTimeOffset.TryParse(value.Text("updatedAt"), out var updatedAt)) { var time = Ui.Text(L.T("最后更新于") + ": " + ConnectionVisuals.RelativeTime(updatedAt), 12); time.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); meta.Children.Add(time); }
            meta.Children.Add(update); for (int index = 0; index < meta.Children.Count; index++) if (meta.Children[index] is FrameworkElement item) { item.VerticalAlignment = VerticalAlignment.Center; item.Margin = new Thickness(0, 0, index == meta.Children.Count - 1 ? 0 : compactProviders ? 8 : 16, compactProviders ? 8 : 0); } meta.Margin = new Thickness(0, 0, 0, compactProviders ? -8 : 0); row.Children.Add(meta);
            var line = new Border { Child = row, BorderThickness = new Thickness(0, providers.Children.Count == 0 ? 0 : 1, 0, 0) }; line.SetResourceReference(Border.BorderBrushProperty, "DividerBrush"); providers.Children.Add(line);
        }
        providerSection.Visibility = providers.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        providerCount.Text = providers.Children.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private string PolicyBrush(string name) => name switch { "GLOBAL" => "LineSystemBrush", "DIRECT" => "TextTertiaryBrush", "REJECT" or "REJECT-DROP" => "DangerBrush", _ => policyColors.GetValueOrDefault(name, "TextTertiaryBrush") };
    private static FrameworkElement ProviderTag(string text, bool ink = false)
    {
        var background = new Border { CornerRadius = new CornerRadius(4), Opacity = ink ? .16 : 1 }; background.SetResourceReference(Border.BackgroundProperty, ink ? "AccentBrush" : "SubtleHoverBrush");
        var label = Ui.Text(text, 12); label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(6, 0, 6, 0); label.SetResourceReference(TextBlock.ForegroundProperty, ink ? "AccentLinkBrush" : "TextSecondaryBrush");
        var tag = new Grid { Height = 20 }; tag.Children.Add(background); tag.Children.Add(label); return tag;
    }
    private async Task UpdateProvider(string name)
    {
        if (!IsRunning || !updating.Add(name)) return; var controller = ControllerIdentity; RenderProviders();
        try { await Session.ApiAsync(HttpMethod.Put, "providers/rules/" + Uri.EscapeDataString(name), cancellationToken: PageToken); if (IsActive && IsRunning && controller == ControllerIdentity) await RefreshAsync(); }
        finally { updating.Remove(name); RenderProviders(); }
    }
    public override async Task NavigateToAsync(NavigationTarget target)
    {
        if (target.Query is null) return;
        search.Text = target.Query;
        if (rows.Count == 0 && IsRunning) await RunAsync(RefreshAsync);
    }
    protected override void OnLanguageChanged() { ApplyFilter(); RenderProviders(); base.OnLanguageChanged(); }
    protected override void OnStatusChanged() { if (!IsRunning) { rows.Clear(); providerData = default; table.ItemsSource = null; providers.Children.Clear(); count.Text = ""; empty.Visibility = Visibility.Collapsed; providerSection.Visibility = Visibility.Collapsed; Message(InactiveMessage); } }
    private void ApplyFilter()
    {
        var term = search.Text.Trim(); var selected = rows.Where(row => (row.Type + " " + row.Payload + " " + row.Proxy).Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        table.ItemsSource = selected; count.Inlines.Clear(); var number = new Run(term.Length > 0 ? $"{selected.Length} / {rows.Count}" : rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)); number.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush"); count.Inlines.Add(number); count.Inlines.Add(new Run(" " + L.T("条规则")));
        empty.Visibility = term.Length > 0 && selected.Length == 0 && IsRunning ? Visibility.Visible : Visibility.Collapsed;
    }
    public sealed record RuleRow(int Index, string Type, string Payload, string Proxy, string BrushKey) { public string DisplayPayload => Payload.Length > 0 ? Payload : "*"; }
}
