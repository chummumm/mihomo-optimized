using System.ComponentModel;
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
    private readonly string shellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mihomo Optimized", "native-shell.json");
    private NativePage? activePage;
    private SearchWindow? searchWindow;
    private NavigationTarget location = new("home");
    private string currentPage = "home";
    private string navigationMode = "expanded";
    private bool navigating;
    private bool selecting;
    private bool allowClose;
    private bool preparingExit;
    private bool preparedExit;
    private bool preferCompact;
    private bool overlayOpen;
    private bool shellCommandBusy;
    private double pillTop = double.NaN;
    private string lastCoreError = "";

    public MainWindow(AppSession session)
    {
        InitializeComponent();
        this.session = session;
        try { if (File.Exists(shellPath)) { using var saved = JsonDocument.Parse(File.ReadAllText(shellPath)); preferCompact = saved.RootElement.Bool("navigationCollapsed"); } }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        SearchButton.Style = FluentControls.Style("Button"); CoreCardButton.Style = FluentControls.Style("Ghost");
        backdrop = new NativeBackdrop(this, ClientArea, session);
        titleBar = new NativeTitleBar(this, MaximizeButton, ToggleMaximize);
        tray = new TrayController(this, session);
        session.StatusChanged += (_, _) => UpdateStatus();
        session.Notification += ShowNotice;
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
        StateChanged += (_, _) => { UpdateCaption(); UpdatePageActivity(); };
        Activated += (_, _) => TitleIdentity.Opacity = 1;
        Deactivated += (_, _) => TitleIdentity.Opacity = .6;
        IsVisibleChanged += (_, _) => { UpdatePageActivity(); UpdateNavigationIndicator(false); };
        SizeChanged += (_, _) => ApplyNavigationSize();
        PreviewKeyDown += OnKey;
        NavigationList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => UpdateNavigationIndicator(false)));
        Loaded += async (_, _) => { ApplyNavigationSize(); UpdateStatus(); if (activePage is null) await NavigateAsync("home"); };
    }

    public Task<bool> NavigateAsync(string key) => NavigateAsync(new NavigationTarget(key));
    public Task<bool> NavigateAsync(NavigationTarget target) => NavigateCoreAsync(target, false, true);
    private async Task<bool> NavigateCoreAsync(NavigationTarget target, bool back, bool remember)
    {
        if (navigating || target.Page is not ("home" or "proxies" or "connections" or "dns" or "rules" or "logs" or "profiles" or "settings")) return false;
        NativePage page;
        bool changed = activePage is null || target.Page != currentPage;
        navigating = true;
        try
        {
            if (changed && activePage is not null && !await activePage.CanLeaveAsync()) { SelectNavigation(currentPage); return false; }
            if (changed)
            {
                if (activePage is IPageBackNavigation previousBack) previousBack.BackStateChanged -= UpdateBackButton;
                if (remember && activePage is not null) history.Add(location with { Action = null });
                activePage?.Deactivate();
                if (!pages.TryGetValue(target.Page, out page!)) { page = PageFactory.Create(target.Page, session); pages.Add(target.Page, page); }
                currentPage = target.Page; activePage = page; PageContent.Content = page;
                if (page is IPageBackNavigation nextBack) nextBack.BackStateChanged += UpdateBackButton;
                SelectNavigation(target.Page); CloseOverlay(false); UpdatePageActivity();
                if (ConnectedNavigation.HasPending(this, target.Target)) { page.BeginAnimation(OpacityProperty, null); page.Opacity = 1; page.RenderTransform = Transform.Identity; }
                else FluentMotion.Enter(page, back);
            }
            else { page = activePage!; CloseOverlay(false); }
            location = target with { Action = null }; UpdateBackButton();
        }
        catch (Exception error) { ShowNotice(error.Message, true); return false; }
        finally { navigating = false; UpdateBackButton(); }
        try
        {
            // Target operations (focus, search or testing) do not hold the shell's
            // navigation lock. A slow latency test must not trap the current page.
            await page.NavigateToAsync(target);
            if (ReferenceEquals(page, activePage)) ConnectedNavigation.Play(this, ConnectedOverlay, page, target.Target);
            UpdateBackButton(); return true;
        }
        catch (Exception error) { ShowNotice(error.Message, true); return false; }
    }

    private IEnumerable<ListBoxItem> NavigationItems => NavigationList.Items.Cast<ListBoxItem>().Concat(SettingsNavigation.Items.Cast<ListBoxItem>());
    private void SelectNavigation(string key)
    {
        selecting = true;
        try
        {
            NavigationList.SelectedItem = NavigationList.Items.Cast<ListBoxItem>().FirstOrDefault(item => item.Tag as string == key);
            SettingsNavigation.SelectedItem = key == "settings" ? SettingsNavigation.Items[0] : null;
            foreach (var item in NavigationItems)
                if (item.Content is Panel panel && panel.Children.OfType<FluentIcon>().FirstOrDefault() is { } icon) icon.Filled = item.Tag as string == key;
        }
        finally { selecting = false; }
        Dispatcher.InvokeAsync(() => UpdateNavigationIndicator(true), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private async void NavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (session is null || navigating || selecting) return;
        if (sender is ListBox list && list.SelectedItem is ListBoxItem item && item.Tag is string key) await NavigateAsync(key);
    }

    private void UpdateStatus()
    {
        string phase = session.Status.Text("phase", fallback: "connecting");
        string label = phase switch { "running" => "运行中", "starting" => "正在启动…", "stopping" => "正在停止…", "stopped" => "已停止", "error" => "需要处理", _ => "正在连接…" };
        PhaseText.Text = L.T(label);
        PhaseText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        CoreStatusDot.SetResourceReference(Border.BackgroundProperty, phase == "running" ? "SuccessBrush" : phase == "error" ? "DangerBrush" : phase is "starting" or "stopping" ? "WarningBrush" : "TextSecondaryBrush");
        CoreAvatarBackground.SetResourceReference(Border.BackgroundProperty, phase == "running" ? "AccentBrush" : "SubtleSurfaceBrush");
        CoreGlyph.Kind = phase == "running" ? "globe-shield" : "power"; CoreGlyph.Filled = phase == "running";
        CoreGlyph.SetResourceReference(Control.ForegroundProperty, phase == "running" ? "AccentTextBrush" : "TextSecondaryBrush");
        CoreProgress.IsActive = phase is "starting" or "stopping";
        string version = ShortVersion(session.Status.Text("coreVersion")); VersionText.Text = version.Length > 0 ? L.F("内核 {0}", version) : "—";
        AutomationProperties.SetName(CoreCardButton, L.T("本地内核") + " · " + PhaseText.Text);
        CoreCardButton.ToolTip = PhaseText.Text + " · " + VersionText.Text;
        UpdateBadge.Visibility = session.Update.Bool("available") ? Visibility.Visible : Visibility.Collapsed;
        string error = session.Status.Text("lastError");
        if (error.Length > 0 && error != lastCoreError) ShowNotice(error, true);
        if (lastCoreError.Length > 0 && error.Length == 0 && NoticeText.Text == lastCoreError) NoticeBorder.Visibility = Visibility.Collapsed;
        lastCoreError = error;
        tray.Refresh();
    }
    private static string ShortVersion(string value)
    {
        if (value.StartsWith("mihomo-optimized-", StringComparison.Ordinal)) return value["mihomo-optimized-".Length..];
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.FirstOrDefault(word => char.IsDigit(word[0]) || word[0] == 'v' && word.Length > 1 && char.IsDigit(word[1])) ?? value;
    }
    private void OnLanguageChanged() { UpdateStatus(); ApplyNavigationContent(); UpdateCaption(); UpdateBackButton(); }
    private void ShowNotice(string text, bool error)
    {
        NoticeText.Text = L.T(text);
        NoticeText.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerBrush" : "TextPrimaryBrush");
        NoticeBorder.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void DismissNotice(object sender, RoutedEventArgs e) => NoticeBorder.Visibility = Visibility.Collapsed;

    public void ShowMain()
    {
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate(); Focus(); UpdatePageActivity();
    }
    public void HideToTray() { CloseOverlay(false); Hide(); activePage?.Deactivate(); }
    private void UpdatePageActivity()
    {
        if (IsVisible && WindowState != WindowState.Minimized) activePage?.Activate();
        else activePage?.Deactivate();
    }
    private void ToggleNavigation(object sender, RoutedEventArgs e)
    {
        if (ActualWidth >= 1008)
        {
            preferCompact = !preferCompact; CloseOverlay(false); ApplyNavigationSize(true); SaveShellPreference();
        }
        else if (overlayOpen) CloseOverlay(true);
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
        if (modeChanged && overlayOpen) CloseOverlay(false);
        navigationMode = next;
        double width = next == "expanded" ? 280 : next == "compact" ? 48 : 0;
        double previous = NavigationDock.ActualWidth;
        NavigationDock.BeginAnimation(WidthProperty, null); NavigationDock.Width = width;
        if (animate && FluentMotion.Enabled && previous > 0) NavigationDock.BeginAnimation(WidthProperty, new DoubleAnimation(previous, width, TimeSpan.FromMilliseconds(250)) { EasingFunction = FluentMotion.Decelerate });
        ApplyNavigationContent();
        TitleName.Visibility = ActualWidth >= 880 ? Visibility.Visible : Visibility.Collapsed;
        TitleIdentity.Visibility = ActualWidth >= 440 ? Visibility.Visible : Visibility.Collapsed;
        SearchLabel.Visibility = ActualWidth >= 700 ? Visibility.Visible : Visibility.Collapsed;
        SearchShortcut.Visibility = ActualWidth >= 1008 ? Visibility.Visible : Visibility.Collapsed;
        SearchButton.HorizontalAlignment = ActualWidth < 700 ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        SearchButton.Width = ActualWidth < 700 ? 34 : double.NaN;
        SearchButton.Padding = ActualWidth < 700 ? new Thickness(8, 3, 8, 3) : new Thickness(12, 3, 12, 3);
        MainContentBorder.Margin = next == "minimal" ? new Thickness(4, 0, 4, 4) : new Thickness(0, 0, 8, 8);
        Dispatcher.InvokeAsync(() => UpdateNavigationIndicator(false), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void ApplyNavigationContent()
    {
        bool expanded = overlayOpen || navigationMode == "expanded";
        NavigationPane.Width = expanded ? 280 : 48;
        foreach (var item in NavigationItems)
        {
            if (item.Content is not Panel row) continue;
            var label = row.Children.OfType<TextBlock>().FirstOrDefault();
            if (label is null) continue;
            label.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            item.ToolTip = expanded ? null : label.Text; AutomationProperties.SetName(item, label.Text);
            item.Padding = expanded ? new Thickness(12, 0, 12, 0) : new Thickness(11, 0, 0, 0);
        }
        CoreText.Visibility = CoreChevron.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        CoreCardButton.Padding = expanded ? new Thickness(8) : new Thickness(0);
        CoreCardButton.Margin = expanded ? new Thickness(4, 6, 4, 12) : new Thickness(4, 6, 4, 12);
        CoreAvatar.Width = CoreAvatar.Height = 40;
        CoreProgress.Width = CoreProgress.Height = 48; CoreCardButton.BorderThickness = new Thickness(0);
        UpdateBadge.Margin = expanded ? new Thickness(8, 0, 4, 0) : new Thickness(-7, -14, 0, 0);
    }
    private void OpenOverlay()
    {
        overlayOpen = true; NavigationDock.Children.Remove(NavigationPane); OverlayPaneHost.Child = NavigationPane;
        OverlayPaneHost.Visibility = NavigationScrim.Visibility = Visibility.Visible; ApplyNavigationContent();
        if (FluentMotion.Enabled)
        {
            var shift = new TranslateTransform(-280, 0); OverlayPaneHost.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)) { EasingFunction = FluentMotion.Decelerate });
            NavigationScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(167)));
        }
        Dispatcher.InvokeAsync(() => { UpdateNavigationIndicator(false); (NavigationList.SelectedItem as ListBoxItem)?.Focus(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void CloseOverlay(bool animate)
    {
        if (!overlayOpen) return;
        overlayOpen = false; OverlayPaneHost.BeginAnimation(OpacityProperty, null); NavigationScrim.BeginAnimation(OpacityProperty, null);
        // Restore the same native controls immediately. No duplicate navigation
        // tree is exposed to UI Automation while the compact pane is in use.
        OverlayPaneHost.Child = null; NavigationDock.Children.Add(NavigationPane);
        OverlayPaneHost.Visibility = NavigationScrim.Visibility = Visibility.Collapsed;
        OverlayPaneHost.RenderTransform = Transform.Identity; ApplyNavigationContent(); UpdateNavigationIndicator(false);
        if (animate) NavigationToggle.Focus();
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
        move.KeyFrames.Add(new EasingDoubleKeyFrame(Math.Min(old, top), midpoint, FluentMotion.Decelerate));
        move.KeyFrames.Add(new EasingDoubleKeyFrame(top, KeyTime.FromPercent(1), FluentMotion.Decelerate));
        var stretch = new DoubleAnimationUsingKeyFrames { Duration = duration };
        stretch.KeyFrames.Add(new DiscreteDoubleKeyFrame(16, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(Math.Abs(top - old) + 16, midpoint, FluentMotion.Decelerate));
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(16, KeyTime.FromPercent(1), FluentMotion.Decelerate));
        SelectionPill.BeginAnimation(Canvas.TopProperty, move); SelectionPill.BeginAnimation(HeightProperty, stretch);
    }

    private void OpenCoreMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = CoreCardButton, Placement = navigationMode == "expanded" || overlayOpen ? PlacementMode.Bottom : PlacementMode.Right };
        menu.Items.Add(new MenuItem { Header = PhaseText.Text + " · " + VersionText.Text, IsEnabled = false }); menu.Items.Add(new Separator());
        bool running = session.Status.Text("phase") == "running";
        bool idle = session.Ready && !session.IsUpdating && !shellCommandBusy && session.Status.Text("phase") is not ("starting" or "stopping");
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

    private void UpdateBackButton() => BackButton.IsEnabled = !navigating && (activePage is IPageBackNavigation { CanGoBack: true } || history.Count > 0);
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
    private void OpenSearch(object sender, RoutedEventArgs e) => ShowSearch();
    private void ShowSearch()
    {
        CloseOverlay(false);
        if (searchWindow is { IsVisible: true }) { searchWindow.FocusInput(); return; }
        var dialog = new SearchWindow(session) { Owner = this }; searchWindow = dialog;
        dialog.Closed += (_, _) => { if (ReferenceEquals(searchWindow, dialog)) searchWindow = null; };
        dialog.Show(); dialog.FocusInput();
    }
    private async void OnKey(object sender, KeyEventArgs e)
    {
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
                if (pages.TryGetValue("profiles", out var editor) && !await editor.CanLeaveAsync()) return false;
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
        foreach (var page in pages.Values) page.Deactivate();
        L.Changed -= OnLanguageChanged; searchWindow?.Close(); backdrop.Dispose(); titleBar.Dispose(); tray.Dispose();
        await session.DisposeAsync(); Close(); Application.Current.Shutdown();
    }

    public async Task<object?> AutomationAsync(string method, JsonElement args)
    {
        switch (method)
        {
            case "native_snapshot":
                return new { ready = session.Ready, visible = IsVisible, phase = session.Status.Text("phase"), page = currentPage, error = NoticeBorder.Visibility == Visibility.Visible ? NoticeText.Text : "", busy = preparingExit || session.Status.Text("phase") is "starting" or "stopping", navigationMode, navigationOverlay = overlayOpen, material = backdrop.Material, customTitleBar = true, canGoBack = BackButton.IsEnabled, language = session.Language, theme = session.Theme, captureKind = "wpf-visual-tree", trayVisible = tray.IsFlyoutVisible, searchVisible = searchWindow?.IsVisible == true };
            case "native_navigate":
                return new { navigated = await NavigateAsync(new NavigationTarget(args.Text("page"), EmptyNull(args.Text("target")), EmptyNull(args.Text("query")), EmptyNull(args.Text("action")))) };
            case "native_show": ShowMain(); return new { visible = IsVisible };
            case "native_hide": HideToTray(); return new { visible = IsVisible };
            case "native_resize":
                double width = args.Number("width"), height = args.Number("height");
                if (width < 640 || width > 2000 || height < 520 || height > 1400) throw new ArgumentOutOfRangeException(nameof(args), "Automation dimensions must be within 640–2000 × 520–1400.");
                ShowMain(); WindowState = WindowState.Normal; Width = width; Height = height; UpdateLayout(); ApplyNavigationSize();
                return new { width = ActualWidth, height = ActualHeight, navigationMode };
            case "native_show_tray": tray.ShowFlyout(); return new { visible = tray.IsFlyoutVisible };
            case "native_finish_exit": preparedExit = true; _ = FinishExitAsync(); return new { exiting = true };
            case "native_screenshot":
                string path = Path.GetFullPath(args.Text("path"));
                if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Screenshot must be a PNG file");
                string surfaceName = args.Text("surface", fallback: "main"); bool traySurface = surfaceName == "tray";
                FrameworkElement surface;
                if (traySurface) surface = tray.VisibleSurface ?? throw new InvalidOperationException("Open the real tray flyout before capturing it.");
                else if (surfaceName == "search") surface = searchWindow is { IsVisible: true, Content: FrameworkElement content } ? content : throw new InvalidOperationException("Open the real search flyout before capturing it.");
                // Include the Window template's AdornerLayer, which contains
                // native input hints and focus adorners outside ClientArea.
                else { ShowMain(); surface = this; }
                surface.UpdateLayout();
                var dpi = VisualTreeHelper.GetDpi(surface);
                int pixelsWide = Math.Max(1, (int)Math.Ceiling(surface.ActualWidth * dpi.DpiScaleX));
                int pixelsHigh = Math.Max(1, (int)Math.Ceiling(surface.ActualHeight * dpi.DpiScaleY));
                string captureKind = args.Text("capture") == "screen" ? "screen-pixels" : "wpf-visual-tree";
                if (captureKind == "screen-pixels")
                {
                    var origin = surface.PointToScreen(new Point());
                    using var bitmap = new System.Drawing.Bitmap(pixelsWide, pixelsHigh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                    graphics.CopyFromScreen((int)Math.Round(origin.X), (int)Math.Round(origin.Y), 0, 0, new System.Drawing.Size(pixelsWide, pixelsHigh), System.Drawing.CopyPixelOperation.SourceCopy);
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                else
                {
                    // A real visual-tree capture works in headless Windows CI.
                    // DWM's Mica/Acrylic is composed outside this tree; opt in to
                    // screen pixels on an interactive desktop to include it.
                    var bitmap = new RenderTargetBitmap(pixelsWide, pixelsHigh, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); bitmap.Render(surface);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file);
                }
                return new { path, captureKind, surface = surfaceName, material = traySurface ? tray.Material : surfaceName == "search" ? "system-flyout" : backdrop.Material };
            default: throw new ArgumentException("Unknown native automation method");
        }
    }
    private static string? EmptyNull(string value) => value.Length == 0 ? null : value;
}
