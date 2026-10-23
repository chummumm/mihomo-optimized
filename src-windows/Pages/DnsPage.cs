using System.Text.Json;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class DnsPage : NativePage
{
    private readonly TextBlock statistics = Ui.Text("等待 DNS 数据", 18, true);
    private readonly DnsTrendChart trend = new();
    private readonly ComboBox duration = new() { ItemsSource = new[] { "近 1 小时", "近 6 小时", "近 24 小时" }, SelectedIndex = 2, Width = 130, Height = 34 };
    private JsonElement latestSeries;
    private UpstreamRow[] upstreamRows = [];
    private readonly TextBox upstreamFilter = Ui.SearchBox("筛选上游或出口", "DnsUpstreamFilter");
    private readonly ComboBox outcome = new() { Width = 125, Height = 34, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
    private readonly TextBlock capacity = Ui.Muted("");
    private readonly DataGrid queries = Ui.Table(("时间", nameof(QueryRow.Time), 88), ("域名", nameof(QueryRow.Domain), -2), ("类型", nameof(QueryRow.Type), 65), ("来源", nameof(QueryRow.Client), -1), ("结果", nameof(QueryRow.Outcome), 110), ("耗时", nameof(QueryRow.Latency), 90), ("RCODE", nameof(QueryRow.Rcode), 90));
    private readonly DataGrid upstreams = Ui.Table(("上游", nameof(UpstreamRow.Address), -2), ("出口", nameof(UpstreamRow.Outbound), -1), ("尝试", nameof(UpstreamRow.Attempts), 90), ("成功", nameof(UpstreamRow.Successes), 90), ("错误", nameof(UpstreamRow.Errors), 80), ("取消", nameof(UpstreamRow.Canceled), 80), ("平均耗时", nameof(UpstreamRow.Latency), 105));
    private readonly DataGrid domains = Ui.Table(("域名", nameof(TopRow.Name), -2), ("查询", nameof(TopRow.Count), 90));
    private readonly DataGrid clients = Ui.Table(("来源", nameof(TopRow.Name), -2), ("查询", nameof(TopRow.Count), 90));
    private readonly TextBox domain = Ui.SearchBox("筛选域名", "DnsDomainFilter");
    private readonly TextBox client = Ui.SearchBox("筛选来源", "DnsClientFilter");
    private readonly ComboBox type = new() { Width = 90, Height = 34, ItemsSource = new[] { "全部类型", "A", "AAAA", "HTTPS", "SVCB", "TXT", "MX", "PTR", "SRV", "NS" }, SelectedIndex = 0 };
    private readonly CheckBox live = new() { Content = "实时刷新", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock position = Ui.Muted("第 1 页");
    private readonly Button previous;
    private readonly Button next;
    private readonly List<string> cursors = [""];
    private int page;
    private string anchor = "";
    private string instance = "";
    private string queryFilter = "";
    private string controller = "";
    private bool observabilityEnabled;

    public DnsPage(AppSession session) : base(session, "dns", "DNS", "本机内存中的 DNS 查询、缓存命中和上游统计")
    {
        Tools.Children.Add(Action("刷新", "RefreshDns", RefreshAsync));
        Tools.Children.Add(Action("清空 DNS 缓存", "FlushDnsCache", async () =>
        {
            if (MessageBox.Show(Window.GetWindow(this), "清空 DNS 回答缓存？后续查询将重新向上游解析，观测记录仍保留。", "清空 DNS 缓存", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            { await Session.ApiAsync(HttpMethod.Post, "cache/dns/flush"); Message("DNS 回答缓存已清空。"); }
        }));
        queries.Id("DnsQueries"); upstreams.Id("DnsUpstreams"); type.Id("DnsTypeFilter");
        var overview = new StackPanel(); overview.Children.Add(Ui.Card(Ui.Column(statistics, capacity), 24));
        trend.Id("DnsTrend"); duration.SelectionChanged += (_, _) => trend.SetSeries(latestSeries, duration.SelectedIndex == 0 ? 1 : duration.SelectedIndex == 1 ? 6 : 24);
        overview.Children.Add(Ui.Card(Ui.Column(Ui.Row(Ui.Text("查询趋势", 16, true), duration), trend, Ui.Muted("每分钟查询 · 缓存命中 · 错误。悬停查看具体数量。"))));
        outcome.ItemsSource = new[] { "", "cache_fresh", "cache_stale", "hosts", "fake_ip", "upstream", "reject", "drop", "error", "local" }.Select(value => new { Label = value.Length == 0 ? "全部结果" : Outcome(value), Value = value }).ToArray(); outcome.SelectedIndex = 0; outcome.Id("DnsOutcomeFilter");
        var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition());
        domains.Height = clients.Height = 360;
        top.Children.Add(Ui.Card(Ui.Column(Ui.Text("热门域名", 16, true), domains)));
        var right = Ui.Card(Ui.Column(Ui.Text("查询来源", 16, true), clients)); right.Margin = new Thickness(12, 0, 0, 16); Grid.SetColumn(right, 1); top.Children.Add(right); overview.Children.Add(top);
        overview.Children.Add(Ui.Muted("统计保存在内核内存中。内核重启后重新计数；热门排行仅统计仍保留的记录。P95 为内核提供的延迟桶上界。"));
        var queryLayout = new Grid(); queryLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); queryLayout.RowDefinitions.Add(new RowDefinition()); queryLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var filter = Ui.Row(domain, client, type, outcome, Action("筛选", "ApplyDnsFilter", async () => { queryFilter = MakeFilter(); ResetPages(); await LoadQueries(); })); queryLayout.Children.Add(filter);
        Grid.SetRow(queries, 1); queryLayout.Children.Add(queries);
        previous = Action("上一页", "PreviousDnsPage", async () => { if (page > 0) { page--; await LoadQueries(); } });
        next = Action("下一页", "NextDnsPage", async () => { if (page + 1 < cursors.Count) { page++; await LoadQueries(); } });
        var paging = Ui.Row(previous, position, next, live); paging.Margin = new Thickness(0, 12, 0, 0); Grid.SetRow(paging, 2); queryLayout.Children.Add(paging);
        queries.MouseDoubleClick += (_, _) => { if (queries.SelectedItem is QueryRow row) DetailsDialog.Show(this, "DNS 查询详情", row.Details); };
        var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "概览", Content = Ui.Scroll(overview) }); tabs.Items.Add(new TabItem { Header = "查询记录", Content = queryLayout }); var upstreamLayout = new Grid(); upstreamLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); upstreamLayout.RowDefinitions.Add(new RowDefinition());
        upstreamFilter.Margin = new Thickness(0, 0, 0, 12); upstreamFilter.TextChanged += (_, _) => FilterUpstreams(); upstreamLayout.Children.Add(upstreamFilter); Grid.SetRow(upstreams, 1); upstreamLayout.Children.Add(upstreams);
        upstreams.MouseDoubleClick += (_, _) => { if (upstreams.SelectedItem is UpstreamRow row) DetailsDialog.Show(this, "DNS 上游详情", row.Details); };
        tabs.Items.Add(new TabItem { Header = "上游统计", Content = upstreamLayout }); SetBody(tabs);
    }
    public override async Task RefreshAsync()
    {
        if (!IsRunning) { Message(InactiveMessage); return; }
        observabilityEnabled = false;
        var meta = await Get("dns/observability");
        if (!meta.Bool("enabled")) { Message("当前配置未开启 DNS 观测。请在配置中启用 dns.observability。"); return; }
        observabilityEnabled = true; Message("");
        if (instance != meta.Text("instance_id")) { instance = meta.Text("instance_id"); ResetPages(); }
        capacity.Text = $"已保留 {meta.Number("retained"):N0} / {meta.Number("capacity"):N0} 条 · 已淘汰 {meta.Number("evicted"):N0} 条 · 内存记账 {Ui.Bytes(meta.Number("accounted_bytes"))}";
        await Task.WhenAll(LoadStats(), LoadUpstreams(), LoadQueries());
    }
    private async Task LoadStats()
    {
        var data = await Get("dns/observability/stats"); var counts = data.Get("last_24h"); var total = counts.Number("queries"); var hits = counts.Number("cache_fresh") + counts.Number("cache_stale");
        var ratio = total > 0 ? $"{hits / total:P1}" : "—";
        statistics.Text = $"近 24 小时  {total:N0} 次查询\n缓存命中  {ratio}     错误  {counts.Number("errors"):N0}\n平均延迟  {counts.Number("elapsed_ms_avg"):0.##} ms     P95 上界  {counts.Number("elapsed_ms_p95"):0.##} ms";
        domains.ItemsSource = data.Get("top_domains").Items().Select(item => new TopRow(item.Text("name"), item.Number("count"))).ToArray();
        clients.ItemsSource = data.Get("top_clients").Items().Select(item => new TopRow(item.Text("name"), item.Number("count"))).ToArray();
        latestSeries = data.Get("series"); trend.SetSeries(latestSeries, duration.SelectedIndex == 0 ? 1 : duration.SelectedIndex == 1 ? 6 : 24);
    }
    private async Task LoadUpstreams()
    {
        var data = await Get("dns/observability/upstreams");
        upstreamRows = data.Get("items").Items().Select(item => new UpstreamRow(item.Text("address"), item.Text("outbound", fallback: "未归因"), item.Number("attempts"), item.Number("successes"), item.Number("errors"), item.Number("canceled"), $"{item.Number("elapsed_ms_avg"):0.##} ms", DescribeUpstream(item))).ToArray(); FilterUpstreams();
    }
    private void FilterUpstreams() => upstreams.ItemsSource = upstreamRows.Where(row => (row.Address + " " + row.Outbound).Contains(upstreamFilter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
    private string MakeFilter()
    {
        var parts = new List<string>();
        if (domain.Text.Trim().Length > 0) parts.Add("qname=" + Uri.EscapeDataString(domain.Text.Trim()));
        if (client.Text.Trim().Length > 0) parts.Add("client=" + Uri.EscapeDataString(client.Text.Trim()));
        if (type.SelectedIndex > 0) parts.Add("qtype=" + Uri.EscapeDataString(type.SelectedItem?.ToString() ?? ""));
        if (outcome.SelectedValue is string result && result.Length > 0) parts.Add("outcome=" + Uri.EscapeDataString(result));
        return parts.Count > 0 ? "&" + string.Join('&', parts) : "";
    }
    private void ResetPages() { page = 0; cursors.Clear(); cursors.Add(""); anchor = ""; }
    private async Task LoadQueries()
    {
        var requestedPage = page;
        var path = "dns/observability/queries?limit=100" + queryFilter;
        if (page > 0) path += "&cursor=" + Uri.EscapeDataString(cursors[page]) + "&anchor=" + Uri.EscapeDataString(anchor);
        var data = await Get(path);
        if (requestedPage != page) return;
        if (data.Text("instance_id") != instance) { ResetPages(); instance = data.Text("instance_id"); if (requestedPage != 0) { await LoadQueries(); return; } }
        if (page == 0) { anchor = data.Text("anchor_id"); cursors.Clear(); cursors.Add(""); }
        var nextCursor = data.Text("next_cursor"); if (data.Bool("has_more") && nextCursor.Length > 0 && cursors.Count <= page + 1) cursors.Add(nextCursor);
        queries.ItemsSource = data.Get("items").Items().Select(item => new QueryRow(Ui.Time(item.Text("time")), item.Text("qname"), item.Text("qtype"), item.Text("client"), Outcome(item.Text("outcome")), $"{item.Number("elapsed_ms"):0.##} ms", item.Text("rcode"), DescribeQuery(item))).ToArray();
        position.Text = $"第 {page + 1} 页 · 每页 100 条 · 已保留 {data.Number("retained"):N0} 条"; previous.IsEnabled = page > 0; next.IsEnabled = data.Bool("has_more") && nextCursor.Length > 0;
    }
    protected override Task LiveAsync(CancellationToken cancellationToken) => PollAsync(TimeSpan.FromSeconds(5), async () => { if (!observabilityEnabled) return; await Task.WhenAll(LoadStats(), LoadUpstreams()); if (live.IsChecked == true && page == 0) await LoadQueries(); }, cancellationToken);
    protected override void OnStatusChanged()
    {
        if (!IsRunning) Message(InactiveMessage);
        var current = ControllerIdentity; if (controller != current) { observabilityEnabled = false; controller = current; instance = ""; ResetPages(); queries.ItemsSource = null; upstreams.ItemsSource = null; }
    }
    private static string DescribeQuery(JsonElement item)
    {
        var answers = string.Join("\n", item.Get("answers").Items().Select(answer => $"  {answer.Text("type")}  {answer.Text("value")}  TTL {answer.Number("ttl"):0} 秒"));
        var sources = string.Join("\n", item.Get("upstreams").Items().Select(upstream => $"  {upstream.Text("address")}  →  {upstream.Text("outbound", fallback: "未归因")}" + (upstream.Bool("reused") ? "（复用回答）" : "")));
        if (sources.Length == 0) sources = item.Text("outcome") is "cache_fresh" or "cache_stale" ? "缓存命中，本次没有请求上游" : item.Text("upstream", fallback: "本次未记录上游交换");
        return $"时间：{item.Text("time")}\n域名：{item.Text("qname")}\n类型：{item.Text("qtype")}\n客户端：{item.Text("client")}\n协议：{item.Text("protocol")}\n来源：{item.Text("source")}\n结果：{Outcome(item.Text("outcome"))}\nRCODE：{item.Text("rcode")}\n耗时：{item.Number("elapsed_ms"):0.##} ms\n错误：{item.Text("error", fallback: "无")}\n\n上游与实际出口\n{sources}\n\n回答\n{(answers.Length > 0 ? answers : "没有答案记录")}" + (item.Bool("answers_truncated") ? "\n答案列表已达到内核记录上限" : "");
    }
    private static string DescribeUpstream(JsonElement item) => $"上游：{item.Text("address")}\n实际出口：{item.Text("outbound", fallback: "未归因")}\n出口类型：{item.Text("outbound_type", fallback: "unknown")}\n\n尝试：{item.Number("attempts"):N0}\n成功：{item.Number("successes"):N0}\n错误：{item.Number("errors"):N0}\n取消：{item.Number("canceled"):N0}\n超时：{item.Number("timeouts"):N0}\nRCODE 错误：{item.Number("rcode_errors"):N0}\n\n全部平均耗时：{item.Number("elapsed_ms_avg"):0.##} ms\n成功平均耗时：{item.Number("success_elapsed_ms_avg"):0.##} ms\n错误平均耗时：{item.Number("error_elapsed_ms_avg"):0.##} ms\n取消平均耗时：{item.Number("canceled_elapsed_ms_avg"):0.##} ms\n\n取消单独统计，不与错误合并。";
    private static string Outcome(string value) => value switch { "cache_fresh" => "缓存命中", "cache_stale" => "过期缓存", "hosts" => "Hosts", "fake_ip" => "Fake-IP", "upstream" => "上游回答", "reject" => "拒绝", "drop" => "丢弃", "error" => "错误", "local" => "本地回答", _ => value };
    public sealed record QueryRow(string Time, string Domain, string Type, string Client, string Outcome, string Latency, string Rcode, string Details);
    public sealed record UpstreamRow(string Address, string Outbound, double Attempts, double Successes, double Errors, double Canceled, string Latency, string Details);
    public sealed record TopRow(string Name, double Count);
}
