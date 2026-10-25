using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Documents;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class ProxiesPage : NativePage
{
    private readonly long constructionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
    private double constructionFieldsMs, constructionControlsMs, constructionTopologyMs, constructionSnapshotMs, constructionTotalMs;
    private object? lastRenderTiming, constructionRenderTiming, lastNavigationGeometry;
    private readonly StackPanel content = new();
    private readonly StackPanel groupList = new();
    private readonly StackPanel providerList = new();
    private readonly StackPanel nodeSection = new();
    private readonly ProxyTopologyControl topology = new();
    private readonly ProxyTopologyModelBuilder topologyModel = new();
    private readonly ProxyPreferences preferences = new();
    private readonly TextBox search = Ui.SearchBox("筛选节点", "ProxySearch", "filter", rightIcon: true);
    private readonly WrapPanel summary = new();
    private readonly TextBlock flowDown = Ui.Muted(""), flowUp = Ui.Muted(""), flowCount = Ui.Muted("");
    private readonly TextBlock idleMap = Ui.Hint("当前没有活动连接。");
    private readonly ToggleButton breakConnections = new() { VerticalAlignment = VerticalAlignment.Center, Style = FluentControls.Style("Switch") };
    private readonly TextBlock breakLabel = Ui.Hint("切换时打断包含策略组的连接");
    private readonly Dictionary<string, string> pendingSelections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> providersBusy = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProxyTilePanel> providerPanels = new(StringComparer.Ordinal);
    private readonly ProxyTilePanel nodesPanel = new();
    private readonly LivePing liveDot = new() { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border topologyReveal = new() { ClipToBounds = true };
    private readonly FluentIcon topologyCaret = new("chevron-up", 12);
    private readonly TextBlock topologyToggleLabel = Ui.Label("收起拓扑");
    private readonly TextBlock topologyRoute = Ui.Hint("SOURCE → POLICY → EXIT");
    private readonly TextBlock topologyHint = Ui.Hint("悬停查看链路，点击策略组跳到它。");
    private long topologyTransition;
    private readonly ProgressBar progress = new() { Height = 3, Margin = new Thickness(0, 0, 0, 16), Visibility = Visibility.Collapsed, Maximum = 100 };
    private readonly Button testAll;
    private readonly FluentIcon testIcon = new("speed", 16) { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock testLabel = Ui.Text("");
    private readonly Button toggleTopology;
    private readonly Grid topologyBody = new();
    private readonly ScrollViewer scroll;
    private CancellationTokenSource? scrollLifetime;
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
    private JsonElement presentedProxies, presentedProviders, presentedConfigs;
    private string[] order = [];
    private string mode = "rule";
    private string lastController = "";
    private Task? refreshing;
    private CancellationToken refreshToken;
    private string refreshController = "";
    private long refreshGeneration;
    private long preparationGeneration;
    private bool? preparationCacheHit;
    private double? preparationElapsedMs;
    private double? preparationSnapshotMs, preparationGroupMs, preparationLayoutMs, preparationLayoutWaitMs;
    private int unchangedReadbacks;
    private bool preparationPending;
    private NavigationTarget? preparedNavigation;
    private string preparedController = "";
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
    private string renderedLanguage = "";

    public ProxiesPage(AppSession session) : base(session, "proxies", "代理", "策略组、代理节点与实时流量路径")
    {
        constructionFieldsMs = System.Diagnostics.Stopwatch.GetElapsedTime(constructionStarted).TotalMilliseconds;
        var constructionStage = System.Diagnostics.Stopwatch.GetTimestamp();
        search.Width = 200; search.Margin = new Thickness(0, 0, 8, 0); Tools.Children.Add(search);
        testAll = Action("测速", "MeasureAllProxies", MeasureAll, true);
        testAll.Margin = new Thickness(0);
        var testContent = new StackPanel { Orientation = Orientation.Horizontal }; testContent.Children.Add(testIcon); testContent.Children.Add(testLabel); testAll.Content = testContent; testLabel.Text = L.T("测速");
        L.Bind(testAll, ToolTipProperty, "全部测速 (Ctrl+T)"); Tools.Children.Add(testAll);
        var refreshMenu = new ContextMenu(); var refresh = L.Bind(new MenuItem().Id("RefreshProxies"), HeaderedItemsControl.HeaderProperty, "刷新"); refresh.Click += async (_, _) => await RunAsync(RefreshAsync); refreshMenu.Items.Add(refresh); ContextMenu = refreshMenu;
        summary.Id("ProxySummary"); SetHeaderMeta(summary); progress.Id("ProxyTestProgress"); content.Children.Add(progress);
        var topologyHeader = new DockPanel { MinHeight = 40, Margin = new Thickness(16, 8, 8, 8) };
        toggleTopology = Action("收起拓扑", "ToggleProxyTopology", () => { preferences.TopologyOpen = !preferences.TopologyOpen; preferences.Save(); ApplyTopologyVisibility(); return Task.CompletedTask; });
        toggleTopology.Style = FluentControls.Style("Ghost"); toggleTopology.Padding = new Thickness(11, 0, 11, 0); toggleTopology.Height = 32; toggleTopology.Margin = new Thickness(0); DockPanel.SetDock(toggleTopology, Dock.Right);
        var toggleCopy = new StackPanel { Orientation = Orientation.Horizontal }; toggleCopy.Children.Add(topologyToggleLabel); topologyCaret.Margin = new Thickness(8, 0, 0, 0); toggleCopy.Children.Add(topologyCaret); toggleTopology.Content = toggleCopy; topologyHeader.Children.Add(toggleTopology);
        topologyRoute.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); topologyRoute.Margin = new Thickness(12, 0, 0, 0);
        var topologyTitle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; topologyTitle.Children.Add(Ui.Label("实时流量拓扑", 14, true)); topologyTitle.Children.Add(topologyRoute); topologyHeader.Children.Add(topologyTitle);
        var bar = new Grid { Margin = new Thickness(16, 12, 16, 0) }; bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); bar.ColumnDefinitions.Add(new ColumnDefinition());
        var live = new StackPanel { Orientation = Orientation.Horizontal }; live.Children.Add(liveDot);
        foreach (var reading in new[] { flowDown, flowUp, flowCount }) { reading.LineHeight = 12 * 1.43; reading.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; live.Children.Add(reading); }
        flowDown.Margin = new Thickness(0, 0, 14, 0); flowUp.Margin = new Thickness(0, 0, 14, 0); bar.Children.Add(live);
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 0, 0) }; legend.Children.Add(Legend("代理流量", "AccentBrush")); legend.Children.Add(Legend("直连", "FaintBrush")); Grid.SetColumn(legend, 1); bar.Children.Add(legend);
        topologyHint.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); topologyHint.HorizontalAlignment = HorizontalAlignment.Right; topologyHint.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(topologyHint, 2); bar.Children.Add(topologyHint);
        topologyBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); topologyBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); topologyBody.Children.Add(bar);
        var stage = new Grid { Margin = new Thickness(16, 8, 16, 16) }; stage.Children.Add(topology); idleMap.VerticalAlignment = VerticalAlignment.Bottom; idleMap.HorizontalAlignment = HorizontalAlignment.Center; idleMap.Margin = new Thickness(0, 0, 0, 6); idleMap.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); idleMap.IsHitTestVisible = false; stage.Children.Add(idleMap); Grid.SetRow(stage, 1); topologyBody.Children.Add(stage);
        topologyReveal.Child = topologyBody; topologyReveal.BorderThickness = new Thickness(0, 1, 0, 0); topologyReveal.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        var topologyCard = Ui.Card(Ui.Column(topologyHeader, topologyReveal), 0); topologyCard.CornerRadius = new CornerRadius(4); topologyCard.Margin = new Thickness(0); content.Children.Add(topologyCard);
        topologyCard.SizeChanged += (_, _) =>
        {
            var compact = PageContainerWidth <= 640; topology.SetCompact(compact); search.Width = compact ? Math.Max(1, Body.ActualWidth) : 200; topologyHeader.Margin = new Thickness(compact ? 12 : 16, 8, 8, 8);
            topologyRoute.Visibility = topologyHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            bar.Margin = compact ? new Thickness(12, 10, 12, 0) : new Thickness(16, 12, 16, 0); stage.Margin = compact ? new Thickness(10, 8, 10, 12) : new Thickness(16, 8, 16, 16);
        };
        content.Children.Add(groupList); content.Children.Add(providerList); content.Children.Add(nodeSection);
        scroll = Ui.Scroll(content).Id("ProxiesScrollViewer"); scroll.ScrollChanged += (_, _) => UpdateMapAnimation(); scroll.Content = null; SetScrollableBody(content, scroll); SetHeaderBottomMargin(4);
        breakConnections.Id("BreakConnectionsOnSwitch"); L.Bind(breakConnections, AutomationProperties.NameProperty, "切换时打断包含策略组的连接");
        breakConnections.Checked += (_, _) => { if (!applyingPreferences) Session.SetCloseConnectionsOnSwitch(true); };
        breakConnections.Unchecked += (_, _) => { if (!applyingPreferences) Session.SetCloseConnectionsOnSwitch(false); };
        search.TextChanged += (_, _) => { filterTimer.Stop(); filterTimer.Start(); };
        search.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { search.Clear(); e.Handled = true; } };
        filterTimer.Tick += (_, _) => { filterTimer.Stop(); Render(); };
        renderTimer.Tick += (_, _) => { renderTimer.Stop(); Render(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; _ = RunAsync(MeasureAll); } };
        topology.Picked += (action, name) => _ = RunAsync(() => NavigateToAsync(new NavigationTarget("proxies", name, Action: action)));
        Session.ThemeChanged += topology.RefreshPalette;
        Session.HostEvent += (name, data) =>
        {
            if (name != "desktop-config-changed") return;
            InvalidateRefresh();
            if (IsActive) _ = RunAsync(RefreshAsync);
        };
        groupList.SizeChanged += (_, _) => breakLabel.Visibility = PageContainerWidth <= 640 ? Visibility.Collapsed : Visibility.Visible;
        constructionControlsMs = System.Diagnostics.Stopwatch.GetElapsedTime(constructionStage).TotalMilliseconds;
        constructionStage = System.Diagnostics.Stopwatch.GetTimestamp();
        ApplyTopologyVisibility();
        constructionTopologyMs = System.Diagnostics.Stopwatch.GetElapsedTime(constructionStage).TotalMilliseconds;
        constructionStage = System.Diagnostics.Stopwatch.GetTimestamp();
        TryPresentRuntimeSnapshot();
        constructionSnapshotMs = System.Diagnostics.Stopwatch.GetElapsedTime(constructionStage).TotalMilliseconds;
        constructionRenderTiming = lastRenderTiming;
        constructionTotalMs = System.Diagnostics.Stopwatch.GetElapsedTime(constructionStarted).TotalMilliseconds;
    }

    public override Task RefreshAsync()
    {
        if (refreshing is { IsCompleted: false } && refreshToken == PageToken && refreshController == ControllerIdentity) return refreshing;
        refreshToken = PageToken; refreshController = ControllerIdentity;
        return refreshing = RefreshCore(++refreshGeneration, refreshToken, refreshController);
    }
    private void InvalidateRefresh() { refreshGeneration++; refreshing = null; }
    private async Task RefreshCore(long generation, CancellationToken token, string controller)
    {
        if (!IsRunning) { ClearInactive(); return; }
        ResetControllerData(controller);
        var proxyTask = Get("proxies", token); var providerTask = Get("providers/proxies", token); var configTask = Get("configs", token);
        await Task.WhenAll(proxyTask, providerTask, configTask);
        if (!IsRunning || controller != ControllerIdentity || generation != refreshGeneration || token.IsCancellationRequested) return;
        PresentRuntimeData(await proxyTask, await providerTask, await configTask);
        await ApplyPendingNavigation(); StartTopology();
    }
    private void ResetControllerData(string controller)
    {
        if (lastController == controller) return;
        lastController = controller; measured.Clear(); tiles.Clear(); topologyModel.Clear(); up = down = 0;
        presentedProxies = presentedProviders = presentedConfigs = default;
    }
    private bool TryPresentRuntimeSnapshot()
    {
        if (!IsRunning || !Session.TryGetProxyRuntimeSnapshot(out var proxies, out var providers, out var configs)) return false;
        ResetControllerData(ControllerIdentity);
        if (!proxies.Equals(presentedProxies) || !providers.Equals(presentedProviders) || !configs.Equals(presentedConfigs) ||
            shownProviders != Session.ShowProxyProvider || breakConnections.IsChecked != Session.CloseConnectionsOnSwitch ||
            renderedLanguage != L.ResolvedLanguage)
            PresentRuntimeData(proxies, providers, configs);
        return true;
    }
    // Called only by the shell's one-time, inactive Home -> Proxies preparation.
    // It consumes existing read-only snapshots and never creates a page lifetime.
    internal bool TryPrepareIdleSnapshot() => !IsActive && TryPresentRuntimeSnapshot();
    private void PresentRuntimeData(JsonElement proxies, JsonElement providers, JsonElement configs)
    {
        // Activation still requests authoritative data. A new JSON document
        // with identical content need not rebuild the just-realized controls
        // while their connected entrance is running. Preference/language
        // changes on a cached, inactive page still require a fresh rendering.
        if (presentedProxies.ValueKind != JsonValueKind.Undefined &&
            shownProviders == Session.ShowProxyProvider &&
            breakConnections.IsChecked == Session.CloseConnectionsOnSwitch &&
            renderedLanguage == L.ResolvedLanguage &&
            JsonElement.DeepEquals(proxies, presentedProxies) &&
            JsonElement.DeepEquals(providers, presentedProviders) &&
            JsonElement.DeepEquals(configs, presentedConfigs))
        {
            presentedProxies = proxies; presentedProviders = providers; presentedConfigs = configs;
            unchangedReadbacks++;
            Message(""); return;
        }
        var all = proxies.Get("proxies"); providerData = providers.Get("providers"); mode = configs.Text("mode").ToLowerInvariant();
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
        Message(""); Render();
        presentedProxies = proxies; presentedProviders = providers; presentedConfigs = configs;
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
        renderedLanguage = L.ResolvedLanguage;
        var stage = System.Diagnostics.Stopwatch.GetTimestamp();
        RenderGroups(); var groupsMs = System.Diagnostics.Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
        stage = System.Diagnostics.Stopwatch.GetTimestamp();
        RenderProviders(); var providersMs = System.Diagnostics.Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
        stage = System.Diagnostics.Stopwatch.GetTimestamp();
        RenderNodes(); var nodesMs = System.Diagnostics.Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
        stage = System.Diagnostics.Stopwatch.GetTimestamp();
        RefreshMap(); var topologyMs = System.Diagnostics.Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
        stage = System.Diagnostics.Stopwatch.GetTimestamp();
        UpdateSummary(); ApplyTestControls();
        lastRenderTiming = new { groupsMs, providersMs, nodesMs, topologyMs, summaryMs = System.Diagnostics.Stopwatch.GetElapsedTime(stage).TotalMilliseconds };
    }
    private void UpdateSummary()
    {
        var all = StandaloneNodes().Concat(VisibleProviderData().SelectMany(provider => provider.Value.Get("proxies").Items()).Select(item => nodes.GetValueOrDefault(item.Text("name"))).OfType<ProxyNodeInfo>()).ToArray();
        summary.Children.Clear();
        foreach (var (number, label) in new[] { (Groups(false).Count(), "个策略组"), (all.Length, "个节点"), (all.Count(item => !item.Failed && item.Delay > 0), "个可用") })
        {
            var item = Ui.Muted("", 13); item.Margin = new Thickness(0, 0, 14, 0); var reading = new Run(number.ToString()); reading.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush"); item.Inlines.Add(reading); item.Inlines.Add(" " + L.T(label)); summary.Children.Add(item);
        }
    }
    private static FrameworkElement Legend(string label, string resource)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
        var swatch = new Border { Width = 16, Height = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }; swatch.SetResourceReference(Border.BackgroundProperty, resource); row.Children.Add(swatch); row.Children.Add(Ui.Hint(label)); return row;
    }
    private void RenderGroups()
    {
        var groups = Groups().ToArray();
        var names = groups.Select(group => group.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in groupCards.Keys.Where(name => !names.Contains(name)).ToArray()) { groupList.Children.Remove(groupCards[stale]); groupCards.Remove(stale); expanders.Remove(stale); }
        if (groups.Length == 0) { groupList.Children.Clear(); if (IsRunning && nodes.Count == 0) groupList.Children.Add(Ui.Hint("当前配置没有代理节点，可在配置页导入订阅。")); return; }
        if (groupList.Children.Count == 0 || groupList.Children[0] is not DockPanel) { groupList.Children.Clear(); groupList.Children.Add(new DockPanel()); }
        var sectionHeader = (DockPanel)groupList.Children[0]; sectionHeader.Children.Clear(); sectionHeader.Margin = new Thickness(0, 28, 0, 8);
        Detach(breakConnections); Detach(breakLabel); var setting = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; breakLabel.Margin = new Thickness(0, 0, 12, 0); setting.Children.Add(breakLabel); setting.Children.Add(breakConnections); DockPanel.SetDock(setting, Dock.Right); sectionHeader.Children.Add(setting);
        sectionHeader.Children.Add(SectionHeading("策略组", groups.Length));
        var open = preferences.OpenGroups ?? groups.Take(1).Select(item => item.Name).ToHashSet(StringComparer.Ordinal); var query = search.Text.Trim();
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            if (!groupCards.TryGetValue(group.Name, out var stored))
            {
                var name = group.Name; var card = new ProxyGroupCard(name, member => _ = RunAsync(() => Select(name, member)), () => _ = RunAsync(() => Measure(nodes.GetValueOrDefault(name)?.Members ?? [])));
                card.Expander.IsExpanded = open.Contains(name); card.OpenChanged += expanded => SaveOpen(name, expanded, Groups().ToArray());
                groupCards[name] = stored = card; expanders[name] = card.Expander;
            }
            var view = (ProxyGroupCard)stored; if (view.Expander.IsExpanded != open.Contains(group.Name)) view.Expander.IsExpanded = open.Contains(group.Name);
            view.Update(group, nodes, query, IsRunning, pendingSelections.GetValueOrDefault(group.Name));
            var old = groupList.Children.IndexOf(view); if (old != index + 1) { if (old >= 0) groupList.Children.RemoveAt(old); groupList.Children.Insert(index + 1, view); }
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
        var heading = SectionHeading("代理集", providers.Length); heading.Margin = new Thickness(0, 28, 0, 8); providerList.Children.Add(heading);
        foreach (var provider in providers)
        {
            var name = provider.Name; var items = provider.Value.Get("proxies").Items().Select(item => nodes.GetValueOrDefault(item.Text("name"))).OfType<ProxyNodeInfo>().OrderBy(DelayOrder).ToArray();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var actions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            var updated = provider.Value.Text("updatedAt"); if (DateTimeOffset.TryParse(updated, out var time)) { var updatedText = Ui.Muted(L.F("最后更新于 {0}", UpdatedAgo(time)), 12); updatedText.Margin = new Thickness(0, 0, 8, 0); updatedText.VerticalAlignment = VerticalAlignment.Center; actions.Children.Add(updatedText); }
            Button ProviderAction(string title, string id, string icon, bool check)
            {
                var button = Action(title, id, () => ChangeProvider(name, check)); button.Style = FluentControls.Style("Ghost"); button.Height = 28; button.FontSize = 13; button.Margin = new Thickness(0, 0, 4, 0); button.Padding = new Thickness(10, 0, 10, 0); button.IsEnabled = !providersBusy.ContainsKey(name);
                var busy = providersBusy.TryGetValue(name, out var checking) && checking == check;
                var glyph = new FluentIcon(icon, 14) { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }; ProxyVisuals.SetBusyIcon(glyph, busy);
                var copy = new StackPanel { Orientation = Orientation.Horizontal }; copy.Children.Add(glyph); copy.Children.Add(Ui.Label(title, 13)); if (busy) copy.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush"); button.Content = copy;
                actions.Children.Add(button); return button;
            }
            ProviderAction("健康检查", "CheckProvider_" + name, "healthcheck", true); ProviderAction("更新", "UpdateProvider_" + name, "update", false);
            DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
            var identity = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var title = Ui.Text(name, 14, true); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.Margin = new Thickness(0, 0, 10, 0); identity.Children.Add(title);
            var badge = new Border { Child = Ui.Muted(provider.Value.Text("vehicleType"), 12), Height = 20, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }; badge.SetResourceReference(Border.BackgroundProperty, "SubtleHoverBrush"); identity.Children.Add(badge); identity.Children.Add(Ui.Muted(items.Length.ToString(), 12)); header.Children.Add(identity);
            if (!providerPanels.TryGetValue(name, out var panel)) providerPanels[name] = panel = new ProxyTilePanel();
            Detach(panel); SetTiles(panel, "provider:" + name, items);
            var body = new Grid(); body.Children.Add(new Border { Child = Ui.Column(header, panel), Padding = new Thickness(16) });
            if (providersBusy.ContainsKey(name)) body.Children.Add(new ProxyProviderLoading().Id("ProviderProgress_" + name));
            var card = Ui.Card(body, 0); card.CornerRadius = new CornerRadius(4); card.Margin = new Thickness(0, 0, 0, 4); providerList.Children.Add(card);
        }
    }
    private static StackPanel SectionHeading(string title, int count)
    {
        var label = Ui.Label(title, 14, true); label.LineHeight = 20; label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; label.Margin = new Thickness(0, 0, 12, 0);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; heading.Children.Add(label); heading.Children.Add(Ui.Muted(count.ToString(), 12)); return heading;
    }
    private static string UpdatedAgo(DateTimeOffset time)
    {
        var span = DateTimeOffset.UtcNow - time; var seconds = Math.Max(0, span.TotalSeconds);
        if (seconds < 45) return L.T("几秒前");
        if (seconds < 90) return L.T("1 分钟前");
        if (seconds < 45 * 60) return L.F("{0} 分钟前", Math.Round(span.TotalMinutes));
        if (seconds < 90 * 60) return L.T("1 小时前");
        if (seconds < 22 * 3600) return L.F("{0} 小时前", Math.Round(span.TotalHours));
        if (seconds < 36 * 3600) return L.T("1 天前");
        if (seconds < 26 * 86400) return L.F("{0} 天前", Math.Round(span.TotalDays));
        if (seconds < 45 * 86400) return L.T("1 个月前");
        if (seconds < 320 * 86400) return L.F("{0} 个月前", Math.Round(span.TotalDays / 30));
        return L.F("{0} 年前", Math.Max(1, Math.Round(span.TotalDays / 365)));
    }
    private async Task ChangeProvider(string name, bool check)
    {
        if (!providersBusy.TryAdd(name, check)) return; InvalidateRefresh(); RenderProviders();
        try
        {
            if (check) await Get("providers/proxies/" + Uri.EscapeDataString(name) + "/healthcheck");
            else await Session.ApiAsync(HttpMethod.Put, "providers/proxies/" + Uri.EscapeDataString(name), cancellationToken: PageToken);
            InvalidateRefresh(); measured.Clear(); await RefreshAsync();
        }
        finally { providersBusy.Remove(name); RenderProviders(); }
    }
    private void RenderNodes()
    {
        nodeSection.Children.Clear(); var all = StandaloneNodes().ToArray(); if (all.Length == 0) return;
        var query = search.Text.Trim(); IEnumerable<ProxyNodeInfo> shown = all.Where(item => query.Length == 0 || (item.Name + " " + item.Type).Contains(query, StringComparison.CurrentCultureIgnoreCase));
        shown = sort switch { 0 => shown.OrderBy(DelayOrder), 1 => shown.OrderByDescending(DelayOrder), _ => shown };
        var list = shown.ToArray(); var header = new DockPanel { Margin = new Thickness(0, 28, 0, 8) };
        var sortButton = Action(sort == 0 ? "延迟从低到高" : sort == 1 ? "延迟从高到低" : "原始顺序", "SortProxyNodes", () => { sort = (sort + 1) % 3; RenderNodes(); return Task.CompletedTask; }); sortButton.Style = FluentControls.Style("Ghost"); sortButton.Margin = new Thickness(0); sortButton.Content = FluentControls.ButtonContent(sort == 2 ? "sort" : sort == 0 ? "sort-ascending" : "sort-descending", sort == 0 ? "延迟从低到高" : sort == 1 ? "延迟从高到低" : "原始顺序");
        DockPanel.SetDock(sortButton, Dock.Right); header.Children.Add(sortButton);
        var heading = Ui.Row(Ui.Label("节点", 14, true), Ui.Muted(list.Length.ToString(), 12)); var hint = Ui.Hint("点按节点单独测速"); hint.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); hint.Margin = new Thickness(10, 0, 0, 0); heading.Children.Add(hint); header.Children.Add(heading); header.SizeChanged += (_, _) => hint.Visibility = PageContainerWidth <= 640 ? Visibility.Collapsed : Visibility.Visible; nodeSection.Children.Add(header);
        if (list.Length == 0) { var empty = Ui.Hint("没有符合筛选条件的节点。"); empty.Margin = new Thickness(16); nodeSection.Children.Add(empty); return; }
        Detach(nodesPanel); SetTiles(nodesPanel, "nodes", list); nodeSection.Children.Add(nodesPanel);
    }
    private void SetTiles(ProxyTilePanel panel, string section, IReadOnlyList<ProxyNodeInfo> list)
    {
        var selected = list.Select(node => Tile(section, node)).ToArray();
        foreach (var item in panel.Children.OfType<ProxyNodeTile>().Where(item => !selected.Contains(item)).ToArray()) panel.Children.Remove(item);
        for (var index = 0; index < selected.Length; index++)
        {
            var item = selected[index]; var previous = panel.Children.IndexOf(item);
            if (previous == index) continue; if (previous >= 0) panel.Children.RemoveAt(previous); else Detach(item); panel.Children.Insert(index, item);
        }
    }
    private static double DelayOrder(ProxyNodeInfo node) => node.Failed || node.Delay <= 0 ? double.MaxValue : node.Delay;
    private ProxyNodeTile Tile(string section, ProxyNodeInfo node)
    {
        if (!tiles.TryGetValue((section, node.Name), out var tile))
        {
            tile = new ProxyNodeTile().Id(section == "nodes" ? "ProxyNode_" + node.Name : section.StartsWith("provider:", StringComparison.Ordinal) ? "ProviderNode_" + section[9..] + "_" + node.Name : "FocusedProxyNode_" + node.Name);
            var name = node.Name; tile.Click += async (_, _) => await RunAsync(() => Measure([name])); tiles[(section, node.Name)] = tile;
        }
        tile.SetProvider(section.StartsWith("provider:", StringComparison.Ordinal)); tile.Update(node, testing.Contains(node.Name)); return tile;
    }
    private static void Detach(UIElement item) { if (VisualTreeHelper.GetParent(item) is Panel panel) panel.Children.Remove(item); }

    private async Task Select(string group, string name)
    {
        if (!IsRunning || !nodes.TryGetValue(group, out var current) || current.Type != "Selector" || current.Now == name || !selecting.Add(group)) return;
        InvalidateRefresh();
        pendingSelections[group] = name; RenderGroups();
        var token = PageToken; var controller = ControllerIdentity;
        try
        {
            await Session.ApiAsync(HttpMethod.Put, "proxies/" + Uri.EscapeDataString(group), new { name }, token);
            if (controller != ControllerIdentity || token.IsCancellationRequested) return;
            InvalidateRefresh();
            nodes[group] = current with { Now = name }; RenderGroups(); RefreshMap();
            if (Session.CloseConnectionsOnSwitch)
            {
                var connections = await Get("connections", token);
                var ids = connections.Get("connections").Items().Where(item => item.Get("chains").Items().Any(chain => chain.Text() == group)).Select(item => item.Text("id")).ToArray();
                foreach (var id in ids) await Session.ApiAsync(HttpMethod.Delete, "connections/" + Uri.EscapeDataString(id), cancellationToken: token);
            }
            await RefreshAsync();
        }
        finally { selecting.Remove(group); pendingSelections.Remove(group); RenderGroups(); }
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
        progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; ProxyVisuals.Tween(progress, RangeBase.ValueProperty, progress.Value, testTotal > 0 ? testDone * 100d / testTotal : 0, 250);
        testLabel.Text = busy ? L.F("正在测速 {0}/{1}", testDone, testTotal) : L.T("测速");
        ProxyVisuals.SetBusyIcon(testIcon, busy);
        // The reference keeps this accent action enabled while its speed icon
        // works. MeasureAll still rejects a second concurrent batch.
        testAll.IsEnabled = IsRunning;
    }

    internal override async Task PrepareNavigationAsync(NavigationTarget target)
    {
        var generation = ++preparationGeneration;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        preparationCacheHit = null; preparationElapsedMs = null; preparationPending = true;
        preparationSnapshotMs = preparationGroupMs = preparationLayoutMs = preparationLayoutWaitMs = null;
        double? snapshotMs = null, groupMs = null, layoutMs = null, layoutWaitMs = null;
        preparedNavigation = null;
        try
        {
            var stage = System.Diagnostics.Stopwatch.GetTimestamp();
            bool cacheHit = TryPresentRuntimeSnapshot();
            preparationCacheHit = cacheHit;
            snapshotMs = System.Diagnostics.Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
            if (!cacheHit) await RefreshAsync();
            if (generation == preparationGeneration && target.Action == "focus-group" && target.Target is { Length: > 0 } name && IsActive && IsRunning && lastController == ControllerIdentity)
            {
                var controller = ControllerIdentity;
                stage = System.Diagnostics.Stopwatch.GetTimestamp();
                OpenNavigationGroup(name);
                groupMs = System.Diagnostics.Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
                // Realize the target before Main starts its entrance clock.
                // A cache hit is already attached to the live window: waiting
                // at Loaded priority unnecessarily lets normal-priority network
                // continuations rebuild it before this preparation can resume.
                void RealizeTarget()
                {
                    if (generation != preparationGeneration || !IsActive || !IsRunning || controller != ControllerIdentity) return;
                    var layoutStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    scroll.UpdateLayout();
                    layoutMs = System.Diagnostics.Stopwatch.GetElapsedTime(layoutStarted).TotalMilliseconds;
                }
                if (cacheHit) { layoutWaitMs = 0; RealizeTarget(); }
                else
                {
                    var queued = System.Diagnostics.Stopwatch.GetTimestamp();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        layoutWaitMs = System.Diagnostics.Stopwatch.GetElapsedTime(queued).TotalMilliseconds;
                        RealizeTarget();
                    }, DispatcherPriority.Loaded);
                }
                if (generation == preparationGeneration && IsActive && IsRunning && controller == ControllerIdentity && groupCards.ContainsKey(name))
                {
                    preparedNavigation = target;
                    preparedController = controller;
                }
            }
        }
        finally
        {
            if (generation == preparationGeneration)
            {
                preparationElapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                preparationSnapshotMs = snapshotMs; preparationGroupMs = groupMs;
                preparationLayoutMs = layoutMs; preparationLayoutWaitMs = layoutWaitMs;
                preparationPending = false;
            }
        }
    }

    internal object DescribeNavigationPreparation() => new
    {
        cacheHit = preparationCacheHit,
        elapsedMs = preparationElapsedMs,
        snapshotMs = preparationSnapshotMs,
        openGroupMs = preparationGroupMs,
        layoutMs = preparationLayoutMs,
        layoutWaitMs = preparationLayoutWaitMs,
        unchangedReadbacks,
        pending = preparationPending,
        construction = new { fieldsMs = constructionFieldsMs, controlsMs = constructionControlsMs, topologyMs = constructionTopologyMs, snapshotMs = constructionSnapshotMs, totalMs = constructionTotalMs, templateLoadMs = ProxyVisuals.TemplateLoadMilliseconds, render = constructionRenderTiming },
        geometry = lastNavigationGeometry
    };

    public override async Task NavigateToAsync(NavigationTarget target)
    {
        if (preparedNavigation == target && preparedController == ControllerIdentity && IsActive && IsRunning && target.Target is { } preparedName && groupCards.TryGetValue(preparedName, out var preparedCard))
        {
            preparedNavigation = null; pendingNavigation = null;
            await CenterAsync(preparedCard, false);
            Flash(preparedCard);
            return;
        }
        preparedNavigation = null;
        pendingNavigation = target;
        if (target.Query is not null) search.Text = target.Query;
        if (!IsRunning) { Message(InactiveMessage); return; }
        bool snapshotReady = TryPresentRuntimeSnapshot();
        var refresh = RefreshAsync();
        bool targetReady = target.Action switch
        {
            "focus-group" => target.Target is { } group && groupCards.ContainsKey(group),
            "focus-node" => target.Target is { } node && nodes.ContainsKey(node),
            _ => true
        };
        if (snapshotReady && targetReady)
        {
            // Keep real network refreshes and their controller/page guards, but
            // do not hold the connected transition behind a duplicate read.
            _ = RunAsync(() => refresh);
            await ApplyPendingNavigation();
            return;
        }
        await refresh;
        if (pendingNavigation is not null) await ApplyPendingNavigation();
    }
    private async Task ApplyPendingNavigation()
    {
        var target = pendingNavigation; pendingNavigation = null; if (target is null) return;
        if (target.Action == "test-all") { _ = RunAsync(MeasureAll); return; }
        if (target.Target is not { Length: > 0 } name) return;
        if (target.Action == "focus-group")
        {
            OpenNavigationGroup(name);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (IsActive && groupCards.TryGetValue(name, out var card))
            {
                // Search, Home and the topology all use the same short accent
                // highlight. Programmatic navigation does not take keyboard focus.
                Flash(card);
                var connected = Window.GetWindow(this) is { } window && ConnectedNavigation.HasPending(window, name);
                await CenterAsync(card, !connected);
            }
        }
        else if (target.Action == "focus-node")
        {
            search.Clear(); filterTimer.Stop();
            Render();
            await Dispatcher.InvokeAsync(() =>
            {
                var match = tiles.FirstOrDefault(item => item.Key.Name == name && item.Value.IsVisible).Value;
                if (match is null && nodes.TryGetValue(name, out var node))
                {
                    var temporary = new ProxyTilePanel(); match = Tile("focus", node); Detach(match); temporary.Children.Add(match); nodeSection.Children.Add(temporary);
                }
                match?.Reveal();
            }, DispatcherPriority.Loaded);
        }
    }
    private void OpenNavigationGroup(string name)
    {
        bool filtered = search.Text.Length > 0;
        search.Clear(); filterTimer.Stop();
        var open = preferences.OpenGroups ?? Groups().Take(1).Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        bool changed = open.Add(name);
        if (changed) { preferences.OpenGroups = open; preferences.Save(); }
        if (filtered) Render();
        else if (changed) RenderGroups();
    }
    private static void Flash(Border card) => ProxyVisuals.Attention(card);

    private async Task CenterAsync(FrameworkElement element, bool smooth)
    {
        StopScrollNavigation(); scroll.UpdateLayout();
        if (!IsActive || !element.IsVisible || scroll.ViewportHeight <= 0) return;
        var bounds = element.TransformToAncestor(scroll).TransformBounds(new Rect(element.RenderSize));
        var from = scroll.VerticalOffset;
        var destination = Math.Clamp(from + bounds.Top + bounds.Height / 2 - scroll.ViewportHeight / 2, 0, scroll.ScrollableHeight);
        lastNavigationGeometry = new { entranceTranslateY = TransitionSurface.RenderTransform.Value.OffsetY, viewportHeight = scroll.ViewportHeight, targetTop = bounds.Top, targetHeight = bounds.Height, scrollFrom = from, scrollDestination = destination };
        if (!smooth || !ProxyVisuals.Animate || Math.Abs(destination - from) < 1) { scroll.ScrollToVerticalOffset(destination); scroll.UpdateLayout(); return; }
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(PageToken); scrollLifetime = lifetime;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (IsActive && clock.Elapsed.TotalMilliseconds < 250)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                var progress = Math.Clamp(clock.Elapsed.TotalMilliseconds / 250, 0, 1);
                var eased = FluentMotion.Decelerate.Ease(progress);
                scroll.ScrollToVerticalOffset(from + (destination - from) * eased);
                await Task.Delay(16, lifetime.Token);
            }
            if (IsActive && !lifetime.IsCancellationRequested) scroll.ScrollToVerticalOffset(destination);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { if (ReferenceEquals(scrollLifetime, lifetime)) scrollLifetime = null; lifetime.Dispose(); }
    }
    private void StopScrollNavigation() { scrollLifetime?.Cancel(); scrollLifetime = null; }

    private void ApplyTopologyVisibility()
    {
        var expanded = preferences.TopologyOpen; var generation = ++topologyTransition;
        var from = topologyReveal.ActualHeight; topologyReveal.IsHitTestVisible = expanded;
        topologyBody.Measure(new Size(Math.Max(0, topologyReveal.ActualWidth), double.PositiveInfinity)); var height = expanded ? topologyBody.DesiredSize.Height : 0;
        if (!IsLoaded || !ProxyVisuals.Animate) { topologyReveal.BeginAnimation(HeightProperty, null); topologyReveal.Height = expanded ? double.NaN : 0; topologyReveal.Opacity = expanded ? 1 : 0; }
        else
        {
            ProxyVisuals.Tween(topologyReveal, HeightProperty, from, height, 250, () => { if (generation == topologyTransition) topologyReveal.Height = expanded ? double.NaN : 0; });
            ProxyVisuals.Tween(topologyReveal, OpacityProperty, topologyReveal.Opacity, expanded ? 1 : 0, 250);
        }
        L.Bind(topologyToggleLabel, TextBlock.TextProperty, preferences.TopologyOpen ? "收起拓扑" : "展开拓扑"); topologyCaret.Kind = preferences.TopologyOpen ? "chevron-up" : "chevron-down";
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
    private void RefreshMapReadings()
    {
        void Rate(TextBlock target, string arrow, double value)
        {
            target.Inlines.Clear(); target.Inlines.Add(new Run(arrow + " "));
            var number = new Run(Ui.Bytes(value) + "/s").Tabular(); number.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush"); target.Inlines.Add(number);
        }
        Rate(flowDown, "↓", down); Rate(flowUp, "↑", up);
        flowCount.Inlines.Clear(); var count = new Run(activeConnections.ToString()).Tabular(); count.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush"); flowCount.Inlines.Add(count); flowCount.Inlines.Add(new Run(L.F("{0} 个连接", "")));
        var compact = PageContainerWidth <= 640; flowCount.Visibility = compact ? Visibility.Collapsed : Visibility.Visible; flowUp.Margin = new Thickness(0, 0, compact ? 0 : 14, 0);
        liveDot.SetResourceReference(LivePing.FillProperty, activeConnections > 0 ? "SuccessBrush" : "FaintBrush");
    }
    private void UpdateMapAnimation()
    {
        if (!IsActive || !preferences.TopologyOpen || !topology.IsVisible) { topology.SetActive(false); return; }
        try { var bounds = topology.TransformToAncestor(scroll).TransformBounds(new Rect(topology.RenderSize)); topology.SetActive(bounds.Bottom > 0 && bounds.Top < scroll.ViewportHeight); }
        catch (InvalidOperationException) { topology.SetActive(false); }
    }
    public override void Activate() { if (!IsActive) TryPresentRuntimeSnapshot(); base.Activate(); ApplyTopologyVisibility(); }
    public override void Deactivate() { preparedNavigation = null; StopScrollNavigation(); StopTopology(); filterTimer.Stop(); renderTimer.Stop(); base.Deactivate(); }
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
        StopTopology(); nodes.Clear(); providerData = default; presentedProxies = presentedProviders = presentedConfigs = default; order = []; topologyModel.Clear(); up = down = 0; activeConnections = 0;
        groupList.Children.Clear(); providerList.Children.Clear(); nodeSection.Children.Clear(); topology.SetModel(TopologyModel.Empty); UpdateSummary(); RefreshMapReadings(); Message(InactiveMessage);
    }
}
