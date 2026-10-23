using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Animation;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class LogsPage : NativePage
{
    private const int Capacity = 2000;
    private readonly ObservableCollection<LogRow> lines = [];
    private readonly Queue<LogRow> held = new();
    private readonly DataGrid table = Ui.Table(("时间", nameof(LogRow.Time), 160), ("级别", nameof(LogRow.Level), 95), ("内容", nameof(LogRow.Message), -1));
    private readonly ComboBox level = new() { Width = 120, Height = 34, SelectedValuePath = "Tag" };
    private readonly CheckBox pause = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox follow = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock count = Ui.Muted("");
    private readonly TextBlock empty = Ui.Hint("等待新的内核日志");
    private readonly TextBlock pausedText = Ui.Muted("");
    private readonly Border pausedNotice;
    private readonly Button clear;
    private CancellationTokenSource? stream;
    private string controller = "";
    private string defaultLevel = "info";
    private string streamLevel = "";
    private string streamController = "";

    public LogsPage(AppSession session) : base(session, "logs", "日志", "内核实时日志，仅保存在当前应用内存中")
    {
        level.Id("LogLevel"); pause.Id("PauseLogs"); follow.Id("FollowLogs"); table.Id("LogTable");
        L.Bind(pause, ContentControl.ContentProperty, "暂停"); L.Bind(follow, ContentControl.ContentProperty, "跟随最新");
        foreach (var (name, value) in new[] { ("默认", ""), ("调试", "debug"), ("信息", "info"), ("警告", "warning"), ("错误", "error"), ("静默", "silent") })
            level.Items.Add(L.Bind(new ComboBoxItem { Tag = value }, ContentControl.ContentProperty, name));
        level.SelectedIndex = 0;
        clear = Action("清空", "ClearLogs", () =>
        {
            lines.Clear(); held.Clear(); UpdateCount();
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast) table.BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(220)));
            return Task.CompletedTask;
        }); Tools.Children.Add(clear);
        level.SelectionChanged += (_, _) => { if (IsActive) RestartStream(); };
        pause.Checked += (_, _) => ResumeHeld(); pause.Unchecked += (_, _) => ResumeHeld();
        table.ItemsSource = lines;
        var rowStyle = new Style(typeof(DataGridRow));
        foreach (var (value, brush) in new[] { ("ERROR", "DangerBrush"), ("WARNING", "WarningBrush"), ("DEBUG", "TextSecondaryBrush") })
        {
            var trigger = new DataTrigger { Binding = new Binding(nameof(LogRow.Level)), Value = value };
            trigger.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(brush))); rowStyle.Triggers.Add(trigger);
        }
        table.RowStyle = rowStyle;
        table.MouseDoubleClick += (_, _) => { if (table.SelectedItem is LogRow row) DetailsDialog.Show(this, "日志详情", $"{row.Time}  {row.Level}\n\n{row.Message}"); };
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition());
        layout.Children.Add(Ui.Row(Ui.Hint("显示级别"), level, pause, follow, count));
        var surface = new Grid(); surface.Children.Add(table); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; surface.Children.Add(empty);
        var resume = Action("继续", "ResumeLogs", () => { pause.IsChecked = false; ResumeHeld(); return Task.CompletedTask; });
        pausedNotice = Ui.Card(Ui.Row(pausedText, resume), 12); pausedNotice.HorizontalAlignment = HorizontalAlignment.Center; pausedNotice.VerticalAlignment = VerticalAlignment.Bottom; pausedNotice.Margin = new Thickness(12); pausedNotice.Visibility = Visibility.Collapsed; surface.Children.Add(pausedNotice);
        Grid.SetRow(surface, 1); layout.Children.Add(surface); SetBody(layout); UpdateCount();
    }
    private void ResumeHeld()
    {
        if (pause.IsChecked != true) while (held.TryDequeue(out var item)) Add(item);
        UpdateCount();
    }
    public override async Task RefreshAsync()
    {
        Message(IsRunning ? "" : InactiveMessage);
        if (!IsRunning) { StopStream(); return; }
        var source = ControllerIdentity;
        var config = await Get("configs");
        if (!IsActive || !IsRunning || source != ControllerIdentity) return;
        controller = source;
        defaultLevel = config.Text("log-level", fallback: "info");
        RestartStream();
    }
    protected override Task LiveAsync(CancellationToken cancellationToken) { RestartStream(); return Task.CompletedTask; }
    private void RestartStream()
    {
        string selected = level.SelectedValue as string ?? ""; selected = selected.Length == 0 ? defaultLevel : selected;
        if (!IsActive || !IsRunning || selected == "silent") { StopStream(); return; }
        if (stream is not null && streamLevel == selected && streamController == ControllerIdentity) return;
        StopStream();
        stream = CancellationTokenSource.CreateLinkedTokenSource(PageToken);
        streamLevel = selected; streamController = ControllerIdentity;
        var token = stream.Token;
        _ = RunAsync(() => WatchStreamAsync("logs?level=" + Uri.EscapeDataString(selected), Receive, token));
    }
    private void StopStream() { stream?.Cancel(); stream?.Dispose(); stream = null; streamLevel = streamController = ""; }
    public override void Deactivate() { StopStream(); base.Deactivate(); }
    protected override void OnLanguageChanged() { UpdateCount(); base.OnLanguageChanged(); }
    protected override void OnStatusChanged()
    {
        var current = ControllerIdentity;
        if (controller != current) { controller = current; StopStream(); lines.Clear(); held.Clear(); UpdateCount(); }
        if (!IsRunning) StopStream();
        Message(IsRunning ? "" : InactiveMessage);
    }
    private void Receive(JsonElement data)
    {
        var item = new LogRow(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), data.Text("type").ToUpperInvariant(), data.Text("payload"));
        if (pause.IsChecked == true) { held.Enqueue(item); while (held.Count > Capacity) held.Dequeue(); }
        else Add(item); UpdateCount();
    }
    private void Add(LogRow item)
    {
        lines.Add(item); while (lines.Count > Capacity) lines.RemoveAt(0);
        if (follow.IsChecked == true) table.ScrollIntoView(item);
    }
    private void UpdateCount()
    {
        count.Text = L.F("{0:N0} 行 / 最多 {1:N0}", lines.Count, Capacity);
        pausedText.Text = L.F("已暂停 · 暂存 {0:N0} 行", held.Count);
        pausedNotice.Visibility = pause.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        empty.Visibility = lines.Count == 0 && pause.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
        clear.IsEnabled = lines.Count > 0 || held.Count > 0;
    }
    public sealed record LogRow(string Time, string Level, string Message);
}
