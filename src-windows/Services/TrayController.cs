using Icon = System.Drawing.Icon;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Mihomo.Desktop.Controls;
using Forms = System.Windows.Forms;

namespace Mihomo.Desktop.Services;

internal sealed class TrayController : IDisposable
{
    private readonly MainWindow main;
    private readonly AppSession session;
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.ToolStripMenuItem stateItem = new() { Enabled = false };
    private readonly Forms.ToolStripMenuItem openItem = new();
    private readonly Forms.ToolStripMenuItem coreItem = new();
    private readonly Forms.ToolStripMenuItem restartItem = new();
    private readonly Forms.ToolStripMenuItem proxyItem = new();
    private readonly Forms.ToolStripMenuItem tunItem = new();
    private readonly Forms.ToolStripMenuItem restoreItem = new();
    private readonly Forms.ToolStripMenuItem exitItem = new();
    private TrayWindow? flyout;
    private bool commandBusy;
    private bool wasOpenAtMouseDown;
    private DateTime lastHidden;
    private DateTime suppressClicksUntil;
    public bool IsFlyoutVisible => flyout?.IsVisible == true;
    public FrameworkElement? VisibleSurface => flyout?.IsVisible == true ? flyout : null;
    public string Material => flyout?.Material ?? "solid";

    public TrayController(MainWindow main, AppSession session)
    {
        this.main = main; this.session = session;
        using var source = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico"))!.Stream;
        icon = new Forms.NotifyIcon { Text = "Mihomo Optimized", Icon = new Icon(source), Visible = true, ContextMenuStrip = menu };
        menu.Items.AddRange([stateItem, new Forms.ToolStripSeparator(), openItem, coreItem, restartItem,
            new Forms.ToolStripSeparator(), proxyItem, tunItem, restoreItem, new Forms.ToolStripSeparator(), exitItem]);
        openItem.Click += (_, _) => main.ShowMain();
        coreItem.Click += async (_, _) => await Run(IsRunning ? "desktop_stop" : "desktop_start");
        restartItem.Click += async (_, _) => await Run("desktop_restart");
        proxyItem.Click += async (_, _) => await Run("desktop_set_system_proxy", new { enabled = !session.Status.Bool("systemProxy") });
        tunItem.Click += async (_, _) => await ChangeTun(!session.Status.Bool("tun"));
        restoreItem.Click += async (_, _) => await Run("desktop_stop");
        exitItem.Click += async (_, _) => { if (await main.PrepareExitAsync()) await main.FinishExitAsync(); };
        icon.MouseDown += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                wasOpenAtMouseDown = IsFlyoutVisible || DateTime.UtcNow - lastHidden < TimeSpan.FromMilliseconds(250);
        };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left || DateTime.UtcNow < suppressClicksUntil) return;
            if (IsFlyoutVisible || wasOpenAtMouseDown) { flyout?.HideFlyout(); return; }
            ShowFlyout();
        };
        icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            suppressClicksUntil = DateTime.UtcNow.AddMilliseconds(Forms.SystemInformation.DoubleClickTime);
            flyout?.HideFlyout(); main.ShowMain();
        };
        menu.Opening += (_, _) => { flyout?.HideFlyout(); Refresh(); };
        L.Changed += Refresh; Refresh();
    }
    private bool IsRunning => session.Status.Text("phase") == "running";
    private async Task Run(string command, object? args = null)
    {
        if (commandBusy) return;
        commandBusy = true; Refresh();
        try { await session.CommandAsync(command, args); }
        catch (Exception error) { main.ShowMain(); session.Notify(error.Message, true); }
        finally { commandBusy = false; Refresh(); }
    }
    private async Task ChangeTun(bool enabled)
    {
        if (commandBusy) return;
        if (enabled && !session.Status.Get("service").Bool("installed"))
        {
            flyout?.HideFlyout(); main.ShowMain();
            commandBusy = true; Refresh();
            try { await TunConsentDialog.Run(main, async () => { await session.CommandAsync("desktop_set_tun", new { enabled }); }, error => session.Notify(error, true)); }
            finally { commandBusy = false; Refresh(); }
            return;
        }
        await Run("desktop_set_tun", new { enabled });
    }
    public void Refresh()
    {
        string phase = session.Status.Text("phase");
        bool idle = session.Ready && !session.IsUpdating && !commandBusy && phase is not ("starting" or "stopping" or "connecting");
        stateItem.Text = L.T(phase switch { "running" => "内核运行中", "starting" => "正在启动内核…", "stopping" => "正在停止内核…", "error" => "内核需要处理", "stopped" => "内核已停止", _ => "正在连接…" });
        icon.Text = "Mihomo Optimized · " + stateItem.Text;
        openItem.Text = L.T("打开主界面"); coreItem.Text = L.T(IsRunning ? "停止内核" : "启动内核"); restartItem.Text = L.T("重启内核");
        proxyItem.Text = L.T("系统代理"); tunItem.Text = L.T("TUN 模式"); restoreItem.Text = L.T("停止代理并恢复网络"); exitItem.Text = L.T("退出");
        coreItem.Enabled = restoreItem.Enabled = idle; restartItem.Enabled = idle && IsRunning;
        proxyItem.Enabled = idle && session.Status.Get("capabilities").Bool("systemProxy"); tunItem.Enabled = idle && session.Status.Get("capabilities").Bool("tun");
        proxyItem.Checked = session.Status.Bool("systemProxy"); tunItem.Checked = session.Status.Bool("tun");
        exitItem.Enabled = !session.IsUpdating;
        flyout?.Refresh(commandBusy);
    }
    internal void ShowFlyout()
    {
        if (flyout is null)
        {
            flyout = new TrayWindow(main, session, Run, ChangeTun);
            flyout.IsVisibleChanged += (_, _) => { if (!flyout.IsVisible) lastHidden = DateTime.UtcNow; };
        }
        if (flyout.IsVisible) { flyout.Activate(); return; }
        flyout.Refresh(commandBusy); PositionFlyout();
        flyout.Show(); flyout.Activate();
        // The supplied host shows/hides this native flyout immediately. Page
        // navigation motion must not be applied to the entire tray window.
    }
    private void PositionFlyout()
    {
        if (flyout is null) return;
        var point = Forms.Cursor.Position; var area = Forms.Screen.FromPoint(point).WorkingArea;
        var handle = new WindowInteropHelper(flyout).EnsureHandle();
        // Move the HWND to the target monitor before reading its per-monitor DPI.
        // WPF processes WM_DPICHANGED during this call; all final coordinates below
        // are physical pixels, including work-area clamps on vertical taskbars.
        _ = SetWindowPos(handle, new IntPtr(-1), area.Left + 12, area.Top + 12, 0, 0, 0x0001 | 0x0010);
        double scale = Math.Max(1, GetDpiForWindow(handle)) / 96d;
        int margin = (int)Math.Round(12 * scale);
        int width = Math.Max(1, Math.Min((int)Math.Round(360 * scale), area.Width - margin * 2));
        int height = Math.Max(1, Math.Min((int)Math.Round(492 * scale), area.Height - margin * 2));
        flyout.Width = width / scale; flyout.Height = height / scale; flyout.UpdateLayout();
        int left, top;
        if (point.X < area.Left) { left = area.Left + margin; top = point.Y - height / 2; }
        else if (point.X >= area.Right) { left = area.Right - width - margin; top = point.Y - height / 2; }
        else if (point.Y < area.Top) { left = point.X - width / 2; top = area.Top + margin; }
        else { left = point.X - width / 2; top = area.Bottom - height - margin; }
        left = Clamp(left, area.Left + margin, area.Right - width - margin);
        top = Clamp(top, area.Top + margin, area.Bottom - height - margin);
        _ = SetWindowPos(handle, new IntPtr(-1), left, top, width, height, 0x0010);
    }
    private static int Clamp(int value, int minimum, int maximum) => Math.Clamp(value, Math.Min(minimum, maximum), Math.Max(minimum, maximum));
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    public void Dispose()
    {
        L.Changed -= Refresh; flyout?.Close();
        icon.Visible = false; var image = icon.Icon; icon.Dispose(); image?.Dispose(); menu.Dispose();
    }
}

internal sealed class TrayWindow : Window
{
    private readonly MainWindow main;
    private readonly AppSession session;
    private readonly Func<string, object?, Task> command;
    private readonly Func<bool, Task> changeTun;
    private readonly NativeBackdrop backdrop;
    private readonly TextBlock state = Ui.Muted("");
    private readonly Ellipse stateDot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ModeSelector mode = new("TrayOutboundMode") { Stretch = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ToggleButton proxy;
    private readonly ToggleButton tun;
    private readonly ToggleButton lan;
    private readonly ExitSummaryList exits = new("TrayExitSummary", compact: true);
    private readonly StackPanel stopped;
    private readonly TextBlock stoppedTitle = Ui.Text("", 14, true);
    private readonly TextBlock stoppedHint = Ui.Muted("", 13);
    private readonly TextBlock notice = Ui.Text("", 12);
    private readonly FluentIcon stoppedIcon = new("plug-disconnected", 24);
    private readonly FluentProgressRing stoppedProgress = new() { Width = 28, Height = 28 };
    private readonly FluentProgressRing powerProgress = new() { Width = 16, Height = 16 };
    private readonly TextBlock down = Ui.Text("0 B/s", 12);
    private readonly TextBlock up = Ui.Text("0 B/s", 12);
    private readonly TrafficChart sparkline = new() { Height = 28, Compact = true, Margin = new Thickness(12, 0, 12, 0), MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button power;
    private readonly FluentIcon powerIcon = new("power", 16);
    private ProxyExitSnapshot exitSnapshot = ProxyExitSnapshot.Empty;
    private CancellationTokenSource? visible;
    private string controller = "";
    private bool applying;
    private bool outerBusy;
    private bool changing;
    private bool runtimeReady;
    private bool allowLan;
    private bool hiding;
    private bool closing;
    private bool closed;
    private long snapshotSequence;
    public Grid ClientArea { get; } = new();
    public string Material => backdrop.Material;
    private bool Running => session.Status.Text("phase") == "running";
    private string Controller => session.Status.Get("controller").Text("url") + "\n" + session.Status.Get("controller").Text("secret");

    public TrayWindow(MainWindow main, AppSession session, Func<string, object?, Task> command, Func<bool, Task> changeTun)
    {
        this.main = main; this.session = session; this.command = command; this.changeTun = changeTun;
        Width = 360; Height = 492; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        L.Bind(this, TitleProperty, "Mihomo Optimized 快捷面板"); this.Id("TrayFlyout");
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI Variable, Segoe UI, Microsoft YaHei UI, Microsoft YaHei"); FontSize = 14;
        ClientArea.SetResourceReference(Panel.BackgroundProperty, "DialogBottomBrush");
        ClientArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); ClientArea.RowDefinitions.Add(new RowDefinition()); ClientArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = ClientArea }; frame.SetResourceReference(Border.BorderBrushProperty, "AcrylicStrokeBrush"); Content = frame;
        var header = new Grid { Margin = new Thickness(16, 16, 12, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var logo = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")), Width = 32, Height = 32, Clip = new RectangleGeometry(new Rect(0, 0, 32, 32), 8, 8) };
        header.Children.Add(new Border { Child = logo, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal }; state.TextWrapping = TextWrapping.NoWrap; state.TextTrimming = TextTrimming.CharacterEllipsis; state.Id("TrayCoreStatus"); statusRow.Children.Add(stateDot); statusRow.Children.Add(state);
        var name = Ui.Text("Mihomo Optimized", 14, true); name.LineHeight = 20; state.LineHeight = 16;
        var identity = Ui.Column(name, statusRow); identity.Margin = new Thickness(0, 0, 12, 0); identity.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(identity, 1); header.Children.Add(identity);
        var settings = FluentControls.IconButton("nav-settings", "设置", "TrayOpenSettings"); settings.VerticalAlignment = VerticalAlignment.Center; settings.Margin = new Thickness(0, 0, 12, 0); settings.Click += async (_, _) => { HideFlyout(); main.ShowMain(); await main.NavigateAsync("settings"); }; Grid.SetColumn(settings, 2); header.Children.Add(settings);
        var open = FluentControls.IconButton("window-new", "打开主界面", "TrayOpenMain"); open.VerticalAlignment = VerticalAlignment.Center; open.Click += (_, _) => { HideFlyout(); main.ShowMain(); }; Grid.SetColumn(open, 3); header.Children.Add(open); ClientArea.Children.Add(header);
        mode.Margin = new Thickness(0, 0, 0, 12); mode.Selected += async value => await Run("desktop_patch_config", new { patch = new { mode = value } });
        var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(-4, 0, -4, 12) };
        (proxy, var proxyTile) = MakeTile("nav-proxies", "系统代理", "TraySystemProxy");
        (tun, var tunTile) = MakeTile("shield-lock", "TUN 模式", "TrayTun");
        (lan, var lanTile) = MakeTile("wifi", "局域网", "TrayAllowLan"); tiles.Children.Add(proxyTile); tiles.Children.Add(tunTile); tiles.Children.Add(lanTile);
        proxy.Checked += ProxyChanged; proxy.Unchecked += ProxyChanged;
        tun.Checked += TunChanged; tun.Unchecked += TunChanged;
        lan.Checked += LanChanged; lan.Unchecked += LanChanged;
        exits.Selected += async (exit, _) => { HideFlyout(); main.ShowMain(); await main.NavigateAsync(new NavigationTarget("proxies", Target: exit.Group, Action: "focus-group")); };
        var emptyGlyph = new Grid { Width = 48, Height = 48 }; emptyGlyph.Children.Add(stoppedIcon); emptyGlyph.Children.Add(stoppedProgress);
        var emptyCircle = new Border { CornerRadius = new CornerRadius(24), Child = emptyGlyph, Margin = new Thickness(0, 0, 0, 6) }; emptyCircle.SetResourceReference(Border.BackgroundProperty, "ControlAltBrush");
        stopped = Ui.Column(emptyCircle, stoppedTitle, stoppedHint); stopped.VerticalAlignment = VerticalAlignment.Center; stopped.Margin = new Thickness(12);
        foreach (FrameworkElement item in stopped.Children) item.HorizontalAlignment = HorizontalAlignment.Center;
        stoppedHint.TextAlignment = TextAlignment.Center; stoppedHint.Margin = new Thickness(0, 6, 0, 0); stoppedHint.MaxWidth = 280;
        stoppedIcon.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
        notice.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush"); notice.Visibility = Visibility.Collapsed; notice.Margin = new Thickness(0, 0, 0, 12); notice.Id("TrayError");
        var body = new Grid();
        for (int row = 0; row < 4; row++) body.RowDefinitions.Add(new RowDefinition { Height = row < 3 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        body.Children.Add(mode); Grid.SetRow(tiles, 1); body.Children.Add(tiles); Grid.SetRow(notice, 2); body.Children.Add(notice);
        exits.VerticalAlignment = VerticalAlignment.Top; Grid.SetRow(exits, 3); body.Children.Add(exits); Grid.SetRow(stopped, 3); body.Children.Add(stopped);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(16, 4, 16, 12) };
        Grid.SetRow(scroll, 1); ClientArea.Children.Add(scroll);
        scroll.ScrollChanged += (_, e) => { if (e.ViewportHeightChange != 0) body.MinHeight = Math.Max(0, e.ViewportHeight); };
        var footerGrid = new Grid(); footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footerGrid.ColumnDefinitions.Add(new ColumnDefinition()); footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        down.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); up.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        ByteReading.SetRate(down, 0); ByteReading.SetRate(up, 0);
        var uploadRate = Rate("arrow-up", up); uploadRate.Margin = new Thickness(0, 2, 0, 0);
        var rates = Ui.Column(Rate("arrow-down", down), uploadRate); rates.MinWidth = 92; rates.VerticalAlignment = VerticalAlignment.Center; footerGrid.Children.Add(rates); Grid.SetColumn(sparkline, 1); footerGrid.Children.Add(sparkline);
        var powerContents = new Grid(); powerContents.Children.Add(powerIcon); powerContents.Children.Add(powerProgress);
        power = FluentControls.IconButton("power", "启动内核", "TrayPower", 36); power.Content = powerContents; power.Style = FluentControls.Style("Button"); power.VerticalAlignment = VerticalAlignment.Center;
        power.Click += async (_, _) => await Run(Running ? "desktop_stop" : "desktop_start"); Grid.SetColumn(power, 2); footerGrid.Children.Add(power);
        var footer = new Border { Child = footerGrid, Padding = new Thickness(16, 12, 12, 12), BorderThickness = new Thickness(0, 1, 0, 0) }; footer.SetResourceReference(Border.BorderBrushProperty, "DividerBrush"); footer.SetResourceReference(Border.BackgroundProperty, "DialogTopBrush"); Grid.SetRow(footer, 2); ClientArea.Children.Add(footer);
        backdrop = new NativeBackdrop(this, ClientArea, session, transient: true);
        IsVisibleChanged += (_, _) =>
        {
            visible?.Cancel(); visible?.Dispose(); visible = null;
            if (IsVisible) { visible = new CancellationTokenSource(); _ = ObserveLiveAsync(visible.Token); }
        };
        Deactivated += (_, _) => HideFlyout();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { HideFlyout(); e.Handled = true; } };
        L.Changed += LanguageChanged;
        Closed += (_, _) => { closed = true; L.Changed -= LanguageChanged; visible?.Cancel(); visible?.Dispose(); visible = null; backdrop.Dispose(); };
        Refresh(false);
    }
    internal void HideFlyout()
    {
        if (hiding || closing || closed || !IsVisible) return;
        hiding = true;
        try { Hide(); }
        finally { hiding = false; }
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing an active HWND can raise Deactivated before Closed. Do not
        // change its visibility while WPF is already destroying the window.
        closing = true;
        base.OnClosing(e);
        if (e.Cancel) closing = false;
    }
    private static (ToggleButton Button, FrameworkElement Tile) MakeTile(string icon, string label, string id)
    {
        var button = new ToggleButton { Content = new FluentIcon(icon, 16), Style = FluentControls.Style("QuickToggle") }.Id(id);
        L.Bind(button, AutomationProperties.NameProperty, label);
        var caption = Ui.Label(label, 12); caption.LineHeight = 16; caption.TextWrapping = TextWrapping.NoWrap; caption.TextTrimming = TextTrimming.CharacterEllipsis; caption.HorizontalAlignment = HorizontalAlignment.Center; caption.TextAlignment = TextAlignment.Center; caption.Margin = new Thickness(0, 6, 0, 0);
        var tile = Ui.Column(button, caption); tile.Margin = new Thickness(4, 0, 4, 0); return (button, tile);
    }
    private static FrameworkElement Rate(string name, TextBlock value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var glyph = new FluentIcon(name, 12) { Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center }; glyph.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
        row.Children.Add(glyph); row.Children.Add(value); return row;
    }
    private async void ProxyChanged(object sender, RoutedEventArgs e) { if (!applying && proxy.IsEnabled) await Run("desktop_set_system_proxy", new { enabled = proxy.IsChecked == true }); }
    private async void LanChanged(object sender, RoutedEventArgs e) { if (!applying && lan.IsEnabled) await Run("desktop_patch_config", new { patch = new Dictionary<string, object> { ["allow-lan"] = lan.IsChecked == true } }); }
    private async void TunChanged(object sender, RoutedEventArgs e)
    {
        if (applying || !tun.IsEnabled || changing) return;
        changing = true; bool enabled = tun.IsChecked == true; Refresh(outerBusy);
        try { await changeTun(enabled); }
        catch (Exception error) { ShowError(error.Message); }
        finally { changing = false; Refresh(outerBusy); }
    }
    private void LanguageChanged() => Refresh(outerBusy);
    public void Refresh(bool operationBusy)
    {
        outerBusy = operationBusy; applying = true;
        try
        {
            bool running = Running; string phase = session.Status.Text("phase", fallback: "connecting");
            bool busy = phase is "starting" or "stopping" or "connecting";
            if (controller != Controller || !running && runtimeReady)
            {
                controller = Controller; runtimeReady = false; allowLan = false; exitSnapshot = ProxyExitSnapshot.Empty;
                sparkline.Clear(); ByteReading.SetRate(down, 0); ByteReading.SetRate(up, 0);
            }
            string label = L.T(phase switch { "running" => "运行中", "starting" => "正在启动…", "stopping" => "正在停止…", "error" => "需要处理", "stopped" => "已停止", _ => "正在连接…" });
            string modeLabel = L.T(mode.Mode switch { "global" => "全局模式", "direct" => "直连模式", _ => "规则模式" }); state.Text = label + (running ? " · " + modeLabel : "");
            stateDot.SetResourceReference(Shape.FillProperty, running ? "SuccessBrush" : busy ? "WarningBrush" : phase == "error" ? "DangerBrush" : "TextTertiaryBrush");
            bool interactive = session.Ready && !busy && !outerBusy && !changing && !session.IsUpdating;
            proxy.IsChecked = session.Status.Bool("systemProxy"); tun.IsChecked = session.Status.Bool("tun"); lan.IsChecked = allowLan;
            power.IsEnabled = interactive; proxy.IsEnabled = interactive && session.Status.Get("capabilities").Bool("systemProxy"); tun.IsEnabled = interactive && session.Status.Get("capabilities").Bool("tun"); lan.IsEnabled = mode.IsEnabled = interactive && running && runtimeReady && !session.IsDirty;
            foreach (var item in new[] { proxy, tun, lan }) if (item.Content is FluentIcon glyph) glyph.Filled = item.IsChecked == true;
            exits.Visibility = running ? Visibility.Visible : Visibility.Collapsed; stopped.Visibility = running ? Visibility.Collapsed : Visibility.Visible; exits.Update(exitSnapshot.Summaries.Take(3));
            stoppedTitle.Text = busy ? label : L.T("内核未运行"); stoppedHint.Text = session.Status.Text("lastError", fallback: busy ? L.T("首次启动可能需要更长时间，请稍候。") : L.T("启动后即可切换模式、出口与快捷开关。"));
            stoppedIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible; stoppedProgress.IsActive = busy;
            powerProgress.IsActive = busy || outerBusy || changing; powerIcon.Visibility = powerProgress.IsActive ? Visibility.Collapsed : Visibility.Visible;
            powerIcon.Filled = running;
            power.SetResourceReference(Control.BackgroundProperty, running ? "AccentBrush" : "ControlBrush");
            power.SetResourceReference(Control.ForegroundProperty, running ? "AccentTextBrush" : "TextPrimaryBrush");
            power.SetResourceReference(Control.BorderBrushProperty, running ? "AccentBrush" : "ControlStrokeBrush");
            powerProgress.SetResourceReference(FluentProgressRing.ForegroundProperty, running ? "AccentTextBrush" : "AccentBrush");
            L.Bind(power, AutomationProperties.NameProperty, running ? "停止内核" : "启动内核"); L.Bind(power, ToolTipProperty, running ? "停止内核" : "启动内核");
        }
        finally { applying = false; }
    }
    private async Task Run(string method, object? args = null)
    {
        if (applying || changing) return;
        changing = true; Refresh(outerBusy);
        try { await command(method, args); if (IsVisible && Running && visible is { } lifetime) await ReadSnapshotAsync(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error.Message); }
        finally { changing = false; Refresh(outerBusy); }
    }
    private void ShowError(string error) { notice.Text = error; notice.Visibility = error.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
    private async Task ObserveLiveAsync(CancellationToken token)
    {
        try { await Task.WhenAll(ReadSnapshotLoopAsync(token), ReadTrafficAsync(token)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested && IsVisible) ShowError(error.Message); }
    }
    private async Task ReadSnapshotLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (Running && !changing)
            {
                try { await ReadSnapshotAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error) { if (!token.IsCancellationRequested && IsVisible) ShowError(error.Message); }
            }
            await Task.Delay(2000, token);
        }
    }
    private async Task ReadSnapshotAsync(CancellationToken token)
    {
        string source = Controller; long sequence = ++snapshotSequence;
        var configTask = session.ApiAsync(HttpMethod.Get, "configs", cancellationToken: token);
        var exitsTask = ProxyExitSnapshot.ReadAsync(session, token); await Task.WhenAll(configTask, exitsTask);
        if (token.IsCancellationRequested || !IsVisible || !Running || source != Controller || sequence != snapshotSequence) return;
        var config = await configTask; mode.SetMode(config.Text("mode")); allowLan = config.Bool("allow-lan"); runtimeReady = true; controller = source; exitSnapshot = await exitsTask;
        ShowError(""); Refresh(outerBusy);
    }
    private async Task ReadTrafficAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (Running)
            {
                string source = Controller;
                try
                {
                    await foreach (var sample in session.StreamAsync("traffic", token))
                    {
                        if (token.IsCancellationRequested) return;
                        if (source != Controller || !Running) break;
                        double upload = sample.Number("up"), download = sample.Number("down");
                        ByteReading.SetRate(up, upload); ByteReading.SetRate(down, download); sparkline.Add(upload, download);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (OperationCanceledException) { }
                catch (Exception error) { if (!token.IsCancellationRequested && IsVisible && source == Controller) ShowError(error.Message); }
            }
            await Task.Delay(2000, token);
        }
    }
}
