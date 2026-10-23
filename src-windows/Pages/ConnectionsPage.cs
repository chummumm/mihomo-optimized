using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class ConnectionsPage : NativePage
{
    private readonly ObservableCollection<ConnectionRow> rows = [];
    private readonly Dictionary<string, ConnectionRow> byId = new(StringComparer.Ordinal);
    private readonly DataGrid table = Ui.Table(("目标", nameof(ConnectionRow.Host), -2), ("进程 / 来源", nameof(ConnectionRow.Source), -1.4), ("网络", nameof(ConnectionRow.Network), 80), ("规则", nameof(ConnectionRow.Rule), -1.5), ("出口", nameof(ConnectionRow.Chain), -1.5), ("上传", nameof(ConnectionRow.Upload), 100), ("下载", nameof(ConnectionRow.Download), 100), ("下载 / 秒", nameof(ConnectionRow.Rate), 110), ("状态", nameof(ConnectionRow.State), 75));
    private readonly TextBox search = Ui.SearchBox("筛选域名、进程、来源、规则或出口", "ConnectionSearch");
    private readonly TrafficChart chart = new() { Height = 120, MinHeight = 100 };
    private readonly TextBlock traffic = Ui.Muted("等待流量采样");
    private readonly DataGrid aggregates = Ui.Table(("分组", nameof(AggregateRow.Name), -2), ("连接", nameof(AggregateRow.Count), 90), ("上传", nameof(AggregateRow.Upload), -1), ("下载", nameof(AggregateRow.Download), -1));
    private readonly ComboBox groupBy = new() { ItemsSource = new[] { "设备", "进程", "出口" }, SelectedIndex = 0, Width = 120, Height = 34 };
    private readonly TextBlock count = Ui.Muted("等待内核连接");
    private readonly CheckBox keepClosed = new() { Content = "保留已关闭", VerticalAlignment = VerticalAlignment.Center };
    private readonly Queue<string> closedOrder = new();
    private int discarded;
    private readonly ICollectionView view;
    private readonly Button close;
    private bool paused;
    private long sampledAt;

    public ConnectionsPage(AppSession session) : base(session, "connections", "连接", "实时连接、进程流量与实际出站路径")
    {
        Tools.Children.Add(Action("刷新", "RefreshConnections", RefreshAsync));
        close = Action("关闭所选", "CloseConnection", async () => { if (table.SelectedItem is ConnectionRow { Closed: false } row) await Session.ApiAsync(HttpMethod.Delete, "connections/" + Uri.EscapeDataString(row.Id)); });
        close.IsEnabled = false; Tools.Children.Add(close);
        Tools.Children.Add(Action("关闭全部", "CloseAllConnections", async () =>
        {
            if (MessageBox.Show(Window.GetWindow(this), "关闭所有当前连接？应用需要重新建立网络连接。", "关闭连接", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                await Session.ApiAsync(HttpMethod.Delete, "connections");
        }));
        var pause = new CheckBox { Content = "暂停列表", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) }.Id("PauseConnections"); pause.Click += (_, _) => { paused = pause.IsChecked == true; sampledAt = 0; };
        keepClosed.Id("KeepClosedConnections"); keepClosed.Click += (_, _) => { if (keepClosed.IsChecked != true) ClearClosed(); };
        view = CollectionViewSource.GetDefaultView(rows); table.ItemsSource = view; table.Id("ConnectionTable");
        view.Filter = item => item is ConnectionRow row && row.Search.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
        search.TextChanged += (_, _) => { view.Refresh(); UpdateCount(); };
        table.SelectionChanged += (_, _) => close.IsEnabled = table.SelectedItem is ConnectionRow { Closed: false };
        table.MouseDoubleClick += (_, _) => { if (table.SelectedItem is ConnectionRow row) DetailsDialog.Show(this, "连接详情", row.Details); };
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition());
        var filters = Ui.Row(search, pause, keepClosed, Action("清除历史", "ClearConnectionHistory", () => { ClearClosed(); return Task.CompletedTask; }), count); filters.Margin = new Thickness(0, 0, 0, 8); layout.Children.Add(filters);
        var graph = new Expander { Header = traffic, Content = chart, IsExpanded = false, Margin = new Thickness(0, 0, 0, 12) }.Id("ConnectionThroughput"); Grid.SetRow(graph, 1); layout.Children.Add(graph);
        var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "活动连接", Content = table });
        var grouped = new Grid(); grouped.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grouped.RowDefinitions.Add(new RowDefinition());
        grouped.Children.Add(Ui.Row(Ui.Muted("按以下维度分组"), groupBy)); Grid.SetRow(aggregates, 1); grouped.Children.Add(aggregates); groupBy.SelectionChanged += (_, _) => UpdateAggregates();
        aggregates.Id("ConnectionGroups"); groupBy.Id("ConnectionGroupBy");
        aggregates.MouseDoubleClick += (_, _) => { if (aggregates.SelectedItem is AggregateRow row) { search.Text = row.Name; tabs.SelectedIndex = 0; } };
        tabs.Items.Add(new TabItem { Header = "流量分组", Content = grouped }); Grid.SetRow(tabs, 2); layout.Children.Add(tabs); SetBody(layout);
    }
    public override async Task RefreshAsync() { if (!IsRunning) { Message(InactiveMessage); return; } Apply(await Get("connections")); }
    protected override Task LiveAsync(CancellationToken cancellationToken) => Task.WhenAll(
        WatchStreamAsync("connections", snapshot => { if (!paused) Apply(snapshot); }, cancellationToken),
        WatchStreamAsync("traffic", value => { var up = value.Number("up"); var down = value.Number("down"); traffic.Text = $"↓ {Ui.Bytes(down)}/s    ↑ {Ui.Bytes(up)}/s   ·   展开查看吞吐曲线"; chart.Add(up, down); }, cancellationToken));
    protected override void OnStatusChanged()
    {
        if (!IsRunning) { rows.Clear(); byId.Clear(); closedOrder.Clear(); discarded = 0; sampledAt = 0; count.Text = InactiveMessage; chart.Clear(); aggregates.ItemsSource = null; traffic.Text = "等待流量采样"; }
    }
    private void Apply(JsonElement snapshot)
    {
        Message(""); var now = Stopwatch.GetTimestamp(); var elapsed = sampledAt == 0 ? 0 : Stopwatch.GetElapsedTime(sampledAt, now).TotalSeconds; sampledAt = now;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in snapshot.Get("connections").Items())
        {
            var id = item.Text("id"); if (id.Length == 0) continue; present.Add(id);
            if (!byId.TryGetValue(id, out var row)) { row = new ConnectionRow(id); row.Update(item, 0); byId.Add(id, row); rows.Add(row); }
            else row.Update(item, elapsed);
        }
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            var row = rows[index]; if (present.Contains(row.Id) || row.Closed) continue;
            if (keepClosed.IsChecked == true) { row.MarkClosed(); closedOrder.Enqueue(row.Id); }
            else { byId.Remove(row.Id); rows.RemoveAt(index); }
        }
        while (closedOrder.Count > 5000)
        {
            var expired = closedOrder.Dequeue(); if (byId.TryGetValue(expired, out var row) && row.Closed) { rows.Remove(row); byId.Remove(expired); discarded++; }
        }
        close.IsEnabled = table.SelectedItem is ConnectionRow { Closed: false };
        UpdateCount(); UpdateAggregates();
    }
    private void ClearClosed()
    {
        for (var index = rows.Count - 1; index >= 0; index--) if (rows[index].Closed) { byId.Remove(rows[index].Id); rows.RemoveAt(index); }
        closedOrder.Clear(); discarded = 0; UpdateCount(); UpdateAggregates();
    }
    private void UpdateAggregates()
    {
        var kind = groupBy.SelectedIndex;
        aggregates.ItemsSource = rows.GroupBy(row => kind == 0 ? row.SourceAddress : kind == 1 ? row.Process : row.Chain, StringComparer.Ordinal)
            .Select(group => new { Name = string.IsNullOrWhiteSpace(group.Key) ? "未知" : group.Key, Count = group.Count(), Upload = group.Sum(row => row.UploadBytes), Download = group.Sum(row => row.DownloadBytes) })
            .OrderByDescending(group => group.Download + group.Upload)
            .Select(group => new AggregateRow(group.Name, group.Count, Ui.Bytes(group.Upload), Ui.Bytes(group.Download))).ToArray();
    }
    public sealed record AggregateRow(string Name, int Count, string Upload, string Download);
    private void UpdateCount() => count.Text = $"{rows.Count(row => !row.Closed):N0} 活动" + (keepClosed.IsChecked == true ? $" · {rows.Count(row => row.Closed):N0} 已关闭 / 上限 5,000" : "") + (discarded > 0 ? $" · 已淘汰 {discarded:N0}" : "") + (search.Text.Length > 0 ? $" · 匹配 {view.Cast<object>().Count():N0}" : "");

    public sealed class ConnectionRow(string id) : INotifyPropertyChanged
    {
        public string Id { get; } = id;
        public bool Closed { get; private set; }
        public string State => Closed ? "已关闭" : "活动";
        public string Host { get; private set; } = "";
        public string Source { get; private set; } = "";
        public string SourceAddress { get; private set; } = "";
        public string Process { get; private set; } = "";
        public double UploadBytes { get; private set; }
        public double DownloadBytes { get; private set; }
        public string Network { get; private set; } = "";
        public string Rule { get; private set; } = "";
        public string Chain { get; private set; } = "";
        public string Upload { get; private set; } = "";
        public string Download { get; private set; } = "";
        public string Rate { get; private set; } = "";
        public string Details { get; private set; } = "";
        public string Search => string.Join(' ', Host, Source, SourceAddress, Process, Network, Rule, Chain);
        private double previousDownload;
        private bool seen;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void MarkClosed() { Closed = true; Rate = "—"; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
        public void Update(JsonElement data, double elapsed)
        {
            var metadata = data.Get("metadata"); var process = metadata.Text("processPath"); var downloaded = data.Number("download");
            Closed = false;
            Host = metadata.Text("host", fallback: metadata.Text("sniffHost", fallback: metadata.Text("destinationIP"))) + ":" + metadata.Text("destinationPort");
            var source = metadata.Text("sourceIP");
            var validSource = System.Net.IPAddress.TryParse(source, out var address) && !address.Equals(System.Net.IPAddress.Any) && !address.Equals(System.Net.IPAddress.IPv6Any) && !(address.IsIPv4MappedToIPv6 && address.MapToIPv4().Equals(System.Net.IPAddress.Any));
            SourceAddress = data.Bool("dns") ? "mihomo" : validSource ? address!.ToString() : metadata.Text("type") == "Inner" ? "mihomo" : "未知"; Process = process.Length > 0 ? System.IO.Path.GetFileName(process) : "未知"; UploadBytes = data.Number("upload"); DownloadBytes = downloaded;
            Source = process.Length > 0 ? System.IO.Path.GetFileName(process) : SourceAddress;
            Network = metadata.Text("network").ToUpperInvariant() + (data.Bool("dns") ? " DNS" : "");
            Rule = data.Bool("dns") ? "" : data.Text("rule") + " " + data.Text("rulePayload"); Chain = data.Bool("dns") ? data.Get("chains").Items().FirstOrDefault().Text() : string.Join(" → ", data.Get("chains").Items().Select(item => item.Text()).Reverse());
            Upload = Ui.Bytes(data.Number("upload")); Download = Ui.Bytes(downloaded); Rate = seen && elapsed > 0 ? Ui.Bytes(Math.Max(0, downloaded - previousDownload) / elapsed) : "—";
            previousDownload = downloaded; seen = true; Details = $"目标：{Host}\n目标 IP：{metadata.Text("destinationIP")}\n嗅探域名：{metadata.Text("sniffHost")}\n进程：{process}\n来源：{metadata.Text("sourceIP")}:{metadata.Text("sourcePort")}\n网络：{Network}\n入口：{metadata.Text("type")} / {metadata.Text("inboundName")}\n规则：{Rule}\n出口：{Chain}\n建立时间：{data.Text("start")}\n上传：{Upload}\n下载：{Download}\n连接 ID：{Id}";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
}
