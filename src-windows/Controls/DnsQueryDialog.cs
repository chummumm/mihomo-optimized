using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

internal static class DnsQueryDialog
{
    internal static void Show(FrameworkElement owner, JsonElement query)
    {
        var window = Window.GetWindow(owner); if (window is null) return;
        bool narrow = window.ActualWidth <= 768;
        var body = new StackPanel(); var fields = new Grid();
        fields.ColumnDefinitions.Add(new ColumnDefinition());
        if (!narrow) { fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); fields.ColumnDefinitions.Add(new ColumnDefinition()); }
        string outcome = query.Text("outcome"), cache = query.Text("cache");
        bool cached = cache is "fresh" or "stale", local = outcome is "hosts" or "fake_ip" or "local" or "reject" or "drop";
        string noExchange = L.T(cached ? "缓存命中" : local ? "本地应答（本次未访问上游）" : "未知 / 未归因");
        var upstreams = query.Get("upstreams").Items().ToArray();
        string Reused(JsonElement item) => item.Bool("reused") ? " (" + L.T("复用候选答案") + ")" : "";
        string addresses = string.Join('\n', upstreams.Select(item => item.Text("address") + Reused(item)));
        string exits = string.Join('\n', upstreams.Select(item => item.Text("outbound", fallback: L.T("未知 / 未归因")) + Reused(item)).Distinct());
        string Unknown(string value) => value == "unknown" ? L.T("未知") : value;
        var values = new (string Label, string Value)[]
        {
            ("域名", query.Text("qname")),
            ("时间", DateTimeOffset.TryParse(query.Text("time"), out var time) ? time.ToLocalTime().ToString(CultureInfo.CurrentCulture) : "—"),
            ("客户端", Unknown(query.Text("client"))), ("类型", query.Text("qtype")), ("协议", Unknown(query.Text("protocol"))), ("查询来源", Unknown(query.Text("source"))),
            ("结果来源", L.T(outcome switch { "cache_fresh" => "有效缓存", "cache_stale" => "过期缓存", "hosts" => "Hosts", "fake_ip" => "Fake IP", "upstream" => "上游", "reject" => "拒绝", "drop" => "丢弃", "error" => "错误", "local" => "本地答复", _ => outcome })),
            ("缓存", L.T(cache switch { "fresh" => "有效缓存命中", "stale" => "过期缓存命中", "miss" => "未命中", "none" => "未使用", _ => cache })),
            ("RCODE", query.Text("rcode", fallback: L.T("没有 DNS 答复"))), ("耗时", DnsVisuals.Number(query.Number("elapsed_ms"), 2) + " ms"),
            ("上游", addresses.Length > 0 ? addresses : query.Text("upstream", fallback: noExchange)),
            ("实际出站", exits.Length > 0 ? exits : noExchange), ("错误类别", query.Text("error", fallback: "—"))
        };
        for (int index = 0; index < values.Length; index++)
        {
            int row = index == 0 ? 0 : narrow ? index : 1 + (index - 1) / 2;
            while (fields.RowDefinitions.Count <= row) fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(narrow ? 84 : 96) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            var label = DnsVisuals.Caption(values[index].Label); label.LineHeight = 20; label.VerticalAlignment = VerticalAlignment.Top;
            var value = Ui.Text(values[index].Value, 12, index == 0); value.FontFamily = DnsVisuals.Mono; value.LineHeight = 20; value.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(value, 2); grid.Children.Add(label); grid.Children.Add(value);
            var frame = new Border { Child = grid, Padding = new Thickness(0, 7, 0, 7), BorderThickness = new Thickness(0, 0, 0, 1) }; frame.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
            Grid.SetRow(frame, row); Grid.SetColumn(frame, index == 0 || narrow ? 0 : (index - 1) % 2 * 2); if (index == 0) Grid.SetColumnSpan(frame, narrow ? 1 : 3); fields.Children.Add(frame);
        }
        body.Children.Add(fields);
        var heading = Ui.Label("答案记录", 13, true); heading.Margin = new Thickness(0, 20, 0, 8); body.Children.Add(heading);
        var answers = query.Get("answers").Items().Select(item => new Answer(item.Text("type"), item.Text("value"), DnsVisuals.Number(item.Number("ttl")))).ToArray();
        if (answers.Length == 0) body.Children.Add(DnsVisuals.Caption("没有答案记录。"));
        else
        {
            var table = Ui.Table(("类型", nameof(Answer.Type), 80), ("内容", nameof(Answer.Value), -1), ("TTL (s)", nameof(Answer.Ttl), 90)); DnsVisuals.Table(table);
            table.ItemsSource = answers; table.MaxHeight = window.ActualHeight * .62;
            foreach (var column in table.Columns.OfType<DataGridTextColumn>()) column.ElementStyle = DnsVisuals.TextStyle(true, 12, false, true);
            var border = new Border { Child = table, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) }; border.SetResourceReference(Border.BorderBrushProperty, "DividerBrush"); body.Children.Add(border);
        }
        if (query.Bool("answers_truncated")) body.Children.Add(Notice("此处只保留了部分答案记录，不表示实际 DNS 答复被截断。"));
        if (query.Bool("upstreams_truncated")) body.Children.Add(Notice("此处只保留了部分上游来源。"));
        var attribution = DnsVisuals.Caption("显示最终答案对应的上游与实际出口；多上游合并时列出参与来源。候选答案复用会单独标注，缓存命中与本地应答不代表本次访问了上游。"); attribution.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(attribution);
        var close = new Button { Content = Ui.Label("关闭"), Height = 32, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        body.Children.Add(close);
        var dialog = new FluentDialog(window, "DNS 查询详情", body, [], true, DialogDismissal.Presence).Id("DnsQueryDetails"); close.Click += (_, _) => dialog.Complete(false); dialog.ShowDialog();
    }
    private static Border Notice(string message)
    {
        var text = Ui.Label(message, 12); text.LineHeight = 19.2; text.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        var border = new Border { Child = text, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 8, 0, 12) }; border.SetResourceReference(Border.BackgroundProperty, "CriticalBackgroundBrush"); return border;
    }
    private sealed record Answer(string Type, string Value, string Ttl);
}
