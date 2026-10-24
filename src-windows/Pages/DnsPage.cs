using System.Text.Json;
using System.Diagnostics;
using System.Net.Http;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class DnsPage : NativePage
{
    private readonly Grid statistics = new(), topGrid = new();
    private readonly List<MetricView> metrics = [];
    private readonly List<Border> sections = [];
    private readonly DnsTrendChart trend = new();
    private readonly SettingsSegments duration = new();
    private JsonElement latestMeta, latestStats, latestUpstreams, latestQueries;
    private readonly TextBox upstreamFilter = DnsVisuals.InputBox("筛选上游或出口", "DnsUpstreamFilter");
    private readonly ComboBox outcome = new() { SelectedValuePath = "Tag" };
    private readonly DnsFlowPanel capacity = new() { Gap = 20, RowGap = 6, Margin = new Thickness(0, -8, 0, 20) };
    private readonly TextBlock upstreamNote = DnsVisuals.Caption("", false), upstreamDefinition = DnsVisuals.Caption("", false);
    private readonly DataGrid queries = Ui.Table(("时间", nameof(QueryRow.Time), 100), ("域名", nameof(QueryRow.Domain), -2), ("类型", nameof(QueryRow.Type), 70), ("客户端", nameof(QueryRow.Client), 130), ("结果来源", nameof(QueryRow.Outcome), 125), ("RCODE", nameof(QueryRow.Rcode), 95), ("耗时", nameof(QueryRow.Latency), 100));
    private readonly DataGrid upstreams = Ui.Table(("实际出站", nameof(UpstreamRow.Outbound), 135), ("上游", nameof(UpstreamRow.Address), 240), ("交换次数", nameof(UpstreamRow.Attempts), 110), ("成功交换", nameof(UpstreamRow.Successes), 110), ("错误", nameof(UpstreamRow.Errors), 110), ("取消", nameof(UpstreamRow.Canceled), 110), ("超时", nameof(UpstreamRow.Timeouts), 90), ("RCODE 错误", nameof(UpstreamRow.RcodeErrors), 110), ("已完成成功率", nameof(UpstreamRow.Ratio), 130), ("成功平均耗时", nameof(UpstreamRow.Latency), 150));
    private readonly DnsTopList domains = new(), clients = new();
    private readonly TextBox domain = DnsVisuals.InputBox("精确域名", "DnsDomainFilter"), client = DnsVisuals.InputBox("精确 IP 或 unknown", "DnsClientFilter"), type = DnsVisuals.InputBox("A / AAAA", "DnsTypeFilter");
    private readonly CheckBox live = new() { IsChecked = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly FluentIcon liveIcon = new("play", 14);
    private readonly TextBlock liveLabel = Ui.Text(""), liveHint = DnsVisuals.Caption("", false);
    private readonly LivePing liveDot = new() { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock position = Ui.Text("", 12), pageRows = DnsVisuals.Caption("", false), queryNote = DnsVisuals.Caption("", false);
    private readonly ComboBox pageSize = new() { Width = 72, Height = 28, ItemsSource = new[] { 50, 100 }, SelectedIndex = 0 };
    private readonly TextBox jumpInput = DnsVisuals.InputBox("跳转到指定页码", "DnsPageNumber");
    private readonly Button previous, next, first, last, jump;
    private readonly DnsTabs tabs = new();
    private readonly TextBlock overviewUpdated = DnsVisuals.Caption("", false), queryUpdated = DnsVisuals.Caption("", false), upstreamUpdated = DnsVisuals.Caption("", false);
    private readonly TextBlock trendUnit = DnsVisuals.Caption("", false), trendEmpty = DnsVisuals.Caption("暂时没有 DNS 查询。"), rangeEmpty = DnsVisuals.Caption("此时间范围内没有查询。"), queryEmpty = DnsVisuals.Caption("暂时没有 DNS 查询。"), upstreamEmpty = DnsVisuals.Caption("暂时没有记录到上游交换。");
    // The reference's adjacent 8px block margins collapse into one gap.
    // StackPanel margins add, so the trend alone supplies that spacing.
    private readonly DnsFlowPanel trendHeading = new() { Gap = 12, RowGap = 8, SpaceBetween = true, AlignTop = true };
    private readonly DnsPagingActions pageActions = new() { Gap = 8, RowGap = 8 };
    private Border? jumpFrame, paginationFrame;
    private readonly DnsFlowPanel legend = new() { Gap = 20, RowGap = 8, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Border stateCard;
    private readonly FluentIcon stateIcon = new("info-o", 28);
    private readonly TextBlock stateTitle = Ui.Text("", 14, true), stateHint = Ui.Text("", 14);
    private readonly DnsLoadingBar stateLoading = new();
    private readonly Button stateRetry;
    private DateTimeOffset? statsAt, queriesAt, upstreamsAt;
    private readonly List<string> cursors = [""];
    private int page, queryRead, metaRead;
    private int? total;
    private bool queryBusy;
    private string anchor = "", instance = "", queryFilter = "", controller = "";
    private bool observabilityEnabled;
    private long metadataAt;
    private int Limit => pageSize.SelectedItem is int value ? value : 50;
    private UpstreamRow[] upstreamRows = [];
    private QueryRow[] queryRows = [];

    public DnsPage(AppSession session) : base(session, "dns", "DNS", "")
    {
        queries.Id("DnsQueries"); upstreams.Id("DnsUpstreams"); tabs.Id("DnsTabs"); duration.Id("DnsTrendDuration"); pageSize.Id("DnsPageSize"); live.Id("DnsLive"); trend.Id("DnsTrend");
        DnsVisuals.Table(queries); DnsVisuals.Table(upstreams); ConfigureQueryColumns(); ConfigureUpstreamColumns();
        queries.SizeChanged += (_, args) => { if (args.WidthChanged) FitQueryColumns(); };
        upstreams.SizeChanged += (_, args) => { if (args.WidthChanged) FitUpstreamColumns(); };
        foreach (var input in new Control[] { domain, client, type, outcome, pageSize, jumpInput }) DnsVisuals.Input(input);
        pageSize.Width = 72; pageSize.Height = 28; pageSize.FontSize = 12;
        position.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        domain.MaxLength = 254; client.MaxLength = 64; type.MaxLength = 16;
        foreach (string label in new[] { "1 小时", "6 小时", "24 小时" }) duration.Items.Add(L.Bind(new ListBoxItem(), ContentControl.ContentProperty, label));
        var rangeStyle = new Style(typeof(ListBoxItem), duration.ItemContainerStyle);
        rangeStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 26d)); rangeStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12d)); rangeStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 0, 12, 0)));
        duration.ItemContainerStyle = rangeStyle;
        duration.Loaded += (_, _) => { if (duration.Template.FindName("PART_Selection", duration) is Border selection) selection.Height = 26; };
        duration.SelectedIndex = 2; duration.SelectionChanged += (_, _) => RenderTrend();
        foreach (string value in new[] { "", "cache_fresh", "cache_stale", "hosts", "fake_ip", "upstream", "reject", "drop", "error", "local" })
            outcome.Items.Add(L.Bind(new ComboBoxItem { Tag = value }, ContentControl.ContentProperty, value.Length == 0 ? "全部" : OutcomeLabel(value)));
        outcome.SelectedIndex = 0; outcome.Id("DnsOutcomeFilter");

        var overview = ContentPanel();
        overview.Children.Add(Heading("最近 24 小时", overviewUpdated, "RefreshDns", RefreshAsync));
        foreach (string label in new[] { "查询量", "QPS", "缓存命中率", "错误", "平均耗时" })
        {
            var title = DnsVisuals.Caption(label); title.FontWeight = FontWeights.Medium; title.LineHeight = 17.16;
            var value = Ui.Text("—", 28, true); value.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont"); value.Margin = new Thickness(0, 6, 0, 0); value.LineHeight = 36;
            var hint = DnsVisuals.Caption("", false); hint.Margin = new Thickness(0, 6, 0, 0);
            DnsMeter? meter = label == "缓存命中率" ? new DnsMeter { Margin = new Thickness(0, 6, 0, 0) } : null;
            var column = Ui.Column(title, value); if (meter is not null) column.Children.Add(meter); column.Children.Add(hint);
            var border = new Border { Child = column }; border.SetResourceReference(Border.BorderBrushProperty, "DividerBrush"); statistics.Children.Add(border); metrics.Add(new MetricView(border, value, hint, meter));
        }
        overview.Children.Add(DnsVisuals.Card(statistics, new Thickness(0), 0));
        var definition = DnsVisuals.Caption("缓存命中率 =（有效答案缓存命中 + 过期答案缓存命中）/ 同一时间窗口内完成的全部客户端 DNS 查询；Hosts、Fake IP 和候选缓存不计作命中。"); definition.Margin = new Thickness(0, 8, 0, 0); overview.Children.Add(definition);
        trendUnit.LineHeight = 17.16; trendUnit.Margin = new Thickness(0, 2, 0, 0);
        trendHeading.Children.Add(Ui.Column(Ui.Label("DNS 查询趋势", 14, true), trendUnit)); trendHeading.Children.Add(duration);
        trend.Margin = new Thickness(0, 8, 0, 0); trendEmpty.TextAlignment = TextAlignment.Center; trendEmpty.Margin = new Thickness(16, 32, 16, 32);
        foreach (var (label, key) in new[] { ("查询量", "AccentBrush"), ("缓存命中", "ChartUpBrush"), ("错误", "DangerBrush") })
        { var mark = new Border { Width = 12, Height = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 0, 8, 0) }; mark.SetResourceReference(Border.BackgroundProperty, key); var text = Ui.Label(label, 12); text.LineHeight = 17.16; text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); legend.Children.Add(DnsVisuals.Inline(mark, text)); }
        rangeEmpty.LineHeight = 17.16; legend.Children.Add(rangeEmpty);
        overview.Children.Add(Section(Ui.Column(trendHeading, trend, trendEmpty, legend)));
        var domainsCard = Section(Top("热门域名", domains)); var clientsCard = Section(Top("热门客户端", clients)); topGrid.Children.Add(domainsCard); topGrid.Children.Add(clientsCard); overview.Children.Add(topGrid);

        var queryLayout = ContentPanel();
        var filters = new DnsFilterPanel();
        filters.Children.Add(Field("域名", domain)); filters.Children.Add(Field("客户端", client)); filters.Children.Add(Field("类型", type)); filters.Children.Add(Field("结果来源", outcome));
        var apply = Action("应用筛选", "ApplyDnsFilter", ApplyFilters, true); apply.Margin = new Thickness(0, 0, 8, 0);
        apply.Content = DnsVisuals.Inline(new FluentIcon("search", 15) { Margin = new Thickness(0, 0, 6, 0) }, Ui.Label("应用筛选"));
        var reset = Action("重置", "ResetDnsFilter", async () => { domain.Clear(); client.Clear(); type.Clear(); outcome.SelectedIndex = 0; await ApplyFilters(); }); reset.Margin = new Thickness(0);
        filters.Children.Add(DnsVisuals.Inline(apply, reset));
        var filterHint = DnsVisuals.Caption("域名与客户端使用精确匹配；域名忽略大小写和一个末尾根点。"); filterHint.Margin = new Thickness(0, 6, 0, 0);
        queryLayout.Children.Add(Section(Ui.Column(filters, filterHint)));
        foreach (var input in new[] { domain, client, type }) input.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await RunAsync(ApplyFilters); } };
        live.Style = (Style)DnsVisuals.Templates["DnsLive"]; liveIcon.Margin = new Thickness(0, 0, 6, 0); live.Content = DnsVisuals.Inline(liveIcon, liveLabel);
        var liveItems = new DnsFlowPanel { Gap = 8, RowGap = 8 }; liveItems.Children.Add(live); liveItems.Children.Add(liveDot); liveItems.Children.Add(liveHint);
        var liveRow = new DnsFlowPanel { Gap = 12, RowGap = 8, SpaceBetween = true, Margin = new Thickness(0, 16, 0, 0) };
        liveRow.Children.Add(liveItems); liveRow.Children.Add(ResourceStatus(queryUpdated, "RefreshDnsQueries", LoadQueries)); queryLayout.Children.Add(liveRow);
        first = Action("首页", "FirstDnsPage", () => GoToPage(0)); previous = Action("上一页", "PreviousDnsPage", () => GoToPage(page - 1)); next = Action("下一页", "NextDnsPage", () => GoToPage(page + 1)); last = Action("末页", "LastDnsPage", () => GoToPage(cursors.Count - 1));
        DnsVisuals.SmallButton(first, "caret-line-left"); DnsVisuals.SmallButton(previous, "caret-left"); DnsVisuals.SmallButton(next, "caret-right", true); DnsVisuals.SmallButton(last, "caret-line-right", true);
        jumpInput.Width = 56; jumpInput.Height = 28; jumpInput.Padding = new Thickness(6, 0, 6, 0); jumpInput.MaxLength = 6; jumpInput.FontFamily = DnsVisuals.Mono; jumpInput.FontSize = 12; jumpInput.TextAlignment = TextAlignment.Center;
        jumpInput.PreviewTextInput += (_, e) => e.Handled = e.Text.Any(character => !char.IsAsciiDigit(character));
        jump = Action("跳转", "JumpDnsPage", () => int.TryParse(jumpInput.Text, out int number) ? GoToPage(number - 1) : Task.CompletedTask); DnsVisuals.SmallButton(jump);
        jumpInput.TextChanged += (_, _) =>
        {
            string clean = new(jumpInput.Text.Where(char.IsAsciiDigit).Take(6).ToArray());
            if (clean != jumpInput.Text) { int caret = jumpInput.CaretIndex; jumpInput.Text = clean; jumpInput.CaretIndex = Math.Min(caret, clean.Length); return; }
            UpdatePaging();
        }; jumpInput.KeyDown += async (_, e) => { if (e.Key == Key.Enter && jump.IsEnabled) { e.Handled = true; await RunAsync(() => GoToPage(int.Parse(jumpInput.Text) - 1)); } };
        pageSize.SelectionChanged += async (_, _) => { ResetPages(); if (observabilityEnabled) await RunAsync(LoadQueries); };
        live.Checked += async (_, _) => { UpdateLive(); if (live.IsChecked == true) { ResetPages(); await RunAsync(LoadQueries); } }; live.Unchecked += (_, _) => UpdateLive();
        var pageInfo = new DnsFlowPanel { Gap = 16, RowGap = 8 }; pageInfo.Children.Add(position);
        var sizeLabel = Ui.Label("每页条数", 12); sizeLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); sizeLabel.Margin = new Thickness(0, 0, 6, 0); pageInfo.Children.Add(DnsVisuals.Inline(sizeLabel, pageSize)); pageInfo.Children.Add(pageRows);
        foreach (var button in new[] { first, previous, next, last }) pageActions.Children.Add(button);
        var jumpLabel = Ui.Label("跳至", 12); jumpLabel.Margin = new Thickness(0, 0, 6, 0); var jumpSuffix = Ui.Label("页", 12); jumpSuffix.Margin = new Thickness(6, 0, 8, 0);
        jumpLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); jumpSuffix.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        jumpFrame = new Border { Child = DnsVisuals.Inline(jumpLabel, jumpInput, jumpSuffix, jump), BorderThickness = new Thickness(1, 0, 0, 0), Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(12, 0, 0, 0) }; jumpFrame.SetResourceReference(Border.BorderBrushProperty, "DividerBrush"); pageActions.Children.Add(jumpFrame);
        var pagination = new DnsFlowPanel { Gap = 12, RowGap = 12, SpaceBetween = true }; pagination.Children.Add(pageInfo); pagination.Children.Add(pageActions);
        paginationFrame = new Border { Child = pagination, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 10, 14, 10) }; paginationFrame.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        var queryCard = DnsVisuals.Card(Ui.Column(TableSurface(queries, queryEmpty), paginationFrame), new Thickness(0), 12); queryCard.SetResourceReference(Border.BackgroundProperty, "DialogTopBrush"); queryLayout.Children.Add(queryCard);
        queryNote.Margin = new Thickness(0, 6, 0, 0); queryLayout.Children.Add(queryNote);
        queries.MouseDoubleClick += (_, _) => { if (queries.SelectedItem is QueryRow row) DnsQueryDialog.Show(this, row.Data); };

        var upstreamLayout = ContentPanel(); upstreamLayout.Children.Add(Heading("本次收集累计", upstreamUpdated, "RefreshDnsUpstreams", LoadUpstreams));
        var upstreamScope = DnsVisuals.Caption("统计业务解析的实际上游交换，包含后台刷新与辅助查询；节点域名与 DNS 服务器域名的基础解析、缓存命中和本地答案不计入。"); upstreamScope.Margin = new Thickness(0, 2, 0, 0);
        var upstreamRatio = DnsVisuals.Caption("成功率 = 成功 /（成功 + 错误），取消与进行中的交换不计入分母；超时和 RCODE 错误是错误总数的子集。竞速完成或停止等待都可能产生取消，不等于客户端解析失败。"); upstreamRatio.Margin = new Thickness(0, 2, 0, 0); upstreamDefinition.Margin = new Thickness(0, 2, 0, 4);
        upstreamLayout.Children.Add(Ui.Column(upstreamScope, upstreamRatio, upstreamDefinition));
        upstreamFilter.TextChanged += (_, _) => FilterUpstreams(); DnsVisuals.Input(upstreamFilter); upstreamFilter.Width = 260;
        var upstreamMenu = new ContextMenu(); upstreamMenu.Items.Add(new MenuItem { Header = upstreamFilter, StaysOpenOnClick = true }); upstreams.ContextMenu = upstreamMenu;
        var upstreamCard = DnsVisuals.Card(TableSurface(upstreams, upstreamEmpty), new Thickness(0), 12); upstreamCard.SetResourceReference(Border.BackgroundProperty, "DialogTopBrush"); upstreamLayout.Children.Add(upstreamCard); upstreamNote.Margin = new Thickness(0, 6, 0, 0); upstreamLayout.Children.Add(upstreamNote);
        upstreams.MouseDoubleClick += (_, _) => { if (upstreams.SelectedItem is UpstreamRow row) DetailsDialog.Show(this, "DNS 上游详情", DescribeUpstream(row.Data)); };
        tabs.Items.Add(L.Bind(new TabItem { Content = overview }.Id("DnsOverviewTab"), HeaderedContentControl.HeaderProperty, "概览"));
        tabs.Items.Add(L.Bind(new TabItem { Content = queryLayout }.Id("DnsQueriesTab"), HeaderedContentControl.HeaderProperty, "查询日志"));
        tabs.Items.Add(L.Bind(new TabItem { Content = upstreamLayout }.Id("DnsUpstreamsTab"), HeaderedContentControl.HeaderProperty, "上游统计"));

        stateIcon.Margin = new Thickness(0, 0, 0, 12); stateIcon.SetResourceReference(Control.ForegroundProperty, "TextTertiaryBrush"); stateTitle.Margin = new Thickness(0, 0, 0, 8); stateHint.LineHeight = 23.8; stateHint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        stateRetry = Action("重试", "RetryDns", RefreshAsync); stateRetry.HorizontalAlignment = HorizontalAlignment.Left; stateRetry.Margin = new Thickness(0, 16, 0, 0);
        stateCard = DnsVisuals.Card(Ui.Column(stateLoading, stateIcon, stateTitle, stateHint, stateRetry), new Thickness(24), 8); stateCard.MaxWidth = 640; stateCard.HorizontalAlignment = HorizontalAlignment.Stretch;
        var flushMenu = new ContextMenu(); var flush = L.Bind(new MenuItem().Id("FlushDnsCache"), HeaderedItemsControl.HeaderProperty, "清空 DNS 缓存");
        flush.Click += async (_, _) => await RunAsync(async () =>
        {
            await Session.ApiAsync(HttpMethod.Post, "cache/dns/flush"); Message("DNS 回答缓存已清空。");
        }); flushMenu.Items.Add(flush); capacity.ContextMenu = flushMenu;
        SetScrollableBody(Ui.Column(capacity, tabs, stateCard));
        Loaded += (_, _) => Responsive(); SizeChanged += (_, _) => Responsive();
        UpdatePaging(); ShowState("正在读取 DNS 数据…", "", true); RenderTrend();
    }
    private sealed record MetricView(Border Frame, TextBlock Value, TextBlock Hint, DnsMeter? Meter);
    private static StackPanel ContentPanel()
    {
        // TabItem selection colors belong to its header, not to the dashboard
        // content that WPF also treats as the item's logical child.
        var panel = new StackPanel(); panel.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush"); panel.SetValue(TextElement.FontWeightProperty, FontWeights.Normal); return panel;
    }
    private Border Section(UIElement content) { var card = DnsVisuals.Card(content); sections.Add(card); return card; }
    private static StackPanel Top(string label, DnsTopList list)
    {
        var hint = DnsVisuals.Caption("仅统计仍保留的查询明细，不包含已过期或被淘汰的记录。"); hint.Margin = new Thickness(0, 6, 0, 0); list.Margin = new Thickness(0, 12, 0, 0); return Ui.Column(Ui.Label(label, 14, true), hint, list);
    }
    private static StackPanel Field(string label, Control input) { var text = Ui.Label(label, 12); text.FontWeight = FontWeights.Medium; text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); text.Margin = new Thickness(0, 0, 0, 6); return Ui.Column(text, input); }
    private DnsFlowPanel Heading(string title, TextBlock updated, string id, Func<Task> operation)
    {
        var row = new DnsFlowPanel { Gap = 12, RowGap = 8, SpaceBetween = true, Margin = new Thickness(0, 0, 0, 12) }; row.Children.Add(Ui.Label(title, 14, true)); row.Children.Add(ResourceStatus(updated, id, operation)); return row;
    }
    private StackPanel ResourceStatus(TextBlock updated, string id, Func<Task> operation)
    {
        var icon = new FluentIcon("update", 14) { Margin = new Thickness(0, 0, 6, 0) };
        var button = Action("刷新", id, async () =>
        {
            ProxyVisuals.SetBusyIcon(icon, true);
            try { await operation(); }
            finally { ProxyVisuals.SetBusyIcon(icon, false); }
        });
        DnsVisuals.SmallButton(button); button.Content = DnsVisuals.Inline(icon, Ui.Label("刷新", 12)); button.Margin = new Thickness(12, 0, 0, 0); button.Style = FluentControls.Style("Ghost"); L.Bind(button, ToolTipProperty, "刷新"); return DnsVisuals.Inline(updated, button);
    }
    private static Grid TableSurface(DataGrid table, TextBlock empty)
    {
        var grid = new Grid(); grid.Children.Add(table);
        empty.FontSize = 13; empty.LineHeight = 18.59; empty.Margin = new Thickness(16, DnsVisuals.HeaderHeight + 32, 16, 33); empty.TextAlignment = TextAlignment.Center; empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Top; empty.IsHitTestVisible = false; grid.Children.Add(empty);
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false }; line.SetResourceReference(Border.BackgroundProperty, "DividerBrush"); line.SetBinding(VisibilityProperty, new Binding(nameof(empty.Visibility)) { Source = empty }); grid.Children.Add(line); return grid;
    }
    private void Responsive()
    {
        double width = Window.GetWindow(this)?.ActualWidth ?? ActualWidth, height = Window.GetWindow(this)?.ActualHeight ?? 780;
        int count = width <= 768 ? 2 : width <= 1200 ? 3 : 5;
        statistics.ColumnDefinitions.Clear(); statistics.RowDefinitions.Clear(); for (int index = 0; index < count; index++) statistics.ColumnDefinitions.Add(new ColumnDefinition());
        for (int index = 0; index < (5 + count - 1) / count; index++) statistics.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int index = 0; index < metrics.Count; index++)
        {
            var metric = metrics[index]; Grid.SetColumn(metric.Frame, index % count); Grid.SetRow(metric.Frame, index / count); Grid.SetColumnSpan(metric.Frame, width <= 768 && index == 4 ? 2 : 1);
            metric.Frame.BorderThickness = new Thickness(index % count == 0 ? 0 : 1, index >= count ? 1 : 0, 0, 0); metric.Frame.Padding = width <= 768 ? new Thickness(16, 12, 16, 12) : new Thickness(20, 16, 20, 16); metric.Value.FontSize = width <= 768 ? 20 : 28;
        }
        topGrid.ColumnDefinitions.Clear(); topGrid.RowDefinitions.Clear(); topGrid.ColumnDefinitions.Add(new ColumnDefinition());
        if (width <= 768) { topGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); topGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
        else { topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); topGrid.ColumnDefinitions.Add(new ColumnDefinition()); }
        if (topGrid.Children.Count > 1) { Grid.SetRow(topGrid.Children[1], width <= 768 ? 1 : 0); Grid.SetColumn(topGrid.Children[1], width <= 768 ? 0 : 2); }
        foreach (var card in sections) card.Padding = width <= 768 ? new Thickness(16, 12, 16, 12) : new Thickness(20, 16, 20, 16);
        queries.MaxHeight = upstreams.MaxHeight = Math.Max(160, height * .62);
        trendHeading.ForceColumn = width <= 768; trendHeading.InvalidateMeasure();
        pageActions.Narrow = width <= 768; pageActions.InvalidateMeasure();
        if (jumpFrame is not null)
        {
            jumpFrame.Padding = width <= 768 ? new Thickness(0) : new Thickness(12, 0, 0, 0); jumpFrame.Margin = width <= 768 ? new Thickness(0) : new Thickness(4, 0, 0, 0); jumpFrame.BorderThickness = width <= 768 ? new Thickness(0) : new Thickness(1, 0, 0, 0);
            if (jumpFrame.Child is FrameworkElement child) child.HorizontalAlignment = width <= 768 ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
        if (paginationFrame is not null) paginationFrame.Padding = new Thickness(width <= 768 ? 12 : 14, 10, width <= 768 ? 12 : 14, 10);
    }
    private void ShowState(string title, string hint = "", bool loading = false)
    {
        tabs.Visibility = capacity.Visibility = Visibility.Collapsed; stateCard.Visibility = Visibility.Visible;
        L.Bind(stateTitle, TextBlock.TextProperty, title); L.Bind(stateHint, TextBlock.TextProperty, hint); stateHint.Visibility = hint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        stateLoading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed; stateIcon.Visibility = loading ? Visibility.Collapsed : Visibility.Visible; stateRetry.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
    }
    private void UpdateLive()
    {
        bool running = live.IsChecked == true && page == 0; liveIcon.Kind = running ? "pause" : "play"; liveLabel.Text = L.T(running ? "暂停刷新" : "开始实时刷新");
        AutomationProperties.SetName(live, liveLabel.Text); liveDot.Visibility = running ? Visibility.Visible : Visibility.Collapsed; liveHint.Text = L.T(page > 0 ? "正在查看历史页，自动刷新已暂停。" : running ? "最新一页每 5 秒刷新一次。" : "已暂停自动刷新，便于稳定阅读。");
    }
    private void ConfigureQueryColumns()
    {
        ((DataGridTextColumn)queries.Columns[0]).ElementStyle = DnsVisuals.TextStyle(true, 12);
        ((DataGridTextColumn)queries.Columns[2]).ElementStyle = DnsVisuals.TextStyle(true, 12);
        ((DataGridTextColumn)queries.Columns[6]).ElementStyle = DnsVisuals.TextStyle(true, 12);
        var domainColumn = new DataGridTemplateColumn { Width = queries.Columns[1].Width, MinWidth = 0, CanUserResize = false };
        L.Bind(domainColumn, DataGridColumn.HeaderProperty, "域名");
        var domainTemplate = new DataTemplate(); var button = new FrameworkElementFactory(typeof(Button)); button.SetBinding(ContentControl.ContentProperty, new Binding(nameof(QueryRow.Domain))); button.SetBinding(ToolTipProperty, new Binding(nameof(QueryRow.Domain))); button.SetValue(Control.TemplateProperty, LinkTemplate); button.SetValue(Control.BackgroundProperty, Brushes.Transparent); button.SetValue(Control.BorderThicknessProperty, new Thickness(0)); button.SetValue(Control.PaddingProperty, new Thickness(0)); button.SetValue(FrameworkElement.MaxWidthProperty, 320d); button.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left); button.SetValue(Control.FontSizeProperty, 13d); button.SetValue(Control.FontWeightProperty, FontWeights.Medium); button.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) => { if ((sender as FrameworkElement)?.DataContext is QueryRow row) DnsQueryDialog.Show(this, row.Data); })); domainTemplate.VisualTree = button; domainColumn.CellTemplate = domainTemplate; queries.Columns[1] = domainColumn;
        var outcomeColumn = new DataGridTemplateColumn { Width = DataGridLength.Auto, MinWidth = 0, CanUserResize = false }; L.Bind(outcomeColumn, DataGridColumn.HeaderProperty, "结果来源");
        var outcomeTemplate = new DataTemplate(); var pill = new FrameworkElementFactory(typeof(DnsOutcome)); pill.SetBinding(DnsOutcome.ValueProperty, new Binding(nameof(QueryRow.OutcomeKey))); outcomeTemplate.VisualTree = pill; outcomeColumn.CellTemplate = outcomeTemplate; queries.Columns[4] = outcomeColumn;
    }
    private void ConfigureUpstreamColumns()
    {
        ((DataGridTextColumn)upstreams.Columns[0]).ElementStyle = DnsVisuals.TextStyle(false, 13, true);
        var addressStyle = DnsVisuals.TextStyle(true, 12, false, true); addressStyle.Setters.Add(new Setter(MaxWidthProperty, 480d));
        ((DataGridTextColumn)upstreams.Columns[1]).ElementStyle = addressStyle;
        foreach (int index in new[] { 2, 3, 6, 7, 8, 9 }) ((DataGridTextColumn)upstreams.Columns[index]).ElementStyle = DnsVisuals.TextStyle(true, 12);
        foreach (int index in new[] { 2, 3, 6, 7 }) if (((DataGridTextColumn)upstreams.Columns[index]).Binding is Binding binding) binding.StringFormat = "N0";
        for (int index = 4; index <= 5; index++)
        {
            bool error = index == 4; var column = new DataGridTemplateColumn { Width = DataGridLength.Auto, MinWidth = 0, CanUserResize = false }; L.Bind(column, DataGridColumn.HeaderProperty, error ? "错误" : "取消");
            var template = new DataTemplate(); var panel = new FrameworkElementFactory(typeof(StackPanel)); var value = new FrameworkElementFactory(typeof(TextBlock)); value.SetBinding(TextBlock.TextProperty, new Binding(error ? nameof(UpstreamRow.Errors) : nameof(UpstreamRow.Canceled)) { StringFormat = "N0" }); value.SetValue(TextBlock.FontFamilyProperty, DnsVisuals.Mono); value.SetValue(TextBlock.FontSizeProperty, 12d); value.SetValue(TextBlock.LineHeightProperty, 17.16); panel.AppendChild(value);
            string waitPath = error ? nameof(UpstreamRow.ErrorWait) : nameof(UpstreamRow.CanceledWait);
            var wait = new FrameworkElementFactory(typeof(TextBlock), "Wait"); wait.SetBinding(TextBlock.TextProperty, new Binding(waitPath)); wait.SetValue(TextBlock.FontFamilyProperty, DnsVisuals.Mono); wait.SetValue(TextBlock.FontSizeProperty, 11d); wait.SetValue(TextBlock.LineHeightProperty, 15.73); wait.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0)); wait.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); panel.AppendChild(wait); template.VisualTree = panel;
            var hideEmpty = new DataTrigger { Binding = new Binding(waitPath), Value = "" }; hideEmpty.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "Wait")); template.Triggers.Add(hideEmpty); column.CellTemplate = template; upstreams.Columns[index] = column;
        }
    }
    private static readonly ControlTemplate LinkTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <TextBlock Name="Label" Text="{TemplateBinding Content}" FontFamily="{TemplateBinding FontFamily}" FontSize="{TemplateBinding FontSize}" FontWeight="{TemplateBinding FontWeight}" LineHeight="18.59" TextWrapping="NoWrap" TextTrimming="CharacterEllipsis" Foreground="{DynamicResource TextPrimaryBrush}"/>
          <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Label" Property="Foreground" Value="{DynamicResource AccentBrush}"/><Setter TargetName="Label" Property="TextDecorations" Value="Underline"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate>
        """);
    public override async Task RefreshAsync()
    {
        if (await LoadMetadata()) await Task.WhenAll(LoadStats(), LoadUpstreams(), LoadQueries());
    }
    private async Task<bool> LoadMetadata()
    {
        if (!IsRunning) { observabilityEnabled = false; ShowState(InactiveMessage); Message(""); return false; }
        int read = ++metaRead;
        string expectedController = ControllerIdentity;
        var meta = await Get("dns/observability");
        if (!IsActive || !IsRunning || read != metaRead || expectedController != ControllerIdentity) return false;
        controller = expectedController;
        metadataAt = Stopwatch.GetTimestamp();
        latestMeta = meta;
        observabilityEnabled = meta.Bool("enabled") && meta.Number("version") == 1 && meta.Text("storage") == "memory";
        tabs.Visibility = observabilityEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (meta.Number("version") != 1 || meta.Text("storage") != "memory") { ResetPages(); ShowState("当前面板不支持此 DNS 接口版本。"); return false; }
        if (!meta.Bool("enabled")) { ResetPages(); ShowState("DNS 查询统计未开启。", "在内核配置中开启 DNS 查询统计后，才能收集查询数据。"); return false; }
        Message("");
        if (instance != meta.Text("instance_id")) { instance = meta.Text("instance_id"); ResetPages(); latestQueries = latestStats = latestUpstreams = default; }
        stateCard.Visibility = Visibility.Collapsed; capacity.Visibility = Visibility.Visible; RenderCapacity(); return true;
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
        ShowState("正在读取 DNS 数据…", "", true);
        return false;
    }
    private async Task LoadStats()
    {
        string expected = instance; var data = await Get("dns/observability/stats"); if (!Current(data, expected)) return; latestStats = data; statsAt = DateTimeOffset.Now; RenderStats();
    }
    private void RenderCapacity()
    {
        capacity.Children.Clear();
        var memoryText = DnsVisuals.Caption("仅保存在内存中 · 内核重启后清空"); memoryText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var icon = new FluentIcon("info", 14) { Margin = new Thickness(0, 0, 6, 0) }; icon.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush"); capacity.Children.Add(DnsVisuals.Inline(icon, memoryText));
        capacity.Children.Add(Fact("最长保留", DnsVisuals.Number(latestMeta.Number("retention_seconds") / 3600) + " h"));
        capacity.Children.Add(Fact("明细条数上限", DnsVisuals.Number(latestMeta.Number("capacity"))));
        double memory = latestMeta.Number("accounted_bytes"), limit = latestMeta.Number("memory_limit_bytes");
        var budget = Fact("记录记账预算", DnsVisuals.Number(memory / 1048576, 2) + " / " + DnsVisuals.Number(limit / 1048576, 1) + " MiB");
        var meter = new DnsMeter { Width = 48, Height = 4, Duration = 0, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; meter.Set(limit > 0 ? memory / limit : 0);
        capacity.Children.Add(DnsVisuals.Inline(budget, meter));
        capacity.Children.Add(Fact("开始收集时间", DnsVisuals.Time(latestMeta.Text("started_at"), true)));
        static TextBlock Fact(string label, string value)
        {
            var text = DnsVisuals.Caption("", false); text.Inlines.Add(new Run(L.T(label) + ": "));
            var number = new Run(value) { FontWeight = FontWeights.Medium }; number.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush"); text.Inlines.Add(number); return text;
        }
    }
    private void RenderStats()
    {
        var counts = latestStats.Get("last_24h"); var totals = latestStats.Get("totals"); double queriesCount = counts.Number("queries"), hits = counts.Number("cache_fresh") + counts.Number("cache_stale");
        var series = latestStats.Get("series").Items().ToArray(); double qps = series.Length >= 2 ? series[^2].Number("queries") / 60 : 0;
        metrics[0].Value.Text = DnsVisuals.Number(queriesCount); metrics[0].Hint.Text = L.F("本次收集累计: {0}", DnsVisuals.Number(totals.Number("queries")));
        metrics[1].Value.Text = DnsVisuals.Number(qps, 2); metrics[1].Hint.Text = L.T("上一完整分钟的平均每秒查询量");
        metrics[2].Value.Text = Ratio(hits, queriesCount); metrics[2].Hint.Text = L.F("有效缓存: {0} · 过期缓存: {1} · 本次收集累计: {2}", DnsVisuals.Number(counts.Number("cache_fresh")), DnsVisuals.Number(counts.Number("cache_stale")), Ratio(totals.Number("cache_fresh") + totals.Number("cache_stale"), totals.Number("queries")));
        metrics[2].Meter?.Set(queriesCount > 0 ? counts.Number("cache_fresh") / queriesCount : 0, queriesCount > 0 ? counts.Number("cache_stale") / queriesCount : 0);
        metrics[3].Value.Text = DnsVisuals.Number(counts.Number("errors")); metrics[3].Hint.Text = L.F("拒绝: {0} · 丢弃: {1}", DnsVisuals.Number(counts.Number("reject")), DnsVisuals.Number(counts.Number("drop")));
        metrics[4].Value.Text = DnsVisuals.Number(counts.Number("elapsed_ms_avg"), 2) + " ms"; metrics[4].Hint.Text = L.F("P95（近似）: {0} ms", DnsVisuals.Number(counts.Number("elapsed_ms_p95"), 2));
        domains.Set(latestStats.Get("top_domains").Items().Select(item => (item.Text("name"), item.Number("count")))); clients.Set(latestStats.Get("top_clients").Items().Select(item => (item.Text("name"), item.Number("count"))));
        overviewUpdated.Text = Updated(statsAt); RenderTrend();
    }
    private static string Ratio(double hits, double count) => count > 0 ? DnsVisuals.Number(hits * 100 / count, 1) + "%" : "—";
    private static string Updated(DateTimeOffset? date) => date.HasValue ? L.T("更新于") + ": " + DnsVisuals.Time(date.Value) : "";
    private void RenderTrend()
    {
        int hours = duration.SelectedIndex == 0 ? 1 : duration.SelectedIndex == 1 ? 6 : 24;
        trend.SetSeries(latestStats.Get("series"), hours);
        trendUnit.Text = hours == 1 ? L.T("每分钟查询数") : L.F("每分钟查询数 · {0} 分钟均值", hours == 6 ? 5 : 10);
        trend.Visibility = legend.Visibility = trend.HasPoints ? Visibility.Visible : Visibility.Collapsed; trendEmpty.Visibility = trend.HasPoints ? Visibility.Collapsed : Visibility.Visible; rangeEmpty.Visibility = trend.EmptyRange ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task LoadUpstreams() { string expected = instance; var data = await Get("dns/observability/upstreams"); if (!Current(data, expected)) return; latestUpstreams = data; upstreamsAt = DateTimeOffset.Now; RenderUpstreams(); }
    private void RenderUpstreams()
    {
        upstreamRows = latestUpstreams.Get("items").Items().Select(item => new UpstreamRow(item)).ToArray(); FilterUpstreams();
        bool legacy = upstreamRows.Length > 0 && upstreamRows.All(row => row.Data.Get("success_elapsed_ms_avg").ValueKind == JsonValueKind.Undefined);
        L.Bind(upstreams.Columns[9], DataGridColumn.HeaderProperty, legacy ? "综合平均耗时" : "成功平均耗时");
        FitUpstreamColumns();
        upstreamDefinition.Text = L.T(legacy ? "此内核仅提供含失败、取消在内的综合平均耗时，可能未提供成功耗时与实际出站；未知出站不视为 DIRECT。" : "耗时列仅统计成功交换；错误与取消下方分别显示对应的平均等待时间。同一上游按已确认的实际出站分别统计，未知出站不视为 DIRECT。");
        upstreamNote.Text = L.F("上游数量上限: {0} · 超过上游数量上限的交换: {1}", DnsVisuals.Number(latestUpstreams.Number("limit")), DnsVisuals.Number(latestUpstreams.Number("overflowed"))); upstreamUpdated.Text = Updated(upstreamsAt);
    }
    private void FilterUpstreams()
    {
        upstreams.ItemsSource = upstreamRows.Where(row => (row.Address + " " + row.Outbound).Contains(upstreamFilter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
        upstreamEmpty.Visibility = upstreams.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible; upstreams.MinHeight = upstreams.Items.Count > 0 ? 0 : DnsVisuals.EmptyTableHeight; FitUpstreamColumns();
    }
    private Task ApplyFilters() { queryFilter = MakeFilter(); ResetPages(); return LoadQueries(); }
    private string MakeFilter()
    {
        var parts = new List<string>();
        if (domain.Text.Trim().Length > 0) parts.Add("qname=" + Uri.EscapeDataString(domain.Text.Trim())); if (client.Text.Trim().Length > 0) parts.Add("client=" + Uri.EscapeDataString(client.Text.Trim()));
        if (type.Text.Trim().Length > 0) parts.Add("qtype=" + Uri.EscapeDataString(type.Text.Trim().ToUpperInvariant())); if (outcome.SelectedValue is string result && result.Length > 0) parts.Add("outcome=" + Uri.EscapeDataString(result));
        return parts.Count > 0 ? "&" + string.Join('&', parts) : "";
    }
    private void ResetPages() { queryRead++; queryBusy = false; page = 0; cursors.Clear(); cursors.Add(""); anchor = ""; total = null; queryRows = []; queries.ItemsSource = null; latestQueries = default; jumpInput.Clear(); UpdatePaging(); }
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
            latestQueries = data; queriesAt = DateTimeOffset.Now; RenderQueries();
            if (queries.Items.Count > 0) queries.ScrollIntoView(queries.Items[0]);
        }
        finally { if (read == queryRead) { queryBusy = false; UpdatePaging(); } }
    }
    private void RenderQueries()
    {
        queryRows = latestQueries.Get("items").Items().Select(item => new QueryRow(item)).ToArray(); queries.ItemsSource = queryRows;
        string location = L.F("第 {0} / {1} 页 · 共 {2} 条", DnsVisuals.Number(page + 1), total.HasValue ? DnsVisuals.Number(cursors.Count) : "—", total.HasValue ? DnsVisuals.Number(total.Value) : "—");
        position.Inlines.Clear(); int offset = 0;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(location, @"[\d][\d,.\u00a0]*|—"))
        {
            position.Inlines.Add(new Run(location[offset..match.Index])); var reading = new Run(match.Value) { FontWeight = FontWeights.Medium }; reading.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush"); position.Inlines.Add(reading); offset = match.Index + match.Length;
        }
        position.Inlines.Add(new Run(location[offset..])); pageRows.Text = L.F("本页 {0} 条", DnsVisuals.Number(queries.Items.Count));
        queryNote.Text = (!total.HasValue ? L.T("更新内核后可显示总数并直接跳页。") + "\n" : "") + L.T("仅统计仍保留的查询明细，不包含已过期或被淘汰的记录。") + " · " + L.F("保留明细: {0} · 已淘汰: {1}", DnsVisuals.Number(latestQueries.Number("retained")), DnsVisuals.Number(latestQueries.Number("evicted")));
        queryUpdated.Text = Updated(queriesAt); queryEmpty.Text = L.T("保留的明细中没有符合条件的查询。"); queryEmpty.Visibility = queries.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible; queries.MinHeight = queries.Items.Count > 0 ? 0 : DnsVisuals.EmptyTableHeight; FitQueryColumns(); UpdatePaging();
    }
    private void FitQueryColumns()
    {
        var widths = new double[queries.Columns.Count];
        foreach (var row in queryRows)
        {
            string[] values = [row.Time, row.Domain, row.Type, row.Client, row.Outcome, row.Rcode, row.Latency];
            for (int index = 0; index < values.Length; index++)
            {
                bool mono = index is 0 or 2 or 6;
                double width = DnsVisuals.TextWidth(queries, values[index], mono, mono || index == 4 ? 12 : 13, index is 1 or 4 ? FontWeights.Medium : FontWeights.Normal);
                if (index == 1) width = Math.Min(320, width);
                if (index == 4) width += 30; // pill padding, six-pixel lamp and gap
                widths[index] = Math.Max(widths[index], width);
            }
        }
        DnsVisuals.FitColumns(queries, widths);
    }
    private void FitUpstreamColumns()
    {
        var widths = new double[upstreams.Columns.Count];
        foreach (var row in upstreams.Items.OfType<UpstreamRow>())
        {
            string[] values = [row.Outbound, row.Address, DnsVisuals.Number(row.Attempts), DnsVisuals.Number(row.Successes), DnsVisuals.Number(row.Errors), DnsVisuals.Number(row.Canceled), DnsVisuals.Number(row.Timeouts), DnsVisuals.Number(row.RcodeErrors), row.Ratio, row.Latency];
            for (int index = 0; index < values.Length; index++)
            {
                double width = DnsVisuals.TextWidth(upstreams, values[index], index != 0, index == 0 ? 13 : 12, index is 0 or 8 or 9 ? FontWeights.SemiBold : FontWeights.Normal);
                if (index == 1) width = Math.Clamp(width, DnsVisuals.TextWidth(upstreams, new string('0', 20), true, 12), 480);
                if (index is 4 or 5) width = Math.Max(width, DnsVisuals.TextWidth(upstreams, index == 4 ? row.ErrorWait : row.CanceledWait, true, 11));
                widths[index] = Math.Max(widths[index], width);
            }
        }
        DnsVisuals.FitColumns(upstreams, widths);
    }
    private void UpdatePaging()
    {
        bool idle = !queryBusy && observabilityEnabled;
        first.IsEnabled = previous.IsEnabled = idle && page > 0; next.IsEnabled = idle && latestQueries.Bool("has_more") && page + 1 < cursors.Count;
        last.IsEnabled = idle && total.HasValue && page + 1 < cursors.Count; jumpInput.IsEnabled = total.HasValue;
        jumpInput.Tag = (page + 1).ToString(CultureInfo.InvariantCulture);
        jump.IsEnabled = idle && total.HasValue && int.TryParse(jumpInput.Text, out int target) && target > 0 && target <= cursors.Count && target != page + 1; live.IsEnabled = page == 0; UpdateLive();
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
        if (!IsRunning) { observabilityEnabled = false; ShowState(InactiveMessage); Message(""); }
        var current = ControllerIdentity; if (controller != current) { metaRead++; observabilityEnabled = false; controller = current; instance = ""; ResetPages(); latestMeta = latestStats = latestUpstreams = default; upstreams.ItemsSource = null; }
        UpdatePaging();
    }
    private static string DescribeUpstream(JsonElement item) => L.F("上游：{0}\n实际出口：{1}\n出口类型：{2}\n\n尝试：{3:N0}\n成功：{4:N0}\n错误：{5:N0}\n取消：{6:N0}\n超时：{7:N0}\nRCODE 错误：{8:N0}\n\n全部平均耗时：{9:0.##} ms\n成功平均耗时：{10:0.##} ms\n错误平均耗时：{11:0.##} ms\n取消平均耗时：{12:0.##} ms\n\n取消单独统计，不与错误合并。", item.Text("address"), item.Text("outbound", fallback: L.T("未归因")), item.Text("outbound_type", fallback: "unknown"), item.Number("attempts"), item.Number("successes"), item.Number("errors"), item.Number("canceled"), item.Number("timeouts"), item.Number("rcode_errors"), item.Number("elapsed_ms_avg"), item.Number("success_elapsed_ms_avg"), item.Number("error_elapsed_ms_avg"), item.Number("canceled_elapsed_ms_avg"));
    private static string OutcomeLabel(string value) => value switch { "cache_fresh" => "有效缓存", "cache_stale" => "过期缓存", "hosts" => "Hosts", "fake_ip" => "Fake IP", "upstream" => "上游", "reject" => "拒绝", "drop" => "丢弃", "error" => "错误", "local" => "本地答复", _ => value };
    public sealed record QueryRow(JsonElement Data)
    {
        public string Time => DnsVisuals.Time(Data.Text("time")); public string Domain => Data.Text("qname"); public string Type => Data.Text("qtype"); public string Client => Data.Text("client") == "unknown" ? L.T("未知") : Data.Text("client"); public string OutcomeKey => Data.Text("outcome"); public string Outcome => L.T(OutcomeLabel(OutcomeKey)); public string Latency => Data.Number("elapsed_ms").ToString("0.##") + " ms"; public string Rcode => Data.Text("rcode", fallback: "—");
    }
    public sealed record UpstreamRow(JsonElement Data)
    {
        public string Address => Data.Text("address"); public string Outbound => Data.Text("outbound", fallback: L.T("未知 / 未归因")); public double Attempts => Data.Number("attempts"); public double Successes => Data.Number("successes"); public double Errors => Data.Number("errors"); public double Canceled => Data.Number("canceled"); public double Timeouts => Data.Number("timeouts"); public double RcodeErrors => Data.Number("rcode_errors"); public string Ratio => Successes + Errors > 0 ? DnsVisuals.Number(Successes * 100 / (Successes + Errors), 1) + "%" : "—";
        private string Elapsed(string key, double count) => count > 0 && Data.Get(key).ValueKind == JsonValueKind.Number ? Data.Number(key).ToString("0.##") + " ms" : "—";
        public string Latency => Data.Get("success_elapsed_ms_avg").ValueKind == JsonValueKind.Number ? Elapsed("success_elapsed_ms_avg", Successes) : Elapsed("elapsed_ms_avg", Successes + Errors + Canceled); public string ErrorLatency => Elapsed("error_elapsed_ms_avg", Errors); public string CanceledLatency => Elapsed("canceled_elapsed_ms_avg", Canceled);
        public string ErrorWait => ErrorLatency == "—" ? "" : L.F("均值 {0}", ErrorLatency); public string CanceledWait => CanceledLatency == "—" ? "" : L.F("均值 {0}", CanceledLatency);
    }
}
