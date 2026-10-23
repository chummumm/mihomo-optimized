using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class RulesPage : NativePage
{
    private readonly DataGrid table = Ui.Table(("顺序", nameof(RuleRow.Index), 70), ("类型", nameof(RuleRow.Type), 170), ("匹配内容", nameof(RuleRow.DisplayPayload), -2), ("策略", nameof(RuleRow.Proxy), -1));
    private readonly TextBox search = Ui.SearchBox("筛选规则类型、内容或策略", "RuleSearch");
    private readonly StackPanel providers = new();
    private readonly TextBlock count = Ui.Muted("");
    private readonly TextBlock empty = Ui.Hint("没有匹配的规则");
    private readonly Expander providerSection = new() { IsExpanded = true, Margin = new Thickness(0, 0, 0, 12), MaxHeight = 250 };
    private List<RuleRow> rows = [];
    private JsonElement providerData;

    public RulesPage(AppSession session) : base(session, "rules", "规则", "按配置顺序匹配连接，更新规则集合")
    {
        Tools.Children.Add(Action("刷新", "RefreshRules", RefreshAsync));
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition());
        search.TextChanged += (_, _) => ApplyFilter(); search.KeyDown += (_, e) => { if (e.Key == Key.Escape) { search.Clear(); e.Handled = true; } };
        layout.Children.Add(Ui.Row(search, count));
        providerSection.Header = Ui.Label("规则集合", 16, true); providerSection.Content = Ui.Scroll(providers); providerSection.Id("RuleProviders"); Grid.SetRow(providerSection, 1); layout.Children.Add(providerSection);
        var surface = new Grid(); surface.Children.Add(table); table.Id("RuleTable"); table.RowHeight = 40;
        empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; empty.Visibility = Visibility.Collapsed; surface.Children.Add(empty);
        Grid.SetRow(surface, 2); layout.Children.Add(surface); SetBody(layout);
    }
    public override async Task RefreshAsync()
    {
        if (!IsRunning) { Message(InactiveMessage); return; }
        var controller = ControllerIdentity;
        var rulesTask = Get("rules"); var providerTask = Get("providers/rules"); await Task.WhenAll(rulesTask, providerTask);
        if (!IsActive || !IsRunning || controller != ControllerIdentity) return;
        rows = (await rulesTask).Get("rules").Items().Select((item, index) => new RuleRow(index + 1, item.Text("type"), item.Text("payload"), item.Text("proxy"))).ToList(); ApplyFilter();
        providerData = (await providerTask).Get("providers"); RenderProviders(); Message("");
    }
    private void RenderProviders()
    {
        providers.Children.Clear();
        foreach (var provider in providerData.Fields())
        {
            var name = provider.Name; var value = provider.Value;
            var update = Action("更新", "UpdateRuleProvider_" + name, async () => { await Session.ApiAsync(HttpMethod.Put, "providers/rules/" + Uri.EscapeDataString(name)); await RefreshAsync(); });
            update.IsEnabled = value.Text("vehicleType") == "HTTP";
            var row = new DockPanel(); DockPanel.SetDock(update, Dock.Right); row.Children.Add(update);
            row.Children.Add(Ui.Column(Ui.Text(name, 16, true), Ui.Muted(L.F("{0:N0} 条 · {1} · {2}", value.Number("ruleCount"), value.Text("behavior"), value.Text("vehicleType"))), Ui.Muted(value.Text("updatedAt")))); providers.Children.Add(Ui.Card(row, 14));
        }
        providerSection.Visibility = providers.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    public override async Task NavigateToAsync(NavigationTarget target)
    {
        if (target.Query is null) return;
        search.Text = target.Query;
        if (rows.Count == 0 && IsRunning) await RunAsync(RefreshAsync);
        search.Focus(); search.SelectAll();
        if (table.Items.Count > 0) { table.SelectedIndex = 0; table.ScrollIntoView(table.SelectedItem); }
    }
    protected override void OnLanguageChanged() { ApplyFilter(); RenderProviders(); base.OnLanguageChanged(); }
    protected override void OnStatusChanged() { if (!IsRunning) { rows.Clear(); providerData = default; table.ItemsSource = null; providers.Children.Clear(); count.Text = ""; empty.Visibility = Visibility.Collapsed; providerSection.Visibility = Visibility.Collapsed; Message(InactiveMessage); } }
    private void ApplyFilter()
    {
        var term = search.Text.Trim(); var selected = rows.Where(row => (row.Type + " " + row.Payload + " " + row.Proxy).Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        table.ItemsSource = selected; count.Text = L.F("{0:N0} / {1:N0} 条规则", selected.Length, rows.Count); empty.Visibility = selected.Length == 0 && IsRunning ? Visibility.Visible : Visibility.Collapsed;
    }
    public sealed record RuleRow(int Index, string Type, string Payload, string Proxy) { public string DisplayPayload => Payload.Length > 0 ? Payload : "*"; }
}
