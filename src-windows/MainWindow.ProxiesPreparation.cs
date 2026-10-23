using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Pages;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop;

public partial class MainWindow
{
    private DispatcherOperation? proxiesPreparation;
    private ProxiesPage? idleProxiesPage;
    private bool proxiesPreparationComplete;
    private string proxiesPreparationStage = "waiting";
    private double? idleProxiesConstructionMs, idleProxiesLayoutMs;

    private bool CanPrepareProxies => IsLoaded && IsVisible && WindowState != WindowState.Minimized &&
        !allowClose && !preparingExit && !navigating && !EditorOpen && !session.IsUpdating &&
        session.BusyCommand is null && session.Status.Text("phase") == "running" &&
        currentPage == "home" && activePage is HomePage && !overlayOpen && !overlayClosing &&
        !overlayPreparing && searchWindow is not { IsVisible: true } &&
        PageContent.ActualWidth > 0 && PageContent.ActualHeight > 0;

    private void UpdateProxiesPreparation()
    {
        if (idleProxiesPage is not null && ReferenceEquals(activePage, idleProxiesPage))
            proxiesPreparationComplete = true;
        if (proxiesPreparationComplete || !CanPrepareProxies)
        {
            CancelProxiesPreparation();
            return;
        }
        if (proxiesPreparation is not null || idleProxiesPage is null && pages.ContainsKey("proxies")) return;
        proxiesPreparation = Dispatcher.InvokeAsync(PrepareProxiesAtIdle, DispatcherPriority.ContextIdle);
    }

    private void CancelProxiesPreparation()
    {
        proxiesPreparation?.Abort();
        proxiesPreparation = null;
    }

    private void PrepareProxiesAtIdle()
    {
        proxiesPreparation = null;
        if (!CanPrepareProxies || proxiesPreparationComplete) return;
        // Home already fetches these three documents for its exit summary.
        // Never issue a request just to prepare an inactive destination.
        if (!session.TryGetProxyRuntimeSnapshot(out _, out _, out _)) return;
        try
        {
            if (idleProxiesPage is null)
            {
                if (pages.ContainsKey("proxies")) return;
                var started = Stopwatch.GetTimestamp();
                idleProxiesPage = new ProxiesPage(session);
                pages.Add("proxies", idleProxiesPage);
                idleProxiesConstructionMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                proxiesPreparationStage = "constructed";
                // Give input and normal-priority work another turn before the
                // first template/layout pass. This is one page, once per window.
                UpdateProxiesPreparation();
                return;
            }

            if (!idleProxiesPage.TryPrepareIdleSnapshot() || PageContent.Parent is not Grid parent) return;
            var host = new ContentControl
            {
                Visibility = Visibility.Hidden, IsHitTestVisible = false, Focusable = false,
                Width = PageContent.ActualWidth, Height = PageContent.ActualHeight,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = idleProxiesPage
            };
            Grid.SetRow(host, Grid.GetRow(PageContent));
            Panel.SetZIndex(host, -1);
            var layoutStarted = Stopwatch.GetTimestamp();
            try
            {
                // The real owner supplies current DPI, inherited resources and
                // window breakpoints. Hidden still measures, but cannot paint,
                // take focus, or activate the page's HTTP/stream lifetime.
                parent.Children.Add(host);
                host.UpdateLayout();
                idleProxiesLayoutMs = Stopwatch.GetElapsedTime(layoutStarted).TotalMilliseconds;
                proxiesPreparationStage = "completed";
                proxiesPreparationComplete = true;
            }
            finally
            {
                host.Content = null;
                parent.Children.Remove(host);
                idleProxiesPage.Deactivate();
            }
        }
        catch (Exception)
        {
            // Optional preparation must not turn idle Home into an error page.
            // Normal navigation retains its existing creation/error handling.
            proxiesPreparationStage = "failed";
            proxiesPreparationComplete = true;
        }
    }

    private object DescribeProxiesPreparation() => new
    {
        stage = proxiesPreparationStage,
        constructionMs = idleProxiesConstructionMs,
        layoutMs = idleProxiesLayoutMs,
        pending = proxiesPreparation is not null
    };
}
