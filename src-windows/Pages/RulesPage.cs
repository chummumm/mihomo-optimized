using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class RulesPage : NativePage
{
    private readonly DataGrid table = Ui.Table(("顺序", nameof(RuleRow.Index), 70), ("类型", nameof(RuleRow.Type), 170), ("匹配内容", nameof(RuleRow.Payload), -2), ("策略", nameof(RuleRow.Proxy), -1));
    private readonly TextBox search = Ui.SearchBox("筛选规则类型、内容或策略", "RuleSearch");
    private readonly StackPanel providers = new();
    private readonly TextBlock count = Ui.Muted("");
    private List<RuleRow> rows = [];

    public RulesPage(AppSession session) : base(session, "rules", "规则", "按配置顺序匹配连接，更新规则集合")
    {
        Tools.Children.Add(Action("刷新", "RefreshRules", RefreshAsync));
        var tabs = new TabControl(); var rulesPage = new Grid(); rulesPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); rulesPage.RowDefinitions.Add(new RowDefinition());
        search.TextChanged += (_, _) => ApplyFilter();
        rulesPage.Children.Add(Ui.Row(search, count)); Grid.SetRow(table, 1); rulesPage.Children.Add(table); table.Id("RuleTable");
        tabs.Items.Add(new TabItem { Header = "匹配规则", Content = rulesPage }); tabs.Items.Add(new TabItem { Header = "规则集合", Content = Ui.Scroll(providers) }); SetBody(tabs);
    }
    public override async Task RefreshAsync()
    {
        if (!IsRunning) { Message(InactiveMessage); return; }
        var rulesTask = Get("rules"); var providerTask = Get("providers/rules"); await Task.WhenAll(rulesTask, providerTask);
        rows = (await rulesTask).Get("rules").Items().Select((item, index) => new RuleRow(index + 1, item.Text("type"), item.Text("payload"), item.Text("proxy"))).ToList(); ApplyFilter();
        providers.Children.Clear();
        foreach (var provider in (await providerTask).Get("providers").Fields())
        {
            var name = provider.Name; var value = provider.Value;
            var update = Action("更新", "UpdateRuleProvider_" + name, async () => { await Session.ApiAsync(HttpMethod.Put, "providers/rules/" + Uri.EscapeDataString(name)); await RefreshAsync(); });
            update.IsEnabled = value.Text("vehicleType") == "HTTP";
            var row = new DockPanel(); DockPanel.SetDock(update, Dock.Right); row.Children.Add(update);
            row.Children.Add(Ui.Column(Ui.Text(name, 16, true), Ui.Muted($"{value.Number("ruleCount"):N0} 条 · {value.Text("behavior")} · {value.Text("vehicleType")}"), Ui.Muted(value.Text("updatedAt")))); providers.Children.Add(Ui.Card(row));
        }
        if (providers.Children.Count == 0) providers.Children.Add(Ui.Muted("当前配置没有规则集合。")); Message("");
    }
    protected override void OnStatusChanged() { if (!IsRunning) { rows.Clear(); table.ItemsSource = null; providers.Children.Clear(); count.Text = ""; Message(InactiveMessage); } }
    private void ApplyFilter()
    {
        var term = search.Text.Trim(); var selected = rows.Where(row => (row.Type + " " + row.Payload + " " + row.Proxy).Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        table.ItemsSource = selected; count.Text = $"{selected.Length:N0} / {rows.Count:N0} 条规则";
    }
    public sealed record RuleRow(int Index, string Type, string Payload, string Proxy);
}
