using System.Text.Json;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class DnsPage : NativePage
{
    private readonly WrapPanel statistics = new();
    private readonly DnsTrendChart trend = new();
    private readonly ComboBox duration = new() { Width = 130, Height = 34 };
    private JsonElement latestMeta, latestStats, latestUpstreams, latestQueries;
    private readonly TextBox upstreamFilter = Ui.SearchBox("筛选上游或出口", "DnsUpstreamFilter");
    private readonly ComboBox outcome = new() { Width = 140, Height = 34, SelectedValuePath = "Tag" };
    private readonly TextBlock capacity = Ui.Muted("");
    private readonly TextBlock upstreamNote = Ui.Muted("");
    private readonly DataGrid queries = Ui.Table(("时间", nameof(QueryRow.Time), 88), ("域名", nameof(QueryRow.Domain), -2), ("类型", nameof(QueryRow.Type), 65), ("来源", nameof(QueryRow.Client), -1), ("结果", nameof(QueryRow.Outcome), 110), ("耗时", nameof(QueryRow.Latency), 90), ("RCODE", nameof(QueryRow.Rcode), 90));
    private readonly DataGrid upstreams = Ui.Table(("出口", nameof(UpstreamRow.Outbound), 160), ("上游", nameof(UpstreamRow.Address), 230), ("尝试", nameof(UpstreamRow.Attempts), 80), ("成功", nameof(UpstreamRow.Successes), 80), ("错误", nameof(UpstreamRow.Errors), 80), ("取消", nameof(UpstreamRow.Canceled), 80), ("超时", nameof(UpstreamRow.Timeouts), 80), ("RCODE 错误", nameof(UpstreamRow.RcodeErrors), 105), ("成功率", nameof(UpstreamRow.Ratio), 95), ("成功平均耗时", nameof(UpstreamRow.Latency), 130), ("错误平均等待", nameof(UpstreamRow.ErrorLatency), 130), ("取消平均等待", nameof(UpstreamRow.CanceledLatency), 130));
    private readonly DataGrid domains = Ui.Table(("域名", nameof(TopRow.Name), -2), ("查询", nameof(TopRow.Count), 90));
    private readonly DataGrid clients = Ui.Table(("来源", nameof(TopRow.Name), -2), ("查询", nameof(TopRow.Count), 90));
    private readonly TextBox domain = Ui.SearchBox("精确域名", "DnsDomainFilter");
    private readonly TextBox client = Ui.SearchBox("精确来源地址", "DnsClientFilter");
    private readonly TextBox type = Ui.SearchBox("查询类型，例如 A、AAAA、HTTPS", "DnsTypeFilter");
    private readonly CheckBox live = new() { IsChecked = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock position = Ui.Muted("");
    private readonly TextBlock queryNote = Ui.Muted("");
    private readonly ComboBox pageSize = new() { Width = 80, Height = 34, ItemsSource = new[] { 50, 100 }, SelectedIndex = 0 };
    private readonly TextBox jumpInput = Ui.SearchBox("输入页码", "DnsPageNumber");
    private readonly Button previous, next, first, last, jump;
    private readonly TabControl tabs = new();
    private readonly List<string> cursors = [""];
    private int page, queryRead, metaRead;
    private int? total;
    private bool queryBusy;
    private string anchor = "", instance = "", queryFilter = "", controller = "";
    private bool observabilityEnabled;
    private long metadataAt;
    private int Limit => pageSize.SelectedItem is int value ? value : 50;
    private UpstreamRow[] upstreamRows = [];

    public DnsPage(AppSession session) : base(session, "dns", "DNS", "本机内存中的 DNS 查询、缓存命中和上游统计")
    {
        Tools.Children.Add(Action("刷新", "RefreshDns", RefreshAsync));
        Tools.Children.Add(Action("清空 DNS 缓存", "FlushDnsCache", async () =>
        {
            if (MessageBox.Show(Window.GetWindow(this), L.T("清空 DNS 回答缓存？后续查询将重新向上游解析，观测记录仍保留。"), L.T("清空 DNS 缓存"), MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            { await Session.ApiAsync(HttpMethod.Post, "cache/dns/flush"); Message("DNS 回答缓存已清空。"); }
        }));
        queries.Id("DnsQueries"); upstreams.Id("DnsUpstreams"); tabs.Id("DnsTabs"); duration.Id("DnsTrendDuration"); pageSize.Id("DnsPageSize"); live.Id("DnsLive");
        L.Bind(live, ContentControl.ContentProperty, "实时刷新");
        foreach (string label in new[] { "近 1 小时", "近 6 小时", "近 24 小时" }) duration.Items.Add(L.Bind(new ComboBoxItem(), ContentControl.ContentProperty, label));
        duration.SelectedIndex = 2;
        var overview = new StackPanel(); overview.Children.Add(Ui.Card(statistics, 16));
        trend.Id("DnsTrend"); duration.SelectionChanged += (_, _) => RenderTrend();
        overview.Children.Add(Ui.Card(Ui.Column(Ui.Row(Ui.Label("查询趋势", 16, true), duration), trend, Ui.Hint("每分钟查询 · 缓存命中 · 错误。悬停查看具体数量。"))));
        foreach (string value in new[] { "", "cache_fresh", "cache_stale", "hosts", "fake_ip", "upstream", "reject", "drop", "error", "local" })
            outcome.Items.Add(L.Bind(new ComboBoxItem { Tag = value }, ContentControl.ContentProperty, value.Length == 0 ? "全部结果" : OutcomeLabel(value)));
        outcome.SelectedIndex = 0; outcome.Id("DnsOutcomeFilter");
        var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition());
        domains.Height = clients.Height = 330;
        top.Children.Add(Ui.Card(Ui.Column(Ui.Label("热门域名", 16, true), domains)));
        var right = Ui.Card(Ui.Column(Ui.Label("查询来源", 16, true), clients)); right.Margin = new Thickness(12, 0, 0, 16); Grid.SetColumn(right, 1); top.Children.Add(right); overview.Children.Add(top);
        overview.Children.Add(Ui.Hint("统计保存在内核内存中。内核重启后重新计数；热门排行仅统计仍保留的记录。P95 为内核提供的延迟桶上界。"));
        var queryLayout = new Grid(); queryLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); queryLayout.RowDefinitions.Add(new RowDefinition()); queryLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        domain.MaxLength = 254; client.MaxLength = 64; type.MaxLength = 16; type.MinWidth = 120; type.Width = 150;
        var filter = Ui.Row(domain, client, type, outcome, Action("筛选", "ApplyDnsFilter", ApplyFilters), Action("重置", "ResetDnsFilter", async () => { domain.Clear(); client.Clear(); type.Clear(); outcome.SelectedIndex = 0; await ApplyFilters(); }));
        queryLayout.Children.Add(Ui.Column(filter, Ui.Hint("筛选使用精确匹配；翻阅历史页时暂停实时刷新。")));
        foreach (var input in new[] { domain, client, type }) input.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await RunAsync(ApplyFilters); } };
        Grid.SetRow(queries, 1); queryLayout.Children.Add(queries);
        first = Action("首页", "FirstDnsPage", () => GoToPage(0)); previous = Action("上一页", "PreviousDnsPage", () => GoToPage(page - 1)); next = Action("下一页", "NextDnsPage", () => GoToPage(page + 1)); last = Action("末页", "LastDnsPage", () => GoToPage(cursors.Count - 1));
        jumpInput.MinWidth = 65; jumpInput.Width = 65; jumpInput.MaxLength = 6;
        jump = Action("跳转", "JumpDnsPage", () => int.TryParse(jumpInput.Text, out int number) ? GoToPage(number - 1) : Task.CompletedTask);
        jumpInput.TextChanged += (_, _) => UpdatePaging(); jumpInput.KeyDown += async (_, e) => { if (e.Key == Key.Enter && jump.IsEnabled) { e.Handled = true; await RunAsync(() => GoToPage(int.Parse(jumpInput.Text) - 1)); } };
        pageSize.SelectionChanged += async (_, _) => { ResetPages(); if (observabilityEnabled) await RunAsync(LoadQueries); };
        live.Checked += async (_, _) => { if (live.IsChecked == true) { ResetPages(); await RunAsync(LoadQueries); } };
        var paging = Ui.Column(Ui.Row(position, Ui.Hint("每页"), pageSize, live), Ui.Row(first, previous, next, last, jumpInput, jump), queryNote); paging.Margin = new Thickness(0, 12, 0, 0); Grid.SetRow(paging, 2); queryLayout.Children.Add(paging);
        queries.MouseDoubleClick += (_, _) => { if (queries.SelectedItem is QueryRow row) DetailsDialog.Show(this, "DNS 查询详情", DescribeQuery(row.Data)); };
        tabs.Items.Add(L.Bind(new TabItem { Content = Ui.Scroll(overview) }.Id("DnsOverviewTab"), HeaderedContentControl.HeaderProperty, "概览")); tabs.Items.Add(L.Bind(new TabItem { Content = queryLayout }.Id("DnsQueriesTab"), HeaderedContentControl.HeaderProperty, "查询记录"));
        var upstreamLayout = new Grid(); upstreamLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); upstreamLayout.RowDefinitions.Add(new RowDefinition()); upstreamLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        upstreamFilter.TextChanged += (_, _) => FilterUpstreams(); upstreamLayout.Children.Add(Ui.Column(Ui.Row(upstreamFilter), Ui.Hint("成功率不包含取消；成功、错误与取消的平均耗时分别统计。"))); Grid.SetRow(upstreams, 1); upstreamLayout.Children.Add(upstreams); Grid.SetRow(upstreamNote, 2); upstreamLayout.Children.Add(upstreamNote);
        upstreams.MouseDoubleClick += (_, _) => { if (upstreams.SelectedItem is UpstreamRow row) DetailsDialog.Show(this, "DNS 上游详情", DescribeUpstream(row.Data)); };
        tabs.Items.Add(L.Bind(new TabItem { Content = upstreamLayout }.Id("DnsUpstreamsTab"), HeaderedContentControl.HeaderProperty, "上游统计"));
        var body = new Grid(); body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); body.RowDefinitions.Add(new RowDefinition()); capacity.Margin = new Thickness(0, 0, 0, 16); body.Children.Add(capacity); Grid.SetRow(tabs, 1); body.Children.Add(tabs); SetBody(body); UpdatePaging();
    }
    public override async Task RefreshAsync()
    {
        if (await LoadMetadata()) await Task.WhenAll(LoadStats(), LoadUpstreams(), LoadQueries());
    }
    private async Task<bool> LoadMetadata()
    {
        if (!IsRunning) { observabilityEnabled = false; tabs.Visibility = Visibility.Collapsed; Message(InactiveMessage); return false; }
        int read = ++metaRead;
        string expectedController = ControllerIdentity;
        var meta = await Get("dns/observability");
        if (!IsActive || !IsRunning || read != metaRead || expectedController != ControllerIdentity) return false;
        controller = expectedController;
        metadataAt = Stopwatch.GetTimestamp();
        latestMeta = meta;
        observabilityEnabled = meta.Bool("enabled") && meta.Number("version") == 1 && meta.Text("storage") == "memory";
        tabs.Visibility = observabilityEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (meta.Number("version") != 1 || meta.Text("storage") != "memory") { ResetPages(); Message("当前内核的 DNS 观测版本不兼容，请更新应用。"); return false; }
        if (!meta.Bool("enabled")) { ResetPages(); Message("当前配置未开启 DNS 观测。请在配置中启用 dns.observability。"); return false; }
        Message("");
        if (instance != meta.Text("instance_id")) { instance = meta.Text("instance_id"); ResetPages(); latestQueries = latestStats = latestUpstreams = default; }
        RenderCapacity(); return true;
    }
    private bool Current(JsonElement data, string expected)
    {
        if (!IsActive || !IsRunning || controller != ControllerIdentity || expected != instance) return false;
        if (data.Text("instance_id") == instance) return true;
        // A restarted core can answer concurrent requests from different
        // instances. Invalidate once and let the next poll read fresh metadata;
        // recursively refreshing from every response multiplies those races.
        observabilityEnabled = false; instance = ""; ResetPages();
        latestMeta = latestStats = latestUpstreams = default;
        tabs.Visibility = Visibility.Collapsed;
        return false;
    }
    private async Task LoadStats()
    {
        string expected = instance; var data = await Get("dns/observability/stats"); if (!Current(data, expected)) return; latestStats = data; RenderStats();
    }
    private void RenderCapacity() => capacity.Text = L.F("内存存储 · 保留 {0:0.#} 小时 · {1:N0} / {2:N0} 条 · 已淘汰 {3:N0} 条\n内存 {4} / {5} · 启动于 {6}", latestMeta.Number("retention_seconds") / 3600, latestMeta.Number("retained"), latestMeta.Number("capacity"), latestMeta.Number("evicted"), Ui.Bytes(latestMeta.Number("accounted_bytes")), Ui.Bytes(latestMeta.Number("memory_limit_bytes")), latestMeta.Text("started_at"));
    private void RenderStats()
    {
        var data = latestStats; var counts = data.Get("last_24h"); var totals = data.Get("totals"); double totalQueries = counts.Number("queries"); double hits = counts.Number("cache_fresh") + counts.Number("cache_stale");
        var series = data.Get("series").Items().ToArray(); double qps = series.Length >= 2 ? series[^2].Number("queries") / 60 : 0;
        statistics.Children.Clear();
        Metric("近 24 小时查询", totalQueries.ToString("N0"), L.F("本次内核运行累计 {0:N0}", totals.Number("queries")));
        Metric("QPS", qps.ToString("0.##"), L.T("最近一个完整分钟的平均查询数 / 秒"));
        Metric("缓存命中", totalQueries > 0 ? (hits / totalQueries).ToString("P1") : "—", L.F("新鲜 {0:N0} · 过期 {1:N0}", counts.Number("cache_fresh"), counts.Number("cache_stale")));
        Metric("错误", counts.Number("errors").ToString("N0"), L.F("拒绝 {0:N0} · 丢弃 {1:N0}", counts.Number("reject"), counts.Number("drop")));
        Metric("平均延迟", counts.Number("elapsed_ms_avg").ToString("0.##") + " ms", L.F("P95 上界 {0:0.##} ms", counts.Number("elapsed_ms_p95")));
        domains.ItemsSource = data.Get("top_domains").Items().Take(20).Select(item => new TopRow(item.Text("name"), item.Number("count"))).ToArray(); clients.ItemsSource = data.Get("top_clients").Items().Take(20).Select(item => new TopRow(item.Text("name"), item.Number("count"))).ToArray(); RenderTrend();
        void Metric(string label, string value, string hint) { var card = Ui.Column(Ui.Label(label), Ui.Text(value, 28, true), Ui.Muted(hint)); card.Width = 165; card.Margin = new Thickness(0, 0, 18, 12); statistics.Children.Add(card); }
    }
    private void RenderTrend() => trend.SetSeries(latestStats.Get("series"), duration.SelectedIndex == 0 ? 1 : duration.SelectedIndex == 1 ? 6 : 24);
    private async Task LoadUpstreams() { string expected = instance; var data = await Get("dns/observability/upstreams"); if (!Current(data, expected)) return; latestUpstreams = data; RenderUpstreams(); }
    private void RenderUpstreams()
    {
        upstreamRows = latestUpstreams.Get("items").Items().Select(item => new UpstreamRow(item)).ToArray(); FilterUpstreams();
        bool legacy = upstreamRows.Length > 0 && upstreamRows.All(row => row.Data.Get("success_elapsed_ms_avg").ValueKind == JsonValueKind.Undefined);
        L.Bind(upstreams.Columns[9], DataGridColumn.HeaderProperty, legacy ? "综合平均耗时" : "成功平均耗时");
        upstreamNote.Text = L.F("统计当前内核运行周期 · 上游上限 {0:N0} · 超出上限 {1:N0}", latestUpstreams.Number("limit"), latestUpstreams.Number("overflowed")) + (legacy ? "\n" + L.T("当前内核只提供综合平均耗时，包含成功、错误和取消。") : "");
    }
    private void FilterUpstreams() => upstreams.ItemsSource = upstreamRows.Where(row => (row.Address + " " + row.Outbound).Contains(upstreamFilter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
    private Task ApplyFilters() { queryFilter = MakeFilter(); ResetPages(); return LoadQueries(); }
    private string MakeFilter()
    {
        var parts = new List<string>();
        if (domain.Text.Trim().Length > 0) parts.Add("qname=" + Uri.EscapeDataString(domain.Text.Trim())); if (client.Text.Trim().Length > 0) parts.Add("client=" + Uri.EscapeDataString(client.Text.Trim()));
        if (type.Text.Trim().Length > 0) parts.Add("qtype=" + Uri.EscapeDataString(type.Text.Trim().ToUpperInvariant())); if (outcome.SelectedValue is string result && result.Length > 0) parts.Add("outcome=" + Uri.EscapeDataString(result));
        return parts.Count > 0 ? "&" + string.Join('&', parts) : "";
    }
    private void ResetPages() { queryRead++; queryBusy = false; page = 0; cursors.Clear(); cursors.Add(""); anchor = ""; total = null; queries.ItemsSource = null; latestQueries = default; UpdatePaging(); }
    private async Task GoToPage(int target)
    {
        if (queryBusy || target < 0 || target >= cursors.Count || target == page) return;
        page = target; if (page > 0) live.IsChecked = false; jumpInput.Clear(); await LoadQueries();
    }
    private async Task LoadQueries()
    {
        if (!observabilityEnabled) return;
        int read = ++queryRead, requestedPage = page, limit = Limit; string filter = queryFilter, expected = instance;
        var path = "dns/observability/queries?limit=" + limit + filter;
        if (page > 0) path += "&cursor=" + Uri.EscapeDataString(cursors[page]) + "&anchor=" + Uri.EscapeDataString(anchor);
        queryBusy = true; UpdatePaging();
        try
        {
            var data = await Get(path); if (read != queryRead || requestedPage != page || limit != Limit || filter != queryFilter) return;
            if (!Current(data, expected)) return;
            var index = DnsPageIndex.Read(data, limit);
            if (index is not null)
            {
                total = index.Total; anchor = index.Anchor; cursors.Clear(); cursors.AddRange(index.Cursors);
                if (page >= cursors.Count) { page = cursors.Count - 1; await LoadQueries(); return; }
            }
            else
            {
                total = null; if (page == 0) { anchor = data.Text("anchor_id"); cursors.Clear(); cursors.Add(""); }
                string nextCursor = data.Text("next_cursor"); if (data.Bool("has_more") && nextCursor.Length > 0 && !cursors.Contains(nextCursor) && cursors.Count <= page + 1) cursors.Add(nextCursor);
            }
            latestQueries = data; RenderQueries();
            if (queries.Items.Count > 0) queries.ScrollIntoView(queries.Items[0]);
        }
        finally { if (read == queryRead) { queryBusy = false; UpdatePaging(); } }
    }
    private void RenderQueries()
    {
        queries.ItemsSource = latestQueries.Get("items").Items().Select(item => new QueryRow(item)).ToArray();
        position.Text = L.F("第 {0:N0} / {1} 页 · 共 {2} 条 · 本页 {3:N0} 条", page + 1, total.HasValue ? cursors.Count.ToString("N0") : "—", total?.ToString("N0") ?? "—", queries.Items.Count);
        queryNote.Text = L.F("已保留 {0:N0} 条 · 已淘汰 {1:N0} 条", latestQueries.Number("retained"), latestQueries.Number("evicted")) + (!total.HasValue ? "\n" + L.T("当前内核未提供分页索引，可使用上一页和下一页。") : ""); UpdatePaging();
    }
    private void UpdatePaging()
    {
        bool idle = !queryBusy && observabilityEnabled;
        first.IsEnabled = previous.IsEnabled = idle && page > 0; next.IsEnabled = idle && latestQueries.Bool("has_more") && page + 1 < cursors.Count;
        last.IsEnabled = idle && total.HasValue && page + 1 < cursors.Count; jumpInput.IsEnabled = total.HasValue;
        jump.IsEnabled = idle && total.HasValue && int.TryParse(jumpInput.Text, out int target) && target > 0 && target <= cursors.Count && target != page + 1; live.IsEnabled = page == 0;
    }
    protected override Task LiveAsync(CancellationToken cancellationToken) => PollAsync(TimeSpan.FromSeconds(5), async () =>
    {
        string before = instance;
        if ((!observabilityEnabled || metadataAt == 0 || Stopwatch.GetElapsedTime(metadataAt).TotalSeconds >= 10) && !await LoadMetadata()) return;
        await Task.WhenAll(LoadStats(), LoadUpstreams());
        if (observabilityEnabled && (before != instance || live.IsChecked == true && page == 0)) await LoadQueries();
    }, cancellationToken);
    protected override void OnLanguageChanged() { RenderCapacity(); RenderStats(); RenderQueries(); RenderUpstreams(); base.OnLanguageChanged(); }
    protected override void OnStatusChanged()
    {
        if (!IsRunning) { observabilityEnabled = false; tabs.Visibility = Visibility.Collapsed; Message(InactiveMessage); }
        var current = ControllerIdentity; if (controller != current) { metaRead++; observabilityEnabled = false; controller = current; instance = ""; ResetPages(); latestMeta = latestStats = latestUpstreams = default; upstreams.ItemsSource = null; }
        UpdatePaging();
    }
    private static string DescribeQuery(JsonElement item)
    {
        var answers = string.Join("\n", item.Get("answers").Items().Select(answer => $"  {answer.Text("type")}  {answer.Text("value")}  TTL {answer.Number("ttl"):0} s"));
        var sources = string.Join("\n", item.Get("upstreams").Items().Select(upstream => $"  {upstream.Text("address")}  →  {upstream.Text("outbound", fallback: L.T("未归因"))}" + (upstream.Bool("reused") ? L.T("（复用回答）") : "")));
        if (sources.Length == 0) sources = item.Text("outcome") is "cache_fresh" or "cache_stale" ? L.T("缓存命中，本次没有请求上游") : item.Text("outcome") is "hosts" or "fake_ip" or "local" or "reject" or "drop" ? L.T("本地处理，本次没有请求上游") : item.Text("upstream", fallback: L.T("本次未记录上游交换"));
        return L.F("时间：{0}\n域名：{1}\n类型：{2}\n客户端：{3}\n协议：{4}\n来源：{5}\n结果：{6}\n缓存：{7}\nRCODE：{8}\n耗时：{9:0.##} ms\n错误：{10}\n\n上游与实际出口\n{11}\n\n回答\n{12}", item.Text("time"), item.Text("qname"), item.Text("qtype"), item.Text("client"), item.Text("protocol"), item.Text("source"), L.T(OutcomeLabel(item.Text("outcome"))), item.Text("cache"), item.Text("rcode"), item.Number("elapsed_ms"), item.Text("error", fallback: L.T("无")), sources, answers.Length > 0 ? answers : L.T("没有答案记录")) + (item.Bool("answers_truncated") ? "\n" + L.T("答案列表已达到内核记录上限") : "") + (item.Bool("upstreams_truncated") ? "\n" + L.T("上游列表已达到内核记录上限") : "") + "\n\n" + L.T("实际出口记录本次 DNS 交换使用的出站，不代表业务连接的规则命中。");
    }
    private static string DescribeUpstream(JsonElement item) => L.F("上游：{0}\n实际出口：{1}\n出口类型：{2}\n\n尝试：{3:N0}\n成功：{4:N0}\n错误：{5:N0}\n取消：{6:N0}\n超时：{7:N0}\nRCODE 错误：{8:N0}\n\n全部平均耗时：{9:0.##} ms\n成功平均耗时：{10:0.##} ms\n错误平均耗时：{11:0.##} ms\n取消平均耗时：{12:0.##} ms\n\n取消单独统计，不与错误合并。", item.Text("address"), item.Text("outbound", fallback: L.T("未归因")), item.Text("outbound_type", fallback: "unknown"), item.Number("attempts"), item.Number("successes"), item.Number("errors"), item.Number("canceled"), item.Number("timeouts"), item.Number("rcode_errors"), item.Number("elapsed_ms_avg"), item.Number("success_elapsed_ms_avg"), item.Number("error_elapsed_ms_avg"), item.Number("canceled_elapsed_ms_avg"));
    private static string OutcomeLabel(string value) => value switch { "cache_fresh" => "缓存命中", "cache_stale" => "过期缓存", "hosts" => "Hosts", "fake_ip" => "Fake-IP", "upstream" => "上游回答", "reject" => "拒绝", "drop" => "丢弃", "error" => "错误", "local" => "本地回答", _ => value };
    public sealed record QueryRow(JsonElement Data)
    {
        public string Time => Ui.Time(Data.Text("time")); public string Domain => Data.Text("qname"); public string Type => Data.Text("qtype"); public string Client => Data.Text("client"); public string Outcome => L.T(OutcomeLabel(Data.Text("outcome"))); public string Latency => Data.Number("elapsed_ms").ToString("0.##") + " ms"; public string Rcode => Data.Text("rcode");
    }
    public sealed record UpstreamRow(JsonElement Data)
    {
        public string Address => Data.Text("address"); public string Outbound => Data.Text("outbound", fallback: L.T("未归因")); public double Attempts => Data.Number("attempts"); public double Successes => Data.Number("successes"); public double Errors => Data.Number("errors"); public double Canceled => Data.Number("canceled"); public double Timeouts => Data.Number("timeouts"); public double RcodeErrors => Data.Number("rcode_errors"); public string Ratio => Successes + Errors > 0 ? (Successes / (Successes + Errors)).ToString("P1") : "—";
        private string Elapsed(string key, double count) => count > 0 && Data.Get(key).ValueKind == JsonValueKind.Number ? Data.Number(key).ToString("0.##") + " ms" : "—";
        public string Latency => Data.Get("success_elapsed_ms_avg").ValueKind == JsonValueKind.Number ? Elapsed("success_elapsed_ms_avg", Successes) : Elapsed("elapsed_ms_avg", Successes + Errors + Canceled); public string ErrorLatency => Elapsed("error_elapsed_ms_avg", Errors); public string CanceledLatency => Elapsed("canceled_elapsed_ms_avg", Canceled);
    }
    public sealed record TopRow(string Name, double Count);
}
