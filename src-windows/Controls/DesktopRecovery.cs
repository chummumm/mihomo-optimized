using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Mihomo.Desktop.Pages;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

/// <summary>The shared offline page from DesktopUnavailable; the shell decides which routes need it.</summary>
public sealed class DesktopRecovery : NativePage
{
    private const string ConnectionError = "无法连接桌面服务。请刷新状态，或完全退出后再次启动应用。";
    private readonly Grid centeredBody = new();
    private readonly Border symbol;
    private readonly FluentIcon glyph = new("plug-disconnected", 40);
    private readonly FluentProgressRing progress = new() { Width = 40, Height = 40 };
    private readonly TextBlock heading = Ui.Text("", 20, true);
    private readonly TextBlock hint = Ui.Muted("", 14);
    private readonly TextBlock errorText = Ui.Muted("", 13);
    private readonly Border errorBox;
    private readonly Button start;
    private readonly Button retry;
    private readonly FluentIcon startGlyph = new("play", 16) { Margin = new Thickness(0, 0, 8, 0) };
    private readonly FluentProgressRing startProgress = new() { Width = 16, Height = 16, IsButtonSpinner = true, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock startLabel = Ui.Label("启动内核");
    private string? localCommand;
    private string operationError = "";
    private JsonElement lastStatus;
    private bool subscribed;

    public DesktopRecovery(AppSession session) : base(session, "desktopRecovery", "", "")
    {
        this.Id("DesktopRecovery");
        SetHeaderVisible(false);
        var symbolContent = new Grid();
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        progress.HorizontalAlignment = HorizontalAlignment.Center;
        progress.VerticalAlignment = VerticalAlignment.Center;
        progress.Id("RecoveryProgress");
        progress.SetResourceReference(FluentProgressRing.ForegroundProperty, "AccentBrush");
        symbolContent.Children.Add(glyph); symbolContent.Children.Add(progress);
        symbol = new Border
        {
            Child = symbolContent, Width = 72, Height = 72, CornerRadius = new CornerRadius(36),
            HorizontalAlignment = HorizontalAlignment.Center
        }.Id("RecoverySymbol");

        heading.Id("RecoveryTitle"); heading.LineHeight = 28;
        heading.TextAlignment = TextAlignment.Center; heading.Margin = new Thickness(0, 20, 0, 8);
        heading.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        hint.Id("RecoveryHint"); hint.LineHeight = 20; hint.TextAlignment = TextAlignment.Center;
        errorText.Id("RecoveryError"); errorText.LineHeight = 20; errorText.TextAlignment = TextAlignment.Left;
        AutomationProperties.SetLiveSetting(errorText, AutomationLiveSetting.Assertive);
        errorBox = new Border
        {
            Child = errorText, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Collapsed
        };
        errorBox.SetResourceReference(Border.BackgroundProperty, "CriticalBackgroundBrush");

        start = CreateButton("play", "启动内核", "RecoveryStartCore", true);
        var startContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        startGlyph.VerticalAlignment = VerticalAlignment.Center;
        startProgress.VerticalAlignment = VerticalAlignment.Center;
        startProgress.SetResourceReference(FluentProgressRing.ForegroundProperty, "TextSecondaryBrush");
        startContent.Children.Add(startGlyph); startContent.Children.Add(startProgress); startContent.Children.Add(startLabel);
        start.Content = startContent;
        start.Click += async (_, _) => { if (start.IsEnabled) await RunCommandAsync("desktop_start"); };
        retry = CreateButton("update", "刷新状态", "RecoveryRefreshStatus");
        retry.Click += async (_, _) => await RunCommandAsync("desktop_status");
        var settings = CreateButton("nav-settings", "打开设置", "RecoverySettings");
        settings.Click += (_, _) => Session.Navigate("settings");
        var actions = new CenteredActions { Margin = new Thickness(0, 24, 0, 0) };
        actions.Children.Add(start); actions.Children.Add(retry); actions.Children.Add(settings);

        var recovery = new Border
        {
            Child = Ui.Column(symbol, heading, hint, errorBox, actions), MaxWidth = 560,
            Padding = new Thickness(24, 40, 24, 40), HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        }.Id("RecoveryContent");
        centeredBody.Children.Add(recovery);
        SetScrollableBody(centeredBody).Id("RecoveryScrollViewer");
        SizeChanged += (_, _) => UpdateCentering();
        Loaded += LoadedRecovery;
        Unloaded += UnloadedRecovery;
        Refresh();
    }

    private static Button CreateButton(string icon, string label, string id, bool primary = false)
    {
        var button = new Button
        {
            Content = FluentControls.ButtonContent(icon, label), Height = 32,
            Padding = new Thickness(12, 0, 12, 0), Style = FluentControls.Style(primary ? "Primary" : "Button")
        }.Id(id);
        L.Bind(button, AutomationProperties.NameProperty, label);
        return button;
    }

    private void LoadedRecovery(object sender, RoutedEventArgs e)
    {
        if (!subscribed)
        {
            Session.StatusChanged += SessionChanged; L.Changed += Refresh; subscribed = true;
        }
        UpdateCentering(); Refresh();
    }

    private void UnloadedRecovery(object sender, RoutedEventArgs e)
    {
        Session.StatusChanged -= SessionChanged; L.Changed -= Refresh; subscribed = false;
    }

    private void SessionChanged(object? sender, EventArgs e) => Refresh();

    private void UpdateCentering()
    {
        bool narrow = (Window.GetWindow(this)?.ActualWidth ?? ActualWidth) <= 640;
        // NativePage supplies the same stable gutter, 1280px page and 4cqw
        // horizontal padding as .page. Its remaining height centers this body.
        centeredBody.MinHeight = Math.Max(0, ActualHeight - (narrow ? 16 + 32 : 28 + 40));
    }

    public void Refresh()
    {
        // A newly published host status supersedes a previous command error,
        // including a same-phase refresh after the configuration was repaired.
        if (!lastStatus.Equals(Session.Status)) { lastStatus = Session.Status; operationError = ""; }
        string phase = Session.Status.Text("phase");
        string detail = Session.Status.Text("lastError", fallback: operationError);
        bool hasStatus = Session.Ready && phase != "connecting";
        bool connectionFailed = !hasStatus && (phase == "error" || detail.Length > 0);
        bool connecting = !hasStatus && !connectionFailed;
        bool commandBusy = !string.IsNullOrEmpty(Session.BusyCommand) || localCommand == "desktop_start";
        bool busy = commandBusy || phase is "starting" or "stopping";
        string title = "本机内核尚未运行";
        string description = "已有配置时，可从顶部启动内核；首次使用请在设置中导入完整配置。";
        if (!hasStatus)
        {
            title = connectionFailed ? "无法读取本机状态" : "正在连接桌面服务…";
            description = connectionFailed ? ConnectionError : "正在读取本机内核状态，请稍候。";
        }
        else if (phase == "starting")
        {
            title = "正在启动本机内核";
            description = "正在校验配置、准备所需资源并等待内核就绪。首次启动可能需要更长时间，请稍候。";
        }
        else if (phase == "stopping")
        {
            title = "正在停止本机内核";
            description = "正在停止代理连接并恢复系统设置，请稍候。";
        }
        else if (phase == "error")
        {
            title = "本机内核运行遇到问题";
            description = "请查看错误信息或日志，再重试启动；需要时可在设置中编辑配置。";
        }
        heading.Text = L.T(title); hint.Text = L.T(description); errorText.Text = L.T(detail);
        errorBox.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(this, heading.Text);
        AutomationProperties.SetItemStatus(this, hasStatus ? phase : connectionFailed ? "error" : "connecting");
        bool caution = phase == "error" || connectionFailed;
        symbol.SetResourceReference(Border.BackgroundProperty, caution ? "WarningBackgroundBrush" : "ControlAltBrush");
        glyph.SetResourceReference(Control.ForegroundProperty, caution ? "WarningBrush" : "TextSecondaryBrush");
        glyph.Kind = detail.Length > 0 || phase == "error" ? "warning" : "plug-disconnected";
        progress.IsActive = busy || connecting;
        glyph.Visibility = progress.IsActive ? Visibility.Collapsed : Visibility.Visible;
        start.IsEnabled = hasStatus && !busy && phase != "running" && !Session.IsUpdating;
        bool starting = localCommand == "desktop_start" || Session.BusyCommand == "desktop_start";
        startGlyph.Visibility = starting ? Visibility.Collapsed : Visibility.Visible;
        startProgress.IsActive = starting;
        if (starting) startLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        else startLabel.ClearValue(TextBlock.ForegroundProperty);
        retry.Visibility = connectionFailed ? Visibility.Visible : Visibility.Collapsed;
    }

    public override Task RefreshAsync() { Refresh(); return Task.CompletedTask; }

    private async Task RunCommandAsync(string command)
    {
        if (localCommand is not null) return;
        localCommand = command; operationError = ""; Refresh();
        try { await Session.CommandAsync(command); }
        catch (Exception error)
        {
            if (!Session.IsExiting)
            {
                operationError = error is DesktopException { Code: "desktop_unavailable" } ? ConnectionError : error.Message;
                // refreshDesktop shows its error in the recovery body; a failed
                // desktopAction additionally uses the global transient message.
                if (command != "desktop_status") Session.Notify(operationError, true);
            }
        }
        finally { localCommand = null; Refresh(); }
    }

    /// <summary>Flex-wrap with justify-content:center on every line and an exact 8px gap.</summary>
    private sealed class CenteredActions : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            double rowWidth = 0, rowHeight = 0, width = 0, height = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child.Visibility == Visibility.Collapsed) continue;
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double nextWidth = rowWidth == 0 ? child.DesiredSize.Width : rowWidth + 8 + child.DesiredSize.Width;
                if (rowWidth > 0 && nextWidth > availableSize.Width)
                {
                    width = Math.Max(width, rowWidth); height += rowHeight + 8; rowWidth = 0; rowHeight = 0;
                }
                rowWidth += (rowWidth == 0 ? 0 : 8) + child.DesiredSize.Width;
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            return new Size(Math.Max(width, rowWidth), height + rowHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var row = new List<UIElement>(); double rowWidth = 0, rowHeight = 0, y = 0;
            void ArrangeRow()
            {
                double x = (finalSize.Width - rowWidth) / 2;
                foreach (UIElement child in row)
                {
                    child.Arrange(new Rect(x, y + (rowHeight - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height));
                    x += child.DesiredSize.Width + 8;
                }
                y += rowHeight + 8; row.Clear(); rowWidth = rowHeight = 0;
            }
            foreach (UIElement child in InternalChildren)
            {
                if (child.Visibility == Visibility.Collapsed) continue;
                double nextWidth = rowWidth == 0 ? child.DesiredSize.Width : rowWidth + 8 + child.DesiredSize.Width;
                if (row.Count > 0 && nextWidth > finalSize.Width) ArrangeRow();
                rowWidth += (row.Count == 0 ? 0 : 8) + child.DesiredSize.Width;
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height); row.Add(child);
            }
            if (row.Count > 0) ArrangeRow();
            return finalSize;
        }
    }
}
