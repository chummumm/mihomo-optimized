using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class ConnectionsPage : NativePage
{
    private readonly ObservableCollection<ConnectionRow> rows = [];
    private readonly Dictionary<string, ConnectionRow> byId = new(StringComparer.Ordinal);
    private readonly DataGrid table = Ui.Table(("目标", nameof(ConnectionRow.Host), 240), ("网络", nameof(ConnectionRow.Network), 85), ("入口", nameof(ConnectionRow.Type), 100), ("出口", nameof(ConnectionRow.Chain), 220), ("规则", nameof(ConnectionRow.Rule), 160), ("进程", nameof(ConnectionRow.Process), 130), ("速度", nameof(ConnectionRow.Rate), 190), ("上传", nameof(ConnectionRow.Upload), 100), ("下载", nameof(ConnectionRow.Download), 100), ("来源地址", nameof(ConnectionRow.SourceAddress), 140), ("建立时间", nameof(ConnectionRow.Started), 150), ("状态", nameof(ConnectionRow.State), 80));
    private readonly TextBox search = Ui.SearchBox("筛选域名、进程、来源、规则或出口", "ConnectionSearch");
    private readonly TrafficChart chart = new() { Height = 120, MinHeight = 100 };
    private readonly TextBlock traffic = Ui.Hint("等待流量采样");
    private readonly DataGrid aggregates = Ui.Table(("分组", nameof(AggregateRow.Name), -2), ("连接", nameof(AggregateRow.Count), 90), ("上传", nameof(AggregateRow.Upload), -1), ("下载", nameof(AggregateRow.Download), -1));
    private readonly ComboBox groupBy = new() { Width = 120, Height = 34 };
    private readonly TextBlock count = Ui.Hint("等待内核连接");
    private readonly CheckBox keepClosed = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Queue<string> closedOrder = new();
    private int discarded;
    private readonly ICollectionView view;
    private readonly Button close;
    private bool paused;
    private long sampledAt;
    private string controller = "";
    private string sourceFilter = "";
    private bool applyingSources;
    private readonly WrapPanel sourceButtons = new();
    private readonly Dictionary<string, ToggleButton> sources = new(StringComparer.Ordinal);
    private double uploadTotal, downloadTotal;
    private readonly Dictionary<string, string> policyColors = new(StringComparer.Ordinal);

    public ConnectionsPage(AppSession session) : base(session, "connections", "连接", "实时连接、进程流量与实际出站路径")
    {
        var routeFactory = new FrameworkElementFactory(typeof(RouteDisplay)); routeFactory.SetBinding(RouteDisplay.HopsProperty, new Binding(nameof(ConnectionRow.Hops)));
        var routeColumn = new DataGridTemplateColumn { CellTemplate = new DataTemplate { VisualTree = routeFactory }, Width = 220, MinWidth = 100, SortMemberPath = nameof(ConnectionRow.Chain) };
        L.Bind(routeColumn, DataGridColumn.HeaderProperty, "出口"); table.Columns[3] = routeColumn;
        table.FrozenColumnCount = 1;
        Tools.Children.Add(Action("刷新", "RefreshConnections", RefreshAsync));
        close = Action("关闭所选", "CloseConnection", async () => { if (table.SelectedItem is ConnectionRow { Closed: false } row) await Session.ApiAsync(HttpMethod.Delete, "connections/" + Uri.EscapeDataString(row.Id)); });
        close.IsEnabled = false; Tools.Children.Add(close);
        Tools.Children.Add(Action("关闭全部", "CloseAllConnections", async () =>
        {
            if (MessageBox.Show(Window.GetWindow(this), L.T("关闭所有当前连接？应用需要重新建立网络连接。"), L.T("关闭连接"), MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                await Session.ApiAsync(HttpMethod.Delete, "connections");
        }));
        var pause = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) }.Id("PauseConnections"); L.Bind(pause, ContentControl.ContentProperty, "暂停列表"); pause.Checked += PauseChanged; pause.Unchecked += PauseChanged;
        void PauseChanged(object sender, RoutedEventArgs args) { paused = pause.IsChecked == true; sampledAt = 0; }
        L.Bind(keepClosed, ContentControl.ContentProperty, "保留已关闭");
        foreach (var label in new[] { "设备", "进程", "出口" }) groupBy.Items.Add(L.Bind(new ComboBoxItem(), ContentControl.ContentProperty, label));
        groupBy.SelectedIndex = 0;
        keepClosed.Id("KeepClosedConnections"); keepClosed.Unchecked += (_, _) => ClearClosed();
        view = CollectionViewSource.GetDefaultView(rows); table.ItemsSource = view; table.Id("ConnectionTable");
        view.Filter = item => item is ConnectionRow row && (sourceFilter.Length == 0 || row.SourceKey == sourceFilter) && row.Search.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
        search.KeyDown += (_, e) => { if (e.Key == Key.Escape) { search.Clear(); e.Handled = true; } };
        search.TextChanged += (_, _) => { view.Refresh(); UpdateCount(); };
        table.SelectionChanged += (_, _) => close.IsEnabled = table.SelectedItem is ConnectionRow { Closed: false };
        table.MouseDoubleClick += (_, _) => { if (table.SelectedItem is ConnectionRow row) DetailsDialog.ShowLive(this, "连接详情", () => row.Details, () => Session.ApiAsync(HttpMethod.Delete, "connections/" + Uri.EscapeDataString(row.Id)), () => !row.Closed); };
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition());
        var filters = Ui.Row(search, pause, keepClosed, Action("清除历史", "ClearConnectionHistory", () => { ClearClosed(); return Task.CompletedTask; }), count); filters.Margin = new Thickness(0, 0, 0, 8); layout.Children.Add(filters);
        var graph = new Expander { Header = traffic, Content = chart, IsExpanded = true, Margin = new Thickness(0, 0, 0, 12) }.Id("ConnectionThroughput"); Grid.SetRow(graph, 1); layout.Children.Add(graph);
        var sourceScroll = new ScrollViewer { Content = sourceButtons, MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 0, 8) }.Id("ConnectionSources"); Grid.SetRow(sourceScroll, 2); layout.Children.Add(sourceScroll);
        var tabs = new TabControl(); tabs.Items.Add(L.Bind(new TabItem { Content = table }, HeaderedContentControl.HeaderProperty, "活动连接"));
        var grouped = new Grid(); grouped.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grouped.RowDefinitions.Add(new RowDefinition());
        grouped.Children.Add(Ui.Row(Ui.Hint("按以下维度分组"), groupBy)); Grid.SetRow(aggregates, 1); grouped.Children.Add(aggregates); groupBy.SelectionChanged += (_, _) => UpdateAggregates();
        aggregates.Id("ConnectionGroups"); groupBy.Id("ConnectionGroupBy");
        aggregates.MouseDoubleClick += (_, _) => { if (aggregates.SelectedItem is AggregateRow row) { search.Text = row.Name; tabs.SelectedIndex = 0; } };
        tabs.Items.Add(L.Bind(new TabItem { Content = grouped }, HeaderedContentControl.HeaderProperty, "流量分组")); Grid.SetRow(tabs, 3); layout.Children.Add(tabs); SetBody(layout);
    }
    public override async Task RefreshAsync()
    {
        if (!IsRunning) { Message(InactiveMessage); return; }
        var source = ControllerIdentity;
        var proxiesTask = Get("proxies"); var connectionsTask = Get("connections"); await Task.WhenAll(proxiesTask, connectionsTask);
        if (!IsActive || !IsRunning || source != ControllerIdentity) return;
        controller = source;
        var proxies = (await proxiesTask).Get("proxies");
        policyColors.Clear(); int index = 0;
        foreach (var name in proxies.Get("GLOBAL").Get("all").Items().Select(item => item.Text()))
            if (name != "GLOBAL" && proxies.Get(name).Get("all").ValueKind == JsonValueKind.Array) policyColors[name] = "Line" + (index++ % 9) + "Brush";
        Apply(await connectionsTask);
    }
    protected override Task LiveAsync(CancellationToken cancellationToken) => Task.WhenAll(
        WatchStreamAsync("connections", snapshot => { if (!paused) Apply(snapshot); }, cancellationToken),
        WatchStreamAsync("traffic", value => { var up = value.Number("up"); var down = value.Number("down"); traffic.Text = L.F("↓ {0}/s    ↑ {1}/s   ·   总下载 {2} / 总上传 {3}", Ui.Bytes(down), Ui.Bytes(up), Ui.Bytes(downloadTotal), Ui.Bytes(uploadTotal)); chart.Add(up, down); }, cancellationToken));
    protected override void OnStatusChanged()
    {
        if (!IsRunning || controller != ControllerIdentity) { controller = ControllerIdentity; sourceFilter = ""; sources.Clear(); sourceButtons.Children.Clear(); rows.Clear(); byId.Clear(); closedOrder.Clear(); discarded = 0; sampledAt = 0; count.Text = InactiveMessage; chart.Clear(); aggregates.ItemsSource = null; traffic.Text = L.T("等待流量采样"); }
    }
    protected override void OnLanguageChanged() { foreach (var row in rows) row.Relocalize(); UpdateCount(); UpdateAggregates(); UpdateSources(); base.OnLanguageChanged(); }
    private void Apply(JsonElement snapshot)
    {
        uploadTotal = snapshot.Number("uploadTotal"); downloadTotal = snapshot.Number("downloadTotal");
        Message(""); var now = Stopwatch.GetTimestamp(); var elapsed = sampledAt == 0 ? 0 : Stopwatch.GetElapsedTime(sampledAt, now).TotalSeconds; sampledAt = now;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in snapshot.Get("connections").Items())
        {
            var id = item.Text("id"); if (id.Length == 0) continue; present.Add(id);
            if (!byId.TryGetValue(id, out var row)) { row = new ConnectionRow(id, policyColors); row.Update(item, 0); byId.Add(id, row); rows.Add(row); }
            else row.Update(item, elapsed);
        }
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            var row = rows[index]; if (present.Contains(row.Id) || row.Closed) continue;
            row.MarkClosed();
            if (keepClosed.IsChecked == true) { closedOrder.Enqueue(row.Id); }
            else { byId.Remove(row.Id); rows.RemoveAt(index); }
        }
        while (closedOrder.Count > 5000)
        {
            var expired = closedOrder.Dequeue(); if (byId.TryGetValue(expired, out var row) && row.Closed) { rows.Remove(row); byId.Remove(expired); discarded++; }
        }
        close.IsEnabled = table.SelectedItem is ConnectionRow { Closed: false };
        view.Refresh(); UpdateCount(); UpdateAggregates(); UpdateSources();
    }
    private void ClearClosed()
    {
        for (var index = rows.Count - 1; index >= 0; index--) if (rows[index].Closed) { byId.Remove(rows[index].Id); rows.RemoveAt(index); }
        closedOrder.Clear(); discarded = 0; view.Refresh(); UpdateCount(); UpdateAggregates(); UpdateSources();
    }
    private void UpdateAggregates()
    {
        var kind = groupBy.SelectedIndex;
        aggregates.ItemsSource = rows.GroupBy(row => kind == 0 ? row.SourceAddress : kind == 1 ? row.Process : row.Chain, StringComparer.Ordinal)
            .Select(group => new { Name = string.IsNullOrWhiteSpace(group.Key) ? L.T("未知") : group.Key, Count = group.Count(), Upload = group.Sum(row => row.UploadBytes), Download = group.Sum(row => row.DownloadBytes) })
            .OrderByDescending(group => group.Download + group.Upload)
            .Select(group => new AggregateRow(group.Name, group.Count, Ui.Bytes(group.Upload), Ui.Bytes(group.Download))).ToArray();
    }
    public sealed record AggregateRow(string Name, int Count, string Upload, string Download);
    private void UpdateCount() => count.Text = L.F("{0:N0} 活动", rows.Count(row => !row.Closed)) + (keepClosed.IsChecked == true ? L.F(" · {0:N0} 已关闭 / 上限 5,000", rows.Count(row => row.Closed)) : "") + (discarded > 0 ? L.F(" · 已淘汰 {0:N0}", discarded) : "") + (search.Text.Length > 0 || sourceFilter.Length > 0 ? L.F(" · 匹配 {0:N0}", view.Cast<object>().Count()) : "");
    private void UpdateSources()
    {
        var grouped = rows.GroupBy(row => row.SourceKey, StringComparer.Ordinal).ToArray();
        var keys = new HashSet<string>(grouped.Select(group => group.Key), StringComparer.Ordinal) { "" };
        if (!keys.Contains(sourceFilter)) { sourceFilter = ""; view.Refresh(); }
        foreach (string key in sources.Keys.Where(key => !keys.Contains(key)).ToArray()) { sourceButtons.Children.Remove(sources[key]); sources.Remove(key); }
        double total = rows.Sum(row => row.UploadRate + row.DownloadRate);
        Update("", L.T("全部"), rows.Count, total);
        foreach (var group in grouped) Update(group.Key, group.Key == "unknown" ? L.T("未知来源") : group.Key, group.Count(), group.Sum(row => row.UploadRate + row.DownloadRate));
        void Update(string key, string name, int count, double rate)
        {
            if (!sources.TryGetValue(key, out var button))
            {
                button = new ToggleButton { Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 6) }.Id("ConnectionSource_" + (key.Length == 0 ? "all" : key)); sources.Add(key, button); sourceButtons.Children.Add(button);
                var sourceButton = button;
                void Changed(object sender, RoutedEventArgs args)
                {
                    if (applyingSources) return;
                    sourceFilter = sourceButton.IsChecked == true ? key : "";
                    view.Refresh(); UpdateSources(); UpdateCount();
                }
                button.Checked += Changed; button.Unchecked += Changed;
            }
            button.Content = name + " (" + count.ToString("N0") + ")" + (rate >= 1 ? "  ·  " + (total > 0 ? (rate / total).ToString("P0") + "  " : "") + Ui.Bytes(rate) + "/s" : "");
            applyingSources = true;
            try { button.IsChecked = sourceFilter == key; }
            finally { applyingSources = false; }
        }
    }

    public sealed class ConnectionRow(string id, IReadOnlyDictionary<string, string> policyColors) : INotifyPropertyChanged
    {
        public string Id { get; } = id;
        public bool Closed { get; private set; }
        public string State => Closed ? (L.IsEnglish ? "Closed" : "已关闭") : L.T("活动");
        public string Host { get; private set; } = "";
        private string processName = "";
        public string SourceKey { get; private set; } = "unknown";
        public string Source => processName.Length > 0 ? processName : SourceAddress;
        public string SourceAddress => SourceKey == "unknown" ? L.T("未知") : SourceKey;
        public string Process => processName.Length > 0 ? processName : L.T("未知");
        public double UploadBytes { get; private set; }
        public double DownloadBytes { get; private set; }
        public double UploadRate { get; private set; }
        public double DownloadRate { get; private set; }
        public string Type { get; private set; } = "";
        public string Started { get; private set; } = "";
        public string Network { get; private set; } = "";
        public string Rule { get; private set; } = "";
        public string Chain { get; private set; } = "";
        public RouteHop[] Hops { get; private set; } = [];
        public string Upload { get; private set; } = "";
        public string Download { get; private set; } = "";
        public string Rate { get; private set; } = "";
        private JsonElement lastData;
        public string Details
        {
            get
            {
                var metadata = lastData.Get("metadata");
                return L.F("目标：{0}\n目标 IP：{1}\n嗅探域名：{2}\n进程：{3}\n来源：{4}:{5}\n网络：{6}\n入口：{7} / {8}\n规则：{9}\n出口：{10}\n建立时间：{11}\n上传：{12}\n下载：{13}\n连接 ID：{14}\n状态：{15}", Host, metadata.Text("destinationIP"), metadata.Text("sniffHost"), metadata.Text("processPath"), metadata.Text("sourceIP"), metadata.Text("sourcePort"), Network, Type, metadata.Text("inboundName"), Rule, Chain, lastData.Text("start"), Upload, Download, Id, State);
            }
        }
        public string Search => string.Join(' ', Host, Source, SourceAddress, Process, Network, Rule, Chain);
        private double previousDownload, previousUpload;
        private bool seen;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void MarkClosed() { Closed = true; UploadRate = DownloadRate = 0; Rate = "—"; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
        public void Relocalize() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        public void Update(JsonElement data, double elapsed)
        {
            lastData = data.Clone();
            var metadata = data.Get("metadata"); var process = metadata.Text("processPath"); var downloaded = data.Number("download");
            Closed = false;
            Host = metadata.Text("host", fallback: metadata.Text("sniffHost", fallback: metadata.Text("destinationIP"))) + ":" + metadata.Text("destinationPort");
            var source = metadata.Text("sourceIP");
            var validSource = System.Net.IPAddress.TryParse(source, out var address) && !address.Equals(System.Net.IPAddress.Any) && !address.Equals(System.Net.IPAddress.IPv6Any) && !(address.IsIPv4MappedToIPv6 && address.MapToIPv4().Equals(System.Net.IPAddress.Any));
            SourceKey = data.Bool("dns") ? "mihomo" : validSource ? address!.ToString() : metadata.Text("type") == "Inner" ? "mihomo" : "unknown";
            processName = process.Length > 0 ? System.IO.Path.GetFileName(process) : "";
            UploadBytes = data.Number("upload"); DownloadBytes = downloaded;
            Type = data.Bool("dns") ? "DNS" : metadata.Text("type"); Started = DateTimeOffset.TryParse(data.Text("start"), out var started) ? started.ToLocalTime().ToString("MM-dd HH:mm:ss") : data.Text("start");
            Network = metadata.Text("network").ToUpperInvariant() + (data.Bool("dns") ? " DNS" : "");
            Rule = data.Bool("dns") ? "" : data.Text("rule") + " " + data.Text("rulePayload"); Chain = data.Bool("dns") ? data.Get("chains").Items().FirstOrDefault().Text() : string.Join(" → ", data.Get("chains").Items().Select(item => item.Text()).Reverse());
            Hops = (data.Bool("dns") ? data.Get("chains").Items().Take(1) : data.Get("chains").Items().Reverse()).Select(item => item.Text()).Select(name => new RouteHop(name, name is "REJECT" or "REJECT-DROP" ? "DangerBrush" : name is "GLOBAL" or "DIRECT" ? "TextSecondaryBrush" : policyColors.GetValueOrDefault(name, "TextSecondaryBrush"))).ToArray();
            Upload = Ui.Bytes(UploadBytes); Download = Ui.Bytes(downloaded);
            bool continuous = seen && elapsed > 0 && elapsed <= 10 && downloaded >= previousDownload && UploadBytes >= previousUpload;
            UploadRate = continuous ? (UploadBytes - previousUpload) / elapsed : 0; DownloadRate = continuous ? (downloaded - previousDownload) / elapsed : 0;
            Rate = UploadRate + DownloadRate > 0 ? $"↑ {Ui.Bytes(UploadRate)}/s ↓ {Ui.Bytes(DownloadRate)}/s" : "—";
            previousDownload = downloaded; previousUpload = UploadBytes; seen = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
}
