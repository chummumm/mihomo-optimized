using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public abstract class NativePage : UserControl
{
    // The reference's content container has a 15px stable scrollbar gutter on
    // desktop. At <=640px it uses an overlay scrollbar and the full page width.
    // This template owns that space once; a theme's ScrollViewer must not also
    // subtract its own scrollbar width or add a content inset.
    private const double ScrollbarGutter = 15;
    private static readonly ControlTemplate PageScrollTemplate = (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollViewer">
          <Grid Background="{TemplateBinding Background}">
            <ScrollContentPresenter x:Name="PART_ScrollContentPresenter"
                                    Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}"
                                    ContentTemplateSelector="{TemplateBinding ContentTemplateSelector}"
                                    ContentStringFormat="{TemplateBinding ContentStringFormat}"
                                    CanContentScroll="{TemplateBinding CanContentScroll}" Margin="{TemplateBinding Padding}"/>
            <ScrollBar x:Name="PART_VerticalScrollBar" Width="12" HorizontalAlignment="Right"
                       Orientation="Vertical" Minimum="0" Maximum="{TemplateBinding ScrollableHeight}"
                       ViewportSize="{TemplateBinding ViewportHeight}" IsTabStop="False"
                       Value="{Binding VerticalOffset, Mode=OneWay, RelativeSource={RelativeSource TemplatedParent}}"
                       Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}"/>
          </Grid>
        </ControlTemplate>
        """);
    protected readonly AppSession Session;
    protected readonly WrapPanel Tools = new() { HorizontalAlignment = HorizontalAlignment.Right };
    protected readonly Grid Body = new();
    private readonly Grid layout = new();
    private readonly Border pageBox;
    private readonly FluentPageHeader header;
    private readonly ContentPresenter headerMeta = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
    private readonly RowDefinition bodyRow = new();
    private ScrollViewer? pageScroll;
    private double? headerBottomMargin;
    private double pageMaxWidth = 1280;
    private Thickness? pagePadding;
    private bool reservePageGutter = true;
    private readonly TextBlock notice = Ui.Text("");
    private CancellationTokenSource? lifetime;
    private string controllerIdentity = "";
    protected CancellationToken PageToken => lifetime?.Token ?? CancellationToken.None;
    protected bool IsActive => lifetime is { IsCancellationRequested: false };
    protected bool IsRunning => Session.Status.Text("phase") == "running";
    private bool IsNarrowWindow => (Window.GetWindow(this)?.ActualWidth ?? ActualWidth) <= 640;
    private double ReservedGutter => reservePageGutter && !IsNarrowWindow ? ScrollbarGutter : 0;
    // ActualWidth is already inside the shell's top/left border. CSS cqw and
    // @container use this viewport before the page's padding or max-width.
    protected internal double PageContainerWidth => Math.Max(0, ActualWidth - ReservedGutter);
    protected internal double PageHorizontalPadding => IsNarrowWindow ? 16 : Math.Clamp(PageContainerWidth * .04, 16, 40);
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
    // The original route animates inside its fixed scrolling viewport. Moving
    // the ScrollViewer itself changes both scrolling targets and the gutter.
    internal FrameworkElement TransitionSurface => pageBox;

    protected NativePage(AppSession session, string key, string title, string subtitle)
    {
        Session = session; Title = title; this.Id(key + "Page");
        MaxWidth = double.PositiveInfinity;
        pageBox = new Border { Child = layout, HorizontalAlignment = HorizontalAlignment.Left }.Id(key + "PageLayout");
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(bodyRow);
        var titleText = Ui.Label(title, 28, true);
        titleText.LineHeight = 36;
        titleText.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        var heading = Ui.Column(titleText, headerMeta);
        if (!string.IsNullOrEmpty(subtitle)) SetHeaderMeta(Ui.Hint(subtitle, 14));
        header = new FluentPageHeader(heading, Tools).Id(key + "PageHeader");
        layout.Children.Add(header);
        notice.Margin = new Thickness(0, 0, 0, 12); notice.Visibility = Visibility.Collapsed;
        notice.Id(key + "Notice"); Grid.SetRow(notice, 1); layout.Children.Add(notice);
        Grid.SetRow(Body, 2); layout.Children.Add(Body); Content = pageBox;
        SizeChanged += (_, _) => UpdatePageMetrics();
        Loaded += (_, _) => UpdatePageMetrics();
    }

    private void UpdatePageMetrics()
    {
        bool narrow = IsNarrowWindow;
        double horizontal = PageHorizontalPadding;
        pageBox.Width = Math.Max(1, Math.Min(pageMaxWidth, PageContainerWidth));
        pageBox.MinHeight = pageScroll is null ? 0 : Math.Max(0, ActualHeight);
        if (pageScroll is not null) pageScroll.Padding = new Thickness(0, 0, ReservedGutter, 0);
        pageBox.Padding = pagePadding ?? new Thickness(horizontal, narrow ? 16 : 28, horizontal, narrow ? 32 : 40);
        header.Margin = new Thickness(0, 0, 0, headerBottomMargin ?? (narrow ? 16 : 20));
        header.Narrow = narrow;
    }

    protected void SetHeaderMeta(UIElement? meta)
    {
        headerMeta.Content = meta;
        headerMeta.Visibility = meta is null ? Visibility.Collapsed : Visibility.Visible;
    }
    protected void SetHeaderVisible(bool visible)
    {
        header.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        notice.Visibility = visible && !string.IsNullOrEmpty(notice.Text) ? Visibility.Visible : Visibility.Collapsed;
    }
    protected void SetPagePadding(Thickness? padding) { pagePadding = padding; UpdatePageMetrics(); }
    protected void SetPageMaxWidth(double width) { pageMaxWidth = width; UpdatePageMetrics(); }
    /// <summary>Full content-layer subpages, such as the config editor, have no page gutter.</summary>
    protected void SetPageGutter(bool enabled) { reservePageGutter = enabled; UpdatePageMetrics(); }
    protected void SetHeaderBottomMargin(double margin) { headerBottomMargin = margin; UpdatePageMetrics(); }

    /// <summary>Scroll the page heading together with its cards, keeping a stable desktop viewport.</summary>
    protected ScrollViewer SetScrollableBody(UIElement content, ScrollViewer? scroller = null)
    {
        Content = null;
        if (pageScroll is not null) pageScroll.Content = null;
        Body.Children.Clear(); Body.Children.Add(content);
        bodyRow.Height = GridLength.Auto;
        pageScroll = scroller ?? new ScrollViewer();
        pageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        pageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        pageScroll.CanContentScroll = false;
        pageScroll.BorderThickness = new Thickness(0);
        pageScroll.Template = PageScrollTemplate;
        pageScroll.Background = System.Windows.Media.Brushes.Transparent;
        pageScroll.Content = pageBox; Content = pageScroll;
        UpdatePageMetrics(); return pageScroll;
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
    // Connected navigation keeps the outgoing surface visible until target
    // data is ready, then starts entrance motion and measures the live target.
    internal virtual Task PrepareNavigationAsync(NavigationTarget target) => Task.CompletedTask;
    public virtual Task NavigateToAsync(NavigationTarget target) => Task.CompletedTask;
    public abstract Task RefreshAsync();
    protected virtual Task LiveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task<bool> CanLeaveAsync() => Task.FromResult(true);
    protected void SetBody(UIElement content)
    {
        Content = null;
        if (pageScroll is not null) { pageScroll.Content = null; pageScroll = null; }
        bodyRow.Height = new GridLength(1, GridUnitType.Star);
        Body.Children.Clear(); Body.Children.Add(content); Content = pageBox;
        UpdatePageMetrics();
    }
    protected void Message(string text, bool error = false)
    {
        L.Bind(notice, TextBlock.TextProperty, text); notice.Visibility = string.IsNullOrEmpty(text) || header.Visibility != Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
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
        var button = new Button { Content = title, Height = 32, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0) }.Id(id);
        L.Bind(button, ContentControl.ContentProperty, title);
        L.Bind(button, System.Windows.Automation.AutomationProperties.NameProperty, title);
        if (primary) FluentControls.Primary(button);
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
