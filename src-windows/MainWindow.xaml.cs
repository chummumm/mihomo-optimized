using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Pages;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop;

public partial class MainWindow : Window
{
    private readonly AppSession session;
    private readonly Dictionary<string, NativePage> pages = new();
    private readonly List<NavigationTarget> history = new();
    private readonly TrayController tray;
    private readonly NativeBackdrop backdrop;
    private readonly NativeTitleBar titleBar;
    private readonly DesktopRecovery recovery;
    private readonly DesktopUpdateNotice updateNotice;
    private readonly FluentBackdropFilter navigationBackdrop = new()
    {
        CornerRadius = new CornerRadius(0, 8, 8, 0),
        // Minimal navigation travels from -285.6 to 0; compact navigation
        // grows from 48 to 280. Keep their full rightward sampling path ready.
        MotionOverscan = new Thickness(16, 16, 302, 16)
    };
    private readonly FluentBackdropFilter searchBackdrop = new() { CornerRadius = new CornerRadius(7) };
    private readonly Grid navigationOverlaySurface = new();
    internal TransientMessageHost Notifications { get; } = new();
    private readonly string shellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mihomo Optimized", "native-shell.json");
    private NativePage? activePage;
    private SearchWindow? searchWindow;
    private NavigationTarget location = new("home");
    private string currentPage = "home";
    private string navigationMode = "expanded";
    private bool navigating;
    private long navigationRevision;
    private bool selecting;
    private bool allowClose;
    private bool preparingExit;
    private bool preparedExit;
    private bool preferCompact;
    private bool overlayOpen;
    private bool overlayClosing;
    private bool overlayPreparing;
    private long overlayAnimation;
    private EventHandler? navigationEntranceRendering;
    private bool navigationInitialBackdropFromCache;
    private bool? navigationBackdropVerificationSucceeded;
    private bool shellCommandBusy;
    private double pillTop = double.NaN;
    private string lastCoreError = "";
    private sealed class NavigationTiming
    {
        internal long Started { get; } = Stopwatch.GetTimestamp();
        internal double? PageConstructionMs, ActivationMs, PreparationMs, TargetMs, PlayMs;
        internal double? CompletedMs;
        internal bool Connected;
        internal object Describe() => new { connected = Connected, pageConstructionMs = PageConstructionMs,
            activationMs = ActivationMs, preparationMs = PreparationMs, targetMs = TargetMs,
            playMs = PlayMs, completedMs = CompletedMs };
    }
    private NavigationTiming? navigationTiming;

    public MainWindow(AppSession session)
    {
        InitializeComponent();
        this.session = session;
        navigationBackdrop.Id("NavigationBackdrop");
        // The pane is moved out of its dock when opened. Its final backdrop is
        // the page below it, never the pane itself or the old compact rail.
        navigationBackdrop.SetSources(ContentLayout, MainContentBorder);
        navigationBackdrop.ConfigurePreparation(ClientArea, () => new Rect(0, 0, 279, Math.Max(0, ContentLayout.ActualHeight)));
        navigationOverlaySurface.Children.Add(navigationBackdrop);
        OverlayPaneHost.Child = navigationOverlaySurface;
        recovery = new DesktopRecovery(session);
        updateNotice = new DesktopUpdateNotice(session);
        UpdateNoticeSlot.Content = updateNotice;
        AttachNotifications(ClientArea);
        searchBackdrop.Id("SearchBackdrop");
        searchBackdrop.SetSources(ClientArea, TitleBar, ContentLayout, PageOverlayHost, ConnectedOverlay, Notifications);
        searchBackdrop.ConfigurePreparation(ClientArea, () => SearchWindow.PreparationViewport(this, SearchButton, ClientArea));
        SearchButton.OpenRequested += ShowSearch;
        SearchButton.MouseEnter += (_, _) => PrepareBackdropOnIntent(searchBackdrop);
        NavigationToggle.MouseEnter += (_, _) => PrepareBackdropOnIntent(navigationBackdrop);
        try { if (File.Exists(shellPath)) { using var saved = JsonDocument.Parse(File.ReadAllText(shellPath)); preferCompact = saved.RootElement.Bool("navigationCollapsed"); } }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        CoreCardButton.Style = FluentControls.Style("Ghost");
        backdrop = new NativeBackdrop(this, ClientArea, session);
        titleBar = new NativeTitleBar(this, MaximizeButton, ToggleMaximize);
        tray = new TrayController(this, session);
        session.StatusChanged += (_, _) => UpdateStatus();
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSession.RuntimeMode)) UpdateTitleSubtitle();
            if (e.PropertyName is nameof(AppSession.Update) or nameof(AppSession.UpdateDismissedVersion)) UpdateUpdateNotice();
        };
        session.Notification += (text, kind) => Notifications.Show(text, kind);
        session.NavigateRequested += key => _ = NavigateAsync(key);
        session.NavigateTargetRequested += target => _ = NavigateAsync(target);
        session.ExitRequested += async () => { if (await PrepareExitAsync()) await FinishExitAsync(); };
        session.HostEvent += async (name, data) =>
        {
            if (name != "desktop-exit-requested") return;
            if (HostChannel.Text(data, "reason") == "update") { preparedExit = true; await FinishExitAsync(); }
            else { ShowMain(); if (await PrepareExitAsync()) await FinishExitAsync(); }
        };
        L.Changed += OnLanguageChanged;
        Closing += OnClosing;
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) CloseOverlay(false); UpdateCaption(); UpdatePageActivity(); UpdateBackdropPreparation(); };
        Activated += (_, _) => UpdateCaptionActivity(true);
        Deactivated += (_, _) => UpdateCaptionActivity(false);
        IsVisibleChanged += (_, _) => { UpdatePageActivity(); UpdateNavigationIndicator(false); UpdateBackdropPreparation(); };
        SizeChanged += (_, _) => { ConnectedNavigation.Cancel(this); ApplyNavigationSize(); };
        MainContentBorder.SizeChanged += (_, _) => UpdateNoticeMetrics();
        PreviewKeyDown += OnKey;
        NavigationList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => UpdateNavigationIndicator(false)));
        Loaded += async (_, _) => { ApplyNavigationSize(); UpdateStatus(); if (activePage is null) await NavigateAsync("home"); };
    }

    private void UpdateBackdropPreparation()
    {
        bool available = IsLoaded && IsVisible && WindowState != WindowState.Minimized && !allowClose && !EditorOpen;
        searchBackdrop.SetPreparationEnabled(available && !overlayOpen && !overlayClosing);
        navigationBackdrop.SetPreparationEnabled(available && navigationMode != "expanded");
        searchBackdrop.RefreshPreparation(); navigationBackdrop.RefreshPreparation();
    }

    private async void PrepareBackdropOnIntent(FluentBackdropFilter filter)
    {
        if (allowClose || !IsVisible || WindowState == WindowState.Minimized || EditorOpen) return;
        await filter.PrepareAsync();
    }

    public Task<bool> NavigateAsync(string key) => NavigateAsync(new NavigationTarget(key));
    public Task<bool> NavigateAsync(NavigationTarget target) => NavigateCoreAsync(target, false, true);
    private async Task<bool> NavigateCoreAsync(NavigationTarget target, bool back, bool remember)
    {
        if (navigating || target.Page is not ("home" or "proxies" or "connections" or "dns" or "rules" or "logs" or "profiles" or "settings")) return false;
        NativePage page;
        bool connected = false;
        bool changed = activePage is null || target.Page != currentPage;
        if (changed && EditorOpen) return false;
        navigating = true;
        long revision = ++navigationRevision;
        var timing = new NavigationTiming(); navigationTiming = timing;
        try
        {
            if (changed && activePage is not null && !await activePage.CanLeaveAsync()) { SelectNavigation(currentPage); return false; }
            if (changed)
            {
                double routeScrollOffset = ReferenceEquals(PageContent.Content, activePage) ? activePage?.RouteScrollOffset ?? 0 : 0;
                if (activePage is IPageBackNavigation previousBack) previousBack.BackStateChanged -= UpdateBackButton;
                if (remember && activePage is not null) history.Add(location with { Action = null });
                // A portal belongs to its route, even during the short closing
                // animation; a cached page must not leave it over the next one.
                activePage?.SetOverlayHost(null);
                activePage?.Deactivate();
                var stage = Stopwatch.GetTimestamp();
                if (!pages.TryGetValue(target.Page, out page!)) { page = PageFactory.Create(target.Page, session); pages.Add(target.Page, page); }
                timing.PageConstructionMs = Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
                currentPage = target.Page; activePage = page;
                if (page is IPageBackNavigation nextBack) nextBack.BackStateChanged += UpdateBackButton;
                stage = Stopwatch.GetTimestamp();
                SelectNavigation(target.Page); CloseOverlay(true); UpdatePageActivity();
                page.SetRouteScrollOffset(routeScrollOffset);
                timing.ActivationMs = Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
                connected = !RecoveryRequired && ConnectedNavigation.HasPending(this, target.Target);
                timing.Connected = connected;
                if (connected) ConnectedNavigation.Hold(this, ConnectedOverlay, page, target.Target);
                else ConnectedNavigation.Cancel(this);
                if (!connected) FluentMotion.Enter(RecoveryRequired ? recovery : page.TransitionSurface, back);
            }
            else { page = activePage!; CloseOverlay(true); }
            location = target with { Action = null }; UpdateBackButton();
        }
        catch (Exception error) { if (revision == navigationRevision) { ConnectedNavigation.Cancel(this); ShowNotice(error.Message, true); } return false; }
        finally { navigating = false; UpdateBackButton(); UpdateProxiesPreparation(); }
        try
        {
            if (RecoveryRequired) { ConnectedNavigation.Cancel(this); return true; }
            // Target operations (focus, search or testing) do not hold the shell's
            // navigation lock. A slow latency test must not trap the current page.
            if (connected)
            {
                var preparationStarted = Stopwatch.GetTimestamp();
                await page.PrepareNavigationAsync(target);
                timing.PreparationMs = Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds;
                if (revision != navigationRevision || !ReferenceEquals(page, activePage)) return false;
                FluentMotion.Enter(page.TransitionSurface, back);
            }
            var targetStarted = Stopwatch.GetTimestamp();
            await page.NavigateToAsync(target);
            timing.TargetMs = Stopwatch.GetElapsedTime(targetStarted).TotalMilliseconds;
            if (revision != navigationRevision || !ReferenceEquals(page, activePage)) return false;
            var playStarted = Stopwatch.GetTimestamp();
            ConnectedNavigation.Play(this, ConnectedOverlay, page, target.Target, page.TransitionSurface);
            timing.PlayMs = Stopwatch.GetElapsedTime(playStarted).TotalMilliseconds;
            timing.CompletedMs = Stopwatch.GetElapsedTime(timing.Started).TotalMilliseconds;
            UpdateBackButton(); return true;
        }
        catch (Exception error) { if (revision == navigationRevision) { ConnectedNavigation.Cancel(this); ShowNotice(error.Message, true); } return false; }
    }

    private IEnumerable<ListBoxItem> NavigationItems => NavigationList.Items.Cast<ListBoxItem>().Concat(SettingsNavigation.Items.Cast<ListBoxItem>());
    private void SelectNavigation(string key)
    {
        selecting = true;
        try
        {
            NavigationList.SelectedItem = NavigationList.Items.Cast<ListBoxItem>().FirstOrDefault(item => item.Tag as string == key);
            SettingsNavigation.SelectedItem = SettingsNavigation.Items.Cast<ListBoxItem>().FirstOrDefault(item => item.Tag as string == key);
            foreach (var item in NavigationItems)
                if (item.Content is Panel panel && panel.Children.OfType<FluentIcon>().FirstOrDefault() is { } icon) icon.Filled = item.Tag as string == key;
        }
        finally { selecting = false; }
        Dispatcher.InvokeAsync(() => UpdateNavigationIndicator(true), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private async void NavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (session is null || navigating || selecting) return;
        if (EditorOpen) { SelectNavigation(currentPage); return; }
        if (sender is ListBox list && list.SelectedItem is ListBoxItem item && item.Tag is string key) await NavigateAsync(key);
    }

    private void UpdateStatus()
    {
        string phase = session.Status.Text("phase", fallback: "connecting");
        string label = phase switch { "running" => "运行中", "starting" => "正在启动…", "stopping" => "正在停止…", "stopped" => "已停止", "error" => "需要处理", _ => "正在连接…" };
        PhaseText.Text = L.T(label);
        PhaseText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        CoreStatusDot.SetResourceReference(ShellVisuals.BackgroundProperty, phase == "running" ? "SuccessBrush" : phase == "error" ? "DangerBrush" : phase is "starting" or "stopping" ? "WarningBrush" : "TextTertiaryBrush");
        CoreAvatarBackground.SetResourceReference(ShellVisuals.BackgroundProperty, phase == "running" ? "AccentBrush" : "ControlAltBrush");
        CoreGlyph.Kind = phase == "running" ? "globe-shield" : "power"; CoreGlyph.Filled = phase == "running";
        CoreGlyph.SetResourceReference(Control.ForegroundProperty, phase == "running" ? "AccentTextBrush" : "TextSecondaryBrush");
        CoreProgress.IsActive = phase is "starting" or "stopping";
        string version = ShortVersion(session.Status.Text("coreVersion")); VersionText.Text = version.Length > 0 ? L.F("内核 {0}", version) : "—";
        AutomationProperties.SetName(CoreCardButton, L.T("本地内核") + " · " + PhaseText.Text);
        CoreCardButton.ToolTip = PhaseText.Text + " · " + VersionText.Text;
        UpdateUpdateNotice();
        UpdateTitleSubtitle();
        string error = session.Status.Text("lastError");
        if (error.Length > 0 && error != lastCoreError) ShowNotice(error, true);
        lastCoreError = error;
        recovery.Refresh();
        UpdatePageActivity();
        tray.Refresh();
    }
    private static string ShortVersion(string value)
    {
        if (value.StartsWith("mihomo-optimized-", StringComparison.Ordinal)) return value["mihomo-optimized-".Length..];
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.FirstOrDefault(word => char.IsDigit(word[0]) || word[0] == 'v' && word.Length > 1 && char.IsDigit(word[1])) ?? value;
    }
    private void OnLanguageChanged() { UpdateStatus(); ApplyNavigationContent(); UpdateCaption(); UpdateBackButton(); }
    private void UpdateCaptionActivity(bool active)
    {
        TitleName.SetResourceReference(ShellVisuals.ForegroundProperty, active ? "TextPrimaryBrush" : "TextDisabledBrush");
        TitleSubtitle.SetResourceReference(ShellVisuals.ForegroundProperty, active ? "TextTertiaryBrush" : "TextDisabledBrush");
    }
    private void UpdateTitleSubtitle()
    {
        string mode = session.RuntimeMode switch { "rule" => "规则模式", "global" => "全局模式", "direct" => "直连模式", "script" => "脚本模式", _ => "" };
        TitleSubtitle.Text = mode.Length > 0 ? "· " + L.T(mode) : "";
        TitleSubtitle.Visibility = ActualWidth > 640 && session.Status.Text("phase") == "running" && mode.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowNotice(string text, bool error)
    {
        if (!string.IsNullOrEmpty(text)) Notifications.Show(L.T(text), error ? TransientMessageKind.Error : TransientMessageKind.Info);
    }
    private void UpdateUpdateNotice()
    {
        updateNotice.Refresh();
        UpdateBadge.Visibility = updateNotice.Available ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void AttachNotifications(Panel surface)
    {
        if (ReferenceEquals(Notifications.Parent, surface)) return;
        if (Notifications.Parent is Panel previous) previous.Children.Remove(Notifications);
        Grid.SetRowSpan(Notifications, Math.Max(1, (surface as Grid)?.RowDefinitions.Count ?? 1));
        Grid.SetColumnSpan(Notifications, Math.Max(1, (surface as Grid)?.ColumnDefinitions.Count ?? 1));
        Panel.SetZIndex(Notifications, 60);
        surface.Children.Add(Notifications);
    }
    internal void RestoreNotifications()
    {
        var dialog = OwnedWindows.OfType<FluentDialog>().LastOrDefault(window => window.IsVisible && !window.IsClosed);
        AttachNotifications(dialog?.NotificationSurface ?? tray?.NotificationSurface ?? ClientArea);
    }

    public void ShowMain()
    {
        if (allowClose || session.IsExiting) return;
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate(); Focus(); UpdatePageActivity();
    }
    public void HideToTray() { ConnectedNavigation.Cancel(this); CloseOverlay(false); Hide(); activePage?.Deactivate(); }
    private void UpdatePageActivity()
    {
        bool recovering = RecoveryRequired;
        bool resumed = !navigating && ReferenceEquals(PageContent.Content, recovery) && !recovering;
        var shown = recovering ? recovery : activePage;
        if (!ReferenceEquals(PageContent.Content, shown))
        {
            PageContent.Content = shown;
            // The next page can occupy identical bounds. Its content identity,
            // unlike an animated sample on the same page, invalidates readiness.
            searchBackdrop.InvalidatePreparation(); navigationBackdrop.InvalidatePreparation();
        }
        if (IsVisible && WindowState != WindowState.Minimized && !recovering) activePage?.Activate();
        else activePage?.Deactivate();
        if (resumed && activePage is { } page) _ = ResumePageTargetAsync(page, location);
        UpdateProxiesPreparation();
    }
    private bool RecoveryRequired => currentPage is not ("home" or "settings" or "profiles") && session.Status.Text("phase") != "running";
    private async Task ResumePageTargetAsync(NativePage page, NavigationTarget target)
    {
        try { if (ReferenceEquals(page, activePage)) await page.NavigateToAsync(target); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowNotice(error.Message, true); }
    }
    private void ToggleNavigation(object sender, RoutedEventArgs e)
    {
        if (ActualWidth >= 1008)
        {
            preferCompact = !preferCompact; CloseOverlay(false); ApplyNavigationSize(true); SaveShellPreference();
        }
        else if (overlayOpen) CloseOverlay(true, restoreFocus: true);
        else OpenOverlay();
    }
    private void SaveShellPreference()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(shellPath)!); var temporary = shellPath + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(new { navigationCollapsed = preferCompact })); File.Move(temporary, shellPath, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { session.Notify("导航偏好已生效，但未能保存。", true); }
    }
    private void ApplyNavigationSize(bool animate = false)
    {
        if (session is null) return;
        string next = ActualWidth <= 640 ? "minimal" : ActualWidth < 1008 || preferCompact ? "compact" : "expanded";
        bool modeChanged = next != navigationMode;
        if (modeChanged && (overlayOpen || overlayClosing)) CloseOverlay(false);
        navigationMode = next;
        double width = next == "expanded" ? 280 : next == "compact" ? 48 : 0;
        double previous = NavigationDock.ActualWidth;
        NavigationDock.BeginAnimation(WidthProperty, null); NavigationDock.Width = width;
        if (animate && FluentMotion.Enabled && previous > 0) NavigationDock.BeginAnimation(WidthProperty, new DoubleAnimation(previous, width, TimeSpan.FromMilliseconds(250)) { EasingFunction = FluentMotion.Decelerate });
        ApplyNavigationContent(animate);
        TitleName.Visibility = ActualWidth > 640 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTitleSubtitle();
        TitleIdentity.Visibility = ActualWidth > 440 ? Visibility.Visible : Visibility.Collapsed;
        SearchButton.ShowShortcut = ActualWidth > 640;
        SearchButton.Margin = new Thickness(ActualWidth > 440 ? 12 : 4, 0, ActualWidth > 440 ? 12 : 4, 0);
        MainContentBorder.CornerRadius = next == "minimal" ? new CornerRadius(0) : new CornerRadius(8, 0, 0, 0);
        MainContentBorder.BorderThickness = next == "minimal" ? new Thickness(0, 1, 0, 0) : new Thickness(1, 1, 0, 0);
        ApplySubpageState();
        UpdateNoticeMetrics();
        Dispatcher.InvokeAsync(() => UpdateNavigationIndicator(false), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void UpdateNoticeMetrics() => updateNotice.SetContainerWidth(Math.Max(0, MainContentBorder.ActualWidth - MainContentBorder.BorderThickness.Left - MainContentBorder.BorderThickness.Right), ActualWidth <= 640);
    private void ApplyNavigationContent(bool animate = false, bool? showLabels = null)
    {
        bool expanded = showLabels ?? (overlayOpen || overlayClosing || navigationMode == "expanded");
        double previous = NavigationPane.ActualWidth;
        NavigationPane.BeginAnimation(WidthProperty, null); NavigationPane.Width = expanded ? 280 : 48;
        if (animate && FluentMotion.Enabled && previous > 0)
            NavigationPane.BeginAnimation(WidthProperty, new DoubleAnimation(previous, NavigationPane.Width, TimeSpan.FromMilliseconds(250)) { EasingFunction = FluentMotion.Decelerate });
        foreach (var item in NavigationItems)
        {
            if (item.Content is not Panel row) continue;
            var label = row.Children.OfType<TextBlock>().FirstOrDefault();
            if (label is null) continue;
            FadeLabel(label, expanded, animate);
            item.ToolTip = expanded ? null : label.Text; AutomationProperties.SetName(item, label.Text);
            item.Padding = new Thickness(12, 0, 12, 0);
        }
        FadeLabel(CoreText, expanded, animate);
        CoreCardButton.Padding = expanded ? new Thickness(8) : new Thickness(0);
        CoreCardButton.Height = expanded ? 56 : 40;
        CoreAvatar.Width = CoreAvatar.Height = 40;
        CoreProgress.Width = CoreProgress.Height = 48; CoreCardButton.BorderThickness = new Thickness(0);
        UpdateBadge.Margin = expanded ? new Thickness(16, 0, 0, 0) : new Thickness(-10, -18, 0, 0);
    }
    private static void FadeLabel(FrameworkElement label, bool shown, bool animate)
    {
        double from = label.Opacity;
        label.BeginAnimation(OpacityProperty, null); label.Opacity = shown ? 1 : 0;
        label.IsHitTestVisible = shown;
        if (animate && FluentMotion.Enabled)
            label.BeginAnimation(OpacityProperty, new DoubleAnimation(from, label.Opacity, TimeSpan.FromMilliseconds(167)));
    }
    private async void OpenOverlay()
    {
        if (overlayOpen || allowClose || EditorOpen) return;
        long timingSession = NativeMotionCapture.TimingSession;
        NativeMotionCapture.Mark(timingSession, "navigation-show-enter");
        long animation = ++overlayAnimation;
        bool interrupted = overlayClosing; overlayClosing = false; overlayOpen = true;
        navigationInitialBackdropFromCache = false; navigationBackdropVerificationSucceeded = null;
        overlayPreparing = true; UpdateBackdropPreparation();
        var preparation = navigationBackdrop.PrepareAsync();
        bool initialCache = navigationBackdrop.TryPresentPreparedFrame(allowPreparedViewport: true);
        if (!initialCache) await preparation;
        bool CurrentOpening() => animation == overlayAnimation && overlayOpen && !allowClose && !EditorOpen && IsVisible && WindowState != WindowState.Minimized;
        if (!CurrentOpening()) return;
        double previousWidth = interrupted ? OverlayPaneHost.ActualWidth : 48;
        double previousX = interrupted && OverlayPaneHost.RenderTransform is TranslateTransform previous ? previous.X : -285.6;
        StopNavigationEntranceObservation();
        // Arrange the actual pane before consuming the future-viewport cache.
        // No intermediate unverified background is shown or receives input.
        OverlayPaneHost.Opacity = 0; OverlayPaneHost.IsHitTestVisible = false;
        if (NavigationDock.Children.Contains(NavigationPane)) NavigationDock.Children.Remove(NavigationPane);
        if (!navigationOverlaySurface.Children.Contains(NavigationPane)) navigationOverlaySurface.Children.Add(NavigationPane);
        navigationBackdrop.Visibility = Visibility.Visible;
        OverlayPaneHost.BeginAnimation(WidthProperty, null); OverlayPaneHost.Width = 280;
        OverlayPaneHost.Background = Brushes.Transparent;
        bool dark = TryFindResource("AppBackgroundBrush") is SolidColorBrush background && background.Color.R < 128;
        // CSS box-shadow blur 16 uses sigma 8; WPF's Gaussian radius is 3 sigma.
        OverlayPaneHost.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 8, Direction = 270, Opacity = dark ? .26 : .14 };
        OverlayPaneHost.Visibility = Visibility.Visible;
        OverlayPaneHost.RenderTransform = Transform.Identity;
        OverlayPaneHost.UpdateLayout();
        navigationBackdrop.BeginPresentation();
        navigationInitialBackdropFromCache = initialCache && navigationBackdrop.ValidateInitialPresentationFrame();
        if (!navigationInitialBackdropFromCache) navigationBackdrop.CancelInitialPresentation();
        var verification = navigationBackdrop.CompletePreparationAsync(preparation);
        if (!navigationInitialBackdropFromCache)
        {
            bool verified = await verification;
            if (!CurrentOpening()) return;
            navigationBackdropVerificationSucceeded = verified;
            NativeMotionCapture.Mark(timingSession, verified ? "navigation-backdrop-ready" : "navigation-backdrop-unavailable");
        }
        else NativeMotionCapture.Mark(timingSession, "navigation-backdrop-initial-cache");
        overlayPreparing = false;
        OverlayPaneHost.Opacity = 1; OverlayPaneHost.IsHitTestVisible = true;
        NavigationScrim.Visibility = Visibility.Visible;
        ApplyNavigationContent(navigationMode == "compact");
        NativeMotionCapture.Mark(timingSession, "navigation-animation-start");
        if (FluentMotion.Enabled && navigationMode == "compact")
            OverlayPaneHost.BeginAnimation(WidthProperty, new DoubleAnimation(previousWidth, 280, TimeSpan.FromMilliseconds(250)) { EasingFunction = FluentMotion.Decelerate });
        else if (FluentMotion.Enabled)
        {
            var shift = new TranslateTransform(previousX, 0); OverlayPaneHost.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)) { EasingFunction = FluentMotion.Decelerate });
        }
        if (navigationInitialBackdropFromCache)
            ObserveNavigationEntrance(animation, previousWidth, previousX, timingSession);
        _ = Dispatcher.InvokeAsync(() => UpdateNavigationIndicator(false), System.Windows.Threading.DispatcherPriority.Loaded);
        if (navigationInitialBackdropFromCache)
        {
            bool verified = await verification;
            if (!CurrentOpening()) return;
            navigationBackdropVerificationSucceeded = verified;
            NativeMotionCapture.Mark(timingSession, verified ? "navigation-backdrop-ready" : "navigation-backdrop-unavailable");
        }
    }
    private void ObserveNavigationEntrance(long animation, double initialWidth, double initialX, long timingSession)
    {
        StopNavigationEntranceObservation();
        navigationEntranceRendering = (_, _) =>
        {
            if (animation != overlayAnimation || !overlayOpen || allowClose || !IsVisible || WindowState == WindowState.Minimized
                || !navigationBackdrop.AwaitingFirstPresentationFrame)
            { StopNavigationEntranceObservation(); return; }
            bool advanced = !FluentMotion.Enabled || (navigationMode == "compact"
                ? Math.Abs(OverlayPaneHost.Width - initialWidth) > .01
                : OverlayPaneHost.RenderTransform is TranslateTransform shift && Math.Abs(shift.X - initialX) > .01);
            if (!advanced) return;
            navigationBackdrop.FirstPresentationFrameSubmitted();
            NativeMotionCapture.Mark(timingSession, "navigation-initial-cache-nonzero-rendering");
            StopNavigationEntranceObservation();
        };
        CompositionTarget.Rendering += navigationEntranceRendering;
    }
    private void StopNavigationEntranceObservation()
    {
        if (navigationEntranceRendering is null) return;
        CompositionTarget.Rendering -= navigationEntranceRendering; navigationEntranceRendering = null;
    }
    private void CloseOverlay(bool animate, bool restoreFocus = false)
    {
        StopNavigationEntranceObservation();
        if (!overlayOpen && !overlayClosing) return;
        navigationBackdrop.CancelInitialPresentation();
        long animation = ++overlayAnimation;
        bool wasPreparing = overlayPreparing; overlayPreparing = false;
        overlayOpen = false; overlayClosing = true; NavigationScrim.Visibility = Visibility.Collapsed;
        void Complete()
        {
            if (animation != overlayAnimation) return;
            overlayClosing = false;
            OverlayPaneHost.BeginAnimation(WidthProperty, null); OverlayPaneHost.Width = 280;
            OverlayPaneHost.Opacity = 1; OverlayPaneHost.IsHitTestVisible = true;
            navigationOverlaySurface.Children.Remove(NavigationPane);
            if (!NavigationDock.Children.Contains(NavigationPane)) NavigationDock.Children.Add(NavigationPane);
            OverlayPaneHost.Visibility = Visibility.Collapsed; OverlayPaneHost.RenderTransform = Transform.Identity;
            navigationBackdrop.EndPresentation();
            ApplyNavigationContent(); UpdateNavigationIndicator(false);
            UpdateBackdropPreparation();
        }
        if (wasPreparing || !animate || !FluentMotion.Enabled) Complete();
        else if (navigationMode == "compact")
        {
            ApplyNavigationContent(true, false);
            navigationBackdrop.Visibility = Visibility.Collapsed;
            OverlayPaneHost.Background = Brushes.Transparent; OverlayPaneHost.Effect = null;
            var collapse = new DoubleAnimation(OverlayPaneHost.ActualWidth, 48, TimeSpan.FromMilliseconds(250)) { EasingFunction = FluentMotion.Decelerate };
            collapse.Completed += (_, _) => Complete(); OverlayPaneHost.BeginAnimation(WidthProperty, collapse);
        }
        else
        {
            var shift = OverlayPaneHost.RenderTransform as TranslateTransform ?? new TranslateTransform();
            OverlayPaneHost.RenderTransform = shift;
            var slide = new DoubleAnimation(shift.X, -285.6, TimeSpan.FromMilliseconds(250)) { EasingFunction = FluentMotion.Decelerate };
            slide.Completed += (_, _) => Complete(); shift.BeginAnimation(TranslateTransform.XProperty, slide);
        }
        if (restoreFocus) NavigationToggle.Focus();
    }
    private void CloseNavigationOverlay(object sender, MouseButtonEventArgs e) { CloseOverlay(true); e.Handled = true; }
    private void UpdateNavigationIndicator(bool animate)
    {
        var item = NavigationItems.FirstOrDefault(value => value.Tag as string == currentPage);
        if (item is null || !item.IsVisible || item.ActualHeight <= 0 || navigationMode == "minimal" && !overlayOpen) { SelectionPill.Visibility = Visibility.Collapsed; return; }
        double top = item.TranslatePoint(new Point(), SelectionCanvas).Y + (item.ActualHeight - 16) / 2;
        if (top < CoreCardButton.ActualHeight || top + 16 > NavigationPane.ActualHeight) { SelectionPill.Visibility = Visibility.Collapsed; return; }
        SelectionPill.Visibility = Visibility.Visible;
        double old = double.IsNaN(pillTop) ? top : Canvas.GetTop(SelectionPill);
        if (double.IsNaN(old)) old = top;
        SelectionPill.BeginAnimation(Canvas.TopProperty, null); SelectionPill.BeginAnimation(HeightProperty, null);
        Canvas.SetTop(SelectionPill, top); SelectionPill.Height = 16; pillTop = top;
        if (!animate || !FluentMotion.Enabled || Math.Abs(top - old) < 1) return;
        var duration = TimeSpan.FromMilliseconds(460); var midpoint = KeyTime.FromPercent(.42);
        var move = new DoubleAnimationUsingKeyFrames { Duration = duration };
        move.KeyFrames.Add(new DiscreteDoubleKeyFrame(old, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        move.KeyFrames.Add(new SplineDoubleKeyFrame(Math.Min(old, top), midpoint, new KeySpline(.7, 0, 1, .5)));
        move.KeyFrames.Add(new SplineDoubleKeyFrame(top, KeyTime.FromPercent(1), new KeySpline(0, 0, 0, 1)));
        var stretch = new DoubleAnimationUsingKeyFrames { Duration = duration };
        stretch.KeyFrames.Add(new DiscreteDoubleKeyFrame(16, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        stretch.KeyFrames.Add(new SplineDoubleKeyFrame(Math.Abs(top - old) + 16, midpoint, new KeySpline(.7, 0, 1, .5)));
        stretch.KeyFrames.Add(new SplineDoubleKeyFrame(16, KeyTime.FromPercent(1), new KeySpline(0, 0, 0, 1)));
        SelectionPill.BeginAnimation(Canvas.TopProperty, move); SelectionPill.BeginAnimation(HeightProperty, stretch);
    }

    private void OpenCoreMenu(object sender, RoutedEventArgs e)
    {
        if (EditorOpen) return;
        var menu = new ContextMenu { PlacementTarget = CoreCardButton, Placement = navigationMode == "expanded" || overlayOpen ? PlacementMode.Bottom : PlacementMode.Right };
        menu.Items.Add(new MenuItem { Header = PhaseText.Text + " · " + VersionText.Text, IsEnabled = false }); menu.Items.Add(new Separator());
        bool running = session.Status.Text("phase") == "running";
        bool idle = session.Ready && !session.IsUpdating && session.BusyCommand is null && !shellCommandBusy && session.Status.Text("phase") is not ("starting" or "stopping");
        void Add(string text, string icon, bool enabled, Func<Task> operation)
        {
            var item = new MenuItem { Header = L.T(text), Icon = new FluentIcon(icon), IsEnabled = enabled };
            item.Click += async (_, _) => { try { shellCommandBusy = true; await operation(); } catch (Exception error) { ShowNotice(error.Message, true); } finally { shellCommandBusy = false; UpdateStatus(); } }; menu.Items.Add(item);
        }
        Add(running ? "停止内核" : "启动内核", running ? "stop" : "power", idle, async () => { await session.CommandAsync(running ? "desktop_stop" : "desktop_start"); });
        Add("重启内核", "arrow-sync", idle && running, async () => { await session.CommandAsync("desktop_restart"); });
        menu.Items.Add(new Separator());
        Add("打开配置", "nav-profiles", true, async () => { await NavigateAsync("profiles"); });
        Add("查看日志", "nav-logs", running, async () => { await NavigateAsync("logs"); });
        CoreCardButton.ContextMenu = menu; menu.IsOpen = true;
    }

    private bool EditorOpen => activePage is ProfilesPage { CanGoBack: true };
    private void UpdateBackButton()
    {
        BackButton.IsEnabled = !navigating && (activePage is IPageBackNavigation { CanGoBack: true } || history.Count > 0);
        ApplySubpageState();
    }
    private void ApplySubpageState()
    {
        bool editing = EditorOpen, full = activePage is ProfilesPage { HasEditorSurface: true } && ActualWidth <= 768;
        // HTML inert leaves the navigation's appearance intact. Block pointer
        // and keyboard entry while retaining those original colors and metrics.
        NavigationPane.IsHitTestVisible = SearchButton.IsHitTestVisible = UpdateNoticeSlot.IsHitTestVisible = !editing;
        KeyboardNavigation.SetTabNavigation(NavigationPane, editing ? KeyboardNavigationMode.None : KeyboardNavigationMode.Continue);
        KeyboardNavigation.SetTabNavigation(SearchButton, editing ? KeyboardNavigationMode.None : KeyboardNavigationMode.Continue);
        KeyboardNavigation.SetTabNavigation(UpdateNoticeSlot, editing ? KeyboardNavigationMode.None : KeyboardNavigationMode.Continue);
        if (editing) CloseOverlay(false);
        if (editing && searchWindow is { IsVisible: true }) searchWindow.Close();
        if (activePage is ProfilesPage profiles) profiles.SetOverlayHost(full ? PageOverlayHost : ContentPageOverlayHost);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(PageOverlayHost, full);
        MainContentBorder.CornerRadius = navigationMode == "minimal" ? new CornerRadius(0) : new CornerRadius(8, 0, 0, 0);
        MainContentBorder.BorderThickness = navigationMode == "minimal" ? new Thickness(0, 1, 0, 0) : new Thickness(1, 1, 0, 0);
        UpdateBackdropPreparation();
    }
    private async Task GoBackAsync()
    {
        if (navigating) return;
        if (activePage is IPageBackNavigation { CanGoBack: true } subpage) { await subpage.GoBackAsync(); UpdateBackButton(); return; }
        if (history.Count == 0) return;
        var target = history[^1]; if (await NavigateCoreAsync(target, true, false)) history.RemoveAt(history.Count - 1); UpdateBackButton();
    }
    private async void GoBack(object sender, RoutedEventArgs e) { try { await GoBackAsync(); } catch (Exception error) { ShowNotice(error.Message, true); } }
    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseToTray(object sender, RoutedEventArgs e) => HideToTray();
    private void UpdateCaption()
    {
        bool maximized = WindowState == WindowState.Maximized; MaximizeIcon.Kind = maximized ? "restore" : "maximize";
        L.Bind(MaximizeButton, ToolTipProperty, maximized ? "还原" : "最大化"); L.Bind(MaximizeButton, AutomationProperties.NameProperty, maximized ? "还原" : "最大化");
        ClientArea.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
    }
    private void ShowSearch()
    {
        NativeMotionCapture.Mark("search-show-enter");
        if (EditorOpen) return;
        CloseOverlay(false);
        if (searchWindow is { IsVisible: true }) { searchWindow.FocusInput(); return; }
        // Verify current lower-layer pixels before HWND creation and popup
        // layout. Loaded checks the final viewport/revision again.
        NativeMotionCapture.Mark("search-prepare-call");
        var preparation = searchBackdrop.PrepareAsync();
        NativeMotionCapture.Mark("search-prepare-return");
        NativeMotionCapture.Mark("search-new-before");
        var dialog = new SearchWindow(session, SearchButton, searchBackdrop, preparation) { Owner = this }; searchWindow = dialog;
        NativeMotionCapture.Mark("search-new-after");
        dialog.Closed += (_, _) => { if (ReferenceEquals(searchWindow, dialog)) searchWindow = null; };
        dialog.Show(); dialog.FocusInput();
    }
    private async void OnKey(object sender, KeyEventArgs e)
    {
        if (SearchButton.InputFocused && searchWindow is { IsVisible: true } palette && palette.HandleKey(e)) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; ShowSearch(); }
        else if (key == Key.T && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await NavigateAsync(new NavigationTarget("proxies", Action: "test-all")); }
        else if (key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; try { await GoBackAsync(); } catch (Exception error) { ShowNotice(error.Message, true); } }
        else if (key == Key.Escape && overlayOpen) { e.Handled = true; CloseOverlay(true); }
        else if (key == Key.F5 && activePage is not null) { e.Handled = true; try { await activePage.RefreshAsync(); } catch (Exception error) { ShowNotice(error.Message, true); } }
        else if (key == Key.F4 && Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; HideToTray(); }
    }
    private static string? DroppedYaml(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files) return null;
        string extension = Path.GetExtension(files[0]); return extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) ? files[0] : null;
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool accepted = !session.IsUpdating && DroppedYaml(e.Data) is not null;
        DropOverlay.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed; e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
    }
    private void OnDragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(this); if (!new Rect(RenderSize).Contains(point)) DropOverlay.Visibility = Visibility.Collapsed;
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed; e.Handled = true;
        if (!session.IsUpdating && DroppedYaml(e.Data) is { } path) await NavigateAsync(new NavigationTarget("profiles", Target: path, Action: "import-file"));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return; e.Cancel = true; HideToTray();
    }
    public async Task<bool> PrepareExitAsync()
    {
        if (preparedExit) return true;
        if (preparingExit) return false;
        preparingExit = true;
        try
        {
            if (session.IsUpdating) { ShowMain(); ShowNotice("应用正在下载或安装更新，完成后会自动关闭。", false); return false; }
            if (session.IsDirty)
            {
                ShowMain();
                if (pages.TryGetValue("profiles", out var editor) && editor is ProfilesPage profiles && !await profiles.CanExitAsync()) return false;
                if (session.IsDirty) return false;
            }
            if (session.Ready)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await session.SetDirtyAsync(false).WaitAsync(deadline.Token);
                    var reply = await session.CommandAsync("desktop_exit", new { force = false }, deadline.Token);
                    if (reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("exiting", out var exiting) && exiting.ValueKind == JsonValueKind.False) return false;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    // Closing stdin hands long operations to the host's bounded
                    // shutdown and independent proxy-restoration watchdog.
                }
            }
            session.ExpectExit(); preparedExit = true; return true;
        }
        catch (Exception error) { ShowMain(); ShowNotice(error.Message, true); return false; }
        finally { preparingExit = false; }
    }
    public async Task FinishExitAsync()
    {
        if (allowClose) return; allowClose = true;
        StopNavigationEntranceObservation();
        CancelProxiesPreparation();
        foreach (var page in pages.Values) page.Deactivate();
        L.Changed -= OnLanguageChanged; searchWindow?.Close(); backdrop.Dispose(); navigationBackdrop.Dispose(); searchBackdrop.Dispose(); titleBar.Dispose(); tray.Dispose();
        Notifications.Dispose();
        await session.DisposeAsync(); Close(); Application.Current.Shutdown();
    }

    // Sampled only after an opt-in motion recording ends; no WPF layout or
    // synchronous UI work is requested while the screen frames are recorded.
    internal object DescribeMotionDiagnostics() => new
    {
        connected = ConnectedNavigation.Describe(),
        navigation = navigationTiming?.Describe(),
        preparation = pages.TryGetValue("proxies", out var page) && page is ProxiesPage proxies ? proxies.DescribeNavigationPreparation() : null,
        idleProxiesPreparation = DescribeProxiesPreparation(),
        search = searchWindow?.Describe(),
        navigationInitialBackdropFromCache, navigationBackdropVerificationSucceeded,
        navigationBackdrop = navigationBackdrop.Describe(),
        backdrop = FluentBackdropFilter.DescribeCompilation()
    };

    public async Task<object?> AutomationAsync(string method, JsonElement args)
    {
        switch (method)
        {
            case "native_arm_motion": return NativeMotionCapture.Arm(this, args);
            case "native_motion_status": return NativeMotionCapture.Status();
            case "native_snapshot":
                return new { ready = session.Ready, visible = IsVisible, phase = session.Status.Text("phase"), page = currentPage, routeScrollOffset = activePage?.RouteScrollOffset ?? 0, error = session.Status.Text("lastError") is { Length: > 0 } coreError ? coreError : Notifications.IsErrorVisible ? Notifications.VisibleMessageText : "", busy = preparingExit || session.BusyCommand is not null || session.Status.Text("phase") is "starting" or "stopping", navigationMode, navigationOverlay = overlayOpen, material = backdrop.Material, customTitleBar = true, canGoBack = BackButton.IsEnabled, language = session.Language, theme = session.Theme, captureKind = "wpf-visual-tree", trayVisible = tray.IsFlyoutVisible, searchVisible = searchWindow?.IsVisible == true, search = new { input = SearchButton.Describe(), window = searchWindow?.Describe() } };
            case "native_navigate":
                return new { navigated = await NavigateAsync(new NavigationTarget(args.Text("page"), EmptyNull(args.Text("target")), EmptyNull(args.Text("query")), EmptyNull(args.Text("action")))) };
            case "native_show": ShowMain(); return new { visible = IsVisible };
            case "native_hide": HideToTray(); return new { visible = IsVisible };
            case "native_resize":
                double width = args.Number("width"), height = args.Number("height");
                if (width < 640 || width > 2000 || height < 520 || height > 1400) throw new ArgumentOutOfRangeException(nameof(args), "Automation dimensions must be within 640–2000 × 520–1400.");
                ShowMain(); WindowState = WindowState.Normal; NativeVisualCapture.ResizeForCapture(this, width, height); UpdateLayout(); ApplyNavigationSize();
                return new { width = ActualWidth, height = ActualHeight, navigationMode };
            case "native_show_tray": tray.ShowFlyout(); return new { visible = tray.IsFlyoutVisible };
            case "native_finish_exit": preparedExit = true; _ = FinishExitAsync(); return new { exiting = true };
            case "native_screenshot":
                if (args.Text("surface", fallback: "main") == "main") ShowMain();
                return NativeVisualCapture.Save(this, args, args.Text("surface") == "tray" ? tray.Material : args.Text("surface") == "search" ? "system-flyout" : backdrop.Material, session.Update);
            default: throw new ArgumentException("Unknown native automation method");
        }
    }
    private static string? EmptyNull(string value) => value.Length == 0 ? null : value;
}
