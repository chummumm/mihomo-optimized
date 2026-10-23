using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class HomePage : NativePage
{
    private readonly TextBlock phase = Ui.Text("", 20, true);
    private readonly TextBlock description = Ui.Muted("", 14);
    private readonly TextBlock coreVersion = Ui.Text("—", 12);
    private readonly TextBlock proxyAddress = Ui.Text("—", 12);
    private readonly TextBlock coreError = Ui.Text("");
    private readonly TextBlock up = Ui.Text("—", 18, true);
    private readonly TextBlock down = Ui.Text("—", 18, true);
    private readonly TextBlock connectionCount = Ui.Text("—", 28, true);
    private readonly TextBlock processCount = Ui.Muted("");
    private readonly TextBlock downloadTotal = Ui.Text("—", 28, true);
    private readonly TextBlock uploadTotal = Ui.Muted("");
    private readonly TextBlock throughputHint = Ui.Muted("");
    private readonly TrafficChart chart = new() { Height = 160 };
    private readonly ModeSelector modes = new();
    private readonly QuickSettingTile proxy;
    private readonly QuickSettingTile tun;
    private readonly QuickSettingTile autostart;
    private readonly QuickSettingTile lan;
    private readonly Button start;
    private readonly Button stop;
    private readonly Button restart;
    private readonly Border heroGlyph = new() { Width = 64, Height = 64, CornerRadius = new CornerRadius(32) };
    private readonly Border heroHalo = new() { Width = 76, Height = 76, CornerRadius = new CornerRadius(38), BorderThickness = new Thickness(2), Opacity = .28 };
    private readonly FluentIcon heroIcon = new("globe-shield", 28);
    private readonly FluentProgressRing heroProgress = new() { Width = 76, Height = 76 };
    private readonly Grid heroMain = new();
    private readonly WrapPanel heroActions;
    private readonly WrapPanel facts;
    private readonly Grid liveGrid = new();
    private readonly UniformGrid stats = new() { Columns = 1, Rows = 2 };
    private readonly Border chartCard;
    private readonly ExitSummaryList exits = new("HomeExitSummary");
    private readonly Border hero;
    private ProxyExitSnapshot exitSnapshot = ProxyExitSnapshot.Empty;
    private string controller = "";
    private string pending = "";
    private string runtimeMode = "rule";
    private bool refreshing;
    private bool runtimeReady;
    private bool allowLan;
    private int port;
    private int processes;
    private int connections;
    private double downloaded;
    private double uploaded;

    public HomePage(AppSession session) : base(session, "home", "主页", "管理本地代理，查看当前网络状态")
    {
        phase.Id("CoreStatus"); chart.Id("TrafficChart"); connectionCount.Id("HomeConnections"); downloadTotal.Id("HomeTrafficTotal");
        description.TextWrapping = TextWrapping.NoWrap; description.TextTrimming = TextTrimming.CharacterEllipsis; description.Margin = new Thickness(0, 2, 0, 0);
        start = Action("启动", "StartCore", () => ChangeAsync("core", "desktop_start"), true);
        stop = Action("停止", "StopCore", () => ChangeAsync("core", "desktop_stop"));
        restart = Action("重启内核", "RestartCore", () => ChangeAsync("core", "desktop_restart"));
        FluentControls.Primary(start); stop.Style = restart.Style = FluentControls.Style("Button");
        start.Content = FluentControls.ButtonContent("power", "启动"); stop.Content = FluentControls.ButtonContent("stop", "停止"); restart.Content = FluentControls.ButtonContent("arrow-sync", "重启内核");
        heroActions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { start, stop, restart }) { button.Margin = new Thickness(8, 0, 0, 0); heroActions.Children.Add(button); }
        heroMain.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); heroMain.ColumnDefinitions.Add(new ColumnDefinition()); heroMain.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heroMain.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); heroMain.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var emblem = new Grid { Width = 76, Height = 76, Margin = new Thickness(-6, -6, 14, -6) }; heroGlyph.Child = heroIcon; emblem.Children.Add(heroGlyph); emblem.Children.Add(heroHalo); emblem.Children.Add(heroProgress); heroMain.Children.Add(emblem);
        var status = Ui.Column(phase, description); status.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(status, 1); heroMain.Children.Add(status);
        Grid.SetColumn(heroActions, 2); heroMain.Children.Add(heroActions);
        coreError.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush"); coreError.Margin = new Thickness(0, 12, 0, 0); coreError.Visibility = Visibility.Collapsed; coreError.Id("HomeCoreError");
        modes.Width = 234; modes.Selected += async value => await RunAsync(() => ChangeAsync("mode", "desktop_patch_config", new { patch = new { mode = value } }));
        var modeRow = new WrapPanel { VerticalAlignment = VerticalAlignment.Center }; var modeLabel = Ui.Hint("出站模式"); modeLabel.Margin = new Thickness(0, 8, 12, 8); modeRow.Children.Add(modeLabel); modeRow.Children.Add(modes);
        coreVersion.FontFamily = proxyAddress.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        facts = Ui.Row(Ui.Column(Ui.Hint("内核"), coreVersion), Ui.Column(Ui.Hint("本机代理"), proxyAddress));
        facts.Margin = new Thickness(12, 0, 0, 0);
        foreach (FrameworkElement item in facts.Children) item.Margin = new Thickness(0, 0, 20, 0);
        var footContent = new WrapPanel { Orientation = Orientation.Horizontal }; footContent.Children.Add(modeRow); footContent.Children.Add(facts);
        var foot = new Border { Child = footContent, Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(0, 16, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) }; foot.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        hero = Ui.Card(Ui.Column(heroMain, coreError, foot), 24); hero.Margin = new Thickness(0);
        proxy = new QuickSettingTile("nav-proxies", "系统代理", "systemProxy", () => Session.Navigate(new NavigationTarget("settings", "set-system-proxy")));
        tun = new QuickSettingTile("shield-lock", "TUN 模式", "tun", () => Session.Navigate(new NavigationTarget("settings", "set-tun")));
        lan = new QuickSettingTile("wifi", "局域网连接", "allowLan", () => Session.Navigate(new NavigationTarget("settings", "set-lan")));
        autostart = new QuickSettingTile("power", "开机启动", "autostart");
        proxy.Changed += async enabled => await RunAsync(() => ChangeAsync("proxy", "desktop_set_system_proxy", new { enabled }));
        tun.Changed += async enabled => await RunAsync(() => ChangeTunAsync(enabled));
        autostart.Changed += async enabled => await RunAsync(() => ChangeAsync("autostart", "desktop_set_autostart", new { enabled }));
        lan.Changed += async enabled => await RunAsync(() => ChangeAsync("lan", "desktop_patch_config", new { patch = new Dictionary<string, object> { ["allow-lan"] = enabled } }));
        var tiles = new ResponsiveTilePanel { Margin = new Thickness(0, 8, 0, 0) }; foreach (var tile in new[] { proxy, tun, lan, autostart }) tiles.Children.Add(tile);
        var rates = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var downRate = Ui.Column(Legend("下行", "AccentBrush"), down); downRate.Margin = new Thickness(12, 0, 0, 0); rates.Children.Add(downRate);
        var upRate = Ui.Column(Legend("上行", "ChartUpBrush"), up); upRate.Margin = new Thickness(24, 0, 0, 0); rates.Children.Add(upRate);
        var chartHead = new Grid(); chartHead.ColumnDefinitions.Add(new ColumnDefinition()); chartHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        chartHead.Children.Add(Ui.Column(Ui.Label("实时吞吐", 16, true), throughputHint)); Grid.SetColumn(rates, 1); chartHead.Children.Add(rates);
        chart.Margin = new Thickness(0, 16, 0, 0); chartCard = Ui.Card(Ui.Column(chartHead, chart), 20); chartCard.Margin = new Thickness(0);
        var connectionCard = StatCard("nav-connections", "活动连接", connectionCount, processCount); connectionCard.Margin = new Thickness(0, 0, 0, 4);
        var trafficCard = StatCard("arrow-swap", "本次运行流量", downloadTotal, uploadTotal); trafficCard.Margin = new Thickness(0, 4, 0, 0);
        stats.Children.Add(connectionCard); stats.Children.Add(trafficCard);
        liveGrid.Margin = new Thickness(0, 8, 0, 0); liveGrid.ColumnDefinitions.Add(new ColumnDefinition()); liveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        liveGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); liveGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        liveGrid.Children.Add(chartCard); Grid.SetColumn(stats, 1); stats.Margin = new Thickness(8, 0, 0, 0); liveGrid.Children.Add(stats);
        exits.Selected += (exit, identity) => { ConnectedNavigation.Prepare(identity, exit.Group); Session.Navigate(new NavigationTarget("proxies", Target: exit.Group, Action: "focus-group")); };
        var exitHeader = Ui.Column(Ui.Label("当前出口", 18, true), Ui.Hint("点按策略组可跳到代理页")); exitHeader.Margin = new Thickness(0, 24, 0, 12);
        var viewProxies = Action("查看代理节点", "HomeOpenProxies", () => { Session.Navigate("proxies"); return Task.CompletedTask; }); viewProxies.Style = FluentControls.Style("Ghost"); viewProxies.HorizontalAlignment = HorizontalAlignment.Left; viewProxies.Margin = new Thickness(0, 8, 0, 0);
        var body = Ui.Column(hero, tiles, liveGrid, exitHeader, exits, viewProxies); body.SizeChanged += (_, _) => ApplyResponsiveLayout(body.ActualWidth);
        SetBody(Ui.Scroll(body)); UpdateStatus();
    }
    private static FrameworkElement Legend(string name, string brush)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }; dot.SetResourceReference(Border.BackgroundProperty, brush);
        panel.Children.Add(dot); panel.Children.Add(Ui.Hint(name)); return panel;
    }
    private static Border StatCard(string icon, string title, TextBlock value, TextBlock detail)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal }; var glyph = new FluentIcon(icon, 14) { Margin = new Thickness(0, 0, 6, 0) }; glyph.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush"); label.Children.Add(glyph); label.Children.Add(Ui.Hint(title));
        value.Margin = new Thickness(0, 2, 0, 0); detail.TextWrapping = TextWrapping.NoWrap; detail.TextTrimming = TextTrimming.CharacterEllipsis;
        var content = Ui.Column(label, value, detail); content.VerticalAlignment = VerticalAlignment.Center; var card = Ui.Card(content, 18); card.MinHeight = 102; return card;
    }
    private void ApplyResponsiveLayout(double width)
    {
        bool narrow = width <= 720;
        hero.Padding = new Thickness(width <= 480 ? 16 : 24);
        Grid.SetColumn(heroActions, narrow ? 0 : 2); Grid.SetRow(heroActions, narrow ? 1 : 0); Grid.SetColumnSpan(heroActions, narrow ? 3 : 1);
        heroActions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right; heroActions.Margin = narrow ? new Thickness(-8, 18, 0, 0) : new Thickness(0);
        facts.Visibility = width <= 480 ? Visibility.Collapsed : Visibility.Visible;
        liveGrid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(240);
        Grid.SetColumn(stats, narrow ? 0 : 1); Grid.SetRow(stats, narrow ? 1 : 0);
        stats.Columns = narrow ? 2 : 1; stats.Rows = narrow ? 1 : 2; stats.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(8, 0, 0, 0);
        ((Border)stats.Children[0]).Margin = narrow ? new Thickness(0, 0, 4, 0) : new Thickness(0, 0, 0, 4);
        ((Border)stats.Children[1]).Margin = narrow ? new Thickness(4, 0, 0, 0) : new Thickness(0, 4, 0, 0);
        connectionCount.FontSize = downloadTotal.FontSize = width <= 480 ? 20 : 28;
    }
    private async Task ChangeAsync(string key, string method, object? args = null)
    {
        if (pending.Length > 0) { UpdateStatus(); return; }
        pending = key; UpdateStatus();
        try { await Command(method, args); if (IsActive) await RefreshAsync(); }
        finally { pending = ""; UpdateStatus(); }
    }
    private async Task ChangeTunAsync(bool enabled)
    {
        if (enabled && !Session.Status.Get("service").Bool("installed") && Window.GetWindow(this) is { } owner && !TunConsentDialog.Confirm(owner)) { UpdateStatus(); return; }
        await ChangeAsync("tun", "desktop_set_tun", new { enabled });
    }
    public override async Task NavigateToAsync(NavigationTarget target)
    {
        if (target.Action is "toggle-tun" or "toggleTun") await RunAsync(() => ChangeTunAsync(!Session.Status.Bool("tun")));
    }
    private void UpdateStatus()
    {
        var status = Session.Status; string state = status.Text("phase", fallback: "connecting"); bool running = state == "running";
        if (controller != ControllerIdentity || !running && runtimeReady)
        {
            controller = ControllerIdentity; runtimeReady = false; exitSnapshot = ProxyExitSnapshot.Empty; chart.Clear(); up.Text = down.Text = "—";
            processes = connections = 0; downloaded = uploaded = 0; port = 0; allowLan = false;
        }
        bool busy = state is "starting" or "stopping" or "connecting";
        bool interactive = Session.Ready && !busy && !Session.IsUpdating && pending.Length == 0;
        modes.SetMode(runtimeMode);
        phase.Text = L.T(state switch { "running" => "已连接", "starting" => "正在启动", "stopping" => "正在停止", "error" => "运行异常", "stopped" => "未运行", _ => "正在连接…" });
        phase.SetResourceReference(TextBlock.ForegroundProperty, state == "error" ? "DangerBrush" : "TextPrimaryBrush");
        heroGlyph.SetResourceReference(Border.BackgroundProperty, running ? "AccentBrush" : "SubtleSurfaceBrush");
        heroIcon.Kind = state == "error" ? "warning" : "globe-shield"; heroIcon.Filled = running;
        heroIcon.SetResourceReference(Control.ForegroundProperty, running ? "AccentTextBrush" : state == "error" ? "DangerBrush" : "TextSecondaryBrush");
        heroHalo.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); heroHalo.Visibility = running ? Visibility.Visible : Visibility.Collapsed; heroProgress.IsActive = busy;
        string mode = L.T(modes.Mode switch { "global" => "全局模式", "direct" => "直连模式", _ => "规则模式" });
        string? selected = exitSnapshot.Main?.Exit;
        description.Text = state switch
        {
            "starting" => L.T("首次启动可能需要更长时间，请稍候。"),
            "stopping" => L.T("正在关闭连接并恢复系统代理设置。"),
            "running" => mode + (selected is null ? "" : " · " + exitSnapshot.Main!.Group + " → " + selected + (exitSnapshot.Main.Delay > 0 ? $" · {exitSnapshot.Main.Delay:0} ms" : "")),
            "connecting" => L.T("读取本地内核状态…"),
            _ => L.T("已保存的配置") + (Path.GetFileName(status.Text("configPath")) is { Length: > 0 } name ? " · " + name : "")
        };
        description.ToolTip = description.Text;
        coreVersion.Text = status.Text("coreVersion", fallback: "—"); coreVersion.ToolTip = coreVersion.Text;
        coreVersion.TextTrimming = TextTrimming.CharacterEllipsis; coreVersion.MaxWidth = 180; coreVersion.TextWrapping = TextWrapping.NoWrap;
        proxyAddress.Text = port > 0 ? "127.0.0.1:" + port : "—";
        coreError.Text = status.Text("lastError"); coreError.Visibility = coreError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        start.Visibility = running || state == "stopping" ? Visibility.Collapsed : Visibility.Visible;
        stop.Visibility = running || state == "stopping" ? Visibility.Visible : Visibility.Collapsed;
        // Keep the disabled restart action exposed during preparation so keyboard,
        // screen-reader and automation clients can observe that it is unavailable.
        restart.Visibility = running || state is "starting" or "stopping" ? Visibility.Visible : Visibility.Collapsed;
        start.IsEnabled = interactive && !running; stop.IsEnabled = restart.IsEnabled = interactive && running;
        modes.IsEnabled = interactive && running && runtimeReady;
        proxy.SetState(status.Bool("systemProxy"), !running && status.Bool("systemProxy") ? L.T("内核启动后生效") : status.Bool("systemProxy") ? L.F("端口 {0}", port > 0 ? port.ToString(CultureInfo.CurrentCulture) : "—") : L.T("已关闭"), interactive && status.Get("capabilities").Bool("systemProxy"), pending == "proxy");
        tun.SetState(status.Bool("tun"), status.Bool("tun") ? L.T(running ? "已开启" : "内核启动后生效") : !status.Get("service").Bool("installed") ? L.T("需要安装系统服务") : L.T("已关闭"), interactive && status.Get("capabilities").Bool("tun"), pending == "tun");
        lan.SetState(allowLan, allowLan ? L.F("局域网可用 :{0}", port > 0 ? port.ToString(CultureInfo.CurrentCulture) : "—") : L.T("仅本机"), interactive && running && runtimeReady, pending == "lan");
        autostart.SetState(status.Bool("autostart"), L.T(status.Bool("autostart") ? "登录时启动" : "已关闭"), interactive && status.Get("capabilities").Bool("autostart"), pending == "autostart");
        throughputHint.Text = L.T(running ? "最近 60 秒 · 每秒采样" : "内核未运行");
        exits.SetEmptyText(running ? "配置中没有策略组。" : state is "starting" or "stopping" ? description.Text : "启动内核后，这里会显示实时数据。");
        UpdateStatistics(); exits.Update(running ? exitSnapshot.Summaries.Take(4) : Array.Empty<ProxyExit>());
    }
    private void UpdateStatistics()
    {
        connectionCount.Text = IsRunning ? connections.ToString("N0", CultureInfo.CurrentCulture) : "—";
        processCount.Text = IsRunning ? L.F("来自 {0} 个进程", processes) : L.T("内核未运行");
        downloadTotal.Text = IsRunning ? Ui.Bytes(downloaded) : "—"; uploadTotal.Text = L.F("上传 {0}", Ui.Bytes(uploaded));
    }
    protected override void OnStatusChanged() => UpdateStatus();
    protected override void OnLanguageChanged() => UpdateStatus();
    public override async Task RefreshAsync()
    {
        UpdateStatus(); if (!IsRunning || refreshing) return;
        refreshing = true; string source = ControllerIdentity; var token = PageToken;
        try
        {
            var configTask = Get("configs", token); var proxiesTask = ProxyExitSnapshot.ReadAsync(Session, token);
            await Task.WhenAll(configTask, proxiesTask);
            if (token.IsCancellationRequested || source != ControllerIdentity || !IsRunning) return;
            var config = await configTask; runtimeMode = config.Text("mode", fallback: "rule"); modes.SetMode(runtimeMode); allowLan = config.Bool("allow-lan");
            port = (int)Math.Max(0, config.Number("mixed-port") > 0 ? config.Number("mixed-port") : config.Number("port"));
            runtimeReady = true; controller = source; exitSnapshot = await proxiesTask; UpdateStatus();
        }
        finally { refreshing = false; }
    }
    protected override Task LiveAsync(CancellationToken cancellationToken) => Task.WhenAll(
        WatchStreamAsync("traffic", value =>
        {
            double upload = value.Number("up"), download = value.Number("down");
            up.Text = Ui.Bytes(upload) + "/s"; down.Text = Ui.Bytes(download) + "/s"; chart.Add(upload, download);
        }, cancellationToken),
        WatchStreamAsync("connections", value =>
        {
            var active = value.Get("connections").Items().Where(connection => !connection.Bool("completed")).ToArray(); connections = active.Length;
            processes = active.Select(connection => connection.Get("metadata").Text("processPath", fallback: connection.Get("metadata").Text("sourceIP"))).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).Count();
            uploaded = value.Number("uploadTotal"); downloaded = value.Number("downloadTotal"); UpdateStatistics();
        }, cancellationToken),
        PollAsync(TimeSpan.FromSeconds(3), RefreshAsync, cancellationToken));
}
