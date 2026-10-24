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
    private readonly TextBlock coreVersion = Ui.Text("—", 13);
    private readonly TextBlock proxyAddress = Ui.Text("—", 13);
    private readonly TextBlock coreError = Ui.Text("");
    private readonly TextBlock up = Ui.Text("—", 18, true);
    private readonly TextBlock down = Ui.Text("—", 18, true);
    private readonly TextBlock connectionCount = Ui.Text("—", 28, true);
    private readonly TextBlock processCount = Ui.Muted("");
    private readonly TextBlock downloadTotal = Ui.Text("—", 28, true);
    private readonly TextBlock uploadTotal = Ui.Muted("");
    private readonly TextBlock throughputHint = Ui.Muted("");
    private readonly TrafficChart chart = new() { Height = 160, ShowNowLabel = false };
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
    private readonly Grid emblem = new() { Width = 76, Height = 76, Margin = new Thickness(-6, -6, 14, -6) };
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

    public HomePage(AppSession session) : base(session, "home", "主页", "")
    {
        phase.Id("CoreStatus"); chart.Id("TrafficChart"); connectionCount.Id("HomeConnections"); downloadTotal.Id("HomeTrafficTotal");
        description.TextWrapping = TextWrapping.NoWrap; description.TextTrimming = TextTrimming.CharacterEllipsis; description.Margin = new Thickness(0, 2, 0, 0);
        start = Action("启动内核", "StartCore", () => RunUserActionAsync(() => ChangeAsync("core", "desktop_start")), true);
        stop = Action("停止内核", "StopCore", () => RunUserActionAsync(() => ChangeAsync("core", "desktop_stop")));
        restart = Action("重启内核", "RestartCore", () => RunUserActionAsync(() => ChangeAsync("core", "desktop_restart")));
        FluentControls.Primary(start); stop.Style = restart.Style = FluentControls.Style("Button");
        start.Content = FluentControls.ButtonContent("play", "启动内核"); stop.Content = FluentControls.ButtonContent("stop", "停止内核"); restart.Content = new FluentIcon("update", 16);
        restart.Width = restart.Height = 32; restart.Padding = new Thickness(0); L.Bind(restart, ToolTipProperty, "重启内核");
        heroActions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { start, stop, restart }) { button.MinHeight = button.Height = 32; button.Margin = new Thickness(0); heroActions.Children.Add(button); }
        restart.Margin = new Thickness(8, 0, 0, 0);
        heroMain.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); heroMain.ColumnDefinitions.Add(new ColumnDefinition()); heroMain.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heroMain.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); heroMain.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ShellVisuals.SetDuration(heroGlyph, 250); ShellVisuals.SetDuration(heroIcon, 250);
        heroGlyph.Child = heroIcon; emblem.Children.Add(heroGlyph); emblem.Children.Add(heroHalo); emblem.Children.Add(heroProgress); heroMain.Children.Add(emblem);
        var status = Ui.Column(phase, description); status.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(status, 1); heroMain.Children.Add(status);
        Grid.SetColumn(heroActions, 2); heroMain.Children.Add(heroActions);
        coreError.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush"); coreError.Margin = new Thickness(0, 12, 0, 0); coreError.Visibility = Visibility.Collapsed; coreError.Id("HomeCoreError");
        modes.Selected += async value => await RunUserActionAsync(() => ChangeAsync("mode", "desktop_patch_config", new { patch = new { mode = value } }));
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; var modeLabel = Ui.Hint("出站模式", 14); modeLabel.Margin = new Thickness(0, 0, 12, 0); modeLabel.VerticalAlignment = VerticalAlignment.Center; modeRow.Children.Add(modeLabel); modeRow.Children.Add(modes);
        coreVersion.FontFamily = proxyAddress.FontFamily = new FontFamily("Cascadia Mono, Cascadia Code, Consolas");
        coreVersion.LineHeight = proxyAddress.LineHeight = 18.59;
        var coreLabel = Ui.Hint("内核"); var proxyLabel = Ui.Hint("本机代理");
        coreLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); proxyLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        coreLabel.LineHeight = proxyLabel.LineHeight = 16;
        facts = Ui.Row(Ui.Column(coreLabel, coreVersion), Ui.Column(proxyLabel, proxyAddress));
        foreach (FrameworkElement fact in facts.Children) fact.Margin = new Thickness(0);
        ((FrameworkElement)facts.Children[0]).Margin = new Thickness(0, 0, 24, 0);
        var footContent = new HomeFlowRow(24, 12); footContent.Children.Add(modeRow); footContent.Children.Add(facts);
        var foot = new Border { Child = footContent, Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(0, 16, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) }; foot.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        hero = Ui.Card(Ui.Column(heroMain, coreError, foot), 24); hero.Margin = new Thickness(0);
        proxy = new QuickSettingTile("nav-proxies", "系统代理", "systemProxy", () => Session.Navigate(new NavigationTarget("settings", "set-system-proxy")));
        tun = new QuickSettingTile("shield-lock", "TUN 模式", "tun", () => Session.Navigate(new NavigationTarget("settings", "set-tun")));
        lan = new QuickSettingTile("wifi", "局域网连接", "allowLan", () => Session.Navigate(new NavigationTarget("settings", "set-lan")));
        autostart = new QuickSettingTile("power", "开机启动", "autostart");
        proxy.Changed += async enabled => await RunUserActionAsync(() => ChangeAsync("proxy", "desktop_set_system_proxy", new { enabled }));
        tun.Changed += async enabled => await RunUserActionAsync(() => ChangeTunAsync(enabled));
        autostart.Changed += async enabled => await RunUserActionAsync(() => ChangeAsync("autostart", "desktop_set_autostart", new { enabled }));
        lan.Changed += async enabled => await RunUserActionAsync(() => ChangeAsync("lan", "desktop_patch_config", new { patch = new Dictionary<string, object> { ["allow-lan"] = enabled } }));
        var tiles = new ResponsiveTilePanel { Margin = new Thickness(0, 8, 0, 0) }; foreach (var tile in new[] { proxy, tun, lan, autostart }) tiles.Children.Add(tile);
        var rates = new StackPanel { Orientation = Orientation.Horizontal };
        var downRate = Ui.Column(Legend("下行", "AccentBrush"), down); rates.Children.Add(downRate);
        var upRate = Ui.Column(Legend("上行", "ChartUpBrush"), up); upRate.Margin = new Thickness(24, 0, 0, 0); rates.Children.Add(upRate);
        foreach (StackPanel reading in rates.Children) foreach (FrameworkElement item in reading.Children) item.HorizontalAlignment = HorizontalAlignment.Right;
        phase.FontFamily = up.FontFamily = down.FontFamily = connectionCount.FontFamily = downloadTotal.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI Variable, Segoe UI, Microsoft YaHei UI, Microsoft YaHei");
        phase.LineHeight = 28; up.LineHeight = down.LineHeight = 24; description.LineHeight = 20;
        // The reference's smaller inline unit inherits a 24px line-height,
        // extending its Windows rate line box to 28px without moving the digits.
        up.Margin = down.Margin = new Thickness(0, 0, 0, 4);
        ByteReading.SetRate(up, 0); ByteReading.SetRate(down, 0);
        throughputHint.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        var chartHead = new HomeFlowRow(24, 8) { AlignTop = true };
        var chartTitle = Ui.Label("实时吞吐", 14, true); chartTitle.LineHeight = 20;
        chartHead.Children.Add(Ui.Column(chartTitle, throughputHint)); chartHead.Children.Add(rates);
        chart.Margin = new Thickness(0, 8, 0, 0); chartCard = Ui.Card(Ui.Column(chartHead, chart), 20); chartCard.Padding = new Thickness(20, 16, 20, 12); chartCard.Margin = new Thickness(0);
        var connectionCard = StatCard("nav-connections", "活动连接", connectionCount, processCount); connectionCard.Margin = new Thickness(0, 0, 0, 4);
        var trafficCard = StatCard("down-arrow-o", "本次运行流量", downloadTotal, uploadTotal); trafficCard.Margin = new Thickness(0, 4, 0, 0);
        stats.Children.Add(connectionCard); stats.Children.Add(trafficCard);
        liveGrid.Margin = new Thickness(0, 8, 0, 0); liveGrid.ColumnDefinitions.Add(new ColumnDefinition());
        // CSS grid keeps the 8px gap outside the fixed 240px statistics column.
        liveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) }); liveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        liveGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); liveGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        liveGrid.Children.Add(chartCard); Grid.SetColumn(stats, 2); liveGrid.Children.Add(stats);
        exits.Selected += (exit, identity) => { ConnectedNavigation.Prepare(identity, exit.Group); Session.Navigate(new NavigationTarget("proxies", Target: exit.Group, Action: "focus-group")); };
        var exitHeader = new WrapPanel { Margin = new Thickness(0, 28, 0, 8), VerticalAlignment = VerticalAlignment.Center };
        // Preserve the existing accessible navigation command on the section
        // title, without adding a control absent from the supplied composition.
        var viewProxies = Action("当前出口", "HomeOpenProxies", () => { Session.Navigate("proxies"); return Task.CompletedTask; });
        viewProxies.Template = HeaderCommandTemplate(); viewProxies.Content = Ui.Label("当前出口", 14, true); viewProxies.Padding = new Thickness(0); viewProxies.Margin = new Thickness(0, 0, 12, 0); viewProxies.MinHeight = 0; viewProxies.Height = 20; viewProxies.BorderThickness = new Thickness(0);
        var exitHint = Ui.Hint("点按策略组可跳到代理页"); exitHint.VerticalAlignment = VerticalAlignment.Center;
        exitHeader.Children.Add(viewProxies); exitHeader.Children.Add(exitHint);
        var body = Ui.Column(hero, tiles, liveGrid, exitHeader, exits);
        SizeChanged += (_, _) => ApplyResponsiveLayout(PageContainerWidth);
        TransitionSurface.SizeChanged += (_, args) => { if (args.WidthChanged) ApplyResponsiveLayout(PageContainerWidth); };
        Loaded += (_, _) => ApplyResponsiveLayout(PageContainerWidth);
        SetScrollableBody(body).Id("HomeScrollViewer"); UpdateStatus();
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
        value.Margin = new Thickness(0, 2, 0, 2); value.LineHeight = 36; detail.TextWrapping = TextWrapping.NoWrap; detail.TextTrimming = TextTrimming.CharacterEllipsis;
        detail.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        var content = Ui.Column(label, value, detail); content.VerticalAlignment = VerticalAlignment.Center; var card = Ui.Card(content, 18); card.Padding = new Thickness(18, 14, 18, 14); card.Margin = new Thickness(0); return card;
    }
    private void ApplyResponsiveLayout(double width)
    {
        bool narrow = width <= 720;
        hero.Padding = width <= 480 ? new Thickness(16) : new Thickness(24, 20, 24, 20);
        double glyphSize = width <= 480 ? 48 : 64;
        heroGlyph.Width = heroGlyph.Height = glyphSize;
        heroGlyph.CornerRadius = new CornerRadius(glyphSize / 2);
        heroHalo.Width = heroHalo.Height = heroProgress.Width = heroProgress.Height = emblem.Width = emblem.Height = glyphSize + 12;
        heroHalo.CornerRadius = new CornerRadius((glyphSize + 12) / 2);
        Grid.SetColumn(heroActions, narrow ? 0 : 2); Grid.SetRow(heroActions, narrow ? 1 : 0); Grid.SetColumnSpan(heroActions, narrow ? 3 : 1);
        heroActions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right; heroActions.Margin = narrow ? new Thickness(0, 20, 0, 0) : new Thickness(20, 0, 0, 0);
        facts.Visibility = width <= 480 ? Visibility.Collapsed : Visibility.Visible;
        liveGrid.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 8);
        liveGrid.ColumnDefinitions[2].Width = new GridLength(narrow ? 0 : 240);
        Grid.SetColumn(stats, narrow ? 0 : 2); Grid.SetRow(stats, narrow ? 1 : 0);
        stats.Columns = narrow ? 2 : 1; stats.Rows = narrow ? 1 : 2; stats.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        ((Border)stats.Children[0]).Margin = narrow ? new Thickness(0, 0, 4, 0) : new Thickness(0, 0, 0, 4);
        ((Border)stats.Children[1]).Margin = narrow ? new Thickness(4, 0, 0, 0) : new Thickness(0, 4, 0, 0);
        connectionCount.FontSize = downloadTotal.FontSize = width <= 480 ? 20 : 28;
        connectionCount.LineHeight = downloadTotal.LineHeight = width <= 480 ? 28 : 36;
    }
    private async Task RunUserActionAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Session.Notify(error.Message, true); }
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
        if (pending.Length > 0) { UpdateStatus(); return; }
        if (enabled && !Session.Status.Get("service").Bool("installed") && Window.GetWindow(this) is { } owner)
        {
            await TunConsentDialog.Run(owner, () => ChangeAsync("tun", "desktop_set_tun", new { enabled }), error => Session.Notify(error, true),
                onSuccess: () => Session.NotifySuccess("TUN 模式已开启"));
            UpdateStatus(); return;
        }
        await ChangeAsync("tun", "desktop_set_tun", new { enabled });
    }
    public override async Task NavigateToAsync(NavigationTarget target)
    {
        if (target.Action is "toggle-tun" or "toggleTun") await RunUserActionAsync(() => ChangeTunAsync(!Session.Status.Bool("tun")));
    }
    private void UpdateStatus()
    {
        var status = Session.Status; string state = status.Text("phase", fallback: "connecting"); bool running = state == "running";
        if (controller != ControllerIdentity || !running && runtimeReady)
        {
            controller = ControllerIdentity; runtimeReady = false; exitSnapshot = ProxyExitSnapshot.Empty; chart.Clear(); ByteReading.SetRate(up, 0); ByteReading.SetRate(down, 0);
            processes = connections = 0; downloaded = uploaded = 0; port = 0; allowLan = false;
        }
        bool busy = state is "starting" or "stopping" or "connecting";
        bool interactive = Session.Ready && !busy && !Session.IsUpdating && Session.BusyCommand is null && pending.Length == 0;
        modes.SetMode(runtimeMode);
        phase.Text = L.T(state switch { "running" => "已连接", "starting" => "正在启动", "stopping" => "正在停止", "error" => "运行异常", "stopped" => "未运行", _ => "正在连接…" });
        phase.SetResourceReference(TextBlock.ForegroundProperty, state == "error" ? "DangerBrush" : "TextPrimaryBrush");
        heroGlyph.SetResourceReference(ShellVisuals.BackgroundProperty, running ? "AccentBrush" : state == "error" ? "CriticalBackgroundBrush" : "ControlAltBrush");
        heroIcon.Kind = state == "error" ? "warning" : "globe-shield"; heroIcon.Filled = running;
        heroIcon.SetResourceReference(ShellVisuals.ForegroundProperty, running ? "AccentTextBrush" : state == "error" ? "DangerBrush" : "TextSecondaryBrush");
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
        string version = status.Text("coreVersion", fallback: "—");
        coreVersion.Text = ShortVersion(version); coreVersion.ToolTip = version;
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
    private static string ShortVersion(string value)
    {
        if (value.StartsWith("mihomo-optimized-", StringComparison.Ordinal)) return value["mihomo-optimized-".Length..];
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.FirstOrDefault(word => char.IsDigit(word[0]) || word[0] == 'v' && word.Length > 1 && char.IsDigit(word[1])) ?? value;
    }
    private void UpdateStatistics()
    {
        connectionCount.Text = IsRunning ? connections.ToString("N0", CultureInfo.CurrentCulture) : "—";
        processCount.Text = IsRunning ? L.F("来自 {0} 个进程", processes) : L.T("内核未运行");
        downloadTotal.Text = FormatTraffic(downloaded); uploadTotal.Text = L.F("上传 {0}", FormatTraffic(uploaded));
    }
    private static string FormatTraffic(double bytes)
    {
        // Home uses helper.formatTraffic: two fractional digits, truncated before formatting.
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        bytes = double.IsFinite(bytes) ? Math.Max(0, bytes) : 0;
        int exponent = bytes > 0 ? (int)Math.Floor(Math.Log(bytes) / Math.Log(1024)) : 0;
        double value = Math.Floor(bytes / Math.Pow(1024, exponent) * 100) / 100;
        string unit = exponent >= 0 && exponent < units.Length ? units[exponent] : "";
        return value.ToString("F2", CultureInfo.InvariantCulture) + " " + unit;
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
            ByteReading.SetRate(up, upload); ByteReading.SetRate(down, download); chart.Add(upload, download);
        }, cancellationToken),
        WatchStreamAsync("connections", value =>
        {
            var active = value.Get("connections").Items().Where(connection => !connection.Bool("completed")).ToArray(); connections = active.Length;
            processes = active.Select(connection => connection.Get("metadata").Text("processPath", fallback: connection.Get("metadata").Text("sourceIP"))).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).Count();
            uploaded = value.Number("uploadTotal"); downloaded = value.Number("downloadTotal"); UpdateStatistics();
        }, cancellationToken),
        PollAsync(TimeSpan.FromSeconds(3), RefreshAsync, cancellationToken));

    private static ControlTemplate HeaderCommandTemplate() => (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button"><ContentPresenter HorizontalAlignment="Left" VerticalAlignment="Center"/></ControlTemplate>
        """);

    /// <summary>The original two-column flex row: space-between, then one wrap with a fixed row gap.</summary>
    private sealed class HomeFlowRow(double gap, double rowGap) : Panel
    {
        private bool wrap;
        public bool AlignTop { get; init; }
        protected override Size MeasureOverride(Size availableSize)
        {
            var visible = InternalChildren.Cast<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToArray();
            foreach (UIElement child in visible) child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            double width = visible.Sum(child => child.DesiredSize.Width) + Math.Max(0, visible.Length - 1) * gap;
            wrap = width > availableSize.Width;
            double height = wrap ? visible.Sum(child => child.DesiredSize.Height) + Math.Max(0, visible.Length - 1) * rowGap : visible.Select(child => child.DesiredSize.Height).DefaultIfEmpty().Max();
            return new Size(Math.Min(width, availableSize.Width), height);
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            var visible = InternalChildren.Cast<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToArray();
            double y = 0, x = 0;
            foreach (UIElement child in visible)
            {
                if (wrap) { child.Arrange(new Rect(0, y, Math.Min(child.DesiredSize.Width, finalSize.Width), child.DesiredSize.Height)); y += child.DesiredSize.Height + rowGap; }
                else { child.Arrange(new Rect(x, AlignTop ? 0 : (finalSize.Height - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height)); x = finalSize.Width - visible[^1].DesiredSize.Width; }
            }
            return finalSize;
        }
    }
}
