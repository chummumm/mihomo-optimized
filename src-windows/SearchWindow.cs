using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop;

internal sealed class SearchWindow : Window
{
    private sealed record Entry(string Title, string Hint, Func<Task> Run)
    {
        public override string ToString() => Title + "  ·  " + Hint;
    }
    private readonly AppSession session;
    private readonly TextBox search = new() { Margin = new Thickness(16), MinHeight = 38, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ListBox results = new() { Margin = new Thickness(16, 0, 16, 16), BorderThickness = new Thickness(0) };
    private readonly List<Entry> entries = new();
    private readonly CancellationTokenSource lifetime = new();

    public SearchWindow(AppSession session)
    {
        this.session = session;
        Title = "搜索"; Width = 640; Height = 460; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable, Segoe UI, Microsoft YaHei UI"); FontSize = 14;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        search.Id("SearchInput"); results.Id("SearchResults");
        System.Windows.Automation.AutomationProperties.SetName(search, "搜索页面、策略组、节点、规则或命令");
        var layout = new DockPanel(); DockPanel.SetDock(search, Dock.Top); layout.Children.Add(search); layout.Children.Add(results); Content = layout;
        foreach (var (key, title) in new[] { ("home", "主页"), ("proxies", "代理"), ("connections", "连接"), ("dns", "DNS"), ("rules", "规则"), ("logs", "日志"), ("profiles", "配置"), ("settings", "设置") })
            entries.Add(new Entry(title, "页面", () => { session.Navigate(key); return Task.CompletedTask; }));
        AddCommand("启动内核", "desktop_start"); AddCommand("停止内核", "desktop_stop"); AddCommand("重启内核", "desktop_restart");
        entries.Add(new Entry("打开配置文件夹", "命令", async () => { await session.CommandAsync("desktop_open_directory", new { kind = "config" }); }));
        entries.Add(new Entry("系统代理", "切换", async () => { await session.CommandAsync("desktop_set_system_proxy", new { enabled = !session.Status.Bool("systemProxy") }); }));
        foreach (string value in new[] { "rule", "global", "direct" })
            entries.Add(new Entry(value switch { "rule" => "切换规则模式", "global" => "切换全局模式", _ => "切换直连模式" }, "命令", async () => { await session.CommandAsync("desktop_patch_config", new { patch = new Dictionary<string, object> { ["mode"] = value } }); }));
        foreach (string title in new[] { "主题", "开机启动", "TUN 服务", "应用更新", "日志文件夹" })
            entries.Add(new Entry(title, "设置", () => { session.Navigate("settings"); return Task.CompletedTask; }));
        search.TextChanged += (_, _) => Filter();
        search.PreviewKeyDown += (_, e) => { if (e.Key == Key.Down) { results.Focus(); if (results.SelectedIndex < 0) results.SelectedIndex = 0; e.Handled = true; } };
        PreviewKeyDown += async (_, e) => { if (e.Key == Key.Escape) Close(); if (e.Key == Key.Enter) { e.Handled = true; await RunSelected(); } };
        results.MouseDoubleClick += async (_, _) => await RunSelected();
        Loaded += async (_, _) => { search.Focus(); Filter(); await LoadCoreEntries(); };
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
    }

    private void AddCommand(string title, string method) => entries.Add(new Entry(title, "命令", async () => { await session.CommandAsync(method); }));
    private void Filter()
    {
        string term = search.Text.Trim();
        results.ItemsSource = entries.Where(entry => term.Length == 0 || entry.Title.Contains(term, StringComparison.OrdinalIgnoreCase) || entry.Hint.Contains(term, StringComparison.OrdinalIgnoreCase)).Take(80).ToArray();
        if (results.Items.Count > 0) results.SelectedIndex = 0;
    }

    private async Task LoadCoreEntries()
    {
        if (session.Status.Text("phase") != "running") return;
        try
        {
            var response = await session.ApiAsync(HttpMethod.Get, "proxies", cancellationToken: lifetime.Token);
            foreach (var group in response.Get("proxies").Fields().Where(p => p.Value.Get("all").ValueKind == System.Text.Json.JsonValueKind.Array))
            {
                string groupName = group.Name;
                entries.Add(new Entry(groupName, "策略组", () => { session.Navigate("proxies"); return Task.CompletedTask; }));
                if (group.Value.Text("type") != "Selector") continue;
                foreach (var child in group.Value.Get("all").Items().Take(500))
                {
                    string node = child.Text();
                    entries.Add(new Entry(node, "切换 " + groupName, async () => { await session.ApiAsync(HttpMethod.Put, "proxies/" + Uri.EscapeDataString(groupName), new { name = node }); session.Notify(groupName + " 已切换到 " + node); }));
                }
            }
            if (!lifetime.IsCancellationRequested) Filter();
        }
        catch (Exception error) when (error is DesktopException or OperationCanceledException or HttpRequestException) { }
    }

    private async Task RunSelected()
    {
        if (results.SelectedItem is not Entry entry) return;
        Close();
        try { await entry.Run(); } catch (Exception error) { session.Notify(error.Message, true); }
    }
}
