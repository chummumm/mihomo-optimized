using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class SettingsPage : NativePage
{
    private sealed record Choice(string Label, string Value)
    {
        // WPF item peers read Content.ToString(), independently of DisplayMemberPath.
        public override string ToString() => Label;
    }
    private sealed record Destination(SettingCard Card, Control? Control);
    private sealed class SettingCard : Border
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new CardPeer(this);
        private sealed class CardPeer(SettingCard owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
            protected override string GetClassNameCore() => "SettingCard";
        }
    }

    private const string DefaultProbeUrl = "https://www.gstatic.com/generate_204";
    private readonly ComboBox theme = Selection("AppTheme", "应用主题");
    private readonly ComboBox language = Selection("AppLanguage", "显示语言");
    private readonly ComboBox mode = Selection("SettingsProxyMode", "代理模式");
    private readonly CheckBox autostart = Toggle("SettingsAutostart", "开机启动");
    private readonly CheckBox systemProxy = Toggle("SettingsSystemProxy", "系统代理");
    private readonly CheckBox allowLan = Toggle("SettingsAllowLan", "允许局域网连接");
    private readonly CheckBox tun = Toggle("SettingsTun", "TUN 模式");
    private readonly TextBox mixedPort = PortInput("SettingsMixedPort", "混合代理端口");
    private readonly TextBox httpPort = PortInput("SettingsHttpPort", "HTTP 代理端口");
    private readonly TextBox socksPort = PortInput("SettingsSocksPort", "SOCKS5 代理端口");
    private readonly TextBox probeUrl = new() { MinHeight = 34, MaxLength = 2048, VerticalContentAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
    private readonly TextBlock versions = Ui.Text("", 13);
    private readonly TextBlock serviceStatus = Ui.Muted("");
    private readonly TextBlock runtimeStatus = Ui.Muted("");
    private readonly TextBlock configPath = Ui.Muted("");
    private readonly TextBlock logsPath = Ui.Muted("");
    private readonly TextBlock updateStatus = Ui.Text("");
    private readonly TextBlock progress = Ui.Muted("");
    private readonly ProgressBar progressBar = new() { Minimum = 0, Maximum = 100, Height = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBox releaseNotes = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, BorderThickness = new Thickness(0), MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 4) };
    private readonly Button install;
    private readonly Button check;
    private readonly Button openRelease;
    private readonly Button installService;
    private readonly Button uninstallService;
    private readonly Button saveProbe;
    private readonly Button resetProbe;
    private readonly Button openConfig;
    private readonly Button openLogs;
    private readonly Button openProject;
    private readonly ScrollViewer scroll;
    private readonly Dictionary<string, Destination> destinations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBox> ports;
    private readonly HashSet<string> portDrafts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim runtimeRead = new(1, 1);
    private readonly DispatcherTimer focusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private SettingCard? highlighted;
    private JsonElement runtime;
    private JsonElement update;
    private JsonElement updateProgress;
    private string runtimeIdentity = "";
    private string previousPhase = "";
    private bool applying;
    private bool changing;
    private bool probeDraft;
    private bool checking;
    private bool installing;
    private TaskCompletionSource<bool>? pendingOperation;
    private string renderedUpdate = "";

    private bool DesktopBusy => changing || !Session.Ready || Session.IsUpdating || Session.Status.Text("phase") is "connecting" or "starting" or "stopping";
    private bool RuntimeEditable => !DesktopBusy && IsRunning && !Session.IsDirty && runtimeIdentity == ControllerIdentity;

    public SettingsPage(AppSession session) : base(session, "settings", "设置", "外观、网络代理、TUN 与应用程序更新")
    {
        ports = new(StringComparer.Ordinal) { ["mixed-port"] = mixedPort, ["port"] = httpPort, ["socks-port"] = socksPort };
        theme.SelectionChanged += (_, _) => { if (!applying && theme.SelectedValue is string value) Session.SetTheme(value); };
        language.SelectionChanged += (_, _) => { if (!applying && language.SelectedValue is string value) Session.SetLanguage(value); };
        mode.SelectionChanged += async (_, _) =>
        {
            if (!applying && mode.SelectedValue is string value)
                await RunAsync(() => ChangeRuntime(new() { ["mode"] = value }));
        };
        BindToggle(autostart, "desktop_set_autostart"); BindToggle(systemProxy, "desktop_set_system_proxy");
        RoutedEventHandler changeLan = async (_, _) => { if (!applying) await RunAsync(() => ChangeRuntime(new() { ["allow-lan"] = allowLan.IsChecked == true })); };
        allowLan.Checked += changeLan; allowLan.Unchecked += changeLan;
        RoutedEventHandler changeTun = async (_, _) =>
        {
            if (applying) return;
            bool enabled = tun.IsChecked == true;
            await RunAsync(async () =>
            {
                if (enabled && !Session.Status.Get("service").Bool("installed") && !ConfirmServiceInstall(true)) { ApplyStatus(); return; }
                await ChangeDesktop("desktop_set_tun", new { enabled });
            });
            ApplyStatus();
        };
        tun.Checked += changeTun; tun.Unchecked += changeTun;
        foreach (var (key, input) in ports)
        {
            input.TextChanged += (_, _) => { if (!applying) portDrafts.Add(key); };
            input.LostKeyboardFocus += async (_, _) => { if (!applying && !changing && IsActive) await RunAsync(() => CommitPort(key)); };
            input.PreviewKeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; await RunAsync(() => CommitPort(key)); }
                else if (e.Key == Key.Escape) { e.Handled = true; RestorePort(key); }
            };
        }
        probeUrl.Id("SettingsProbeUrl"); L.Bind(probeUrl, AutomationProperties.NameProperty, "测速地址");
        probeUrl.TextChanged += (_, _) => { if (!applying) probeDraft = probeUrl.Text != Session.TestUrl; ApplyProbeControls(); };
        probeUrl.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = RunAsync(SaveProbe); } };
        saveProbe = Action("保存", "SaveProbeUrl", SaveProbe, true);
        resetProbe = Action("恢复默认", "ResetProbeUrl", ResetProbe);
        installService = Action("安装服务", "InstallService", async () =>
        {
            if (ConfirmServiceInstall(false)) await ChangeDesktop("desktop_install_service");
        });
        uninstallService = Action("卸载服务", "UninstallService", async () =>
        {
            if (Session.Status.Bool("tun")) throw new InvalidOperationException(L.T("请先关闭 TUN 模式，再卸载服务。"));
            if (MessageBox.Show(Window.GetWindow(this), L.T("卸载本机服务后，需重新安装才能开启 TUN。普通系统代理仍可使用。继续卸载？"), L.T("卸载服务"), MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                await ChangeDesktop("desktop_uninstall_service");
        });
        check = Action("检查更新", "CheckAppUpdate", CheckUpdate);
        install = Action("下载并安装", "InstallAppUpdate", InstallUpdate, true);
        openRelease = Action("打开发行页面", "OpenAppRelease", OpenReleasePage);
        openConfig = Action("打开配置文件夹", "OpenConfigFolder", () => Command("desktop_open_directory", new { kind = "config" }));
        openLogs = Action("打开日志目录", "OpenLogsFolder", () => Command("desktop_open_directory", new { kind = "logs" }));
        openProject = Action("项目主页", "OpenProject", () => Command("desktop_open_external", new { url = "https://github.com/chummumm/mihomo-optimized" }));
        updateStatus.Id("AppUpdateStatus"); progress.Id("AppUpdateProgress"); progressBar.Id("AppUpdateProgressBar"); releaseNotes.Id("AppReleaseNotes");
        runtimeStatus.Id("SettingsRuntimeStatus"); configPath.Id("SettingsConfigPath"); logsPath.Id("SettingsLogPath"); serviceStatus.Id("SettingsServiceStatus");
        AutomationProperties.SetLiveSetting(progress, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(serviceStatus, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(runtimeStatus, AutomationLiveSetting.Polite);
        var portFields = Ui.Row(PortField("混合", mixedPort), PortField("HTTP", httpPort), PortField("SOCKS5", socksPort));
        var probeActions = Ui.Row(saveProbe, resetProbe); probeActions.Margin = new Thickness(0, 10, 0, 0);
        var updateDetails = Ui.Column(updateStatus, Ui.Row(check, install, openRelease), progress, progressBar, releaseNotes);
        updateStatus.Margin = new Thickness(0, 0, 0, 12);
        var appearance = Section("外观",
            Row("set-theme", "dark-theme", "应用主题", "使用 Windows 强调色，浅色或深色可跟随系统。", theme),
            Row("set-language", "language", "显示语言", "立即切换界面语言，无需重新启动。", language),
            Row("set-autostart", "power", "开机启动", "登录 Windows 后在系统托盘中启动。", autostart));
        var proxy = Section("代理",
            Row("set-system-proxy", "nav-proxies", "系统代理", "让遵循 Windows 系统代理设置的应用通过本机代理连接。", systemProxy),
            Row("set-mode", "arrow-swap", "代理模式", "规则模式按规则分流；全局模式使用统一出口；直连模式不使用代理。", mode),
            Row("set-lan", "wifi", "允许局域网连接", "允许同一网络中的设备连接本机代理端口。", allowLan),
            Row("set-ports", "router", "代理端口", "范围为 0–65535，0 表示关闭。按 Enter 或移开焦点后保存并应用。", null, portFields, mixedPort),
            Row("set-probe", "top-speed", "测速地址", "用于节点与策略组的延迟测试。请输入有效的 HTTP 或 HTTPS 地址。", null, Ui.Column(probeUrl, probeActions), probeUrl));
        runtimeStatus.Margin = new Thickness(2, 8, 2, 0); proxy.Children.Add(runtimeStatus);
        var tunSection = Section("TUN",
            Row("set-tun", "shield-lock", "TUN 模式", "通过虚拟网卡接管流量。首次开启需要安装本机服务。", tun),
            Row("set-service", "shield-check", "TUN 服务", "", Ui.Row(installService, uninstallService), null, installService, serviceStatus));
        var updates = Section("更新与关于",
            Row("desktop-app-update", "update", "应用程序更新", "检查正式发行版本并验证下载来源。", null, updateDetails, check),
            Row("set-about", "info", "Mihomo Optimized", "", openProject, null, openProject, versions));
        destinations["set-update"] = destinations["desktop-app-update"];
        var folders = Section("文件夹",
            Row("set-config-folder", "folder-open", "配置文件夹", "", openConfig, null, openConfig, configPath),
            Row("set-log-folder", "nav-logs", "日志文件夹", "", openLogs, null, openLogs, logsPath));
        var contents = Ui.Column(appearance, proxy, tunSection, updates, folders);
        contents.MaxWidth = 1000; contents.HorizontalAlignment = HorizontalAlignment.Stretch;
        scroll = Ui.Scroll(contents); scroll.Id("SettingsScrollViewer"); SetBody(scroll);
        focusTimer.Tick += (_, _) => RestoreHighlight();
        Session.HostEvent += OnHostEvent;
        Session.PropertyChanged += OnPreferenceChanged;
        PopulateChoices(); ApplyStatus();
    }

    private static ComboBox Selection(string id, string title)
    {
        var input = new ComboBox { MinWidth = 154, MinHeight = 34, DisplayMemberPath = nameof(Choice.Label), SelectedValuePath = nameof(Choice.Value), VerticalAlignment = VerticalAlignment.Center }.Id(id);
        L.Bind(input, AutomationProperties.NameProperty, title); return input;
    }
    private static CheckBox Toggle(string id, string title)
    {
        var input = new CheckBox { Style = FluentControls.Style("Switch"), MinHeight = 34, MinWidth = 40, VerticalAlignment = VerticalAlignment.Center }.Id(id);
        L.Bind(input, AutomationProperties.NameProperty, title); return input;
    }
    private static TextBox PortInput(string id, string title)
    {
        var input = new TextBox { Width = 100, MinHeight = 34, MaxLength = 5, TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono, Consolas") }.Id(id);
        L.Bind(input, AutomationProperties.NameProperty, title); return input;
    }
    private static StackPanel PortField(string title, TextBox input)
    {
        var label = Ui.Label(title, 12); label.Margin = new Thickness(0, 0, 0, 6);
        return Ui.Column(label, input);
    }
    private static StackPanel Section(string title, params UIElement[] children)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 28) };
        var heading = Ui.Label(title, 14, true); heading.Margin = new Thickness(2, 0, 0, 10);
        section.Children.Add(heading); foreach (var child in children) section.Children.Add(child); return section;
    }
    private SettingCard Row(string id, string glyph, string title, string description, FrameworkElement? control,
        FrameworkElement? footer = null, Control? focus = null, TextBlock? descriptionBlock = null)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var icon = new FluentIcon(glyph, 20) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) }; grid.Children.Add(icon);
        var label = Ui.Label(title, 14, true);
        var hint = descriptionBlock ?? Ui.Hint(description); hint.Margin = new Thickness(0, 4, 0, 0);
        hint.Visibility = descriptionBlock is null && description.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var text = Ui.Column(label, hint); Grid.SetColumn(text, 1); grid.Children.Add(text);
        if (control is not null)
        {
            control.Margin = new Thickness(18, 0, 0, 0); control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 2); grid.Children.Add(control);
        }
        else Grid.SetColumnSpan(text, 2);
        if (footer is not null)
        {
            var bottom = new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 14, 0, 0), Margin = new Thickness(0, 14, 0, 0) };
            bottom.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            Grid.SetRow(bottom, 1); Grid.SetColumn(bottom, 1); Grid.SetColumnSpan(bottom, 2); grid.Children.Add(bottom);
        }
        var card = new SettingCard { Child = grid, Padding = new Thickness(16), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 4), MinHeight = 72, Focusable = true }.Id(id);
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        L.Bind(card, AutomationProperties.NameProperty, title);
        destinations[id] = new(card, focus ?? control as Control);
        return card;
    }

    private void PopulateChoices()
    {
        applying = true;
        theme.ItemsSource = new[] { new Choice(L.T("跟随系统"), "system"), new Choice(L.T("浅色"), "light"), new Choice(L.T("深色"), "dark") };
        language.ItemsSource = new[] { new Choice(L.T("跟随系统"), "system"), new Choice("中文", "zh-CN"), new Choice("English", "en-US") };
        mode.ItemsSource = new[] { new Choice(L.T("规则"), "rule"), new Choice(L.T("全局"), "global"), new Choice(L.T("直连"), "direct") };
        theme.SelectedValue = Session.Theme; language.SelectedValue = Session.Language;
        mode.SelectedValue = runtime.Text("mode"); applying = false;
    }

    public override async Task RefreshAsync()
    {
        ApplyStatus();
        if (IsRunning && !changing) await ReadRuntime();
    }
    protected override Task LiveAsync(CancellationToken cancellationToken) => PollAsync(TimeSpan.FromSeconds(5), async () =>
    {
        if (!changing && runtimeRead.CurrentCount > 0) await ReadRuntime();
    }, cancellationToken);
    protected override void OnStatusChanged()
    {
        string phase = Session.Status.Text("phase"); bool becameRunning = previousPhase != phase && phase == "running";
        previousPhase = phase;
        ApplyStatus();
        if (becameRunning && !changing && IsActive) _ = RunAsync(RefreshAsync);
    }
    protected override void OnLanguageChanged()
    {
        PopulateChoices(); ApplyStatus();
        if (checking) updateStatus.Text = L.T("正在检查更新…");
        else if (update.ValueKind == JsonValueKind.Object) RenderUpdate(update);
        if (updateProgress.ValueKind == JsonValueKind.Object) RenderProgress(updateProgress);
    }
    private void OnPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSession.IsDirty) or nameof(AppSession.TestUrl) or nameof(AppSession.Language)) ApplyStatus();
    }
    private void ApplyStatus()
    {
        var status = Session.Status; var capability = status.Get("capabilities"); var service = status.Get("service");
        bool installed = service.Bool("installed"), running = service.Bool("running");
        versions.Text = L.F("客户端 {0} · 内核 {1}", status.Text("appVersion", fallback: "—"), status.Text("coreVersion", fallback: L.T("尚未启动")));
        serviceStatus.Text = L.T(installed ? running ? "服务已安装并运行。TUN 使用此服务管理虚拟网卡。" : "服务已安装，当前未运行。" : "服务尚未安装。首次开启 TUN 时会请求管理员权限。");
        configPath.Text = status.Text("configPath", fallback: L.T("本机配置与订阅文件所在目录。"));
        logsPath.Text = status.Text("logPath", fallback: L.T("查看内核与桌面服务日志。"));
        applying = true;
        theme.SelectedValue = Session.Theme; language.SelectedValue = Session.Language;
        SetToggle(autostart, status.Bool("autostart")); SetToggle(systemProxy, status.Bool("systemProxy")); SetToggle(tun, status.Bool("tun"));
        if (!probeDraft) probeUrl.Text = Session.TestUrl;
        applying = false;
        theme.IsEnabled = language.IsEnabled = !Session.IsUpdating;
        autostart.IsEnabled = !DesktopBusy && capability.Bool("autostart");
        systemProxy.IsEnabled = !DesktopBusy && capability.Bool("systemProxy");
        tun.IsEnabled = !DesktopBusy && !Session.IsDirty && capability.Bool("tun");
        mode.IsEnabled = allowLan.IsEnabled = RuntimeEditable;
        foreach (var port in ports.Values) port.IsEnabled = RuntimeEditable;
        installService.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        uninstallService.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        installService.IsEnabled = !DesktopBusy && capability.Bool("serviceManagement") && !installed;
        uninstallService.IsEnabled = !DesktopBusy && capability.Bool("serviceManagement") && installed && !status.Bool("tun");
        L.Bind(uninstallService, ToolTipProperty, status.Bool("tun") ? "请先关闭 TUN 模式，再卸载服务。" : "卸载本机 TUN 服务");
        openConfig.IsEnabled = openLogs.IsEnabled = openProject.IsEnabled = Session.Ready && !Session.IsUpdating;
        runtimeStatus.Text = Session.IsDirty ? L.T("配置编辑器中有未保存的修改，请先保存或放弃后再修改网络设置。")
            : DesktopBusy ? L.T("正在应用操作，请稍候。")
            : !IsRunning ? L.T("启动内核后可修改代理模式、局域网访问和端口。") : "";
        runtimeStatus.Visibility = runtimeStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (status.Text("lastError").Length > 0) Message(status.Text("lastError"), true);
        if (!checking && !installing && !Session.IsUpdating && Session.Update.Text("currentVersion").Length > 0 && Session.Update.GetRawText() != renderedUpdate) RenderUpdate(Session.Update);
        if (renderedUpdate.Length == 0 && !checking) updateStatus.Text = L.T("检查可用的应用更新");
        ApplyProbeControls(); ApplyUpdateControls();
    }
    private static void SetToggle(CheckBox toggle, bool enabled)
    {
        toggle.IsChecked = enabled; toggle.Content = L.T(enabled ? "已启用" : "已停用");
    }

    private async Task ReadRuntime(bool force = false)
    {
        if (!IsRunning) return;
        // An already submitted change still needs readback if the window is hidden to the tray.
        var cancellation = force ? CancellationToken.None : PageToken;
        await runtimeRead.WaitAsync(cancellation);
        try
        {
            if (!IsRunning) return;
            string identity = ControllerIdentity;
            var result = await Session.ApiAsync(HttpMethod.Get, "configs", cancellationToken: cancellation);
            if (!IsRunning || identity != ControllerIdentity || (changing && !force)) return;
            runtime = result; runtimeIdentity = identity;
            ApplyRuntime(force); ApplyStatus();
        }
        finally { runtimeRead.Release(); }
    }
    private void ApplyRuntime(bool force = false)
    {
        applying = true;
        mode.SelectedValue = runtime.Text("mode"); SetToggle(allowLan, runtime.Bool("allow-lan"));
        foreach (var (key, input) in ports)
        {
            if (force || !portDrafts.Contains(key)) input.Text = runtime.Number(key).ToString("0", CultureInfo.InvariantCulture);
        }
        if (force) portDrafts.Clear();
        applying = false;
    }
    private void BindToggle(CheckBox toggle, string method)
    {
        RoutedEventHandler change = async (_, _) =>
        {
            if (applying) return;
            bool enabled = toggle.IsChecked == true;
            await RunAsync(() => ChangeDesktop(method, new { enabled })); ApplyStatus();
        };
        toggle.Checked += change; toggle.Unchecked += change;
    }
    private async Task ChangeDesktop(string method, object? arguments = null)
    {
        if (DesktopBusy) { ApplyStatus(); throw new InvalidOperationException(L.T("正在应用操作，请稍候。")); }
        BeginChange(); bool success = false;
        try
        {
            await Command(method, arguments); Message("");
            if (IsRunning) await ReadRuntime(force: true);
            success = true;
        }
        finally { EndChange(success); }
    }
    private async Task ChangeRuntime(Dictionary<string, object> patch)
    {
        if (!RuntimeEditable) { ApplyRuntime(force: true); ApplyStatus(); throw new InvalidOperationException(L.T("当前无法修改网络设置，请等待内核运行并保存配置编辑器中的修改。")); }
        BeginChange();
        Exception? failure = null;
        try
        {
            // The host persists the source YAML, checks unsaved edits and updates owned proxy ports.
            await Command("desktop_patch_config", new { patch });
            Message("");
        }
        catch (Exception error) { failure = error; }
        try
        {
            if (IsRunning) await ReadRuntime(force: true);
            else ApplyRuntime(force: true);
        }
        catch (Exception error) { ApplyRuntime(force: true); failure ??= error; }
        finally { EndChange(failure is null); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private void BeginChange()
    {
        changing = true;
        pendingOperation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ApplyStatus();
    }
    private void EndChange(bool success)
    {
        changing = false; ApplyStatus();
        var completion = pendingOperation; pendingOperation = null; completion?.TrySetResult(success);
    }
    public override async Task<bool> CanLeaveAsync()
    {
        // A port commits on focus loss; complete its save/readback before deactivating the page.
        if (pendingOperation is { } pending && !await pending.Task) return false;
        foreach (string key in portDrafts.ToArray())
        {
            try { await CommitPort(key); }
            catch (Exception error) { Message(error.Message, true); return false; }
        }
        return true;
    }
    private async Task CommitPort(string key)
    {
        if (!portDrafts.Contains(key) || changing) return;
        if (!int.TryParse(ports[key].Text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value is < 0 or > 65535)
        {
            RestorePort(key); throw new InvalidOperationException(L.T("端口必须是 0–65535 之间的整数；0 表示关闭。"));
        }
        if (value == (int)runtime.Number(key)) { RestorePort(key); return; }
        await ChangeRuntime(new() { [key] = value });
    }
    private void RestorePort(string key)
    {
        applying = true; ports[key].Text = runtime.Number(key).ToString("0", CultureInfo.InvariantCulture); portDrafts.Remove(key); applying = false;
    }
    private Task SaveProbe()
    {
        if (Session.IsUpdating) throw new InvalidOperationException(L.T("正在安装应用更新，请等待更新完成。"));
        Session.SetTestUrl(probeUrl.Text);
        probeDraft = false; applying = true; probeUrl.Text = Session.TestUrl; applying = false;
        ApplyProbeControls(); Message("测速地址已保存。"); return Task.CompletedTask;
    }
    private Task ResetProbe()
    {
        if (Session.IsUpdating) throw new InvalidOperationException(L.T("正在安装应用更新，请等待更新完成。"));
        Session.SetTestUrl(DefaultProbeUrl); probeDraft = false;
        applying = true; probeUrl.Text = Session.TestUrl; applying = false;
        ApplyProbeControls(); Message("测速地址已恢复默认。"); return Task.CompletedTask;
    }
    private void ApplyProbeControls()
    {
        // TextChanged can fire while the constructor is still assigning the action buttons.
        if (saveProbe is null || resetProbe is null) return;
        probeUrl.IsEnabled = !Session.IsUpdating;
        saveProbe.IsEnabled = !Session.IsUpdating && probeDraft;
        resetProbe.IsEnabled = !Session.IsUpdating && (probeUrl.Text != DefaultProbeUrl || Session.TestUrl != DefaultProbeUrl);
    }
    private bool ConfirmServiceInstall(bool enableTun) => MessageBox.Show(Window.GetWindow(this),
        L.T(enableTun ? "首次开启 TUN 需要安装本机服务，Windows 将请求管理员权限。服务用于管理虚拟网卡，配置仍保存在本机。继续？"
            : "安装本机服务需要管理员权限。服务用于管理 TUN 虚拟网卡，安装后可在这里卸载。继续安装？"),
        L.T(enableTun ? "开启 TUN 模式" : "安装服务"), MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK;

    public override async Task NavigateToAsync(NavigationTarget target)
    {
        if (target.Target is null || !destinations.TryGetValue(target.Target, out var destination)) return;
        await Dispatcher.InvokeAsync(() =>
        {
            RestoreHighlight(); UpdateLayout();
            destination.Card.BringIntoView(); scroll.UpdateLayout();
            var point = destination.Card.TransformToAncestor(scroll).Transform(new Point());
            scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + point.Y - Math.Max(12, (scroll.ViewportHeight - destination.Card.ActualHeight) / 2)));
            if (destination.Control is { IsEnabled: true, IsVisible: true } control) control.Focus();
            else destination.Card.Focus();
            highlighted = destination.Card;
            highlighted.BorderThickness = new Thickness(2); highlighted.Padding = new Thickness(15);
            highlighted.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            focusTimer.Start();
        }, DispatcherPriority.Loaded);
    }
    private void RestoreHighlight()
    {
        focusTimer.Stop();
        if (highlighted is null) return;
        highlighted.BorderThickness = new Thickness(1); highlighted.Padding = new Thickness(16);
        highlighted.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); highlighted = null;
    }
    public override void Deactivate() { RestoreHighlight(); base.Deactivate(); }

    private async Task CheckUpdate()
    {
        if (Session.IsUpdating) throw new InvalidOperationException(L.T("正在安装应用更新，请等待更新完成。"));
        if (checking) return;
        checking = true; updateProgress = default; updateStatus.Text = L.T("正在检查更新…"); progress.Text = ""; progressBar.Visibility = Visibility.Collapsed; ApplyUpdateControls();
        try { var result = await Command("desktop_check_update"); updateProgress = default; RenderUpdate(result); }
        catch { updateStatus.Text = L.T("检查更新未完成，请重试。"); throw; }
        finally { checking = false; ApplyUpdateControls(); }
    }
    private void RenderUpdate(JsonElement result)
    {
        update = result; renderedUpdate = result.GetRawText();
        updateStatus.Text = update.Bool("available") ? L.F("发现新版本 {0}", update.Text("version")) : L.F("当前已是最新版本 {0}", update.Text("currentVersion"));
        releaseNotes.Text = update.Text("body"); releaseNotes.Visibility = releaseNotes.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        progress.Text = update.Bool("available") && !update.Bool("canInstall") ? L.T("当前运行方式不支持应用内安装，请从项目发行页面下载安装包。") : "";
        if (!installing && !Session.IsUpdating) progressBar.Visibility = Visibility.Collapsed;
        ApplyUpdateControls();
    }
    private Task OpenReleasePage()
    {
        if (Session.IsUpdating) throw new InvalidOperationException(L.T("正在安装应用更新，请等待更新完成。"));
        string url = update.Text("releaseUrl");
        return update.Bool("available") && url.Length > 0 ? Command("desktop_open_external", new { url }) : Task.CompletedTask;
    }
    private async Task InstallUpdate()
    {
        if (Session.IsUpdating || installing) throw new InvalidOperationException(L.T("正在安装应用更新，请等待更新完成。"));
        if (!update.Bool("available") || !update.Bool("canInstall")) return;
        if (Session.IsDirty) throw new InvalidOperationException(L.T("请先在配置页保存或放弃未保存的修改，再安装更新。"));
        if (MessageBox.Show(Window.GetWindow(this), L.T("安装更新会暂时停止代理并关闭应用。安装完成后会重新打开，当前配置将保留。继续安装？"), L.T("安装应用更新"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (Session.IsUpdating || installing) throw new InvalidOperationException(L.T("正在安装应用更新，请等待更新完成。"));
        installing = true; ApplyUpdateControls();
        try { await Command("desktop_install_update"); }
        finally { installing = false; ApplyUpdateControls(); }
    }
    private void ApplyUpdateControls()
    {
        bool enabled = !DesktopBusy && !checking && !installing;
        check.IsEnabled = enabled;
        install.IsEnabled = enabled && !Session.IsDirty && update.Bool("available") && update.Bool("canInstall");
        openRelease.Visibility = update.Bool("available") && !update.Bool("canInstall") && update.Text("releaseUrl").Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        openRelease.IsEnabled = enabled;
    }
    private void OnHostEvent(string name, JsonElement data)
    {
        if (name == "desktop-update-progress") { updateProgress = data; RenderProgress(data); }
    }
    private void RenderProgress(JsonElement data)
    {
        string stage = data.Text("stage");
        progress.Text = stage switch
        {
            "checking" => L.T("正在验证更新来源…"),
            "downloading" => data.Number("total") > 0 ? L.F("正在下载 {0} / {1}", Ui.Bytes(data.Number("downloaded")), Ui.Bytes(data.Number("total"))) : L.F("正在下载 {0}", Ui.Bytes(data.Number("downloaded"))),
            "installing" => L.T("正在安装，应用即将关闭…"),
            "ready" => L.T("安装包已就绪…"),
            "error" => data.Text("message", fallback: L.T("更新未完成，请重试。")),
            _ => data.Text("message")
        };
        progressBar.Visibility = stage is "checking" or "downloading" or "installing" or "ready" ? Visibility.Visible : Visibility.Collapsed;
        progressBar.IsIndeterminate = stage != "downloading" || data.Number("total") <= 0;
        if (!progressBar.IsIndeterminate) progressBar.Value = Math.Clamp(data.Number("downloaded") / data.Number("total") * 100, 0, 100);
    }
}
