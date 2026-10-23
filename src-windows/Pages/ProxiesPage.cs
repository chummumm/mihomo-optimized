using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class ProxiesPage : NativePage
{
    private readonly StackPanel groups = new();
    private readonly TreeView topology = new() { BorderThickness = new Thickness(0), Padding = new Thickness(8) };
    private readonly TextBox search = Ui.SearchBox("筛选策略组或节点", "ProxySearch");
    private JsonElement proxies;
    private JsonElement providers;
    private readonly HashSet<string> expanded = [];
    private readonly Dictionary<string, double> measured = new(StringComparer.Ordinal);
    private bool firstLoad = true;
    private bool measuring;

    public ProxiesPage(AppSession session) : base(session, "proxies", "代理", "选择策略组出口，查看节点状态和延迟")
    {
        Tools.Children.Add(Action("全部测速", "MeasureAllProxies", MeasureAll));
        Tools.Children.Add(Action("刷新", "RefreshProxies", RefreshAsync));
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition());
        search.Margin = new Thickness(0, 0, 12, 16); search.TextChanged += (_, _) => Render(); layout.Children.Add(search);
        var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "策略组", Content = Ui.Scroll(groups) }); tabs.Items.Add(new TabItem { Header = "出口关系", Content = topology });
        topology.Id("ProxyTopology"); Grid.SetRow(tabs, 1); layout.Children.Add(tabs); SetBody(layout);
    }
    public override async Task RefreshAsync()
    {
        if (!IsRunning) { Message(InactiveMessage); groups.Children.Clear(); return; }
        var proxyTask = Get("proxies"); var providerTask = Get("providers/proxies");
        await Task.WhenAll(proxyTask, providerTask); proxies = (await proxyTask).Get("proxies"); providers = (await providerTask).Get("providers");
        Message(""); Render(); firstLoad = false;
    }
    private void Render()
    {
        groups.Children.Clear(); RenderTopology(); var term = search.Text.Trim();
        var collection = proxies.Fields().Where(pair => pair.Value.Get("all").ValueKind == JsonValueKind.Array).ToArray();
        if (collection.Length == 0) groups.Children.Add(Ui.Muted(IsRunning ? "当前配置没有代理策略组。可在配置页导入订阅。" : InactiveMessage, 14));
        foreach (var group in collection)
        {
            var all = group.Value.Get("all").Items().Select(item => item.Text()).ToArray();
            var nodes = term.Length == 0 || group.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) ? all : all.Where(name => name.Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            if (nodes.Length == 0) continue;
            var name = group.Name; var selected = group.Value.Text("now"); var canSelect = group.Value.Text("type") == "Selector";
            var content = new StackPanel();
            var actions = Ui.Row(Ui.Muted(group.Value.Text("type") + " · " + all.Length + " 个节点"), Action("测速", "MeasureGroup_" + name, () => Measure(nodes)));
            content.Children.Add(actions);
            var grid = new WrapPanel();
            foreach (var nodeName in nodes)
            {
                var node = proxies.Get(nodeName); var delay = measured.TryGetValue(nodeName, out var value) ? value : node.Get("history").Items().LastOrDefault().Number("delay");
                var label = Ui.Column(Ui.Text(nodeName, 14, true), Ui.Muted(node.Text("type") + " · " + (measured.ContainsKey(nodeName) && delay <= 0 ? "连接超时" : Ui.Delay(delay))));
                var button = Action(nodeName, "Proxy_" + name + "_" + nodeName, async () =>
                {
                    await Session.ApiAsync(HttpMethod.Put, "proxies/" + Uri.EscapeDataString(name), new { name = nodeName }); await RefreshAsync();
                });
                button.Content = label; button.Width = 225; button.MinHeight = 68; button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.IsEnabled = canSelect; button.ToolTip = canSelect ? "选用 " + nodeName : "该策略组由内核自动选择";
                if (nodeName == selected) { button.BorderThickness = new Thickness(2); button.SetResourceReference(Control.BorderBrushProperty, "AccentBrush"); }
                grid.Children.Add(button);
            }
            content.Children.Add(grid);
            var expander = new Expander { Header = Ui.Text(name + "  ·  " + selected, 16, true), Content = content, IsExpanded = firstLoad || expanded.Contains(name), Padding = new Thickness(4) }.Id("ProxyGroup_" + name);
            expander.Expanded += (_, _) => expanded.Add(name); expander.Collapsed += (_, _) => expanded.Remove(name);
            groups.Children.Add(Ui.Card(expander, 16));
        }
        if (providers.Fields().Any())
        {
            var list = Ui.Column(Ui.Text("代理集合", 18, true));
            foreach (var provider in providers.Fields())
            {
                var name = provider.Name; var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
                var action = Action("更新", "UpdateProvider_" + name, async () => { await Session.ApiAsync(HttpMethod.Put, "providers/proxies/" + Uri.EscapeDataString(name)); await RefreshAsync(); });
                action.IsEnabled = provider.Value.Text("vehicleType") == "HTTP";
                var health = Action("健康检查", "CheckProvider_" + name, async () => { await Get("providers/proxies/" + Uri.EscapeDataString(name) + "/healthcheck"); await RefreshAsync(); });
                var actions = Ui.Row(health, action); DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions);
                row.Children.Add(Ui.Column(Ui.Text(name, 14, true), Ui.Muted($"{provider.Value.Get("proxies").Items().Count()} 个节点 · {provider.Value.Text("vehicleType")} · {provider.Value.Text("updatedAt")}"))); var nodeList = new StackPanel();
                foreach (var node in provider.Value.Get("proxies").Items()) nodeList.Children.Add(Ui.Text($"{node.Text("name")}   ·   {node.Text("type")}   ·   {Ui.Delay(node.Get("history").Items().LastOrDefault().Number("delay"))}"));
                list.Children.Add(new Expander { Header = row, Content = nodeList, Margin = new Thickness(0, 8, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
            groups.Children.Add(Ui.Card(list));
        }
    }
    private void RenderTopology()
    {
        topology.Items.Clear(); var remaining = 2000;
        foreach (var group in proxies.Fields().Where(item => item.Value.Get("all").ValueKind == JsonValueKind.Array))
        {
            if (search.Text.Trim().Length > 0 && !group.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) && !group.Value.Get("all").Items().Any(item => item.Text().Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase))) continue;
            if (remaining <= 0) { topology.Items.Add(Ui.Muted("关系较多，请输入策略组名称缩小范围。")); break; }
            topology.Items.Add(Build(group.Name, new HashSet<string>(StringComparer.Ordinal), false));
        }
        TreeViewItem Build(string name, HashSet<string> path, bool selected)
        {
            remaining--; var data = proxies.Get(name); var text = Ui.Text(name + "  ·  " + data.Text("type") + (selected ? "  ·  当前选中" : ""), 14, selected);
            if (selected) text.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            var node = new TreeViewItem { Header = text, IsExpanded = path.Count == 0 || selected, Padding = new Thickness(4) };
            if (!path.Add(name)) { node.Items.Add(Ui.Muted("循环引用，已停止展开")); return node; }
            if (path.Count >= 32) { node.Items.Add(Ui.Muted("层级较深，请从对应策略组继续查看")); return node; }
            var current = data.Text("now");
            foreach (var child in data.Get("all").Items().Select(item => item.Text()))
            {
                if (remaining <= 0) { node.Items.Add(Ui.Muted("更多节点可在策略组中查看")); break; }
                node.Items.Add(Build(child, new HashSet<string>(path, StringComparer.Ordinal), child == current));
            }
            return node;
        }
    }
    private Task MeasureAll() => Measure(proxies.Fields().Where(pair => pair.Value.Get("all").ValueKind != JsonValueKind.Array && pair.Value.Text("type") is not ("Direct" or "Reject" or "Pass")).Select(pair => pair.Name));
    private async Task Measure(IEnumerable<string> names)
    {
        if (measuring) return; measuring = true;
        try
        {
            var targets = names.Distinct(StringComparer.Ordinal).ToArray(); var completed = 0;
            using var limit = new SemaphoreSlim(4);
            await Task.WhenAll(targets.Select(async name =>
            {
                await limit.WaitAsync(PageToken);
                try
                {
                    try { var result = await Get("proxies/" + Uri.EscapeDataString(name) + "/delay?timeout=5000&url=" + Uri.EscapeDataString("https://www.gstatic.com/generate_204")); measured[name] = result.Number("delay"); }
                    catch (OperationCanceledException) { throw; }
                    catch { measured[name] = 0; }
                    completed++; Message($"正在测速 {completed}/{targets.Length}");
                }
                finally { limit.Release(); }
            }));
            Message($"已测量 {targets.Length} 个节点。"); Render();
        }
        finally { measuring = false; }
    }
    protected override void OnStatusChanged() { if (!IsRunning) { proxies = providers = default; groups.Children.Clear(); topology.Items.Clear(); Message(InactiveMessage); } }
}
