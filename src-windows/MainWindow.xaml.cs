using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mihomo.Desktop.Pages;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop;

public partial class MainWindow : Window
{
    private readonly AppSession session;
    private readonly Dictionary<string, NativePage> pages = new();
    private readonly TrayController tray;
    private NativePage? activePage;
    private string currentPage = "home";
    private bool navigating;
    private bool allowClose;
    private bool preparingExit;
    private bool preparedExit;
    private bool paneExpanded = true;
    private string lastCoreError = "";

    public MainWindow(AppSession session)
    {
        InitializeComponent();
        this.session = session;
        tray = new TrayController(this, session);
        session.StatusChanged += (_, _) => UpdateStatus();
        session.Notification += ShowNotice;
        session.NavigateRequested += key => _ = NavigateAsync(key);
        session.ExitRequested += async () => { if (await PrepareExitAsync()) await FinishExitAsync(); };
        session.HostEvent += async (name, data) =>
        {
            if (name != "desktop-exit-requested") return;
            if (HostChannel.Text(data, "reason") == "update") { preparedExit = true; await FinishExitAsync(); }
            else { ShowMain(); if (await PrepareExitAsync()) await FinishExitAsync(); }
        };
        Closing += OnClosing;
        StateChanged += (_, _) => UpdatePageActivity();
        IsVisibleChanged += (_, _) => UpdatePageActivity();
        SizeChanged += (_, _) => ApplyNavigationSize();
        PreviewKeyDown += OnKey;
        Loaded += async (_, _) => { if (activePage is null) await NavigateAsync("home"); };
        NavigationList.SelectedIndex = 0;
    }

    public async Task<bool> NavigateAsync(string key)
    {
        if (navigating) return false;
        if (key is not ("home" or "proxies" or "connections" or "dns" or "rules" or "logs" or "profiles" or "settings")) return false;
        if (activePage is not null && key == currentPage) return true;
        navigating = true;
        try
        {
            if (activePage is not null && !await activePage.CanLeaveAsync()) { SelectNavigation(currentPage); return false; }
            activePage?.Deactivate();
            if (!pages.TryGetValue(key, out var page)) { page = PageFactory.Create(key, session); pages.Add(key, page); }
            currentPage = key; activePage = page; PageContent.Content = page;
            SelectNavigation(key);
            UpdatePageActivity();
            return true;
        }
        catch (Exception error) { ShowNotice(error.Message, true); return false; }
        finally { navigating = false; }
    }

    private void SelectNavigation(string key)
    {
        foreach (ListBoxItem item in NavigationList.Items)
            if (item.Tag as string == key) { NavigationList.SelectedItem = item; break; }
    }

    private async void NavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (session is null || navigating) return;
        if (NavigationList.SelectedItem is ListBoxItem item && item.Tag is string key) await NavigateAsync(key);
    }

    private void UpdateStatus()
    {
        string phase = HostChannel.Text(session.Status, "phase", "connecting");
        PhaseText.Text = phase switch { "running" => "运行中", "starting" => "正在启动…", "stopping" => "正在停止…", "stopped" => "已停止", "error" => "需要处理", _ => "正在连接…" };
        PhaseText.SetResourceReference(TextBlock.ForegroundProperty, phase == "running" ? "SuccessBrush" : phase == "error" ? "DangerBrush" : "TextPrimaryBrush");
        VersionText.Text = HostChannel.Text(session.Status, "coreVersion", "Mihomo Optimized");
        string error = HostChannel.Text(session.Status, "lastError");
        if (error.Length > 0 && error != lastCoreError) ShowNotice(error, true);
        if (lastCoreError.Length > 0 && error.Length == 0 && NoticeText.Text == lastCoreError) NoticeBorder.Visibility = Visibility.Collapsed;
        lastCoreError = error;
        tray.Refresh();
    }

    private void ShowNotice(string text, bool error)
    {
        NoticeText.Text = text;
        NoticeText.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerBrush" : "TextPrimaryBrush");
        NoticeBorder.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void DismissNotice(object sender, RoutedEventArgs e) => NoticeBorder.Visibility = Visibility.Collapsed;

    public void ShowMain()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate(); Focus(); UpdatePageActivity();
    }
    public void HideToTray() { Hide(); activePage?.Deactivate(); }
    private void UpdatePageActivity()
    {
        if (IsVisible && WindowState != WindowState.Minimized) activePage?.Activate();
        else activePage?.Deactivate();
    }
    private void ToggleNavigation(object sender, RoutedEventArgs e) { paneExpanded = !paneExpanded; ApplyNavigationSize(); }
    private void ApplyNavigationSize()
    {
        bool full = paneExpanded && ActualWidth >= 950;
        NavigationColumn.Width = new GridLength(full ? 224 : 76);
        foreach (ListBoxItem item in NavigationList.Items)
        {
            if (item.Content is not StackPanel row) continue;
            if (row.Children.Count > 1 && row.Children[1] is TextBlock label)
            {
                label.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
                item.ToolTip = full ? null : label.Text;
                AutomationProperties.SetName(item, label.Text);
            }
        }
        PhaseText.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        VersionText.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        if (NavigationPane.Children[0] is FrameworkElement card) card.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        SearchButton.MinWidth = ActualWidth < 800 ? 180 : 240;
    }

    private void OpenSearch(object sender, RoutedEventArgs e) => ShowSearch();
    private void ShowSearch() { var dialog = new SearchWindow(session) { Owner = this }; dialog.ShowDialog(); }
    private async void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; ShowSearch(); }
        if (e.Key == Key.F5 && activePage is not null) { e.Handled = true; try { await activePage.RefreshAsync(); } catch (Exception error) { ShowNotice(error.Message, true); } }
        if (e.Key == Key.F4 && Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; HideToTray(); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        HideToTray();
    }

    public async Task<bool> PrepareExitAsync()
    {
        if (preparedExit) return true;
        if (preparingExit) return false;
        preparingExit = true;
        try
        {
            if (session.IsUpdating)
            {
                ShowMain();
                ShowNotice("应用正在下载或安装更新，完成后会自动关闭。", false);
                return false;
            }
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
                    // A long core operation must not trap a clean UI on exit.
                    // Disposing closes stdin; the host performs its bounded
                    // shutdown and its independent watchdog restores proxy.
                }
            }
            session.ExpectExit();
            preparedExit = true;
            return true;
        }
        catch (Exception error) { ShowMain(); ShowNotice(error.Message, true); return false; }
        finally { preparingExit = false; }
    }

    public async Task FinishExitAsync()
    {
        if (allowClose) return;
        allowClose = true;
        foreach (var page in pages.Values) page.Deactivate();
        tray.Dispose();
        await session.DisposeAsync();
        Close();
        Application.Current.Shutdown();
    }

    public async Task<object?> AutomationAsync(string method, JsonElement args)
    {
        switch (method)
        {
            case "native_snapshot":
                return new { ready = session.Ready, visible = IsVisible, phase = HostChannel.Text(session.Status, "phase"), page = currentPage, error = NoticeBorder.Visibility == Visibility.Visible ? NoticeText.Text : "", busy = preparingExit || HostChannel.Text(session.Status, "phase") is "starting" or "stopping" };
            case "native_navigate":
                return new { navigated = await NavigateAsync(HostChannel.Text(args, "page")) };
            case "native_show": ShowMain(); return new { visible = IsVisible };
            case "native_hide": HideToTray(); return new { visible = IsVisible };
            case "native_finish_exit":
                preparedExit = true;
                _ = FinishExitAsync();
                return new { exiting = true };
            case "native_screenshot":
                string path = Path.GetFullPath(HostChannel.Text(args, "path"));
                if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Screenshot must be a PNG file");
                ShowMain(); UpdateLayout();
                // WPF renders the client area; the system caption and resize
                // frame are not part of this visual. Match the bitmap to the
                // rendered element instead of leaving transparent frame space.
                var dpi = VisualTreeHelper.GetDpi(ClientArea);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ClientArea.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ClientArea.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(ClientArea);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(path)) png.Save(file);
                return new { path };
            default: throw new ArgumentException("Unknown native automation method");
        }
    }
}
