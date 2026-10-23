using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

/// <summary>Follows a controller-owned update task; a lost POST is observed, never replayed.</summary>
internal sealed class ResourceUpdatePanel : UserControl
{
    private readonly AppSession session;
    private readonly StackPanel rows = new();
    private readonly TextBlock summary = Ui.Muted("");
    private readonly Button start;
    private readonly MenuItem refresh;
    private readonly MenuItem managedDetails;
    private readonly Border sources;
    private readonly Dictionary<string, ProxyExpander> groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> statusStamps = new(StringComparer.Ordinal);
    private readonly HashSet<string> open = new(StringComparer.Ordinal);
    private JsonElement snapshot;
    private JsonElement previous;
    private string controller = "";
    private string? awaitingRequest;
    private string? awaitingInstance;
    private string problem = "";
    private bool busy;
    private bool unconfirmed;
    private bool restarted;
    private bool unsupported;
    private bool followExpired;
    private bool restartVerified;
    private long requestedAt;
    private long lastRead;
    private long? failedSince;
    private CancellationToken pageToken;
    public bool NeedsRefresh => !busy && !unsupported && (!followExpired && (Active || awaitingRequest is not null) || !Active && Environment.TickCount64 - lastRead >= 30000);
    private bool Running => session.Status.Text("phase") == "running";
    private bool Active => snapshot.Text("state") is "running" or "restarting";

    public ResourceUpdatePanel(AppSession session)
    {
        this.session = session; this.Id("ResourceUpdateTable");
        start = Button("检查并更新资源", "UpdateResources", StartAsync);
        refresh = L.Bind(new MenuItem().Id("RefreshResources"), HeaderedItemsControl.HeaderProperty, "刷新状态"); refresh.Click += async (_, _) => { try { await RefreshAsync(pageToken); } catch (OperationCanceledException) { } catch (Exception error) { problem = error.Message; Render(); } };
        managedDetails = L.Bind(new MenuItem().Id("ResourceManagedDetails"), HeaderedItemsControl.HeaderProperty, "内核与面板任务明细");
        var menu = new ContextMenu(); menu.Items.Add(refresh); menu.Items.Add(managedDetails); ContextMenu = menu;
        var footerContent = new Grid(); footerContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); footerContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); footerContent.ColumnDefinitions.Add(new ColumnDefinition()); footerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); summary.Margin = new Thickness(0, 0, 16, 0); summary.LineHeight = 16; summary.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; footerContent.Children.Add(summary); Grid.SetColumn(start, 1); footerContent.Children.Add(start);
        var footer = new Border { Child = footerContent, Padding = new Thickness(16, 12, 16, 12), MinHeight = 56 }; footer.SetResourceReference(Border.BackgroundProperty, "SubtleSurfaceBrush");
        var policy = Ui.Hint("以下更新仅处理规则与地理数据库。应用、内核及内置界面由桌面更新统一管理。"); policy.LineHeight = 16; policy.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; policy.Margin = new Thickness(0, 0, 0, 4);
        var schedule = Ui.Hint("检查时间按系统的时钟和时区显示，发现变化后才会实际更新。"); schedule.LineHeight = 16; schedule.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        sources = new Border { Child = Ui.Column(policy, schedule), Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(0, 1, 0, 0) }; sources.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        var card = Ui.Card(Ui.Column(rows, sources, footer), 0); card.CornerRadius = new CornerRadius(4); card.Margin = new Thickness(0); Content = card; L.Changed += Render;
        SizeChanged += (_, _) => { var compact = FindContainerWidth() <= 560; Grid.SetColumn(start, compact ? 0 : 1); Grid.SetRow(start, compact ? 1 : 0); start.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right; start.Margin = new Thickness(0, compact ? 8 : 0, 0, 0); };
    }
    private Button Button(string title, string id, Func<Task> operation)
    {
        var button = L.Bind(new Button { MinHeight = 32, Padding = new Thickness(12, 5, 12, 5) }.Id(id), ContentControl.ContentProperty, title);
        L.Bind(button, AutomationProperties.NameProperty, title);
        button.Click += async (_, _) => { try { await operation(); } catch (OperationCanceledException) { } catch (Exception error) { problem = error.Message; Render(); } };
        return button;
    }

    public void UpdateAvailability(CancellationToken token)
    {
        pageToken = token;
        if (!Running)
        {
            rows.Children.Clear(); sources.Visibility = Visibility.Collapsed; summary.Text = L.T(session.Status.Text("phase") switch
            {
                "starting" => "正在准备启动资源并确认内核就绪，请稍候。",
                "connecting" => "正在连接本地服务，请稍候。",
                "stopping" => "正在停止内核，请稍候。",
                _ => "启动内核后可检查并更新规则与 Geo 数据。"
            });
        }
        if (Running) sources.Visibility = Visibility.Visible; SetButtons();
    }
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        pageToken = cancellationToken;
        if (!Running || busy || session.IsUpdating) { UpdateAvailability(cancellationToken); return; }
        var current = session.Status.Get("controller").Text("url") + "\n" + session.Status.Get("controller").Text("secret");
        if (controller != current)
        {
            controller = current; unsupported = followExpired = false; problem = "";
            // Preserve an unfinished result only to label the observed restart, never as success.
            if (!Active) { snapshot = previous = default; restarted = restartVerified = false; }
        }
        busy = true; SetButtons(); var started = Environment.TickCount64;
        try
        {
            var data = await session.ApiAsync(HttpMethod.Get, "upgrade/check", cancellationToken: cancellationToken);
            if (!Running || current != controller) return;
            Accept(data, started, false); failedSince = null; followExpired = false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            unsupported = error.Message.Contains("HTTP 404", StringComparison.Ordinal) || error.Message.Contains("HTTP 405", StringComparison.Ordinal);
            failedSince ??= Environment.TickCount64;
            problem = unsupported ? "此内核不支持资源更新接口。" : Active || awaitingRequest is not null
                ? Environment.TickCount64 - failedSince >= 120000 ? "更新结果暂时无法确认，请刷新状态核对。" : "正在重新连接更新任务，请稍候。"
                : "无法读取资源状态，请刷新后重试。";
            if (Environment.TickCount64 - failedSince >= 120000) { awaitingRequest = awaitingInstance = null; followExpired = true; }
        }
        finally { busy = false; lastRead = Environment.TickCount64; Render(); }
    }

    private async Task StartAsync()
    {
        if (session.IsUpdating) throw new InvalidOperationException(L.T("正在安装应用更新，请等待更新完成。"));
        if (session.IsDirty) throw new InvalidOperationException(L.T("请先保存或放弃配置草稿，再更新资源。"));
        if (!Running || busy) return;
        if (snapshot.ValueKind != JsonValueKind.Object || problem.Length > 0 || Active || awaitingRequest is not null) { failedSince = null; followExpired = false; await RefreshAsync(pageToken); return; }
        awaitingRequest = Guid.NewGuid().ToString("N"); awaitingInstance = snapshot.Text("instance_id"); unconfirmed = restarted = restartVerified = false; previous = default; problem = "";
        busy = true; SetButtons(); var started = Environment.TickCount64;
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = awaitingRequest, ["X-Mihomo-Instance-ID"] = awaitingInstance };
        try
        {
            var data = await session.ApiAsync(HttpMethod.Post, "upgrade/check", cancellationToken: pageToken, headers: headers);
            Accept(data, started, true);
        }
        catch (OperationCanceledException) { unconfirmed = true; throw; }
        catch (Exception)
        {
            // The controller may have accepted the request. The next operation must be GET.
            problem = "更新请求的结果尚未确认，正在读取当前任务。"; unconfirmed = true; failedSince = Environment.TickCount64;
        }
        finally { busy = false; lastRead = 0; Render(); }
        if (awaitingRequest is not null && !pageToken.IsCancellationRequested) await RefreshAsync(pageToken);
    }

    private void Accept(JsonElement data, long started, bool acceptedPost)
    {
        if (data.Text("instance_id").Length == 0 || data.Text("state") is not ("idle" or "running" or "completed" or "partial" or "restarting") || data.Get("items").ValueKind != JsonValueKind.Array || (data.Text("state") != "idle" && data.Text("task_id").Length == 0))
            throw new InvalidOperationException(L.T("内核返回的更新状态格式无效。"));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data.Get("items").Items())
        {
            if (item.Get("id").ValueKind != JsonValueKind.String || item.Get("name").ValueKind != JsonValueKind.String || !ids.Add(item.Text("id")) || item.Text("kind") is not ("kernel" or "ui" or "geodata" or "rule-provider") || item.Text("status") is not ("pending" or "checking" or "updated" or "up_to_date" or "skipped" or "error" or "restarting"))
                throw new InvalidOperationException(L.T("内核返回的更新状态格式无效。"));
            foreach (var key in new[] { "current_version", "latest_version", "reason", "error" })
                if (item.Get(key).ValueKind is not (JsonValueKind.Undefined or JsonValueKind.String)) throw new InvalidOperationException(L.T("内核返回的更新状态格式无效。"));
            if (item.Get("auto_update").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False) || item.Get("next_check_in_ms").ValueKind != JsonValueKind.Undefined && (item.Get("next_check_in_ms").ValueKind != JsonValueKind.Number || !item.Get("next_check_in_ms").TryGetInt64(out var remaining) || remaining < 0))
                throw new InvalidOperationException(L.T("内核返回的更新状态格式无效。"));
        }
        var recovered = awaitingRequest is not null && data.Text("request_id") == awaitingRequest && data.Text("instance_id") == awaitingInstance;
        if (!acceptedPost && awaitingRequest is not null && !recovered) unconfirmed = true;
        else if (acceptedPost || recovered) unconfirmed = false;
        awaitingRequest = awaitingInstance = null;
        if (!unconfirmed && Active && snapshot.Text("instance_id") != data.Text("instance_id"))
        {
            previous = snapshot; restarted = true;
            var requested = previous.Get("items").Items().FirstOrDefault(item => item.Text("kind") == "kernel");
            var current = data.Get("items").Items().FirstOrDefault(item => item.Text("kind") == "kernel");
            restartVerified = requested.Text("latest_version").Length > 0 && requested.Text("latest_version") == current.Text("current_version") && requested.Text("current_version") != requested.Text("latest_version");
        }
        snapshot = data; requestedAt = started; problem = "";
    }
    private void SetButtons()
    {
        var locked = busy || session.IsUpdating || !Running;
        refresh.IsEnabled = !locked;
        start.IsEnabled = !locked && !unsupported && !session.IsDirty && (!Active || followExpired);
        var title = busy || Active && !followExpired ? "正在更新资源…" : problem.Length > 0 || awaitingRequest is not null ? "刷新资源更新状态" : "检查并更新资源";
        var progress = busy || Active && !followExpired; var commandState = (progress ? "busy:" : "idle:") + title;
        if (!Equals(start.Tag, commandState))
        {
            start.Tag = commandState;
            if (progress) { var content = new StackPanel { Orientation = Orientation.Horizontal }; content.Children.Add(new FluentProgressRing { Width = 16, Height = 16, IsActive = true, Margin = new Thickness(0, 0, 8, 0) }); content.Children.Add(Ui.Label(title)); start.Content = content; }
            else start.Content = FluentControls.ButtonContent("arrow-circle-up", title);
        }
        L.Bind(start, AutomationProperties.NameProperty, title);
    }
    private void Render()
    {
        if (!Running) { UpdateAvailability(pageToken); return; }
        sources.Visibility = Visibility.Visible;
        var current = snapshot.Get("items").Items().ToArray(); var displayed = restarted ? previous.Get("items").Items().Concat(current.Where(item => !previous.Get("items").Items().Any(old => old.Text("id") == item.Text("id")))).ToArray() : current;
        AddGroup("geodata", "Geo 数据", displayed.Where(item => item.Text("kind") == "geodata").ToArray(), current);
        AddGroup("rule-provider", "规则集合", displayed.Where(item => item.Text("kind") == "rule-provider").ToArray(), current);
        // Desktop-managed tasks stay inspectable from the context menu without adding a third resource section.
        managedDetails.Items.Clear(); foreach (var item in displayed.Where(item => item.Text("kind") is "kernel" or "ui"))
        {
            var detail = new MenuItem { Header = item.Text("name") + " · " + L.T(Status(item)), ToolTip = item.Text("current_version") }; detail.IsHitTestVisible = false; managedDetails.Items.Add(detail);
        }
        managedDetails.IsEnabled = managedDetails.Items.Count > 0;
        summary.Text = L.T(problem.Length > 0 ? problem : unconfirmed ? "上次请求的结果未确认；不会自动重复更新。" : restarted ? "内核已重新连接，先前更新结果需要核对。" : snapshot.Text("state") switch
        {
            "running" => "正在检查并应用规则与 Geo 数据更新。", "restarting" => "内核正在重新连接…", "partial" => "部分资源更新失败，请查看各项结果。", "completed" => "资源检查和更新已完成。", _ => "检查当前配置中的规则集与 Geo 数据来源，并应用可用的资源更新。"
        });
        var finished = snapshot.Text("finished_at"); if (!Active && DateTimeOffset.TryParse(finished, out var time)) summary.Text += " · " + time.ToLocalTime().ToString("t");
        summary.SetResourceReference(TextBlock.ForegroundProperty, problem.Length > 0 || unconfirmed ? "WarningBrush" : "TextSecondaryBrush"); SetButtons();
    }
    private void AddGroup(string key, string label, JsonElement[] items, JsonElement[] schedules)
    {
        var panel = new StackPanel();
        if (items.Length == 0)
        {
            var empty = Ui.Hint("未配置此类资源。"); var emptyRow = new Border { Child = empty, Padding = new Thickness(32, 12, 16, 12), BorderThickness = new Thickness(0, 1, 0, 0) }; emptyRow.SetResourceReference(Border.BackgroundProperty, "SubtleSurfaceBrush"); emptyRow.SetResourceReference(Border.BorderBrushProperty, "DividerBrush"); panel.Children.Add(emptyRow);
        }
        foreach (var item in items)
        {
            var schedule = schedules.FirstOrDefault(current => current.Text("id") == item.Text("id"));
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55, GridUnitType.Star) }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = Ui.Text(item.Text("name"), 14); name.LineHeight = 20; name.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; var left = Ui.Column(name); left.Margin = new Thickness(0, 0, 16, 0); left.VerticalAlignment = VerticalAlignment.Top;
            TextBlock Version(string value) { var text = Ui.Muted(value, 12); text.FontFamily = new FontFamily("Cascadia Mono, Consolas"); text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis; text.Margin = new Thickness(0, 2, 0, 0); text.ToolTip = value; return text; }
            if (item.Text("current_version").Length > 0) left.Children.Add(Version(item.Text("current_version")));
            var latest = item.Text("latest_version"); if (latest.Length > 0 && latest != item.Text("current_version")) left.Children.Add(Version(L.F("最新版本 {0}", latest))); row.Children.Add(left);
            var status = !unconfirmed && restartVerified && item.Text("kind") == "kernel" ? "已更新" : unconfirmed || restarted && item.Text("status") is "pending" or "checking" or "restarting" ? "结果未确认" : Status(item);
            var state = status == "结果未确认" ? "skipped" : item.Text("status"); var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var statusIcon = new FluentIcon(state switch { "checking" or "restarting" => "update", "updated" or "up_to_date" => "check-circle", "error" => "warning", _ => "info-o" }, 15) { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            statusIcon.SetResourceReference(Control.ForegroundProperty, state is "updated" or "up_to_date" ? "SuccessBrush" : state == "error" ? "WarningBrush" : state is "pending" or "skipped" ? "TextTertiaryBrush" : "TextPrimaryBrush");
            var statusText = Ui.Text(L.T(status), 12); statusText.SetResourceReference(TextBlock.ForegroundProperty, state is "pending" or "skipped" ? "TextTertiaryBrush" : "TextPrimaryBrush");
            var statusCopy = new StackPanel { Orientation = Orientation.Horizontal }; statusCopy.Children.Add(statusIcon); statusCopy.Children.Add(statusText);
            var badge = new Border { Child = statusCopy, Padding = new Thickness(state is "pending" or "skipped" ? 0 : 8, 2, state is "pending" or "skipped" ? 0 : 8, 2), MinHeight = 24, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Right };
            if (state is "pending" or "skipped") badge.Background = Brushes.Transparent; else badge.SetResourceReference(Border.BackgroundProperty, state is "updated" or "up_to_date" ? "SuccessBackgroundBrush" : state == "error" ? "WarningBackgroundBrush" : state is "checking" or "restarting" ? "InfoBackgroundBrush" : "ControlAltBrush"); right.Children.Add(badge);
            var stamp = state + ":" + item.Text("error") + ":" + unconfirmed;
            if (statusStamps.TryGetValue(item.Text("id"), out var old) && old != stamp)
            {
                var move = new TranslateTransform(); badge.RenderTransform = move; ProxyVisuals.Tween(move, TranslateTransform.YProperty, 6, 0, 167); ProxyVisuals.Tween(badge, OpacityProperty, 0, 1, 167);
            }
            statusStamps[item.Text("id")] = stamp;
            if (schedule.Get("auto_update").ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var policy = schedule.Bool("auto_update") ? L.T("自动更新已开启") : L.T("自动更新已关闭");
                if (schedule.Bool("auto_update") && schedule.Number("update_interval") > 0) policy += " · " + L.F("每 {0} {1}", schedule.Number("update_interval"), L.T(schedule.Text("interval_unit") == "seconds" ? "秒" : "小时"));
                var policyText = Ui.Muted(policy, 12); policyText.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); policyText.Margin = new Thickness(0, 4, 0, 0); policyText.TextAlignment = TextAlignment.Right; right.Children.Add(policyText);
                var next = Schedule(schedule); if (next.Length > 0) { var nextText = Ui.Muted(next, 12); nextText.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); nextText.Margin = new Thickness(0, 2, 0, 0); nextText.TextAlignment = TextAlignment.Right; right.Children.Add(nextText); }
            }
            Grid.SetColumn(right, 1); row.Children.Add(right); row.Id("Resource_" + item.Text("id"));
            var rowFrame = new Border { Child = row, Padding = new Thickness(32, 12, 16, 12), MinHeight = 64, BorderThickness = new Thickness(0, 1, 0, 0) }; rowFrame.SetResourceReference(Border.BorderBrushProperty, "DividerBrush"); rowFrame.SetResourceReference(Border.BackgroundProperty, "SubtleSurfaceBrush"); panel.Children.Add(rowFrame);
            row.SizeChanged += (_, _) => { var compact = FindContainerWidth() <= 560; Grid.SetColumn(right, compact ? 0 : 1); Grid.SetRow(right, compact ? 1 : 0); Grid.SetColumnSpan(right, compact ? 2 : 1); right.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right; right.Margin = new Thickness(0, compact ? 8 : 0, 0, 0); };
        }
        var count = items.Count(item => item.Text("reason") != "not_configured");
        var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30, GridUnitType.Star) }); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55, GridUnitType.Star) }); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); header.Children.Add(Ui.Label(label));
        var meta = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 0) }; meta.Children.Add(Ui.Muted(L.F("{0} 项", count) + (items.Any(item => item.Text("status") == "error") ? " · " + L.T("有更新失败") : ""), 12));
        var enabled = schedules.Where(item => items.Any(candidate => candidate.Text("id") == item.Text("id")) && item.Bool("auto_update")).OrderBy(item => item.Get("next_check_in_ms").ValueKind == JsonValueKind.Number ? item.Number("next_check_in_ms") : double.MaxValue).ToArray();
        if (enabled.Length > 0) { var next = Schedule(enabled[0]); if (next.Length > 0) { var text = Ui.Muted(next, 12); text.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); text.Margin = new Thickness(0, 2, 0, 0); meta.Children.Add(text); } }
        Grid.SetColumn(meta, 1); header.Children.Add(meta); var caret = new FluentIcon("chevron-down", 14); caret.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush"); Grid.SetColumn(caret, 2); header.Children.Add(caret);
        if (!groups.TryGetValue(key, out var expander))
        {
            expander = new ProxyExpander { AnimateHeight = false, BorderThickness = new Thickness(0, 1, 0, 0), IsExpanded = open.Contains(key) }.Id("ResourceGroup_" + key); expander.SetResourceReference(Control.BorderBrushProperty, "DividerBrush"); expander.Expanded += (_, _) => open.Add(key); expander.Collapsed += (_, _) => open.Remove(key); groups[key] = expander;
        }
        expander.Header = new Border { Child = header, Padding = new Thickness(16, 8, 16, 8), MinHeight = 56 }; expander.SetCaret(caret); expander.Content = panel; if (!rows.Children.Contains(expander)) rows.Children.Add(expander);
    }
    private double FindContainerWidth()
    {
        for (DependencyObject? item = this; item is not null; item = VisualTreeHelper.GetParent(item)) if (item is Mihomo.Desktop.Pages.NativePage page) return page.PageContainerWidth;
        return ActualWidth;
    }
    private string Schedule(JsonElement item)
    {
        if (!item.Bool("auto_update")) return "";
        var state = item.Text("auto_update_state"); var phase = state == "checking" ? L.T("正在自动检查") : state == "waiting" ? L.T("等待内核执行") : "";
        if (state == "unscheduled" || state.Length == 0 && item.Text("reason") is "not_configured" or "local_provider" or "embedded") return L.T("尚未安排检查");
        DateTimeOffset? next = null;
        if (item.Get("next_check_in_ms").ValueKind == JsonValueKind.Number) next = DateTimeOffset.Now.AddMilliseconds(Math.Clamp(item.Number("next_check_in_ms") - Math.Max(0, Environment.TickCount64 - requestedAt), -365d * 86400000, 3650d * 86400000));
        else if (DateTimeOffset.TryParse(item.Text("next_check"), out var date)) next = date.ToLocalTime();
        var text = next.HasValue ? L.F(state == "retrying" ? "下次重试 {0}" : "下次检查 {0}", next.Value.ToString("yyyy-MM-dd HH:mm:ss zzz")) : phase.Length > 0 ? "" : L.T("调度时间暂不可用");
        if (next <= DateTimeOffset.Now && phase.Length == 0) text += " · " + L.T("已到计划时间");
        if (problem.Length > 0) text += " · " + L.T("调度信息可能已过期");
        return phase.Length > 0 ? phase + (text.Length > 0 ? " · " + text : "") : text;
    }
    private static string Status(JsonElement item) => item.Text("status") switch
    {
        "pending" => "等待检查", "checking" => "检查中", "updated" => "已更新", "up_to_date" => "已是最新", "restarting" => "重启中",
        "skipped" => item.Text("reason") switch { "local_provider" => "本地规则文件", "embedded" => "内置资源", "not_configured" => "未配置", "desktop_managed" => "由桌面应用管理", _ => "已跳过" },
        "error" => item.Text("error") switch { "timeout" => "更新超时", "busy" => "内核繁忙", "configuration_changed" => "配置已经改变", "version_unavailable" => "无法获取版本", _ => "更新失败" }, _ => item.Text("status")
    };
}
