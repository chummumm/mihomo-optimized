using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

/// <summary>Always-visible core version and policy, read from the desktop update owner.</summary>
internal sealed class CoreUpdateRow : Border
{
    private readonly TextBlock current = Ui.Muted("", 12);
    private readonly TextBlock latest = Ui.Muted("", 12);
    private readonly TextBlock status = Ui.Text("", 12);
    private readonly TextBlock policy = Ui.Muted("", 12);
    private readonly TextBlock schedule = Ui.Muted("", 12);
    private readonly TextBlock message = Ui.Muted("", 12);
    private readonly FluentIcon icon = new("info-o", 15);

    public CoreUpdateRow()
    {
        this.Id("Resource_kernel"); Padding = new Thickness(16, 12, 16, 12); MinHeight = 80;
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        current.Id("CoreUpdateCurrentVersion"); latest.Id("CoreUpdateLatestVersion"); status.Id("CoreUpdateStatus");
        policy.Id("CoreUpdatePolicy"); schedule.Id("CoreUpdateSchedule"); message.Id("CoreUpdateMessage");
        current.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont"); latest.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        current.Margin = latest.Margin = new Thickness(0, 3, 0, 0);
        var left = Ui.Column(Ui.Label("内核", 14), current, latest); left.Margin = new Thickness(0, 0, 16, 0); left.VerticalAlignment = VerticalAlignment.Top; grid.Children.Add(left);
        var statusLine = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        icon.Margin = new Thickness(0, 0, 8, 0); statusLine.Children.Add(icon); statusLine.Children.Add(status);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var right = Ui.Column(statusLine, policy, schedule, message); right.HorizontalAlignment = HorizontalAlignment.Right;
        foreach (var text in new[] { policy, schedule, message }) { text.Margin = new Thickness(0, 4, 0, 0); text.TextWrapping = TextWrapping.Wrap; text.TextAlignment = TextAlignment.Right; }
        Grid.SetColumn(right, 1); grid.Children.Add(right); Child = grid;
        SizeChanged += (_, _) =>
        {
            bool compact = ActualWidth <= 560;
            Grid.SetColumn(right, compact ? 0 : 1); Grid.SetRow(right, compact ? 1 : 0); Grid.SetColumnSpan(right, compact ? 2 : 1);
            right.HorizontalAlignment = statusLine.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            right.Margin = new Thickness(0, compact ? 8 : 0, 0, 0);
            foreach (var text in new[] { policy, schedule, message }) text.TextAlignment = compact ? TextAlignment.Left : TextAlignment.Right;
        };
    }

    public void Render(CoreUpdateState observation, string problem, bool unconfirmed)
    {
        var item = observation.Item;
        current.Text = observation.Available && item.Text("current_version").Length > 0 ? item.Text("current_version") : L.T("当前版本暂不可用");
        string version = item.Text("latest_version");
        latest.Text = version.Length > 0 ? L.F("最新版本 {0}", version) : "";
        latest.Visibility = latest.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        string phase = observation.Available ? item.Text("status") : "pending";
        status.Text = L.T(unconfirmed ? "结果未确认" : observation.Available ? Status(item) : "正在读取内核更新状态…");
        string brush = phase == "error" || problem.Length > 0 ? "WarningBrush" : phase is "updated" or "up_to_date" ? "SuccessBrush" : phase is "checking" or "restarting" ? "InfoBrush" : "TextSecondaryBrush";
        icon.Kind = unconfirmed || phase == "error" || problem.Length > 0 ? "warning" : phase is "updated" or "up_to_date" ? "check-circle" : observation.Active ? "update" : "info-o";
        status.SetResourceReference(TextBlock.ForegroundProperty, brush); icon.SetResourceReference(Control.ForegroundProperty, brush);
        policy.Text = observation.Available ? L.T(item.Bool("auto_update") ? "自动更新已开启" : "自动更新已关闭") + " · " + L.F("每 {0} 小时", item.Number("update_interval")) : "";
        var next = observation.NextCheck(DateTimeOffset.Now, Environment.TickCount64);
        string scheduling = item.Text("auto_update_state");
        schedule.Text = !observation.Available || !item.Bool("auto_update") ? "" : next.HasValue
            ? L.F(scheduling == "retrying" ? "下次重试 {0}" : "下次检查 {0}", next.Value.ToString("yyyy-MM-dd HH:mm:ss zzz"))
            : L.T(scheduling is "checking" ? "正在自动检查" : scheduling is "waiting" ? "等待内核执行" : "尚未安排检查");
        message.Text = problem.Length > 0 ? L.T(problem) : item.Text("message");
        if (message.Text.Length == 0) message.Text = item.Text("error");
        message.SetResourceReference(TextBlock.ForegroundProperty, problem.Length > 0 || phase == "error" ? "WarningBrush" : "TextSecondaryBrush");
        foreach (var text in new[] { policy, schedule, message }) text.Visibility = text.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Status(JsonElement item) => item.Text("status") switch
    {
        "checking" => item.Text("stage") switch { "downloading" => "正在下载内核", "verifying" => "正在验证内核", "waiting" => "等待当前操作完成", _ => "检查中" },
        "restarting" => "正在切换内核", "updated" => "已更新", "up_to_date" => "已是最新", "error" => "内核更新失败",
        "skipped" => item.Text("reason") switch { "disabled" or "auto_update_disabled" => "自动更新已关闭", "busy" => "等待当前操作完成", "operation_cancelled" => "更新已取消", _ => "等待检查" },
        _ => "等待检查"
    };
}
