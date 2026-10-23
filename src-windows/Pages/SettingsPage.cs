using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
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
    private sealed class SettingCard : Grid
    {
        private readonly SettingsAttention attention = new();
        internal SettingCard(UIElement content)
        {
            var surface = new Border { Child = content, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
            surface.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            surface.SetResourceReference(Border.BorderBrushProperty, "CardStrokeBrush");
            Children.Add(surface); Children.Add(attention); Focusable = true;
        }
        internal void Flash() => attention.Start();
        internal void ClearAttention() => attention.Stop();
        protected override AutomationPeer OnCreateAutomationPeer() => new CardPeer(this);
        private sealed class CardPeer(SettingCard owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
            protected override string GetClassNameCore() => "SettingCard";
        }
    }

    private const string DefaultProbeUrl = "https://www.gstatic.com/generate_204";
    private readonly ComboBox theme = Selection("AppTheme", "应用主题");
    private readonly SettingsSegments language = Segments("AppLanguage", "显示语言");
    private readonly SettingsSegments mode = Segments("SettingsProxyMode", "代理模式");
    private readonly CheckBox autostart = Toggle("SettingsAutostart", "开机启动");
    private readonly CheckBox systemProxy = Toggle("SettingsSystemProxy", "系统代理");
    private readonly CheckBox allowLan = Toggle("SettingsAllowLan", "允许局域网连接");
    private readonly CheckBox tun = Toggle("SettingsTun", "TUN 模式");
    private readonly TextBox mixedPort = PortInput("SettingsMixedPort", "混合代理端口");
    private readonly TextBox httpPort = PortInput("SettingsHttpPort", "HTTP 代理端口");
    private readonly TextBox socksPort = PortInput("SettingsSocksPort", "SOCKS5 代理端口");
    private readonly TextBox probeUrl = new() { Height = 32, MaxLength = 2048, VerticalContentAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13 };
    private readonly TextBlock versions = Ui.Muted("");
    private readonly TextBlock serviceStatus = Ui.Muted("");
    private readonly TextBlock runtimeStatus = Ui.Muted("");
    private readonly TextBlock configPath = Ui.Muted("");
    private readonly TextBlock logsPath = Ui.Muted("");
    private readonly TextBlock updateStatus = Ui.Text("");
    private readonly TextBlock installedVersion = Ui.Muted("");
    private readonly TextBlock updateDraftHint = Ui.Hint("请先保存或关闭正在编辑的配置，再安装更新。");
    private readonly TextBlock progress = Ui.Muted("");
    private readonly ProgressBar progressBar = new() { Minimum = 0, Maximum = 100, Height = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(16, 0, 16, 12) };
    private readonly TextBox releaseNotes = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = 13, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button releaseNotesToggle = new() { HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(0), Height = 20, MinHeight = 20, FontSize = 13 };
    private readonly StackPanel notesArea = new() { Margin = new Thickness(16, 0, 16, 12), Visibility = Visibility.Collapsed };
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
    private readonly SettingsScrollViewer scroll;
    private readonly List<Action<bool>> responsiveRows = new();
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
    private bool? pendingLan;
    private bool probeDraft;
    private bool checking;
    private bool installing;
    private TaskCompletionSource<bool>? pendingOperation;
    private string renderedUpdate = "";

    private bool DesktopBusy => changing || !Session.Ready || Session.IsUpdating || Session.BusyCommand is not null || Session.Status.Text("phase") is "connecting" or "starting" or "stopping";
    private bool RuntimeEditable => !DesktopBusy && IsRunning && !Session.IsDirty && runtimeIdentity == ControllerIdentity;

    public SettingsPage(AppSession session) : base(session, "settings", "设置", "")
    {
        SetPageMaxWidth(1000);
        ports = new(StringComparer.Ordinal) { ["mixed-port"] = mixedPort, ["port"] = httpPort, ["socks-port"] = socksPort };
        theme.SelectionChanged += (_, _) => { if (!applying && theme.SelectedValue is string value) Session.SetTheme(value); };
        language.SelectionChanged += (_, _) => { if (!applying && language.SelectedValue is string value) Session.SetLanguage(value); };
        mode.SelectionChanged += async (_, _) =>
        {
            if (!applying && mode.SelectedValue is string value)
                await RunAsync(() => ChangeRuntime(new() { ["mode"] = value }));
        };
        BindToggle(autostart, "desktop_set_autostart"); BindToggle(systemProxy, "desktop_set_system_proxy");
        RoutedEventHandler changeLan = async (_, _) => await ChangeLanAsync();
        allowLan.Checked += changeLan; allowLan.Unchecked += changeLan;
        RoutedEventHandler changeTun = async (_, _) =>
        {
            if (applying) return;
            bool enabled = tun.IsChecked == true;
            await RunAsync(async () =>
            {
                if (enabled && !Session.Status.Get("service").Bool("installed"))
                {
                    await TunConsentDialog.Run(Window.GetWindow(this), () => ChangeDesktop("desktop_set_tun", new { enabled = true }), error => Session.Notify(error, true), onSuccess: () => Session.NotifySuccess("TUN 模式已开启"));
                    ApplyStatus(); return;
                }
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
        probeUrl.Id("SettingsProbeUrl"); L.Bind(probeUrl, AutomationProperties.NameProperty, "测速 URL");
        probeUrl.TextChanged += (_, _) => { if (!applying) probeDraft = probeUrl.Text != Session.TestUrl; ApplyProbeControls(); };
        probeUrl.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = RunAsync(SaveProbe); } };
        saveProbe = Action("保存", "SaveProbeUrl", SaveProbe, true);
        resetProbe = Action("恢复默认", "ResetProbeUrl", ResetProbe);
        installService = Action("安装服务", "InstallService", async () =>
        {
            await TunConsentDialog.Run(Window.GetWindow(this), () => ChangeDesktop("desktop_install_service"), error => Session.Notify(error, true), false);
        });
        uninstallService = Action("卸载服务", "UninstallService", async () =>
        {
            if (Session.Status.Bool("tun")) throw new InvalidOperationException(L.T("请先关闭 TUN 模式，再卸载服务。"));
            await ChangeDesktop("desktop_uninstall_service");
        });
        check = Action("检查应用更新", "CheckAppUpdate", CheckUpdate);
        install = Action("安装并重新启动", "InstallAppUpdate", InstallUpdate, true);
        openRelease = Action("下载完整安装包", "OpenAppRelease", OpenReleasePage, true);
        openConfig = Action("打开配置文件夹", "OpenConfigFolder", () => Command("desktop_open_directory", new { kind = "config" }));
        openLogs = Action("打开日志目录", "OpenLogsFolder", () => Command("desktop_open_directory", new { kind = "logs" }));
        openProject = Action("项目主页", "OpenProject", () => Command("desktop_open_external", new { url = "https://github.com/chummumm/mihomo-optimized" }));
        updateStatus.Id("AppUpdateStatus"); progress.Id("AppUpdateProgress"); progressBar.Id("AppUpdateProgressBar"); releaseNotes.Id("AppReleaseNotes");
        runtimeStatus.Id("SettingsRuntimeStatus"); configPath.Id("SettingsConfigPath"); logsPath.Id("SettingsLogPath"); serviceStatus.Id("SettingsServiceStatus");
        AutomationProperties.SetLiveSetting(progress, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(serviceStatus, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(runtimeStatus, AutomationLiveSetting.Polite);
        foreach (var button in new[] { saveProbe, resetProbe, installService, uninstallService, check, install, openRelease, openConfig, openLogs, openProject })
        { button.MinHeight = 32; button.Margin = new Thickness(0); button.Padding = new Thickness(11, 0, 11, 0); }
        installService.Content = FluentControls.ButtonContent("shield-lock", "安装服务");
        uninstallService.Content = FluentControls.ButtonContent("trash", "卸载服务");
        check.Content = FluentControls.ButtonContent("update", "检查应用更新");
        install.Content = FluentControls.ButtonContent("arrow-down", "安装并重新启动");
        openRelease.Content = FluentControls.ButtonContent("arrow-down", "下载完整安装包");
        openConfig.Content = FluentControls.ButtonContent("arrow-square-out", "配置文件夹");
        openLogs.Content = FluentControls.ButtonContent("arrow-square-out", "日志文件夹");
        openProject.Content = "GitHub"; openProject.Style = FluentControls.Style("Ghost");
        var portFields = PortFields();
        var appearance = Section("外观与行为",
            Row("set-theme", "dark-theme", "应用主题", "选择窗口使用的颜色模式", theme),
            Row("set-language", "language", "语言", "", language),
            Row("set-autostart", "power", "开机时启动", "在通知区域启动，并恢复上次的内核运行状态", SettingToggle(autostart), focus: autostart));
        var proxy = Section("代理",
            Row("set-system-proxy", "nav-proxies", "设置为系统代理", "让遵循系统代理设置的应用经过本机内核", SettingToggle(systemProxy), focus: systemProxy),
            Row("set-mode", "arrow-swap", "代理模式", "规则模式按分流规则选择出口；全局模式全部走代理；直连模式不经过代理", mode),
            Row("set-lan", "wifi", "允许来自局域网的连接", "其他设备可以通过本机的代理端口连接", SettingToggle(allowLan), focus: allowLan),
            Row("set-ports", "router", "混合代理端口", "修改后立即应用到运行中的内核", null, portFields, mixedPort),
            Row("set-probe", "top-speed", "测速 URL", "用于手动节点测速，留空使用默认地址。", null, ProbeFields(), probeUrl));
        L.Bind((TextBlock)proxy.Children[0], TextBlock.TextProperty, "代理", "settings-section");
        runtimeStatus.Margin = new Thickness(2, 8, 2, 0); proxy.Children.Add(runtimeStatus);
        var tunSection = Section("TUN 与系统服务",
            Row("set-tun", "shield-lock", "TUN 模式", "TUN 通过系统服务运行。首次启用会请求管理员授权；服务模式需要安装版。", SettingToggle(tun), focus: tun),
            Row("set-service", "shield-check", "系统服务", "", Actions(installService, uninstallService), null, installService, serviceStatus));
        var updates = Section("版本与更新",
            UpdateCard(),
            Row("set-about", "info", "Mihomo Optimized", "", openProject, null, openProject, versions));
        destinations["set-update"] = destinations["desktop-app-update"];
        var folders = Section("文件夹",
            Row("set-config-folder", "folder-open", "配置文件夹", "", openConfig, null, openConfig, configPath),
            Row("set-log-folder", "nav-logs", "日志文件夹", "", openLogs, null, openLogs, logsPath));
        var sections = new[] { appearance, proxy, tunSection, updates, folders };
        for (int index = 0; index < sections.Length; index++) sections[index].Margin = new Thickness(0, index == 0 ? 0 : 28, 0, 0);
        var contents = Ui.Column(sections);
        contents.MaxWidth = 1000; contents.HorizontalAlignment = HorizontalAlignment.Stretch;
        scroll = new SettingsScrollViewer(); scroll.Id("SettingsScrollViewer"); SetScrollableBody(contents, scroll);
        SizeChanged += (_, _) => { foreach (var adapt in responsiveRows) adapt(PageContainerWidth <= 560); };
        focusTimer.Tick += (_, _) => RestoreHighlight();
        Session.HostEvent += OnHostEvent;
        Session.PropertyChanged += OnPreferenceChanged;
        PopulateChoices(); ApplyStatus();
    }

    private static ComboBox Selection(string id, string title)
    {
        var input = new ComboBox { MinWidth = 120, Height = 32, DisplayMemberPath = nameof(Choice.Label), SelectedValuePath = nameof(Choice.Value), VerticalAlignment = VerticalAlignment.Center }.Id(id);
        L.Bind(input, AutomationProperties.NameProperty, title); return input;
    }
    private static SettingsSegments Segments(string id, string title)
    {
        var input = new SettingsSegments { DisplayMemberPath = nameof(Choice.Label), SelectedValuePath = nameof(Choice.Value) }.Id(id);
        L.Bind(input, AutomationProperties.NameProperty, title); return input;
    }
    private static CheckBox Toggle(string id, string title)
    {
        var input = new CheckBox { Style = FluentControls.Style("Switch"), MinHeight = 20, MinWidth = 40, VerticalAlignment = VerticalAlignment.Center }.Id(id);
        L.Bind(input, AutomationProperties.NameProperty, title); return input;
    }
    private static FrameworkElement SettingToggle(CheckBox toggle)
    {
        var state = Ui.Text("", 14); state.MinWidth = 14; state.TextAlignment = TextAlignment.Right; state.Margin = new Thickness(0, 0, 12, 0);
        state.SetBinding(TextBlock.TextProperty, new Binding(nameof(CheckBox.Content)) { Source = toggle });
        var layout = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        layout.Children.Add(state); layout.Children.Add(toggle); return layout;
    }
    private static TextBox PortInput(string id, string title)
    {
        var input = new TextBox { Height = 32, MaxLength = 5, FontSize = 13, TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono, Consolas") }.Id(id);
        L.Bind(input, AutomationProperties.NameProperty, title); return input;
    }
    private Grid PortFields()
    {
        var viewport = new Grid();
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 504 };
        viewport.Children.Add(grid);
        bool narrow = false;
        void FitWidth() { if (viewport.ActualWidth > 0) grid.Width = narrow ? viewport.ActualWidth : Math.Min(504, viewport.ActualWidth); }
        viewport.SizeChanged += (_, _) => FitWidth();
        var fields = new[] { (Title: "混合代理端口", Input: mixedPort), (Title: "HTTP 代理端口", Input: httpPort), (Title: "Socks5 代理端口", Input: socksPort) };
        var labels = fields.Select(field => Ui.Hint(field.Title)).ToArray();
        foreach (var label in labels) { label.LineHeight = 16; label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; grid.Children.Add(label); }
        foreach (var field in fields) grid.Children.Add(field.Input);
        void Adapt(bool compact)
        {
            narrow = compact;
            grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
            grid.MaxWidth = compact ? double.PositiveInfinity : 504;
            if (compact)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                for (int index = 0; index < fields.Length; index++)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    Grid.SetColumn(labels[index], 0); Grid.SetRow(labels[index], index * 2); labels[index].Margin = new Thickness(0, index == 0 ? 0 : 4, 0, 4);
                    Grid.SetColumn(fields[index].Input, 0); Grid.SetRow(fields[index].Input, index * 2 + 1); fields[index].Input.Width = double.NaN;
                }
            }
            else
            {
                for (int index = 0; index < 5; index++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = index % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(12) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (int index = 0; index < fields.Length; index++)
                {
                    Grid.SetColumn(labels[index], index * 2); Grid.SetRow(labels[index], 0); labels[index].Margin = new Thickness(0, 0, 0, 4);
                    Grid.SetColumn(fields[index].Input, index * 2); Grid.SetRow(fields[index].Input, 1); fields[index].Input.Width = double.NaN;
                }
            }
            FitWidth();
        }
        responsiveRows.Add(Adapt); Adapt(false); return viewport;
    }
    private static WrapPanel Actions(params Button[] buttons)
    {
        var panel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(0);
            panel.Children.Add(button);
            button.IsVisibleChanged += (_, _) => Space();
        }
        void Space()
        {
            bool first = true;
            foreach (var button in buttons)
            {
                if (button.Visibility != Visibility.Visible) continue;
                button.Margin = new Thickness(first ? 0 : 8, 0, 0, 0); first = false;
            }
        }
        Space(); return panel;
    }
    private Grid ProbeFields()
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var buttons = Actions(saveProbe, resetProbe); grid.Children.Add(probeUrl); grid.Children.Add(buttons);
        void Arrange()
        {
            bool wrap = grid.ActualWidth > 0 && grid.ActualWidth < 280 + 8 + buttons.DesiredSize.Width;
            Grid.SetColumnSpan(probeUrl, wrap ? 2 : 1); Grid.SetColumn(buttons, wrap ? 0 : 1); Grid.SetRow(buttons, wrap ? 1 : 0); Grid.SetColumnSpan(buttons, wrap ? 2 : 1);
            buttons.HorizontalAlignment = HorizontalAlignment.Left; buttons.Margin = wrap ? new Thickness(0, 8, 0, 0) : new Thickness(8, 0, 0, 0);
        }
        grid.SizeChanged += (_, _) => Arrange(); buttons.SizeChanged += (_, _) => Arrange(); Arrange(); return grid;
    }
    private SettingCard UpdateCard()
    {
        var content = new StackPanel();
        var row = new Grid { Margin = new Thickness(16, 12, 16, 12), MinHeight = 40 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55, GridUnitType.Star) });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = Ui.Label("桌面应用"); title.LineHeight = 20; title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        installedVersion.FontFamily = new FontFamily("Cascadia Mono, Consolas"); installedVersion.Margin = new Thickness(0, 2, 0, 0);
        var name = Ui.Column(title, installedVersion); row.Children.Add(name);
        updateStatus.VerticalAlignment = VerticalAlignment.Top; updateStatus.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(updateStatus, 2); row.Children.Add(updateStatus); content.Children.Add(row);
        void Adapt(bool compact)
        {
            Grid.SetColumnSpan(name, compact ? 3 : 1);
            Grid.SetColumn(updateStatus, compact ? 0 : 2); Grid.SetRow(updateStatus, compact ? 1 : 0); Grid.SetColumnSpan(updateStatus, compact ? 3 : 1);
            updateStatus.Margin = new Thickness(0, compact ? 8 : 0, 0, 0); updateStatus.TextAlignment = compact ? TextAlignment.Left : TextAlignment.Right;
        }
        responsiveRows.Add(Adapt); Adapt(false);
        content.Children.Add(progressBar);
        var hint = Ui.Hint("应用、内核和内置界面一起升级；配置与日志会保留。"); hint.Margin = new Thickness(16, 4, 16, 12); hint.LineHeight = 16; hint.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        content.Children.Add(hint);
        progress.Margin = new Thickness(16, 0, 16, 12); progress.Visibility = Visibility.Collapsed; content.Children.Add(progress);
        releaseNotesToggle.Style = FluentControls.Style("Ghost");
        releaseNotesToggle.Id("AppReleaseNotesToggle");
        releaseNotesToggle.Click += (_, _) => { releaseNotes.Visibility = releaseNotes.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; UpdateNotesToggle(); };
        notesArea.Children.Add(releaseNotesToggle); notesArea.Children.Add(releaseNotes); content.Children.Add(notesArea);
        updateDraftHint.Margin = new Thickness(16, 0, 16, 12); updateDraftHint.Visibility = Visibility.Collapsed; content.Children.Add(updateDraftHint);
        var footer = new Border { Child = Actions(check, install, openRelease), MinHeight = 56, Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(0, 1, 0, 0), CornerRadius = new CornerRadius(0, 0, 3, 3) };
        footer.SetResourceReference(Border.BackgroundProperty, "CardSecondaryBrush"); footer.SetResourceReference(Border.BorderBrushProperty, "DividerBrush"); content.Children.Add(footer);
        var card = new SettingCard(content).Id("desktop-app-update");
        L.Bind(card, AutomationProperties.NameProperty, "桌面应用"); destinations["desktop-app-update"] = new(card, check); return card;
    }
    private void UpdateNotesToggle()
    {
        var label = Ui.Text(update.Text("version"), 13);
        var icon = new FluentIcon(releaseNotes.Visibility == Visibility.Visible ? "chevron-down" : "chevron-right", 12) { Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal }; row.Children.Add(icon); row.Children.Add(label); releaseNotesToggle.Content = row;
        AutomationProperties.SetName(releaseNotesToggle, L.F("发行说明 {0}", update.Text("version")));
    }
    private static StackPanel Section(string title, params UIElement[] children)
    {
        var section = new StackPanel();
        var heading = Ui.Label(title, 14, true); heading.Margin = new Thickness(1, 0, 0, 8); heading.LineHeight = 20; heading.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        section.Children.Add(heading);
        for (int index = 0; index < children.Length; index++) { if (children[index] is FrameworkElement item) item.Margin = new Thickness(0, 0, 0, index == children.Length - 1 ? 0 : 4); section.Children.Add(children[index]); }
        return section;
    }
    private SettingCard Row(string id, string glyph, string title, string description, FrameworkElement? control,
        FrameworkElement? footer = null, Control? focus = null, TextBlock? descriptionBlock = null)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var icon = new FluentIcon(glyph, 20) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center }; grid.Children.Add(icon);
        var label = Ui.Label(title, 14); label.LineHeight = 20; label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        var hint = descriptionBlock ?? Ui.Hint(description); hint.Margin = new Thickness(0); hint.LineHeight = 16; hint.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        hint.Visibility = descriptionBlock is null && description.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var text = Ui.Column(label, hint); Grid.SetColumn(text, 1); grid.Children.Add(text);
        if (control is not null)
        {
            control.Margin = new Thickness(16, 0, 16, 0); control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 2); grid.Children.Add(control);
        }
        else Grid.SetColumnSpan(text, 2);
        var rows = new StackPanel();
        rows.Children.Add(new Border { Child = grid, Padding = new Thickness(16, 12, 16, 12), MinHeight = 68 });
        Border? bottom = null;
        if (footer is not null)
        {
            bottom = new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(52, 12, 16, 16), CornerRadius = new CornerRadius(0, 0, 3, 3) };
            bottom.SetResourceReference(Border.BorderBrushProperty, "DividerBrush"); bottom.SetResourceReference(Border.BackgroundProperty, "CardSecondaryBrush"); rows.Children.Add(bottom);
        }
        void Adapt(bool compact)
        {
            Grid.SetColumnSpan(text, compact || control is null ? 2 : 1);
            if (control is not null)
            {
                Grid.SetRow(control, compact ? 1 : 0); Grid.SetColumn(control, compact ? 1 : 2);
                Grid.SetColumnSpan(control, compact ? 2 : 1); control.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                // The reference keeps its fourth (empty chevron) grid track,
                // including the 16px gap after the control, at desktop widths.
                control.Margin = compact ? new Thickness(0, 4, 0, 0) : new Thickness(16, 0, 16, 0);
            }
            if (bottom is not null) bottom.Padding = new Thickness(compact ? 16 : 52, 12, 16, 16);
        }
        responsiveRows.Add(Adapt); Adapt(false);
        var card = new SettingCard(rows).Id(id);
        L.Bind(card, AutomationProperties.NameProperty, title);
        destinations[id] = new(card, focus ?? control as Control);
        return card;
    }

    private void PopulateChoices()
    {
        applying = true;
        theme.ItemsSource = new[] { new Choice(L.T("跟随系统"), "system"), new Choice(L.T("浅色"), "light"), new Choice(L.T("深色"), "dark") };
        // These labels do not change with the selected language. Keep the same
        // containers so the selection indicator can finish its 167ms slide.
        language.ItemsSource ??= new[] { new Choice("中文", "zh-CN"), new Choice("English", "en-US") };
        mode.ItemsSource = new[] { new Choice(L.T("规则"), "rule"), new Choice(L.ShortMode("global"), "global"), new Choice(L.ShortMode("direct"), "direct") };
        theme.SelectedValue = Session.Theme; language.SelectedValue = L.ResolvedLanguage;
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
        UpdateNotesToggle();
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
        installedVersion.Text = update.Text("currentVersion", fallback: status.Text("appVersion", fallback: "—"));
        serviceStatus.Text = L.T(installed ? running ? "正在运行" : "已安装" : "未安装");
        configPath.Text = status.Text("configPath", fallback: L.T("主配置、备份和资源缓存"));
        logsPath.Text = L.T("内核与桌面程序的日志，按天轮转");
        applying = true;
        theme.SelectedValue = Session.Theme; language.SelectedValue = L.ResolvedLanguage;
        SetToggle(autostart, status.Bool("autostart")); SetToggle(systemProxy, status.Bool("systemProxy")); SetToggle(tun, status.Bool("tun"));
        SetToggle(allowLan, pendingLan ?? runtime.Bool("allow-lan"));
        if (!probeDraft) probeUrl.Text = Session.TestUrl;
        applying = false;
        theme.IsEnabled = language.IsEnabled = !Session.IsUpdating;
        autostart.IsEnabled = !DesktopBusy && capability.Bool("autostart");
        systemProxy.IsEnabled = !DesktopBusy && capability.Bool("systemProxy");
        tun.IsEnabled = !DesktopBusy && !Session.IsDirty && capability.Bool("tun");
        mode.IsEnabled = RuntimeEditable;
        // The reference LAN switch stays visually enabled during its own
        // request. Further input is guarded until the existing write/readback
        // finishes; the other controls still use the normal busy gate.
        allowLan.IsEnabled = pendingLan.HasValue || RuntimeEditable;
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
        if (renderedUpdate.Length == 0 && !checking) updateStatus.Text = "";
        ApplyProbeControls(); ApplyUpdateControls();
    }
    private static void SetToggle(CheckBox toggle, bool enabled)
    {
        toggle.IsChecked = enabled; toggle.Content = L.T(enabled ? "开" : "关");
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
        mode.SelectedValue = runtime.Text("mode"); SetToggle(allowLan, pendingLan ?? runtime.Bool("allow-lan"));
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
    private async Task ChangeLanAsync()
    {
        if (applying) return;
        if (pendingLan is bool requested)
        {
            // UI Automation can toggle an enabled control again while an
            // asynchronous write is pending. Restore its requested state
            // without submitting or overlapping another mutation.
            applying = true;
            try { SetToggle(allowLan, requested); }
            finally { applying = false; }
            return;
        }
        bool enabled = allowLan.IsChecked == true;
        pendingLan = enabled;
        allowLan.Content = L.T(enabled ? "开" : "关");
        try { await RunAsync(() => ChangeRuntime(new() { ["allow-lan"] = enabled })); }
        finally
        {
            // Only the display is optimistic. ChangeRuntime still persists
            // the configuration and reads the controller back before returning;
            // failure restores the last actual value instead of claiming success.
            pendingLan = null;
            ApplyRuntime(); ApplyStatus();
        }
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
    public override async Task NavigateToAsync(NavigationTarget target)
    {
        if (target.Target is null || !destinations.TryGetValue(target.Target, out var destination)) return;
        await Dispatcher.InvokeAsync(() =>
        {
            RestoreHighlight(); UpdateLayout();
            scroll.Center(destination.Card);
            highlighted = destination.Card;
            highlighted.Flash();
            focusTimer.Start();
        }, DispatcherPriority.Loaded);
    }
    private void RestoreHighlight()
    {
        focusTimer.Stop();
        if (highlighted is null) return;
        highlighted.ClearAttention(); highlighted = null;
    }
    public override void Deactivate() { RestoreHighlight(); scroll.CancelAnimation(); base.Deactivate(); }

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
        updateStatus.Text = update.Bool("available") ? L.F("发现新版本 · {0}", update.Text("version")) : L.T("已是最新版本");
        releaseNotes.Text = update.Text("body"); notesArea.Visibility = releaseNotes.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (notesArea.Visibility == Visibility.Collapsed) releaseNotes.Visibility = Visibility.Collapsed;
        UpdateNotesToggle();
        progress.Text = update.Bool("available") && !update.Bool("canInstall") ? L.T("便携版请下载完整安装包，或退出后解压新版便携包。配置会保留。") : "";
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
        if (Session.IsUpdating || installing) throw new InvalidOperationException(L.T("正在安装应用更新，请等待更新完成。"));
        installing = true; ApplyUpdateControls();
        try { await Command("desktop_install_update"); }
        finally { installing = false; ApplyUpdateControls(); }
    }
    private void ApplyUpdateControls()
    {
        bool enabled = !DesktopBusy && !checking && !installing;
        check.IsEnabled = enabled;
        install.Visibility = update.Bool("available") && update.Bool("canInstall") ? Visibility.Visible : Visibility.Collapsed;
        install.IsEnabled = enabled && !Session.IsDirty && update.Bool("available") && update.Bool("canInstall");
        openRelease.Visibility = update.Bool("available") && !update.Bool("canInstall") && update.Text("releaseUrl").Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        openRelease.IsEnabled = enabled;
        progress.Visibility = progress.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        updateDraftHint.Visibility = Session.IsDirty ? Visibility.Visible : Visibility.Collapsed;
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
        progress.Visibility = progress.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        progressBar.Visibility = stage is "checking" or "downloading" or "installing" or "ready" ? Visibility.Visible : Visibility.Collapsed;
        progressBar.IsIndeterminate = stage != "downloading" || data.Number("total") <= 0;
        if (!progressBar.IsIndeterminate) progressBar.Value = Math.Clamp(data.Number("downloaded") / data.Number("total") * 100, 0, 100);
    }
}
