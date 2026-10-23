using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop;

internal sealed class SearchWindow : Window
{
    private sealed class ResultItem : ListBoxItem
    {
        internal required Func<Task> Execute { get; init; }
        internal required Func<bool> CanExecute { get; init; }
    }
    private sealed class ResultList : ListBox
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new ResultsPeer(this);
        private sealed class ResultsPeer(ResultList owner) : ListBoxAutomationPeer(owner)
        {
            protected override ItemAutomationPeer CreateItemAutomationPeer(object item) => item is ResultItem result ? new ResultPeer(result, this) : base.CreateItemAutomationPeer(item);
        }
        private sealed class ResultPeer(ResultItem owner, SelectorAutomationPeer selector) : ListBoxItemAutomationPeer(owner, selector), IInvokeProvider
        {
            public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);
            public void Invoke()
            {
                if (!owner.IsEnabled || !owner.CanExecute()) throw new ElementNotEnabledException();
                _ = owner.Dispatcher.BeginInvoke(new Action(async () => await owner.Execute()));
            }
        }
    }

    private sealed record Entry(string Key, string Category, string Icon, string Title, string Detail,
        string Keywords, Func<Task> Run, Func<bool>? CanRun = null)
    {
        internal bool IsEnabled => CanRun?.Invoke() ?? true;
        internal bool Matches(string query) => Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Detail.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Keywords.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private const int MaximumVisibleResults = 8;
    private readonly AppSession session;
    private readonly TextBox search = new() { MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(12, 3, 12, 3), MaxLength = 2048, FontSize = 12 };
    private readonly ResultList results = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
    private readonly TextBlock resultCount = Ui.Muted("");
    private readonly TextBlock state = Ui.Muted("");
    private readonly TextBlock empty = Ui.Text("", 14);
    private readonly List<Entry> entries = new();
    private readonly List<Entry> coreEntries = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly NativeBackdrop backdrop;
    private readonly Grid flyoutSurface;
    private Window? anchorOwner;
    private bool positioning;
    private string coreIdentity = "";
    private string runtimeMode = "";
    private string commandState = "";
    private string? loadError;
    private bool loading;
    private bool closed;
    private bool choosing;

    private bool RuntimeReady => session.Ready && !session.IsUpdating && session.Status.Text("phase") == "running";
    private bool DesktopReady => session.Ready && !session.IsUpdating && session.Status.Text("phase") is not ("connecting" or "starting" or "stopping");
    private string ControllerIdentity => session.Status.Get("controller").Text("url") + "\n" + session.Status.Get("controller").Text("secret");
    private string CommandState => string.Join("|", session.Ready, session.IsUpdating, session.IsDirty, session.Status.Text("phase"), session.Status.Bool("systemProxy"), session.Status.Bool("tun"),
        session.Status.Get("capabilities").ToString());

    public SearchWindow(AppSession session)
    {
        this.session = session;
        L.Bind(this, TitleProperty, "搜索"); this.Id("GlobalSearchWindow");
        Width = 380; Height = 420; MinWidth = 200; MinHeight = 160;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual; ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI, Microsoft YaHei UI"); FontSize = 14;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        search.Id("SearchInput"); results.Id("SearchResults"); resultCount.Id("SearchResultCount");
        state.Id("SearchStatus"); empty.Id("SearchEmpty");
        L.Bind(search, AutomationProperties.NameProperty, "搜索页面、策略组、节点、设置、规则或命令");
        L.Bind(search, ToolTipProperty, "搜索页面、策略组、节点、设置、规则或命令");
        AutomationProperties.SetLiveSetting(resultCount, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(state, AutomationLiveSetting.Polite);
        var layout = new Grid { Margin = new Thickness(8) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        resultCount.Margin = new Thickness(4, 2, 4, 6); layout.Children.Add(resultCount);
        var resultsArea = new Grid(); resultsArea.Children.Add(results);
        empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center;
        empty.Margin = new Thickness(24); resultsArea.Children.Add(empty);
        Grid.SetRow(resultsArea, 1); layout.Children.Add(resultsArea);
        state.Margin = new Thickness(4, 8, 4, 4); state.MaxHeight = 54; state.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetRow(state, 2); layout.Children.Add(state);
        var keys = Ui.Hint("↑ ↓ 选择    Enter 打开    Esc 关闭    Ctrl+T 全部测速", 11);
        keys.Margin = new Thickness(4, 8, 4, 0); Grid.SetRow(keys, 3); layout.Children.Add(keys);
        var border = new Border { Child = layout, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 4, 0, 0) };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        flyoutSurface = new Grid(); flyoutSurface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); flyoutSurface.RowDefinitions.Add(new RowDefinition());
        flyoutSurface.Children.Add(search); Grid.SetRow(border, 1); flyoutSurface.Children.Add(border);
        Content = flyoutSurface;
        backdrop = new NativeBackdrop(this, flyoutSurface, session, transient: true);
        ScrollViewer.SetHorizontalScrollBarVisibility(results, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(results, ScrollBarVisibility.Auto);
        search.TextChanged += (_, _) => { searchDelay.Stop(); searchDelay.Start(); };
        searchDelay.Tick += (_, _) => { searchDelay.Stop(); Filter(); };
        PreviewKeyDown += OnKeyDown;
        SourceInitialized += (_, _) => PositionFlyout();
        Loaded += async (_, _) =>
        {
            AttachOwner(); PositionFlyout(); FocusInput(); FluentMotion.Enter(layout);
            await LoadCoreEntries();
        };
        Deactivated += (_, _) => { if (IsLoaded && !closed) Close(); };
        Closed += (_, _) =>
        {
            closed = true; searchDelay.Stop(); lifetime.Cancel(); lifetime.Dispose();
            session.StatusChanged -= OnStatusChanged; L.Changed -= OnLanguageChanged;
            if (anchorOwner is not null)
            {
                anchorOwner.LocationChanged -= CloseForOwnerChange; anchorOwner.SizeChanged -= CloseForOwnerSize;
                anchorOwner.StateChanged -= CloseForOwnerChange; anchorOwner.IsVisibleChanged -= CloseForOwnerVisibility;
            }
            backdrop.Dispose();
        };
        session.StatusChanged += OnStatusChanged; L.Changed += OnLanguageChanged;
        RebuildEntries();
    }

    public void FocusInput() { Activate(); search.Focus(); search.SelectAll(); }
    private void AttachOwner()
    {
        if (Owner is null || anchorOwner is not null) return;
        anchorOwner = Owner;
        anchorOwner.LocationChanged += CloseForOwnerChange; anchorOwner.SizeChanged += CloseForOwnerSize;
        anchorOwner.StateChanged += CloseForOwnerChange; anchorOwner.IsVisibleChanged += CloseForOwnerVisibility;
    }
    private void CloseForOwnerChange(object? sender, EventArgs e) { if (!closed) Close(); }
    private void CloseForOwnerSize(object sender, SizeChangedEventArgs e) { if (!closed) Close(); }
    private void CloseForOwnerVisibility(object sender, DependencyPropertyChangedEventArgs e) { if (e.NewValue is false && !closed) Close(); }
    private void PositionFlyout()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (closed || positioning || Owner is null || handle == IntPtr.Zero) return;
        positioning = true;
        try
        {
            var anchor = Owner.FindName("SearchButton") as FrameworkElement ?? Owner;
            Point origin = anchor.PointToScreen(new Point());
            var dpi = VisualTreeHelper.GetDpi(anchor);
            double scale = dpi.DpiScaleX, verticalScale = dpi.DpiScaleY;
            var work = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)origin.X, (int)origin.Y)).WorkingArea;
            double availableWidth = Math.Max(200, work.Width / scale - 16);
            double width = Math.Min(Math.Clamp(anchor.ActualWidth, 320, 680), availableWidth);
            double inputHeight = Math.Max(32, Math.Min(40, anchor.ActualHeight));
            double left = Math.Clamp(origin.X, work.Left + 8 * scale, Math.Max(work.Left + 8 * scale, work.Right - (width + 8) * scale));
            double top = Math.Clamp(origin.Y, work.Top + 8 * verticalScale, Math.Max(work.Top + 8 * verticalScale, work.Bottom - 168 * verticalScale));
            double wantedHeight = inputHeight + 4 + 32 + Math.Max(1, results.Items.Count) * 54 + 32 + 18 + (state.Visibility == Visibility.Visible ? 54 : 0);
            double height = Math.Min(wantedHeight, Math.Max(160, (work.Bottom - top) / verticalScale - 8));
            search.Width = Math.Min(width, anchor.ActualWidth > 0 ? anchor.ActualWidth : width);
            search.Height = inputHeight; search.HorizontalAlignment = HorizontalAlignment.Left;
            search.Margin = new Thickness(Math.Clamp((origin.X - left) / scale, 0, Math.Max(0, width - search.Width)), 0, 0, 0);
            Width = width; Height = height;
            // Physical coordinates keep the flyout on the owner's monitor at mixed DPI settings.
            _ = SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(left), (int)Math.Round(top), (int)Math.Round(width * scale), (int)Math.Round(height * verticalScale), 0x0014);
        }
        finally { positioning = false; }
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    private void RebuildEntries()
    {
        commandState = CommandState;
        entries.Clear();
        bool running = session.Status.Text("phase") == "running";
        AddCommand(running ? "cmd-stop" : "cmd-start", running ? "停止内核" : "启动内核", running ? "stop" : "play",
            running ? "desktop_stop" : "desktop_start", "core start stop 内核 启动 停止", () => DesktopReady,
            L.T(running ? "运行中" : "已停止"));
        entries.Add(new Entry("cmd-proxy", "command", "nav-proxies", L.T(session.Status.Bool("systemProxy") ? "关闭系统代理" : "开启系统代理"), "Windows",
            "system proxy 系统代理", async () => await session.CommandAsync("desktop_set_system_proxy", new { enabled = !session.Status.Bool("systemProxy") }),
            () => DesktopReady && session.Status.Get("capabilities").Bool("systemProxy")));
        AddNavigation("cmd-tun", "command", "shield-lock", session.Status.Bool("tun") ? "关闭 TUN 模式" : "开启 TUN 模式", "TUN",
            "tun virtual adapter 虚拟网卡", new("home", Action: "toggle-tun"), () => DesktopReady && !session.IsDirty && session.Status.Get("capabilities").Bool("tun"));
        AddNavigation("cmd-import", "command", "arrow-import", "导入配置", "", "import yaml subscription 导入 订阅 配置", new("profiles", Action: "import"), () => DesktopReady && session.Status.Get("capabilities").Bool("configImport"));
        AddNavigation("cmd-test", "command", "speed", "全部测速", "Ctrl+T", "latency speed test 测速 延迟", new("proxies", Action: "test-all"), () => RuntimeReady);
        AddNavigation("cmd-edit", "command", "edit", "编辑配置", "YAML", "yaml config editor 编辑 配置", new("profiles", Action: "edit"), () => session.Ready && !session.IsUpdating);
        AddCommand("cmd-restart", "重启内核", "arrow-sync", "desktop_restart", "core restart 重启 内核", () => DesktopReady && !session.IsDirty);
        foreach (string mode in new[] { "rule", "global", "direct" })
        {
            string title = mode switch { "rule" => "切换规则模式", "global" => "切换全局模式", _ => "切换直连模式" };
            string detail = runtimeMode == mode ? L.T("当前模式") : L.T(mode switch { "rule" => "规则", "global" => "全局", _ => "直连" });
            entries.Add(new Entry("mode-" + mode, "command", "arrow-swap", L.T(title), detail, "mode " + mode + " " + title,
                async () => await session.CommandAsync("desktop_patch_config", new { patch = new Dictionary<string, object> { ["mode"] = mode } }),
                () => RuntimeReady && !session.IsDirty));
        }
        entries.Add(new Entry("cmd-config-folder", "command", "folder-open", L.T("打开配置文件夹"), "", "config directory folder 配置 目录 文件夹",
            async () => await session.CommandAsync("desktop_open_directory", new { kind = "config" }), () => session.Ready && !session.IsUpdating));
        entries.Add(new Entry("cmd-log-folder", "command", "nav-logs", L.T("打开日志目录"), "", "logs directory folder 日志 目录 文件夹",
            async () => await session.CommandAsync("desktop_open_directory", new { kind = "logs" }), () => session.Ready && !session.IsUpdating));
        foreach (var (page, title, icon, keywords) in new[]
        {
            ("home", "主页", "nav-home", "home overview dashboard 首页 概览"), ("proxies", "代理", "nav-proxies", "proxies policy group node 代理 策略组 节点"),
            ("connections", "连接", "nav-connections", "connections traffic 连接 流量"), ("dns", "DNS", "nav-dns", "dns lookup domain 域名 查询"),
            ("rules", "规则", "nav-rules", "rules match 规则 匹配"), ("logs", "日志", "nav-logs", "logs errors 日志 错误"),
            ("profiles", "配置", "nav-profiles", "profiles config subscription yaml 配置 订阅"), ("settings", "设置", "nav-settings", "settings preferences 设置 偏好")
        }) AddNavigation("page-" + page, "page", icon, title, L.T("页面"), keywords, new(page));
        entries.AddRange(coreEntries);
        foreach (var target in SearchTargets.Settings)
            AddNavigation("setting-" + target.Id, "setting", "setting", target.Title, L.T(target.Page == "profiles" ? "配置" : "设置"), target.Keywords,
                new(target.Page, Target: target.Id));
        Filter(preserveSelection: true);
    }

    private void AddCommand(string key, string title, string icon, string method, string keywords, Func<bool> enabled, string detail = "") =>
        entries.Add(new Entry(key, "command", icon, L.T(title), detail, keywords, async () => await session.CommandAsync(method), enabled));

    private void AddNavigation(string key, string category, string icon, string title, string detail, string keywords, NavigationTarget target, Func<bool>? enabled = null) =>
        entries.Add(new Entry(key, category, icon, L.T(title), detail, keywords, () => { session.Navigate(target); return Task.CompletedTask; }, enabled));

    private void Filter(bool preserveSelection = false)
    {
        if (closed) return;
        string? selectedKey = preserveSelection && results.SelectedItem is ListBoxItem { Tag: Entry previous } ? previous.Key : null;
        string query = search.Text.Trim();
        var candidates = query.Length == 0 ? entries.Where(entry => entry.Category == "command").Take(6).ToArray()
            : entries.Where(entry => entry.Matches(query)).OrderByDescending(entry => entry.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        results.Items.Clear();
        foreach (Entry entry in candidates.Take(MaximumVisibleResults)) results.Items.Add(RenderResult(entry, query));
        int selected = 0;
        if (selectedKey is not null)
            for (int index = 0; index < results.Items.Count; index++)
                if (((ListBoxItem)results.Items[index]).Tag is Entry entry && entry.Key == selectedKey) { selected = index; break; }
        if (results.Items.Count > 0) results.SelectedIndex = selected;
        resultCount.Text = query.Length == 0 ? L.T("快捷命令 · 输入关键词搜索全部内容")
            : candidates.Length > MaximumVisibleResults ? L.F("{0} 个结果，显示前 {1} 项；请细化关键词", candidates.Length, MaximumVisibleResults)
            : L.F("{0} 个结果", candidates.Length);
        empty.Text = L.F("未找到与“{0}”匹配的内容", query);
        empty.Visibility = candidates.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        state.Text = loading ? L.T("正在读取策略组、节点和规则…")
            : loadError is not null ? L.F("部分内容未能读取：{0}。按 F5 重试。", loadError)
            : !RuntimeReady ? L.T("启动内核后，可搜索策略组、节点和规则。页面与设置始终可搜索。")
            : L.T("选择结果可定位到页面、控件或规则；节点结果会定位到节点。");
        state.Visibility = loading || loadError is not null || !RuntimeReady ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) PositionFlyout();
    }

    private ListBoxItem RenderResult(Entry entry, string query)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FluentIcon(entry.Icon, 16) { VerticalAlignment = VerticalAlignment.Center });
        var title = Ui.Text("", 14, true); title.TextTrimming = TextTrimming.CharacterEllipsis; title.TextWrapping = TextWrapping.NoWrap;
        AppendHighlight(title, entry.Title, query);
        var detail = Ui.Muted(entry.Detail); detail.TextWrapping = TextWrapping.NoWrap; detail.TextTrimming = TextTrimming.CharacterEllipsis;
        detail.Margin = new Thickness(0, 4, 0, 0); detail.Visibility = entry.Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var text = Ui.Column(title, detail); Grid.SetColumn(text, 1); row.Children.Add(text);
        string category = L.T(entry.Category switch { "command" => "命令", "page" => "页面", "group" => "策略组", "node" => "节点", "setting" => "设置", _ => "规则" });
        var label = Ui.Muted(category, 11); label.Margin = new Thickness(16, 0, 0, 0); Grid.SetColumn(label, 2); row.Children.Add(label);
        var item = new ResultItem { Execute = () => RunEntry(entry), CanExecute = () => entry.IsEnabled && !closed && !choosing,
            Content = row, Tag = entry, Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 2, 0, 2), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Opacity = entry.IsEnabled ? 1 : 0.5, ToolTip = entry.IsEnabled ? null : L.T("当前状态无法执行此命令，请等待正在进行的操作完成。") }.Id("SearchResult_" + entry.Key);
        AutomationProperties.SetName(item, entry.Title + " · " + category + (entry.Detail.Length > 0 ? " · " + entry.Detail : ""));
        AutomationProperties.SetHelpText(item, entry.IsEnabled ? "" : L.T("当前状态无法执行此命令，请等待正在进行的操作完成。"));
        item.MouseEnter += (_, _) => { results.SelectedItem = item; };
        item.PreviewMouseLeftButtonUp += async (_, e) => { e.Handled = true; await RunEntry(entry); };
        return item;
    }

    private static void AppendHighlight(TextBlock text, string title, string query)
    {
        int start = query.Length == 0 ? -1 : title.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (start < 0) { text.Inlines.Add(new Run(title)); return; }
        text.Inlines.Add(new Run(title[..start]));
        var highlight = new Run(title.Substring(start, query.Length));
        highlight.SetResourceReference(TextElement.BackgroundProperty, "AccentBrush");
        highlight.SetResourceReference(TextElement.ForegroundProperty, "AccentTextBrush");
        text.Inlines.Add(highlight); text.Inlines.Add(new Run(title[(start + query.Length)..]));
    }

    private async Task LoadCoreEntries()
    {
        if (closed || loading || !RuntimeReady) return;
        string identity = ControllerIdentity;
        loading = true; loadError = null; Filter(preserveSelection: true);
        try
        {
            Task<JsonElement> proxies = session.ApiAsync(HttpMethod.Get, "proxies", cancellationToken: lifetime.Token);
            Task<JsonElement> rules = session.ApiAsync(HttpMethod.Get, "rules", cancellationToken: lifetime.Token);
            Task<JsonElement> configs = session.ApiAsync(HttpMethod.Get, "configs", cancellationToken: lifetime.Token);
            // Keep readable categories available even when a different endpoint fails.
            try { await Task.WhenAll(proxies, rules, configs); } catch (Exception) when (!lifetime.IsCancellationRequested) { }
            if (closed || lifetime.IsCancellationRequested || identity != ControllerIdentity || !RuntimeReady) return;
            coreEntries.Clear(); coreIdentity = identity;
            if (configs.IsCompletedSuccessfully) runtimeMode = configs.Result.Text("mode");
            if (proxies.IsCompletedSuccessfully)
            {
                foreach (var proxy in proxies.Result.Get("proxies").Fields())
                {
                    string name = proxy.Name; bool group = proxy.Value.Get("all").ValueKind == JsonValueKind.Array;
                    string detail = proxy.Value.Text("type");
                    if (group) detail += " · " + proxy.Value.Text("now", fallback: "—");
                    else
                    {
                        double delay = proxy.Value.Get("history").Items().LastOrDefault().Number("delay");
                        if (delay > 0) detail += " · " + delay.ToString("0.#") + " ms";
                    }
                    coreEntries.Add(new Entry((group ? "group-" : "node-") + name, group ? "group" : "node", group ? "nav-proxies" : "globe", name, detail,
                        (group ? "group policy 策略组 " : "node proxy 节点 ") + detail,
                        () => { session.Navigate(new NavigationTarget("proxies", Target: name, Action: group ? "focus-group" : "focus-node")); return Task.CompletedTask; }, () => RuntimeReady));
                }
            }
            if (rules.IsCompletedSuccessfully)
            {
                int index = 0;
                foreach (var rule in rules.Result.Get("rules").Items())
                {
                    string payload = rule.Text("payload"); int ruleIndex = index++;
                    if (payload.Length == 0) continue;
                    string detail = rule.Text("type") + " → " + rule.Text("proxy");
                    coreEntries.Add(new Entry("rule-" + ruleIndex, "rule", "nav-rules", payload, detail, "rule 规则 " + rule.Text("proxy"),
                        () => { session.Navigate(new NavigationTarget("rules", Query: payload)); return Task.CompletedTask; }, () => RuntimeReady));
                }
            }
            loadError = new[] { proxies, rules, configs }.FirstOrDefault(task => task.IsFaulted)?.Exception?.GetBaseException().Message;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) loadError = error.Message; }
        finally
        {
            loading = false;
            if (!closed) RebuildEntries();
        }
    }

    private async void OnStatusChanged(object? sender, EventArgs e)
    {
        if (closed) return;
        bool changed = commandState != CommandState;
        if ((!RuntimeReady || coreIdentity != ControllerIdentity) && coreIdentity.Length > 0)
        { coreEntries.Clear(); runtimeMode = ""; coreIdentity = ""; changed = true; }
        if (changed) RebuildEntries();
        if (RuntimeReady && coreIdentity.Length == 0) await LoadCoreEntries();
    }
    private void OnLanguageChanged() => RebuildEntries();

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; search.Focus(); search.SelectAll(); return; }
        if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (entries.FirstOrDefault(entry => entry.Key == "cmd-test") is { } test) await RunEntry(test);
            return;
        }
        if (e.Key == Key.F5) { e.Handled = true; await LoadCoreEntries(); return; }
        if (e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            if (searchDelay.IsEnabled) { searchDelay.Stop(); Filter(); }
            if (results.Items.Count == 0) return;
            int step = e.Key == Key.Down ? 1 : -1;
            results.SelectedIndex = (results.SelectedIndex + step + results.Items.Count) % results.Items.Count;
            results.ScrollIntoView(results.SelectedItem); return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (searchDelay.IsEnabled) { searchDelay.Stop(); Filter(); }
            if (results.SelectedItem is ListBoxItem { Tag: Entry entry }) await RunEntry(entry);
        }
    }

    private async Task RunEntry(Entry entry)
    {
        if (choosing || closed) return;
        if (!entry.IsEnabled) { state.Text = L.T("当前状态无法执行此命令，请等待正在进行的操作完成。"); return; }
        choosing = true;
        // Close the palette before navigation so the destination receives keyboard focus.
        Close();
        try { await entry.Run(); }
        catch (Exception error) { session.Notify(error.Message, true); }
    }

}
