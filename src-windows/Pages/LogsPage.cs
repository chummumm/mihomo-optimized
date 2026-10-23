using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class LogsPage : NativePage
{
    private const int Capacity = 2000;
    private readonly ObservableCollection<LogRow> lines = [];
    private readonly Queue<LogRow> held = new();
    private readonly DataGrid table = Ui.Table(("时间", nameof(LogRow.Time), 100), ("级别", nameof(LogRow.Level), 95), ("内容", nameof(LogRow.Message), -1));
    private readonly ComboBox level = new() { Width = 120, Height = 34, ItemsSource = new[] { "debug", "info", "warning", "error" }, SelectedIndex = 1 };
    private readonly CheckBox pause = new() { Content = "暂停", VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox follow = new() { Content = "跟随最新", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock count = Ui.Muted("0 行");
    private CancellationTokenSource? stream;
    private string controller = "";

    public LogsPage(AppSession session) : base(session, "logs", "日志", "内核实时日志，仅保存在当前应用内存中")
    {
        level.Id("LogLevel"); pause.Id("PauseLogs"); follow.Id("FollowLogs"); table.Id("LogTable");
        Tools.Children.Add(Action("清空", "ClearLogs", () => { lines.Clear(); held.Clear(); UpdateCount(); return Task.CompletedTask; }));
        level.SelectionChanged += (_, _) => { if (IsActive) RestartStream(); };
        pause.Click += (_, _) => { if (pause.IsChecked != true) { while (held.TryDequeue(out var item)) Add(item); } UpdateCount(); };
        table.ItemsSource = lines;
        table.MouseDoubleClick += (_, _) => { if (table.SelectedItem is LogRow row) DetailsDialog.Show(this, "日志详情", $"{row.Time}  {row.Level}\n\n{row.Message}"); };
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition());
        layout.Children.Add(Ui.Row(Ui.Muted("显示级别"), level, pause, follow, count)); Grid.SetRow(table, 1); layout.Children.Add(table); SetBody(layout);
    }
    public override Task RefreshAsync() { Message(IsRunning ? "" : InactiveMessage); return Task.CompletedTask; }
    protected override Task LiveAsync(CancellationToken cancellationToken) { RestartStream(); return Task.CompletedTask; }
    private void RestartStream()
    {
        stream?.Cancel(); stream?.Dispose(); stream = CancellationTokenSource.CreateLinkedTokenSource(PageToken);
        _ = RunAsync(() => WatchStreamAsync("logs?level=" + Uri.EscapeDataString(level.SelectedItem?.ToString() ?? "info"), Receive, stream.Token));
    }
    public override void Deactivate() { stream?.Cancel(); stream?.Dispose(); stream = null; base.Deactivate(); }
    protected override void OnStatusChanged()
    {
        var current = ControllerIdentity;
        if (controller != current) { controller = current; lines.Clear(); held.Clear(); UpdateCount(); }
        Message(IsRunning ? "" : InactiveMessage);
    }
    private void Receive(JsonElement data)
    {
        var item = new LogRow(DateTime.Now.ToString("HH:mm:ss.fff"), data.Text("type").ToUpperInvariant(), data.Text("payload"));
        if (pause.IsChecked == true) { held.Enqueue(item); while (held.Count > Capacity) held.Dequeue(); }
        else Add(item); UpdateCount();
    }
    private void Add(LogRow item)
    {
        lines.Add(item); while (lines.Count > Capacity) lines.RemoveAt(0);
        if (follow.IsChecked == true) table.ScrollIntoView(item);
    }
    private void UpdateCount() => count.Text = $"{lines.Count:N0} 行 / 最多 {Capacity:N0}" + (held.Count > 0 ? $" · 暂存 {held.Count:N0} 行" : "");
    public sealed record LogRow(string Time, string Level, string Message);
}
