using Icon = System.Drawing.Icon;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mihomo.Desktop.Controls;
using Forms = System.Windows.Forms;

namespace Mihomo.Desktop.Services;

internal sealed class TrayController : IDisposable
{
    private readonly MainWindow main;
    private readonly AppSession session;
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.ToolStripMenuItem stateItem = new("正在连接…") { Enabled = false };
    private readonly Forms.ToolStripMenuItem coreItem = new("启动内核");
    private readonly Forms.ToolStripMenuItem restartItem = new("重启内核");
    private readonly Forms.ToolStripMenuItem proxyItem = new("系统代理");
    private readonly Forms.ToolStripMenuItem tunItem = new("TUN 模式");
    private readonly Forms.Timer clickTimer = new();
    private TrayWindow? flyout;

    public TrayController(MainWindow main, AppSession session)
    {
        this.main = main; this.session = session;
        using var source = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico"))!.Stream;
        icon = new Forms.NotifyIcon { Text = "Mihomo Optimized", Icon = new Icon(source), Visible = true, ContextMenuStrip = menu };
        menu.Items.Add(stateItem); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("打开主界面", null, (_, _) => main.ShowMain());
        menu.Items.Add(coreItem); menu.Items.Add(restartItem);
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add(proxyItem); menu.Items.Add(tunItem);
        menu.Items.Add("停止代理并恢复网络", null, async (_, _) => await Run("desktop_stop"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => { if (await main.PrepareExitAsync()) await main.FinishExitAsync(); });
        coreItem.Click += async (_, _) => await Run(IsRunning ? "desktop_stop" : "desktop_start");
        restartItem.Click += async (_, _) => await Run("desktop_restart");
        proxyItem.Click += async (_, _) => await Run("desktop_set_system_proxy", new { enabled = !Flag("systemProxy") });
        tunItem.Click += async (_, _) => await ChangeTun();
        clickTimer.Interval = Forms.SystemInformation.DoubleClickTime;
        clickTimer.Tick += (_, _) => { clickTimer.Stop(); ShowFlyout(); };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) clickTimer.Start(); };
        icon.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) { clickTimer.Stop(); flyout?.Hide(); main.ShowMain(); } };
        menu.Opening += (_, _) => { flyout?.Hide(); Refresh(); };
    }

    private bool IsRunning => HostChannel.Text(session.Status, "phase") == "running";
    private bool Flag(string key) => session.Status.TryGetProperty(key, out var flag) && flag.ValueKind == JsonValueKind.True;
    private async Task Run(string command, object? args = null)
    {
        try { await session.CommandAsync(command, args); }
        catch (Exception error) { main.ShowMain(); session.Notify(error.Message, true); }
    }

    private async Task ChangeTun()
    {
        bool enabled = !Flag("tun");
        if (enabled && !session.Status.Get("service").Bool("installed"))
        {
            flyout?.Hide(); main.ShowMain();
            if (MessageBox.Show(main, "TUN 接管系统网络，需要安装本机服务。接下来 Windows 将请求管理员权限。是否继续？", "启用 TUN", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        }
        await Run("desktop_set_tun", new { enabled });
    }

    public void Refresh()
    {
        string phase = HostChannel.Text(session.Status, "phase");
        bool idle = session.Ready && phase is not ("starting" or "stopping");
        stateItem.Text = phase switch { "running" => "内核运行中", "starting" => "正在启动内核…", "stopping" => "正在停止内核…", "error" => "内核需要处理", "stopped" => "内核已停止", _ => "正在连接…" };
        icon.Text = "Mihomo Optimized · " + stateItem.Text;
        coreItem.Text = IsRunning ? "停止内核" : "启动内核";
        coreItem.Enabled = restartItem.Enabled = proxyItem.Enabled = tunItem.Enabled = idle;
        proxyItem.Checked = Flag("systemProxy"); tunItem.Checked = Flag("tun");
        flyout?.Refresh();
    }

    private void ShowFlyout()
    {
        if (flyout is null) flyout = new TrayWindow(main, session, ChangeTun);
        if (flyout.IsVisible) { flyout.Hide(); return; }
        flyout.Refresh();
        flyout.Show();
        flyout.UpdateLayout();
        var point = Forms.Cursor.Position;
        var area = Forms.Screen.FromPoint(point).WorkingArea;
        // SetWindowPos uses physical pixels, avoiding wrong-DPI WPF bounds on a secondary screen.
        var handle = new System.Windows.Interop.WindowInteropHelper(flyout).Handle;
        var dpi = VisualTreeHelper.GetDpi(flyout);
        int margin = (int)Math.Round(10 * dpi.DpiScaleX);
        int width = Math.Min((int)Math.Ceiling(flyout.ActualWidth * dpi.DpiScaleX), Math.Max(1, area.Width - 2 * margin));
        int height = Math.Min((int)Math.Ceiling(flyout.ActualHeight * dpi.DpiScaleY), Math.Max(1, area.Height - 2 * margin));
        int left = Math.Clamp(point.X - width / 2, area.Left + margin, Math.Max(area.Left + margin, area.Right - width - margin));
        int top = Math.Clamp(point.Y - height - margin, area.Top + margin, Math.Max(area.Top + margin, area.Bottom - height - margin));
        SetWindowPos(handle, new IntPtr(-1), left, top, width, height, 0x0040);
        flyout.Activate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    public void Dispose()
    {
        clickTimer.Stop(); clickTimer.Dispose(); flyout?.Close();
        icon.Visible = false; var image = icon.Icon; icon.Dispose(); image?.Dispose(); menu.Dispose();
    }
}

internal sealed class TrayWindow : Window
{
    private readonly AppSession session;
    private readonly TextBlock state = Ui.Text("正在连接…", 20, true);
    private readonly TextBlock traffic = Ui.Muted("↑ —    ↓ —", 14);
    private readonly Button start = new();
    private readonly Button restart = new() { Content = "重启" };
    private readonly CheckBox proxy = new() { Content = "系统代理" };
    private readonly CheckBox tun = new() { Content = "TUN 模式" };
    private readonly CheckBox auto = new() { Content = "开机启动" };
    private readonly ComboBox mode = new() { ItemsSource = new[] { "规则", "全局", "直连" }, MinWidth = 100 };
    private readonly Func<Task> changeTun;
    private CancellationTokenSource? visible;
    private bool updating;

    public TrayWindow(MainWindow main, AppSession session, Func<Task> changeTun)
    {
        this.session = session; this.changeTun = changeTun;
        Title = "Mihomo Optimized"; Width = 360; Height = 406; MinWidth = 200; MinHeight = 180;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI, Microsoft YaHei UI"); FontSize = 14;
        var title = Ui.Muted("Mihomo Optimized", 12); title.Margin = new Thickness(0, 0, 0, 10);
        var open = new Button { Content = "打开主界面", MinHeight = 34, HorizontalAlignment = HorizontalAlignment.Stretch };
        open.Click += (_, _) => { Hide(); main.ShowMain(); };
        foreach (var button in new[] { start, restart }) { button.MinHeight = 34; button.Padding = new Thickness(14, 4, 14, 4); }
        foreach (var toggle in new[] { proxy, tun, auto }) toggle.Margin = new Thickness(0, 8, 0, 8);
        var panel = Ui.Column(title, state, Ui.Muted("本机代理"), Ui.Row(start, restart), Ui.Row(Ui.Muted("出站模式"), mode), proxy, tun, auto, traffic, open);
        state.Margin = new Thickness(0, 0, 0, 8); traffic.Margin = new Thickness(0, 12, 0, 18);
        Content = new ScrollViewer { Content = panel, Padding = new Thickness(24, 20, 24, 20), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        start.Click += async (_, _) => await Run(HostChannel.Text(session.Status, "phase") == "running" ? "desktop_stop" : "desktop_start");
        restart.Click += async (_, _) => await Run("desktop_restart");
        proxy.Click += async (_, _) => { if (!updating) await Run("desktop_set_system_proxy", new { enabled = proxy.IsChecked == true }); };
        tun.Click += async (_, _) => { if (!updating) await this.changeTun(); };
        auto.Click += async (_, _) => { if (!updating) await Run("desktop_set_autostart", new { enabled = auto.IsChecked == true }); };
        mode.SelectionChanged += async (_, _) =>
        {
            if (updating || mode.SelectedIndex < 0) return;
            await Run("desktop_patch_config", new { patch = new Dictionary<string, object> { ["mode"] = new[] { "rule", "global", "direct" }[mode.SelectedIndex] } });
        };
        IsVisibleChanged += (_, _) =>
        {
            visible?.Cancel(); visible?.Dispose(); visible = null;
            if (IsVisible) { visible = new CancellationTokenSource(); _ = ReadLiveAsync(visible.Token); }
        };
        Deactivated += (_, _) => Hide();
        Closed += (_, _) => { visible?.Cancel(); visible?.Dispose(); };
    }

    public void Refresh()
    {
        updating = true;
        string phase = HostChannel.Text(session.Status, "phase");
        state.Text = phase switch { "running" => "运行中", "starting" => "正在启动…", "stopping" => "正在停止…", "error" => "需要处理", _ => "已停止" };
        start.Content = phase == "running" ? "停止内核" : "启动内核";
        bool idle = session.Ready && phase is not ("starting" or "stopping");
        start.IsEnabled = restart.IsEnabled = proxy.IsEnabled = tun.IsEnabled = auto.IsEnabled = idle;
        mode.IsEnabled = idle && phase == "running" && !session.IsDirty;
        proxy.IsChecked = session.Status.Bool("systemProxy"); tun.IsChecked = session.Status.Bool("tun"); auto.IsChecked = session.Status.Bool("autostart");
        updating = false;
    }

    private async Task Run(string command, object? args = null)
    {
        try { await session.CommandAsync(command, args); }
        catch (Exception error) { session.Notify(error.Message, true); }
        finally { Refresh(); }
    }

    private async Task ReadLiveAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (HostChannel.Text(session.Status, "phase") == "running")
            {
                try
                {
                    var config = await session.ApiAsync(HttpMethod.Get, "configs", cancellationToken: cancellationToken);
                    updating = true;
                    mode.SelectedIndex = HostChannel.Text(config, "mode") switch { "global" => 1, "direct" => 2, _ => 0 };
                    updating = false;
                    await foreach (var sample in session.StreamAsync("traffic", cancellationToken))
                        traffic.Text = $"↑ {Ui.Bytes(sample.Number("up"))}/s    ↓ {Ui.Bytes(sample.Number("down"))}/s";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception) { traffic.Text = "等待内核数据…"; }
            }
            try { await Task.Delay(2000, cancellationToken); } catch (OperationCanceledException) { return; }
        }
    }
}
