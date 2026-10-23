using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class HomePage : NativePage
{
    private readonly TextBlock phase = Ui.Text("正在连接", 24, true);
    private readonly TextBlock description = Ui.Muted("读取本地内核状态…", 14);
    private readonly TextBlock up = Ui.Text("—", 28, true);
    private readonly TextBlock down = Ui.Text("—", 28, true);
    private readonly TextBlock summary = Ui.Muted("内核启动后显示实时流量");
    private readonly TrafficChart chart = new();
    private readonly ComboBox modes = new() { MinWidth = 160, Height = 34, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
    private readonly CheckBox proxy = new() { Content = "系统代理" };
    private readonly CheckBox tun = new() { Content = "TUN 模式" };
    private readonly CheckBox autostart = new() { Content = "开机启动" };
    private readonly CheckBox lan = new() { Content = "允许局域网" };
    private readonly Button start;
    private readonly Button stop;
    private readonly Button restart;
    private bool updating;
    private string controller = "";

    public HomePage(AppSession session) : base(session, "home", "主页", "管理本地代理，查看当前网络状态")
    {
        phase.Id("CoreStatus"); chart.Id("TrafficChart");
        start = Action("启动内核", "StartCore", () => CommandAndRefresh("desktop_start"), true);
        stop = Action("停止", "StopCore", () => CommandAndRefresh("desktop_stop"));
        restart = Action("重启", "RestartCore", () => CommandAndRefresh("desktop_restart"));
        var state = Ui.Column(phase, description, Ui.Row(start, stop, restart));
        description.Margin = new Thickness(0, 8, 0, 20);
        modes.ItemsSource = new[] { new { Label = "规则模式", Value = "rule" }, new { Label = "全局模式", Value = "global" }, new { Label = "直连模式", Value = "direct" } };
        modes.Id("OutboundMode");
        modes.SelectionChanged += async (_, _) =>
        {
            if (updating || modes.SelectedValue is not string mode) return;
            await RunAsync(async () => { await Command("desktop_patch_config", new { patch = new { mode } }); await RefreshAsync(); });
        };
        var modeRow = new DockPanel(); var modeLabel = Ui.Text("出站模式", 14, true); DockPanel.SetDock(modes, Dock.Right); modeRow.Children.Add(modes); modeRow.Children.Add(modeLabel);
        BindSwitch(proxy, "systemProxy", "desktop_set_system_proxy");
        BindSwitch(tun, "tun", "desktop_set_tun");
        BindSwitch(autostart, "autostart", "desktop_set_autostart");
        lan.Id("allowLan"); lan.Margin = new Thickness(0, 4, 24, 4);
        lan.Click += async (_, _) =>
        {
            if (updating) return;
            await RunAsync(async () => { await Command("desktop_patch_config", new { patch = new Dictionary<string, object> { ["allow-lan"] = lan.IsChecked == true } }); await RefreshAsync(); });
        };
        var rates = new Grid(); rates.ColumnDefinitions.Add(new ColumnDefinition()); rates.ColumnDefinitions.Add(new ColumnDefinition());
        rates.Children.Add(Ui.Column(Ui.Muted("↓ 下载 / 秒"), down));
        var upload = Ui.Column(Ui.Muted("↑ 上传 / 秒"), up); Grid.SetColumn(upload, 1); rates.Children.Add(upload);
        var activity = Ui.Column(rates, chart, summary); chart.Margin = new Thickness(0, 20, 0, 12);
        var body = Ui.Column(Ui.Card(state, 24), Ui.Card(modeRow), Ui.Card(Ui.Row(proxy, tun, lan, autostart)), Ui.Card(activity, 24));
        body.Children.Add(Action("查看代理节点", "HomeOpenProxies", () => { Session.Navigate("proxies"); return Task.CompletedTask; }));
        SetBody(Ui.Scroll(body));
    }
    private void BindSwitch(CheckBox check, string id, string command)
    {
        check.Id(id); check.Margin = new Thickness(0, 4, 24, 4);
        check.Click += async (_, _) =>
        {
            if (updating) return;
            var enabled = check.IsChecked == true;
            if (command == "desktop_set_tun" && enabled && !Session.Status.Get("service").Bool("installed") &&
                MessageBox.Show(Window.GetWindow(this), "TUN 模式需要安装本机服务。Windows 将请求管理员权限，用于创建虚拟网卡和接管网络流量。", "启用 TUN", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            { check.IsChecked = false; return; }
            await RunAsync(async () => { check.IsEnabled = false; try { await Command(command, new { enabled }); } finally { UpdateStatus(); } });
        };
    }
    private async Task CommandAndRefresh(string command) { await Command(command); await RefreshAsync(); }
    private void UpdateStatus()
    {
        updating = true;
        var status = Session.Status; var state = status.Text("phase");
        phase.Text = state switch { "running" => "代理已就绪", "starting" => "正在启动内核", "stopping" => "正在停止内核", "stopped" => "内核未启动", "error" => "内核需要处理", _ => "正在连接" };
        description.Text = state == "starting" ? "正在准备资源并确认内核就绪，请稍候。" : state == "running" ? "内核 " + status.Text("coreVersion", fallback: "—") : status.Text("configPath", fallback: "读取本地配置…");
        var busy = state is "starting" or "stopping" or "connecting";
        start.IsEnabled = !busy && state != "running"; stop.IsEnabled = !busy && state == "running"; restart.IsEnabled = !busy && state == "running";
        proxy.IsChecked = status.Bool("systemProxy"); tun.IsChecked = status.Bool("tun"); autostart.IsChecked = status.Bool("autostart");
        proxy.IsEnabled = tun.IsEnabled = autostart.IsEnabled = !busy; lan.IsEnabled = modes.IsEnabled = !busy && state == "running";
        var currentController = ControllerIdentity;
        if (controller != currentController) { controller = currentController; chart.Clear(); up.Text = down.Text = "—"; summary.Text = "内核启动后显示实时流量"; }
        if (status.Text("lastError") is { Length: > 0 } error) Message(error, true); else Message("");
        updating = false;
    }
    protected override void OnStatusChanged() { UpdateStatus(); }
    public override async Task RefreshAsync()
    {
        UpdateStatus(); if (!IsRunning) return;
        var config = await Get("configs"); updating = true;
        modes.SelectedValue = config.Text("mode"); lan.IsChecked = config.Bool("allow-lan"); updating = false;
    }
    protected override Task LiveAsync(CancellationToken cancellationToken) => Task.WhenAll(
        WatchStreamAsync("traffic", value => { var upload = value.Number("up"); var download = value.Number("down"); up.Text = Ui.Bytes(upload); down.Text = Ui.Bytes(download); chart.Add(upload, download); }, cancellationToken),
        WatchStreamAsync("connections", value => { summary.Text = $"{value.Get("connections").Items().Count():N0} 个活动连接    上传 {Ui.Bytes(value.Number("uploadTotal"))}    下载 {Ui.Bytes(value.Number("downloadTotal"))}"; }, cancellationToken));
}
