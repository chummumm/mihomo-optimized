using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class ProxiesPage : NativePage
{
    private readonly StackPanel content = new();
    private readonly StackPanel groupList = new();
    private readonly StackPanel providerList = new();
    private readonly StackPanel nodeSection = new();
    private readonly ProxyTopologyControl topology = new();
    private readonly ProxyTopologyModelBuilder topologyModel = new();
    private readonly ProxyPreferences preferences = new();
    private readonly TextBox search = Ui.SearchBox("筛选策略组、节点或类型", "ProxySearch");
    private readonly TextBlock summary = Ui.Muted("");
    private readonly TextBlock flow = Ui.Muted("");
    private readonly TextBlock idleMap = Ui.Hint("暂无活动连接，显示当前策略组出口");
    private readonly CheckBox breakConnections = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar progress = new() { Height = 3, Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed, Maximum = 100 };
    private readonly Button testAll;
    private readonly Button toggleTopology;
    private readonly Grid topologyBody = new();
    private readonly ScrollViewer scroll;
    private readonly Dictionary<string, ProxyNodeInfo> nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProxyNodeInfo> measured = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Section, string Name), ProxyNodeTile> tiles = [];
    private readonly Dictionary<string, Border> groupCards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Expander> expanders = new(StringComparer.Ordinal);
    private readonly HashSet<string> testing = new(StringComparer.Ordinal);
    private readonly HashSet<string> selecting = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim probeSlots = new(4);
    private readonly DispatcherTimer filterTimer = new() { Interval = TimeSpan.FromMilliseconds(130) };
    private readonly DispatcherTimer renderTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private JsonElement providerData;
    private string[] order = [];
    private string mode = "rule";
    private string lastController = "";
    private Task? refreshing;
    private CancellationToken refreshToken;
    private string refreshController = "";
    private long refreshGeneration;
    private string lastPhase = "";
    private CancellationTokenSource? topologyLifetime;
    private NavigationTarget? pendingNavigation;
    private int sort;
    private int testTotal;
    private int testDone;
    private int activeConnections;
    private double up;
    private double down;
    private bool applyingPreferences;
    private bool shownProviders;

    public ProxiesPage(AppSession session) : base(session, "proxies", "代理", "策略组、代理节点与实时流量路径")
    {
        search.Width = 200; search.Margin = new Thickness(0, 0, 8, 8); Tools.Children.Add(search);
        testAll = Action("全部测速", "MeasureAllProxies", MeasureAll, true); L.Bind(testAll, ToolTipProperty, "全部测速 (Ctrl+T)"); Tools.Children.Add(testAll);
        Tools.Children.Add(Action("刷新", "RefreshProxies", RefreshAsync));
        summary.Margin = new Thickness(0, 0, 0, 16); summary.Id("ProxySummary"); content.Children.Add(summary); progress.Id("ProxyTestProgress"); content.Children.Add(progress);
        var topologyHeader = new DockPanel();
        toggleTopology = Action("隐藏", "ToggleProxyTopology", () => { preferences.TopologyOpen = !preferences.TopologyOpen; preferences.Save(); ApplyTopologyVisibility(); return Task.CompletedTask; });
        toggleTopology.Margin = new Thickness(0); DockPanel.SetDock(toggleTopology, Dock.Right); topologyHeader.Children.Add(toggleTopology);
        topologyHeader.Children.Add(Ui.Row(Ui.Label("流量拓扑", 16, true), Ui.Hint("来源 → 策略组 → 出口")));
        var legend = Ui.Row(flow, Ui.Hint("代理"), Ui.Hint("直连"), Ui.Hint("点击策略组或出口可定位")); legend.Margin = new Thickness(0, 12, 0, 6);
        topologyBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); topologyBody.RowDefinitions.Add(new RowDefinition()); topologyBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        topologyBody.Children.Add(legend); Grid.SetRow(topology, 1); topologyBody.Children.Add(topology); Grid.SetRow(idleMap, 2); idleMap.HorizontalAlignment = HorizontalAlignment.Center; idleMap.Margin = new Thickness(0, 4, 0, 0); topologyBody.Children.Add(idleMap);
        content.Children.Add(Ui.Card(Ui.Column(topologyHeader, topologyBody), 16));
        content.Children.Add(groupList); content.Children.Add(providerList); content.Children.Add(nodeSection);
        scroll = Ui.Scroll(content).Id("ProxiesScrollViewer"); scroll.ScrollChanged += (_, _) => UpdateMapAnimation(); SetBody(scroll);
        L.Bind(breakConnections, ContentControl.ContentProperty, "切换节点时断开关联连接"); breakConnections.Id("BreakConnectionsOnSwitch");
        breakConnections.Checked += (_, _) => { if (!applyingPreferences) Session.SetCloseConnectionsOnSwitch(true); };
        breakConnections.Unchecked += (_, _) => { if (!applyingPreferences) Session.SetCloseConnectionsOnSwitch(false); };
        search.TextChanged += (_, _) => { filterTimer.Stop(); filterTimer.Start(); };
        search.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { search.Clear(); e.Handled = true; } };
        filterTimer.Tick += (_, _) => { filterTimer.Stop(); Render(); };
        renderTimer.Tick += (_, _) => { renderTimer.Stop(); Render(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; _ = RunAsync(MeasureAll); } };
        topology.Picked += (action, name) => _ = RunAsync(() => NavigateToAsync(new NavigationTarget("proxies", name, Action: action)));
        Session.ThemeChanged += topology.RefreshPalette;
        Session.HostEvent += (name, data) => { if (name == "desktop-config-changed" && IsActive) _ = RunAsync(RefreshAsync); };
        ApplyTopologyVisibility();
    }

    public override Task RefreshAsync()
    {
        if (refreshing is { IsCompleted: false } && refreshToken == PageToken && refreshController == ControllerIdentity) return refreshing;
        refreshToken = PageToken; refreshController = ControllerIdentity;
        return refreshing = RefreshCore(++refreshGeneration, refreshToken, refreshController);
    }
    private async Task RefreshCore(long generation, CancellationToken token, string controller)
    {
        if (!IsRunning) { ClearInactive(); return; }
        if (lastController != controller) { lastController = controller; measured.Clear(); tiles.Clear(); topologyModel.Clear(); up = down = 0; }
        var proxyTask = Get("proxies", token); var providerTask = Get("providers/proxies", token); var configTask = Get("configs", token);
        await Task.WhenAll(proxyTask, providerTask, configTask);
        if (!IsRunning || controller != ControllerIdentity || generation != refreshGeneration || token.IsCancellationRequested) return;
        var all = (await proxyTask).Get("proxies"); providerData = (await providerTask).Get("providers"); mode = (await configTask).Text("mode").ToLowerInvariant();
        nodes.Clear();
        foreach (var item in all.Fields()) nodes[item.Name] = ProxyNodeInfo.From(item.Name, item.Value);
        foreach (var provider in VisibleProviderData()) foreach (var item in provider.Value.Get("proxies").Items())
        {
            var name = item.Text("name"); if (name.Length > 0 && !nodes.ContainsKey(name)) nodes[name] = ProxyNodeInfo.From(name, item);
        }
        foreach (var item in measured) if (nodes.ContainsKey(item.Key)) nodes[item.Key] = nodes[item.Key] with { History = item.Value.History, Alive = item.Value.Alive };
        order = all.Get("GLOBAL").Get("all").Items().Select(item => item.Text()).ToArray();
        if (order.Length == 0) order = nodes.Keys.ToArray();
        foreach (var key in tiles.Keys.Where(key => !nodes.ContainsKey(key.Name)).ToArray()) tiles.Remove(key);
        Message(""); Render(); await ApplyPendingNavigation(); StartTopology();
    }
    private IEnumerable<JsonProperty> VisibleProviderData() => providerData.Fields().Where(item => item.Name != "default" && item.Value.Text("vehicleType") != "Compatible");
    private IEnumerable<ProxyNodeInfo> Groups(bool withGlobal = true)
    {
        if (withGlobal && mode == "global" && nodes.TryGetValue("GLOBAL", out var global)) yield return global;
        foreach (var name in order.Distinct(StringComparer.Ordinal)) if (name != "GLOBAL" && nodes.TryGetValue(name, out var group) && group.IsGroup) yield return group;
    }
    private IEnumerable<ProxyNodeInfo> StandaloneNodes() => order.Distinct(StringComparer.Ordinal).Where(name => name is not ("GLOBAL" or "DIRECT" or "REJECT" or "REJECT-DROP" or "PASS") && nodes.TryGetValue(name, out var value) && !value.IsGroup).Select(name => nodes[name]);

    private void Render()
    {
        applyingPreferences = true; breakConnections.IsChecked = Session.CloseConnectionsOnSwitch; applyingPreferences = false;
        shownProviders = Session.ShowProxyProvider;
        RenderGroups(); RenderProviders(); RenderNodes(); RefreshMap(); UpdateSummary(); ApplyTestControls();
    }
    private void UpdateSummary()
    {
        var all = StandaloneNodes().Concat(VisibleProviderData().SelectMany(provider => provider.Value.Get("proxies").Items()).Select(item => nodes.GetValueOrDefault(item.Text("name"))).OfType<ProxyNodeInfo>()).ToArray();
        summary.Text = L.F("{0} 个策略组    {1} 个节点    {2} 个可用", Groups(false).Count(), all.Length, all.Count(item => !item.Failed && item.Delay > 0));
    }
    private void RenderGroups()
    {
        groupList.Children.Clear(); groupCards.Clear(); expanders.Clear(); var groups = Groups().ToArray();
        if (groups.Length == 0) { if (IsRunning && nodes.Count == 0) groupList.Children.Add(Ui.Hint("当前配置没有代理节点，可在配置页导入订阅。")); return; }
        var sectionHeader = new DockPanel { Margin = new Thickness(0, 8, 0, 12) };
        Detach(breakConnections); DockPanel.SetDock(breakConnections, Dock.Right); sectionHeader.Children.Add(breakConnections); sectionHeader.Children.Add(Ui.Text(L.F("策略组  {0}", groups.Length), 18, true)); groupList.Children.Add(sectionHeader);
        var open = preferences.OpenGroups ?? groups.Take(1).Select(item => item.Name).ToHashSet(StringComparer.Ordinal); var query = search.Text.Trim();
        foreach (var group in groups)
        {
            var exitName = ProxyNodeInfo.ResolveExit(group.Now, nodes); var exit = exitName is null ? null : nodes.GetValueOrDefault(exitName);
            var via = exitName == "DIRECT" ? L.T("直接连接") : exitName is "REJECT" or "REJECT-DROP" ? L.T("拒绝连接") : exitName is not null && exitName != group.Now ? "→ " + exitName : exit is null ? "" : exit.Type + (exit.Udp ? " · UDP" : "");
            var identity = Ui.Column(Ui.Text(group.Name, 14, true), Ui.Muted(group.Now + (via.Length > 0 ? "  " + via : "")));
            foreach (TextBlock text in identity.Children) { text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis; text.ToolTip = text.Text; }
            ConnectedNavigation.SetTarget(identity, group.Name);
            var header = new Grid { MinHeight = 54, Margin = new Thickness(8, 0, 0, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) }); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new FluentIcon { Kind = group.Type switch { "URLTest" => "top-speed", "Fallback" => "arrow-swap", "LoadBalance" => "data-area", _ => "nav-proxies" }, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            var colorIndex = Array.FindIndex(groups.Where(item => item.Name != "GLOBAL").ToArray(), item => item.Name == group.Name);
            icon.SetResourceReference(Control.ForegroundProperty, group.Name == "GLOBAL" || colorIndex < 0 ? "TextSecondaryBrush" : "Line" + colorIndex % 9 + "Brush"); header.Children.Add(icon); Grid.SetColumn(identity, 1); header.Children.Add(identity);
            var typeLabel = group.Type == "Selector" ? L.T("手动选择") : group.Type == "URLTest" ? L.T("自动选择") : group.Type;
            var info = Ui.Column(Ui.Muted(typeLabel + " · " + L.F("{0} 个出口", group.Members.Count)), Ui.Muted(exit?.DelayLabel ?? "—"));
            var test = Action("测速", "MeasureGroup_" + group.Name, () => Measure(group.Members)); test.Margin = new Thickness(8, 0, 0, 0); test.IsEnabled = IsRunning && group.Members.Any(name => nodes.GetValueOrDefault(name) is { CanTest: true, IsGroup: false });
            var right = Ui.Row(info, test); right.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(right, 2); header.Children.Add(right);
            var members = new ProxyTilePanel();
            foreach (var memberName in group.Members)
            {
                var member = nodes.GetValueOrDefault(memberName); var matches = query.Length == 0 || group.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || memberName == group.Now || (memberName + " " + member?.Type).Contains(query, StringComparison.CurrentCultureIgnoreCase);
                if (!matches) continue;
                var copy = Ui.Column(Ui.Text(memberName, 13, true), Ui.Muted((member?.Type ?? "") + "  ·  " + (member?.DelayLabel ?? "—"), 11));
                foreach (TextBlock text in copy.Children) { text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis; }
                if (member?.Failed == true) ((TextBlock)copy.Children[1]).SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                var button = Action(memberName, "Proxy_" + group.Name + "_" + memberName, () => Select(group.Name, memberName));
                AutomationProperties.SetName(button, memberName);
                button.Content = copy; button.ToolTip = memberName + "\n" + (group.Type == "Selector" ? L.T("选用此节点") : L.T("此策略组由内核自动选择"));
                button.MinHeight = 62; button.Margin = new Thickness(3); button.Padding = new Thickness(12, 8, 12, 8); button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.IsEnabled = group.Type == "Selector" && !selecting.Contains(group.Name) && IsRunning;
                if (memberName == group.Now) { button.BorderThickness = new Thickness(2); button.SetResourceReference(Control.BorderBrushProperty, "AccentBrush"); }
                members.Children.Add(button);
            }
            var inner = new StackPanel();
            var note = group.Type switch { "URLTest" => "自动选择延迟较低的出口。", "Fallback" => "当前出口不可用时，自动切换到后续可用节点。", "LoadBalance" => "连接由内核在多个节点之间分配。", _ => "" };
            if (note.Length > 0) { var text = Ui.Hint(note); text.Margin = new Thickness(0, 0, 0, 8); inner.Children.Add(text); }
            if (members.Children.Count == 0) inner.Children.Add(Ui.Hint("没有匹配的节点。")); else inner.Children.Add(members);
            var well = new Border { Child = inner, Padding = new Thickness(12), BorderThickness = new Thickness(0, 1, 0, 0) }; well.SetResourceReference(Border.BackgroundProperty, "SubtleSurfaceBrush"); well.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            var expander = new Expander { Header = header, Content = well, IsExpanded = open.Contains(group.Name), HorizontalContentAlignment = HorizontalAlignment.Stretch }.Id("ProxyGroup_" + group.Name);
            var name = group.Name; expander.Expanded += (_, _) => SaveOpen(name, true, groups); expander.Collapsed += (_, _) => SaveOpen(name, false, groups);
            var card = Ui.Card(expander, 0); card.Margin = new Thickness(0, 0, 0, 4); groupList.Children.Add(card); groupCards[name] = card; expanders[name] = expander;
        }
    }
    private void SaveOpen(string name, bool open, ProxyNodeInfo[] groups)
    {
        preferences.OpenGroups ??= groups.Take(1).Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        if (open) preferences.OpenGroups.Add(name); else preferences.OpenGroups.Remove(name); preferences.Save();
    }

    private void RenderProviders()
    {
        providerList.Children.Clear(); providerList.Visibility = Session.ShowProxyProvider ? Visibility.Visible : Visibility.Collapsed;
        if (!Session.ShowProxyProvider) return;
        var providers = VisibleProviderData().ToArray(); if (providers.Length == 0) return;
        var heading = Ui.Text(L.F("代理集合  {0}", providers.Length), 18, true); heading.Margin = new Thickness(0, 28, 0, 12); providerList.Children.Add(heading);
        foreach (var provider in providers)
        {
            var name = provider.Name; var items = provider.Value.Get("proxies").Items().Select(item => nodes.GetValueOrDefault(item.Text("name"))).OfType<ProxyNodeInfo>().OrderBy(DelayOrder).ToArray();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var actions = Ui.Row(Action("健康检查", "CheckProvider_" + name, async () => { await Get("providers/proxies/" + Uri.EscapeDataString(name) + "/healthcheck"); measured.Clear(); await RefreshAsync(); }), Action("更新", "UpdateProvider_" + name, async () => { await Session.ApiAsync(HttpMethod.Put, "providers/proxies/" + Uri.EscapeDataString(name), cancellationToken: PageToken); measured.Clear(); await RefreshAsync(); }));
            DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
            var updated = provider.Value.Text("updatedAt");
            header.Children.Add(Ui.Column(Ui.Text(name, 14, true), Ui.Muted(provider.Value.Text("vehicleType") + " · " + L.F("{0} 个节点", items.Length) + (updated.Length > 0 ? " · " + L.F("更新于 {0}", DateTimeOffset.TryParse(updated, out var time) ? time.ToLocalTime().ToString("g") : updated) : ""))));
            var panel = new ProxyTilePanel(); foreach (var item in items) panel.Children.Add(Tile("provider:" + name, item));
            var card = Ui.Card(Ui.Column(header, panel), 16); card.Margin = new Thickness(0, 0, 0, 4); providerList.Children.Add(card);
        }
    }
    private void RenderNodes()
    {
        nodeSection.Children.Clear(); var all = StandaloneNodes().ToArray(); if (all.Length == 0) return;
        var query = search.Text.Trim(); IEnumerable<ProxyNodeInfo> shown = all.Where(item => query.Length == 0 || (item.Name + " " + item.Type).Contains(query, StringComparison.CurrentCultureIgnoreCase));
        shown = sort switch { 0 => shown.OrderBy(DelayOrder), 1 => shown.OrderByDescending(DelayOrder), _ => shown };
        var list = shown.ToArray(); var header = new DockPanel { Margin = new Thickness(0, 28, 0, 12) };
        var sortButton = Action(sort == 0 ? "延迟从低到高" : sort == 1 ? "延迟从高到低" : "原始顺序", "SortProxyNodes", () => { sort = (sort + 1) % 3; RenderNodes(); return Task.CompletedTask; });
        DockPanel.SetDock(sortButton, Dock.Right); header.Children.Add(sortButton); header.Children.Add(Ui.Row(Ui.Text(L.F("代理节点  {0}", list.Length), 18, true), Ui.Hint("点击节点可单独测速"))); nodeSection.Children.Add(header);
        if (list.Length == 0) { nodeSection.Children.Add(Ui.Hint("没有匹配的节点。")); return; }
        var panel = new ProxyTilePanel(); foreach (var node in list) panel.Children.Add(Tile("nodes", node)); nodeSection.Children.Add(panel);
    }
    private static double DelayOrder(ProxyNodeInfo node) => node.Failed || node.Delay <= 0 ? double.MaxValue : node.Delay;
    private ProxyNodeTile Tile(string section, ProxyNodeInfo node)
    {
        if (!tiles.TryGetValue((section, node.Name), out var tile))
        {
            tile = new ProxyNodeTile().Id(section == "nodes" ? "ProxyNode_" + node.Name : section.StartsWith("provider:", StringComparison.Ordinal) ? "ProviderNode_" + section[9..] + "_" + node.Name : "FocusedProxyNode_" + node.Name);
            var name = node.Name; tile.Click += async (_, _) => await RunAsync(() => Measure([name])); tiles[(section, node.Name)] = tile;
        }
        Detach(tile); tile.Update(node, testing.Contains(node.Name)); return tile;
    }
    private static void Detach(UIElement item) { if (VisualTreeHelper.GetParent(item) is Panel panel) panel.Children.Remove(item); }

    private async Task Select(string group, string name)
    {
        if (!IsRunning || !nodes.TryGetValue(group, out var current) || current.Type != "Selector" || current.Now == name || !selecting.Add(group)) return;
        RenderGroups();
        var token = PageToken; var controller = ControllerIdentity;
        try
        {
            await Session.ApiAsync(HttpMethod.Put, "proxies/" + Uri.EscapeDataString(group), new { name }, token);
            if (controller != ControllerIdentity || token.IsCancellationRequested) return;
            nodes[group] = current with { Now = name }; RenderGroups(); RefreshMap();
            if (Session.CloseConnectionsOnSwitch)
            {
                var connections = await Get("connections", token);
                var ids = connections.Get("connections").Items().Where(item => item.Get("chains").Items().Any(chain => chain.Text() == group)).Select(item => item.Text("id")).ToArray();
                foreach (var id in ids) await Session.ApiAsync(HttpMethod.Delete, "connections/" + Uri.EscapeDataString(id), cancellationToken: token);
            }
            await RefreshAsync();
        }
        finally { selecting.Remove(group); RenderGroups(); }
    }
    private Task MeasureAll() => testing.Count > 0 ? Task.CompletedTask : Measure(nodes.Values.Where(item => !item.IsGroup && item.CanTest).Select(item => item.Name));
    private async Task Measure(IEnumerable<string> names)
    {
        if (!IsRunning) { Message(InactiveMessage); return; }
        var targets = names.Distinct(StringComparer.Ordinal).Where(name => nodes.GetValueOrDefault(name) is { CanTest: true, IsGroup: false } && !testing.Contains(name)).ToArray();
        if (targets.Length == 0) return;
        if (testing.Count == 0) testTotal = testDone = 0;
        testTotal += targets.Length; foreach (var name in targets) testing.Add(name); Render();
        var token = PageToken; var controller = ControllerIdentity; var url = Session.TestUrl;
        try
        {
            await Task.WhenAll(targets.Select(async name =>
            {
                var acquired = false;
                try
                {
                    await probeSlots.WaitAsync(token); acquired = true;
                    double delay;
                    try { delay = (await Get("proxies/" + Uri.EscapeDataString(name) + "/delay?timeout=5000&url=" + Uri.EscapeDataString(url), token)).Number("delay"); }
                    catch (OperationCanceledException) { throw; }
                    catch { delay = 0; }
                    if (controller != ControllerIdentity || token.IsCancellationRequested || !nodes.TryGetValue(name, out var previous)) return;
                    var value = previous with { Alive = delay > 0, History = previous.History.Append(new ProxyLatency(delay, 0, DateTimeOffset.UtcNow.ToString("O") + ":" + delay)).TakeLast(10).ToArray() };
                    nodes[name] = measured[name] = value;
                    foreach (var item in tiles.Where(item => item.Key.Name == name)) item.Value.Update(value, false);
                    if (!renderTimer.IsEnabled) renderTimer.Start();
                }
                finally
                {
                    if (acquired) probeSlots.Release(); testing.Remove(name); testDone++; ApplyTestControls();
                }
            }));
            Message(L.F("已测量 {0} 个节点。", targets.Length));
        }
        finally { if (IsActive) Render(); }
    }
    private void ApplyTestControls()
    {
        var busy = testing.Count > 0;
        progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; progress.Value = testTotal > 0 ? testDone * 100d / testTotal : 0;
        if (busy) testAll.Content = L.F("正在测速 {0}/{1}", testDone, testTotal); else L.Bind(testAll, ContentControl.ContentProperty, "全部测速");
        testAll.IsEnabled = IsRunning && !busy;
    }

    public override async Task NavigateToAsync(NavigationTarget target)
    {
        pendingNavigation = target;
        if (target.Query is not null) search.Text = target.Query;
        if (!IsRunning) { Message(InactiveMessage); return; }
        await RefreshAsync();
        if (pendingNavigation is not null) await ApplyPendingNavigation();
    }
    private async Task ApplyPendingNavigation()
    {
        var target = pendingNavigation; pendingNavigation = null; if (target is null) return;
        if (target.Action == "test-all") { _ = RunAsync(MeasureAll); return; }
        if (target.Target is not { Length: > 0 } name) return;
        search.Clear(); filterTimer.Stop();
        if (target.Action == "focus-group")
        {
            preferences.OpenGroups ??= Groups().Take(1).Select(item => item.Name).ToHashSet(StringComparer.Ordinal); preferences.OpenGroups.Add(name); preferences.Save(); Render();
            await Dispatcher.InvokeAsync(() => { if (groupCards.TryGetValue(name, out var card)) { card.BringIntoView(); expanders[name].Focus(); Flash(card); } }, DispatcherPriority.Loaded);
        }
        else if (target.Action == "focus-node")
        {
            Render();
            await Dispatcher.InvokeAsync(() =>
            {
                var match = tiles.FirstOrDefault(item => item.Key.Name == name && item.Value.IsVisible).Value;
                if (match is null && nodes.TryGetValue(name, out var node))
                {
                    var temporary = new ProxyTilePanel(); match = Tile("focus", node); temporary.Children.Add(match); nodeSection.Children.Add(temporary);
                }
                match?.Reveal();
            }, DispatcherPriority.Loaded);
        }
    }
    private static void Flash(Border card)
    {
        if (card.TryFindResource("AccentBrush") is not SolidColorBrush accent || card.TryFindResource("BorderBrush") is not SolidColorBrush border) return;
        var brush = new SolidColorBrush(accent.Color); card.BorderBrush = brush;
        if (!SystemParameters.ClientAreaAnimation) return;
        var animation = new ColorAnimation(accent.Color, border.Color, TimeSpan.FromMilliseconds(1200));
        animation.Completed += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private void ApplyTopologyVisibility()
    {
        topologyBody.Visibility = preferences.TopologyOpen ? Visibility.Visible : Visibility.Collapsed;
        L.Bind(toggleTopology, ContentControl.ContentProperty, preferences.TopologyOpen ? "隐藏" : "显示");
        if (preferences.TopologyOpen) StartTopology(); else StopTopology(); UpdateMapAnimation();
    }
    protected override Task LiveAsync(CancellationToken cancellationToken) { StartTopology(); return Task.CompletedTask; }
    private void StartTopology()
    {
        if (!IsActive || !IsRunning || !preferences.TopologyOpen || topologyLifetime is { IsCancellationRequested: false }) return;
        topologyLifetime = CancellationTokenSource.CreateLinkedTokenSource(PageToken); var token = topologyLifetime.Token;
        _ = RunAsync(() => Task.WhenAll(
            WatchStreamAsync("connections", data => { topologyModel.Sample(data); RefreshMap(); }, token),
            WatchStreamAsync("traffic", data => { up = data.Number("up"); down = data.Number("down"); RefreshMapReadings(); }, token)));
        UpdateMapAnimation();
    }
    private void StopTopology() { topologyLifetime?.Cancel(); topologyLifetime?.Dispose(); topologyLifetime = null; topology.SetActive(false); }
    private void RefreshMap()
    {
        var model = topologyModel.Build(nodes, Groups(false)); activeConnections = model.Connections; topology.SetModel(model); idleMap.Visibility = model.Connections == 0 ? Visibility.Visible : Visibility.Collapsed; RefreshMapReadings();
    }
    private void RefreshMapReadings() => flow.Text = $"↓ {Ui.Bytes(down)}/s    ↑ {Ui.Bytes(up)}/s    " + L.F("{0} 个连接", activeConnections);
    private void UpdateMapAnimation()
    {
        if (!IsActive || !preferences.TopologyOpen || !topology.IsVisible) { topology.SetActive(false); return; }
        try { var bounds = topology.TransformToAncestor(scroll).TransformBounds(new Rect(topology.RenderSize)); topology.SetActive(bounds.Bottom > 0 && bounds.Top < scroll.ViewportHeight); }
        catch (InvalidOperationException) { topology.SetActive(false); }
    }
    public override void Activate() { base.Activate(); ApplyTopologyVisibility(); }
    public override void Deactivate() { StopTopology(); filterTimer.Stop(); renderTimer.Stop(); base.Deactivate(); }
    protected override void OnStatusChanged()
    {
        var phase = Session.Status.Text("phase"); var becameRunning = phase == "running" && lastPhase != "running"; lastPhase = phase;
        if (!IsRunning) ClearInactive();
        else { if (shownProviders != Session.ShowProxyProvider) Render(); if (becameRunning) _ = RunAsync(RefreshAsync); StartTopology(); }
        applyingPreferences = true; breakConnections.IsChecked = Session.CloseConnectionsOnSwitch; applyingPreferences = false;
        ApplyTestControls();
    }
    protected override void OnLanguageChanged() { Render(); if (!IsRunning) Message(InactiveMessage); }
    private void ClearInactive()
    {
        StopTopology(); nodes.Clear(); providerData = default; order = []; topologyModel.Clear(); up = down = 0; activeConnections = 0;
        groupList.Children.Clear(); providerList.Children.Clear(); nodeSection.Children.Clear(); topology.SetModel(TopologyModel.Empty); UpdateSummary(); RefreshMapReadings(); Message(InactiveMessage);
    }
}
