using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class SettingsPage : NativePage
{
    private readonly TextBlock versions = Ui.Text("正在读取版本…", 16, true);
    private readonly TextBlock serviceStatus = Ui.Muted("");
    private readonly TextBlock updateStatus = Ui.Text("检查可用的应用更新");
    private readonly TextBlock progress = Ui.Muted("");
    private readonly TextBox releaseNotes = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, BorderThickness = new Thickness(0), MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 12) };
    private readonly ComboBox theme = new() { MinWidth = 160, Height = 34, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
    private readonly Button install;
    private readonly Button check;
    private readonly Button openRelease;
    private readonly Button installService;
    private readonly Button uninstallService;
    private JsonElement update;
    private bool applyingTheme;
    private bool checking;
    private bool installing;
    private string renderedUpdate = "";

    public SettingsPage(AppSession session) : base(session, "settings", "设置", "外观、Windows 服务与应用程序更新")
    {
        theme.ItemsSource = new[] { new { Label = "跟随系统", Value = "system" }, new { Label = "浅色", Value = "light" }, new { Label = "深色", Value = "dark" } };
        theme.Id("AppTheme"); theme.SelectionChanged += (_, _) => { if (!applyingTheme && theme.SelectedValue is string value) Session.SetTheme(value); };
        var appearance = new DockPanel(); DockPanel.SetDock(theme, Dock.Right); appearance.Children.Add(theme); appearance.Children.Add(Ui.Column(Ui.Text("应用主题", 15, true), Ui.Muted("使用 Windows 强调色，支持浅色和深色")));
        installService = Action("安装服务", "InstallService", async () => { await Command("desktop_install_service"); await RefreshAsync(); });
        uninstallService = Action("卸载服务", "UninstallService", async () =>
        {
            if (MessageBox.Show(Window.GetWindow(this), "卸载本机服务将关闭 TUN 模式。普通系统代理仍可使用。继续卸载？", "卸载服务", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            { await Command("desktop_uninstall_service"); await RefreshAsync(); }
        });
        check = Action("检查更新", "CheckAppUpdate", CheckUpdate);
        install = Action("下载并安装", "InstallAppUpdate", InstallUpdate, true); install.IsEnabled = false;
        openRelease = Action("打开发行页面", "OpenAppRelease", OpenReleasePage); openRelease.Visibility = Visibility.Collapsed;
        var application = Ui.Column(versions, Ui.Muted("Windows 原生客户端 · 本地配置与现有内核"), Ui.Row(Action("打开日志目录", "OpenLogsFolder", () => Command("desktop_open_directory", new { kind = "logs" })), Action("项目主页", "OpenProject", () => Command("desktop_open_external", new { url = "https://github.com/chummumm/mihomo-optimized" }))));
        var updateCard = Ui.Column(Ui.Text("应用程序更新", 18, true), updateStatus, Ui.Row(check, install, openRelease), progress, releaseNotes);
        updateStatus.Margin = new Thickness(0, 10, 0, 14); progress.Id("AppUpdateProgress"); updateStatus.Id("AppUpdateStatus");
        SetBody(Ui.Scroll(Ui.Column(Ui.Card(appearance), Ui.Card(Ui.Column(Ui.Text("TUN 服务", 18, true), serviceStatus, Ui.Row(installService, uninstallService))), Ui.Card(updateCard), Ui.Card(application))));
        Session.HostEvent += OnHostEvent;
        Session.StatusChanged += (_, _) => ApplyUpdateControls();
    }
    public override Task RefreshAsync()
    {
        var status = Session.Status;
        versions.Text = $"Mihomo Optimized  {status.Text("appVersion", fallback: "—")}\n内核  {status.Text("coreVersion", fallback: "尚未启动")}";
        var service = status.Get("service"); var installed = service.Bool("installed"); var running = service.Bool("running");
        serviceStatus.Text = installed ? running ? "服务已安装并运行。TUN 使用此服务管理虚拟网卡。" : "服务已安装，当前未运行。" : "服务尚未安装。首次开启 TUN 时会请求管理员权限。";
        serviceStatus.Margin = new Thickness(0, 8, 0, 16);
        installService.IsEnabled = !Session.IsUpdating && (!installed || !running); uninstallService.IsEnabled = !Session.IsUpdating && installed;
        applyingTheme = true; theme.SelectedValue = Session.Theme; applyingTheme = false;
        if (!checking && !installing && !Session.IsUpdating && Session.Update.Text("currentVersion").Length > 0 && Session.Update.GetRawText() != renderedUpdate)
            RenderUpdate(Session.Update);
        ApplyUpdateControls();
        return Task.CompletedTask;
    }
    protected override void OnStatusChanged() { _ = RefreshAsync(); }
    private async Task CheckUpdate()
    {
        if (Session.IsUpdating) throw new InvalidOperationException("正在安装应用更新，请等待更新完成。");
        if (checking) return; checking = true; updateStatus.Text = "正在检查更新…"; progress.Text = ""; ApplyUpdateControls();
        try
        {
            RenderUpdate(await Command("desktop_check_update"));
        }
        catch { updateStatus.Text = "检查更新未完成，请重试。"; throw; }
        finally { checking = false; ApplyUpdateControls(); }
    }
    private void RenderUpdate(JsonElement result)
    {
        update = result; renderedUpdate = result.GetRawText();
        updateStatus.Text = update.Bool("available") ? "发现新版本 " + update.Text("version") : "当前已是最新版本 " + update.Text("currentVersion");
        releaseNotes.Text = update.Text("body"); releaseNotes.Visibility = releaseNotes.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        progress.Text = update.Bool("available") && !update.Bool("canInstall") ? "当前运行方式不支持应用内安装，请从项目发行页面下载安装包。" : "";
    }
    private Task OpenReleasePage()
    {
        if (Session.IsUpdating) throw new InvalidOperationException("正在安装应用更新，请等待更新完成。");
        var url = update.Text("releaseUrl");
        return update.Bool("available") && url.Length > 0 ? Command("desktop_open_external", new { url }) : Task.CompletedTask;
    }
    private async Task InstallUpdate()
    {
        if (Session.IsUpdating || installing) throw new InvalidOperationException("正在安装应用更新，请等待更新完成。");
        if (!update.Bool("available") || !update.Bool("canInstall")) return;
        if (Session.IsDirty) throw new InvalidOperationException("请先在配置页保存或放弃未保存的修改，再安装更新。");
        if (MessageBox.Show(Window.GetWindow(this), "安装更新会暂时停止代理并关闭应用。安装完成后会重新打开，当前配置将保留。继续安装？", "安装应用更新", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (Session.IsUpdating || installing) throw new InvalidOperationException("正在安装应用更新，请等待更新完成。");
        installing = true; ApplyUpdateControls();
        try { await Command("desktop_install_update"); }
        finally { installing = false; ApplyUpdateControls(); }
    }
    private void ApplyUpdateControls()
    {
        check.IsEnabled = !checking && !installing && !Session.IsUpdating;
        install.IsEnabled = !checking && !installing && !Session.IsUpdating && update.Bool("available") && update.Bool("canInstall");
        openRelease.Visibility = update.Bool("available") && !update.Bool("canInstall") && update.Text("releaseUrl").Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        openRelease.IsEnabled = !checking && !installing && !Session.IsUpdating;
    }
    private void OnHostEvent(string name, JsonElement data)
    {
        if (name != "desktop-update-progress") return;
        progress.Text = data.Text("stage") switch
        {
            "checking" => "正在验证更新来源…",
            "downloading" => "正在下载 " + Ui.Bytes(data.Number("downloaded")) + (data.Number("total") > 0 ? " / " + Ui.Bytes(data.Number("total")) : ""),
            "installing" => "正在安装，应用即将关闭…",
            "ready" => "安装包已就绪…",
            "error" => data.Text("message", fallback: "更新未完成，请重试。"),
            _ => data.Text("message")
        };
    }
}
