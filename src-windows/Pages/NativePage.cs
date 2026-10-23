using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public abstract class NativePage : UserControl
{
    protected readonly AppSession Session;
    protected readonly WrapPanel Tools = new() { HorizontalAlignment = HorizontalAlignment.Right };
    protected readonly Grid Body = new();
    private readonly TextBlock notice = Ui.Text("");
    private CancellationTokenSource? lifetime;
    private string controllerIdentity = "";
    protected CancellationToken PageToken => lifetime?.Token ?? CancellationToken.None;
    protected bool IsActive => lifetime is { IsCancellationRequested: false };
    protected bool IsRunning => Session.Status.Text("phase") == "running";
    protected string ControllerIdentity => Session.Status.Get("controller").Text("url") + "\n" + Session.Status.Get("controller").Text("secret");
    protected string InactiveMessage => L.T(Session.Status.Text("phase") switch
    {
        "connecting" => "正在连接本地服务，请稍候。",
        "starting" => "正在准备启动资源并确认内核就绪，请稍候。",
        "stopping" => "正在停止内核，请稍候。",
        "error" => Session.Status.Text("lastError", fallback: "内核需要处理，请在主页查看详细信息。"),
        _ => "内核未运行，可在主页启动。"
    });
    public string Title { get; }

    protected NativePage(AppSession session, string key, string title, string subtitle)
    {
        Session = session; Title = title; this.Id(key + "Page");
        var layout = new Grid { Margin = new Thickness(28, 20, 16, 12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { Margin = new Thickness(0, 0, 12, 20) };
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = Ui.Column(Ui.Label(title, 28, true), Ui.Hint(subtitle));
        header.Children.Add(heading);
        Grid.SetColumn(Tools, 1); header.Children.Add(Tools);
        header.SizeChanged += (_, _) =>
        {
            bool compact = header.ActualWidth < 700;
            Grid.SetRow(Tools, compact ? 1 : 0); Grid.SetColumn(Tools, compact ? 0 : 1);
            Grid.SetColumnSpan(Tools, compact ? 2 : 1); Grid.SetColumnSpan(heading, compact ? 2 : 1);
            Tools.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            Tools.MaxWidth = Math.Max(1, header.ActualWidth - (compact ? 0 : 240));
            Tools.Margin = new Thickness(0, compact ? 12 : 0, 0, 0);
        };
        layout.Children.Add(header);
        notice.Margin = new Thickness(0, 0, 12, 12); notice.Visibility = Visibility.Collapsed;
        notice.Id(key + "Notice"); Grid.SetRow(notice, 1); layout.Children.Add(notice);
        Grid.SetRow(Body, 2); layout.Children.Add(Body); Content = layout;
    }

    public virtual void Activate()
    {
        if (IsActive) return;
        lifetime = new CancellationTokenSource(); Session.StatusChanged += StatusChanged; L.Changed += OnLanguageChanged;
        var token = lifetime.Token;
        controllerIdentity = ControllerIdentity;
        _ = RunAsync(async () => { await RunAsync(RefreshAsync); if (!token.IsCancellationRequested) await LiveAsync(token); });
    }
    public virtual void Deactivate()
    {
        Session.StatusChanged -= StatusChanged; L.Changed -= OnLanguageChanged; lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
    }
    private void StatusChanged(object? sender, EventArgs e)
    {
        OnStatusChanged();
        var current = ControllerIdentity;
        if (controllerIdentity != current) { controllerIdentity = current; if (IsRunning && IsActive) _ = RunAsync(RefreshAsync); }
    }
    protected virtual void OnStatusChanged() { }
    protected virtual void OnLanguageChanged() => OnStatusChanged();
    public virtual Task NavigateToAsync(NavigationTarget target) => Task.CompletedTask;
    public abstract Task RefreshAsync();
    protected virtual Task LiveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task<bool> CanLeaveAsync() => Task.FromResult(true);
    protected void SetBody(UIElement content) { Body.Children.Clear(); Body.Children.Add(content); }
    protected void Message(string text, bool error = false)
    {
        L.Bind(notice, TextBlock.TextProperty, text); notice.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        notice.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerBrush" : "TextSecondaryBrush");
    }
    protected async Task RunAsync(Func<Task> operation, Button? button = null)
    {
        if (button is not null) button.IsEnabled = false;
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Message(error.Message, true); }
        finally { if (button is not null) button.IsEnabled = true; }
    }
    protected Button Action(string title, string id, Func<Task> operation, bool primary = false)
    {
        var button = new Button { Content = title, MinHeight = 34, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 8) }.Id(id);
        L.Bind(button, ContentControl.ContentProperty, title);
        L.Bind(button, System.Windows.Automation.AutomationProperties.NameProperty, title);
        if (primary) { button.SetResourceReference(Control.BackgroundProperty, "AccentBrush"); button.SetResourceReference(Control.ForegroundProperty, "AccentTextBrush"); }
        button.Click += async (_, _) => { await RunAsync(operation, button); OnStatusChanged(); }; return button;
    }
    protected Task<JsonElement> Get(string path, CancellationToken cancellationToken = default) => Session.ApiAsync(HttpMethod.Get, path, cancellationToken: cancellationToken == default ? PageToken : cancellationToken);
    protected Task<JsonElement> Command(string method, object? args = null) => Session.CommandAsync(method, args);
    protected async Task PollAsync(TimeSpan interval, Func<Task> operation, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(interval, cancellationToken);
            if (IsActive && IsVisible && IsRunning) await RunAsync(operation);
        }
    }
    protected async Task WatchStreamAsync(string path, Action<JsonElement> receive, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (IsRunning)
            {
                var source = ControllerIdentity;
                try
                {
                    await foreach (var item in Session.StreamAsync(path, cancellationToken))
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        if (source != ControllerIdentity || !IsRunning) break;
                        await Dispatcher.InvokeAsync(() => { if (source == ControllerIdentity && IsRunning) receive(item); });
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (OperationCanceledException) { /* Controller generation changed; reconnect to its new authenticated endpoint. */ }
                catch (Exception error) { await Dispatcher.InvokeAsync(() => { if (source == ControllerIdentity && IsRunning) Message(error.Message, true); }); }
            }
            await Task.Delay(2000, cancellationToken);
        }
    }
}

public static class PageFactory
{
    public static NativePage Create(string key, AppSession session) => key switch
    {
        "home" => new HomePage(session), "proxies" => new ProxiesPage(session), "connections" => new ConnectionsPage(session),
        "dns" => new DnsPage(session), "rules" => new RulesPage(session), "logs" => new LogsPage(session),
        "profiles" => new ProfilesPage(session), "settings" => new SettingsPage(session), _ => new HomePage(session)
    };
}
