using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop;

internal sealed class SearchFlyout : ContentControl
{
    private static readonly long templatesStarted = Stopwatch.GetTimestamp();
    private static readonly ResourceDictionary templates = (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <ControlTemplate x:Key="Flyout" TargetType="ContentControl">
            <ContentPresenter Content="{TemplateBinding Content}" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
          </ControlTemplate>
          <Style x:Key="Results" TargetType="ListBox">
            <Setter Property="BorderThickness" Value="0"/><Setter Property="Padding" Value="0"/>
            <Setter Property="Background" Value="Transparent"/><Setter Property="Focusable" Value="False"/>
            <Setter Property="ItemsPanel"><Setter.Value><ItemsPanelTemplate><StackPanel/></ItemsPanelTemplate></Setter.Value></Setter>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBox"><ItemsPresenter/></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="Result" TargetType="ListBoxItem">
            <Setter Property="MinHeight" Value="40"/><Setter Property="Padding" Value="11,4"/><Setter Property="Margin" Value="0"/>
            <Setter Property="Focusable" Value="False"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
              <Grid>
                <Border x:Name="Frame" CornerRadius="4" Background="Transparent" Padding="{TemplateBinding Padding}">
                  <ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Stretch"/>
                </Border>
                <Border x:Name="Marker" Width="3" Height="16" CornerRadius="2" HorizontalAlignment="Left" VerticalAlignment="Center" Background="{DynamicResource AccentBrush}" Visibility="Collapsed"/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsSelected" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource SubtleHoverBrush}"/><Setter TargetName="Marker" Property="Visibility" Value="Visible"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
        </ResourceDictionary>
        """);
    private static readonly double templatesMilliseconds = Stopwatch.GetElapsedTime(templatesStarted).TotalMilliseconds;
    private sealed class ResultItem : ListBoxItem
    {
        internal required Func<Task> Execute { get; set; }
        internal required Func<bool> CanExecute { get; set; }
    }
    private sealed class ResultList : ListBox
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new ResultsPeer(this);
        private sealed class ResultsPeer(ResultList owner) : ListBoxAutomationPeer(owner)
        {
            protected override ItemAutomationPeer CreateItemAutomationPeer(object item) => item is ResultItem result ? new ResultPeer(result, this) : base.CreateItemAutomationPeer(item);
        }
        private sealed class ResultPeer(ResultItem owner, SelectorAutomationPeer selector) : ListBoxItemAutomationPeer(owner, selector), IInvokeProvider
        {
            public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);
            public void Invoke()
            {
                if (!owner.IsEnabled || !owner.CanExecute()) throw new ElementNotEnabledException();
                _ = owner.Dispatcher.BeginInvoke(new Action(async () => await owner.Execute()));
            }
        }
    }

    private sealed record Entry(string Key, string Category, string Icon, string Title, string Detail,
        string Keywords, Func<Task> Run, Func<bool>? CanRun = null)
    {
        internal bool IsEnabled => CanRun?.Invoke() ?? true;
        internal bool Matches(string query) => Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Detail.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Keywords.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
    private sealed record ResultAppearance(string Key, string Category, string Icon, string Title, string Detail, bool Enabled, bool IsEnglish);

    private const int MaximumVisibleResults = 8;
    // CSS's 16 px box-shadow blur is sigma 8 (WPF radius 24). Reserve its
    // complete support, including the 8 px drop and the 12 px entrance offset.
    private const double ShadowInset = 32;
    private readonly long timingSession = NativeMotionCapture.TimingSession;
    private readonly Stopwatch openClock = StartOpenClock();
    private static Stopwatch StartOpenClock()
    {
        var clock = Stopwatch.StartNew();
        NativeMotionCapture.Mark("search-instance-fields");
        return clock;
    }
    private double constructorStartedMilliseconds, constructorCompletedMilliseconds;
    private double? loadedMilliseconds,
        attachOwnerCompletedMilliseconds, suggestionsStartedMilliseconds, coreRequestsStartedMilliseconds,
        coreResponsesReadyMilliseconds, coreEntriesCompletedMilliseconds, backdropReadyMilliseconds, firstRenderingMilliseconds,
        entranceFirstTickMilliseconds, entranceClockCompletedMilliseconds, entranceFirstRenderingMilliseconds;
    private double? entranceFirstRenderedOpacity;
    private double? initialBackdropMilliseconds, backdropVerificationCompletedMilliseconds;
    private bool initialBackdropFromCache;
    private bool entranceClockHasAdvanced;
    private AnimationClock? entranceClock;
    private bool? backdropVerificationSucceeded;
    private readonly Window owner;
    private readonly Canvas overlayHost;
    private readonly AppSession session;
    private readonly GlobalSearchBox inputBox;
    private readonly int instance;
    private readonly TextBox search;
    private readonly ResultList results = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
    private readonly TextBlock resultCount = Ui.Hint("试试这些");
    private readonly TextBlock state = Ui.Muted("");
    private readonly TextBlock empty = Ui.Muted("", 13);
    private readonly StackPanel suggestions;
    private readonly ScrollViewer suggestionScroll;
    private readonly Border suggestionBorder;
    private readonly List<Entry> entries = new();
    private readonly List<Entry> coreEntries = new();
    private ResultAppearance[] renderedResults = [];
    private string renderedQuery = "";
    private readonly CancellationTokenSource lifetime = new();
    private readonly FluentBackdropFilter backdropFilter;
    private readonly Task<FluentBackdropFilter.PreparationTicket?> preparedPixels;
    private readonly object backdropBeforeAttachment;
    private readonly Grid suggestionContent;
    private bool shown;
    private bool ownerAttached;
    private bool positioning;
    private bool positionQueued;
    private Rect positionedBounds = Rect.Empty;
    private int positionCount;
    private double positionMilliseconds;
    private string coreIdentity = "";
    private string runtimeMode = "";
    private string commandState = "";
    private string? loadError;
    private bool loading;
    private bool closing;
    private bool closed;
    private bool choosing;

    private bool RuntimeReady => session.Ready && !session.IsUpdating && session.Status.Text("phase") == "running";
    private bool DesktopReady => session.Ready && !session.IsUpdating && session.Status.Text("phase") is not ("connecting" or "starting" or "stopping");
    private string ControllerIdentity => session.Status.Get("controller").Text("url") + "\n" + session.Status.Get("controller").Text("secret");
    private string CommandState => string.Join("|", session.Ready, session.IsUpdating, session.IsDirty, session.Status.Text("phase"), session.Status.Bool("systemProxy"), session.Status.Bool("tun"),
        session.Status.Get("capabilities").ToString());

    public SearchFlyout(Window owner, Canvas overlayHost, AppSession session, GlobalSearchBox inputBox,
        FluentBackdropFilter backdrop, Task<FluentBackdropFilter.PreparationTicket?> preparedPixels)
    {
        NativeMotionCapture.Mark(timingSession, "search-constructor-enter");
        constructorStartedMilliseconds = openClock.Elapsed.TotalMilliseconds;
        this.owner = owner; this.overlayHost = overlayHost;
        this.session = session; this.inputBox = inputBox; search = inputBox.Input;
        backdropFilter = backdrop; this.preparedPixels = preparedPixels;
        instance = inputBox.PaletteCreated();
        L.Bind(this, AutomationProperties.NameProperty, "搜索"); this.Id("GlobalSearchWindow");
        Width = 380; Height = 330; MinWidth = 0; MinHeight = 0;
        Focusable = false; HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI Variable, Segoe UI, Microsoft YaHei UI, Microsoft YaHei"); FontSize = 14;
        Background = null; Template = (ControlTemplate)templates["Flyout"];
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        results.Id("SearchResults"); resultCount.Id("SearchResultCount");
        state.Id("SearchStatus"); empty.Id("SearchEmpty");
        AutomationProperties.SetLiveSetting(resultCount, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(state, AutomationLiveSetting.Polite);
        results.Style = (Style)templates["Results"];
        suggestions = new StackPanel();
        resultCount.Margin = new Thickness(11, 8, 11, 4); resultCount.FontWeight = FontWeights.SemiBold; resultCount.LineHeight = 16; resultCount.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        suggestions.Children.Add(resultCount); suggestions.Children.Add(results);
        empty.Margin = new Thickness(12); empty.HorizontalAlignment = HorizontalAlignment.Left; suggestions.Children.Add(empty);
        // Status remains available to assistive technology; the original palette has no persistent footer.
        state.Visibility = Visibility.Collapsed; suggestions.Children.Add(state);
        suggestionScroll = new ScrollViewer { Content = suggestions, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(4), CanContentScroll = false };
        backdropBeforeAttachment = backdropFilter.DescribePreparedFrame();
        suggestionContent = new Grid(); suggestionContent.Children.Add(backdropFilter); suggestionContent.Children.Add(suggestionScroll);
        suggestionBorder = new Border { Child = suggestionContent, Background = Brushes.Transparent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), MaxHeight = 380, Margin = new Thickness(ShadowInset) };
        if (FluentMotion.Enabled) { suggestionBorder.Opacity = 0; suggestionBorder.RenderTransform = new TranslateTransform(0, -12); }
        suggestionBorder.SetResourceReference(Border.BorderBrushProperty, "AcrylicStrokeBrush");
        suggestionBorder.Effect = new DropShadowEffect { Direction = 270, ShadowDepth = 8, BlurRadius = 24, Color = Colors.Black, Opacity = IsDark ? .26 : .14 };
        // Only the result panel receives pointer input. Its shadow reservation
        // overlaps the title bar and must let clicks reach the real input below.
        var flyoutSurface = new Grid { Background = null }; flyoutSurface.Children.Add(suggestionBorder);
        Content = flyoutSurface;
        ScrollViewer.SetHorizontalScrollBarVisibility(results, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(results, ScrollBarVisibility.Auto);
        RebuildEntries();
        constructorCompletedMilliseconds = openClock.Elapsed.TotalMilliseconds;
        NativeMotionCapture.Mark(timingSession, "search-constructor-completed");
    }

    public event EventHandler? Closed;

    public void Show()
    {
        Dispatcher.VerifyAccess();
        if (closed) throw new InvalidOperationException("A closed search flyout cannot be shown again.");
        if (shown) { FocusInput(); return; }
        shown = true;
        try
        {
            Loaded += OnLoaded; Unloaded += OnUnloaded;
            search.TextChanged += QueryChanged;
            session.StatusChanged += OnStatusChanged; session.ThemeChanged += OnThemeChanged; L.Changed += OnLanguageChanged;
            AttachOwner();
            PositionFlyout();
            overlayHost.Children.Add(this);
        }
        catch { Close(); throw; }
    }

    public void Close()
    {
        Dispatcher.VerifyAccess();
        if (closed) return;
        closing = true; closed = true;
        Loaded -= OnLoaded; Unloaded -= OnUnloaded;
        CompositionTarget.Rendering -= FirstRendering;
        CompositionTarget.Rendering -= EntranceRendering;
        suggestionBorder.ApplyAnimationClock(OpacityProperty, null);
        if (suggestionBorder.RenderTransform is TranslateTransform movement) movement.BeginAnimation(TranslateTransform.YProperty, null);
        entranceClock = null;
        search.TextChanged -= QueryChanged;
        AutomationProperties.SetHelpText(search, "");
        session.StatusChanged -= OnStatusChanged; session.ThemeChanged -= OnThemeChanged; L.Changed -= OnLanguageChanged;
        if (ownerAttached)
        {
            owner.LocationChanged -= CloseForOwnerChange; owner.SizeChanged -= CloseForOwnerSize;
            owner.StateChanged -= CloseForOwnerChange; owner.IsVisibleChanged -= CloseForOwnerVisibility;
            owner.PreviewMouseDown -= OwnerPointerDown; owner.Closed -= CloseForOwnerClosed;
            ownerAttached = false;
        }
        lifetime.Cancel(); lifetime.Dispose();
        // The owner retains the prepared filter between openings. End this
        // presentation before detaching it, and never dispose the shared cache.
        backdropFilter.EndPresentation();
        suggestionContent.Children.Remove(backdropFilter);
        overlayHost.Children.Remove(this);
        inputBox.PaletteClosed(instance);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (closed || loadedMilliseconds is not null) return;
        NativeMotionCapture.Mark(timingSession, "search-loaded");
        loadedMilliseconds = openClock.Elapsed.TotalMilliseconds;
        PositionFlyout(); FocusInput();
        backdropFilter.BeginPresentation();
        CompositionTarget.Rendering += FirstRendering;
        var coreLoad = LoadInitialEntries();
        initialBackdropFromCache = backdropFilter.TryPresentPreparedFrame();
        if (initialBackdropFromCache)
        {
            initialBackdropMilliseconds = openClock.Elapsed.TotalMilliseconds;
            NativeMotionCapture.Mark(timingSession, "search-backdrop-initial-cache");
            EnterSuggestions();
        }
        NativeMotionCapture.Mark(timingSession, "search-await-backdrop");
        bool verified = await backdropFilter.CompletePreparationAsync(preparedPixels);
        if (closing || closed) return;
        backdropVerificationSucceeded = verified;
        backdropVerificationCompletedMilliseconds = openClock.Elapsed.TotalMilliseconds;
        if (verified)
        {
            backdropReadyMilliseconds = backdropVerificationCompletedMilliseconds;
            NativeMotionCapture.Mark(timingSession, "search-backdrop-ready");
        }
        else NativeMotionCapture.Mark(timingSession, "search-backdrop-unavailable");
        if (!initialBackdropFromCache)
        {
            // A failed capture keeps the existing usable solid fallback and
            // explicit diagnostic; it is never reported as a ready blur.
            initialBackdropMilliseconds = openClock.Elapsed.TotalMilliseconds;
            EnterSuggestions();
        }
        await coreLoad;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => Close();

    protected override AutomationPeer OnCreateAutomationPeer() => new SearchFlyoutPeer(this);
    private sealed class SearchFlyoutPeer(SearchFlyout owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetClassNameCore() => nameof(SearchFlyout);
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }

    private bool IsDark => TryFindResource("AppBackgroundBrush") is SolidColorBrush brush && brush.Color.R < 128;
    private void OnThemeChanged()
    {
        if (suggestionBorder.Effect is DropShadowEffect shadow) shadow.Opacity = IsDark ? .26 : .14;
    }
    private void EnterSuggestions()
    {
        if (closing || closed || suggestionsStartedMilliseconds is not null) return;
        NativeMotionCapture.Mark(timingSession, "search-animation-start");
        suggestionsStartedMilliseconds = openClock.Elapsed.TotalMilliseconds;
        suggestionBorder.Opacity = 1;
        if (!FluentMotion.Enabled)
        {
            suggestionBorder.RenderTransform = Transform.Identity;
            if (initialBackdropFromCache) CompositionTarget.Rendering += EntranceRendering;
            return;
        }
        var movement = new TranslateTransform(); suggestionBorder.RenderTransform = movement;
        SettingsSegments.Animate(movement, TranslateTransform.YProperty, -12, 0, 250);
        // The same CSS keyframes as SettingsSegments.Animate. These callbacks
        // only identify WPF clock/render submission; screen pixels remain the
        // independent evidence of when the result panel was actually presented.
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(250), FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromPercent(1), new KeySpline(0, 0, 0, 1)));
        entranceClock = fade.CreateClock();
        entranceClock.CurrentTimeInvalidated += (sender, _) =>
        {
            if (!entranceClockHasAdvanced && sender is Clock { CurrentState: ClockState.Active, CurrentProgress: > 0 })
            {
                entranceClockHasAdvanced = true;
                NativeMotionCapture.Mark(timingSession, "search-animation-first-progress");
            }
            if (entranceFirstTickMilliseconds is not null) return;
            entranceFirstTickMilliseconds = openClock.Elapsed.TotalMilliseconds;
            NativeMotionCapture.Mark(timingSession, "search-animation-first-tick");
        };
        entranceClock.Completed += (_, _) =>
        {
            entranceClockCompletedMilliseconds ??= openClock.Elapsed.TotalMilliseconds;
            NativeMotionCapture.Mark(timingSession, "search-animation-completed");
        };
        CompositionTarget.Rendering += EntranceRendering;
        suggestionBorder.ApplyAnimationClock(OpacityProperty, entranceClock);
    }
    private void FirstRendering(object? sender, EventArgs args)
    {
        firstRenderingMilliseconds ??= openClock.Elapsed.TotalMilliseconds;
        // The opt-in recorder keeps at most 32 submission samples, all within
        // 600 ms of the original routed input. Normal application use retains
        // the previous single-callback behavior; Closed always unsubscribes.
        if (!NativeMotionCapture.SearchRendering(timingSession, instance, suggestionBorder.Opacity,
                suggestionBorder.RenderTransform is TranslateTransform movement ? movement.Y : 0))
            CompositionTarget.Rendering -= FirstRendering;
    }
    private void EntranceRendering(object? sender, EventArgs args)
    {
        if (entranceFirstRenderingMilliseconds is null)
        {
            entranceFirstRenderingMilliseconds = openClock.Elapsed.TotalMilliseconds;
            entranceFirstRenderedOpacity = suggestionBorder.Opacity;
            NativeMotionCapture.Mark(timingSession, "search-animation-first-rendering");
        }
        if (initialBackdropFromCache)
        {
            if (!backdropFilter.AwaitingFirstPresentationFrame)
            {
                CompositionTarget.Rendering -= EntranceRendering;
                return;
            }
            // The base value is already 1 before WPF applies the new clock.
            // The panel is already part of the owner's presentation channel.
            // Observe an advanced clock at Rendering, not a pre-tick base
            // value. The filter's original 100 ms deadline is unchanged.
            if (!IsLoaded || (FluentMotion.Enabled && entranceClock is not { CurrentState: ClockState.Active, CurrentProgress: > 0 })
                || suggestionBorder.Opacity <= 0) return;
            backdropFilter.FirstPresentationFrameSubmitted();
            NativeMotionCapture.Mark(timingSession, "search-initial-cache-nonzero-rendering");
        }
        CompositionTarget.Rendering -= EntranceRendering;
    }

    public void FocusInput()
    {
        if (closing || closed) return;
        inputBox.FocusInput();
    }
    internal object Describe() => new
    {
        instance, visible = IsVisible, closing, closed, choosing, host = "main-window", resultCount = results.Items.Count,
        renderingTier = RenderCapability.Tier >> 16, positionCount, positionMilliseconds, backdrop = backdropFilter.Describe(),
        initialBackdropFromCache, backdropVerificationSucceeded, backdropBeforeAttachment,
        // Relative timings only: no search text, entry names or controller data.
        lifecycleMilliseconds = new { templates = templatesMilliseconds, constructorStarted = constructorStartedMilliseconds,
            constructorCompleted = constructorCompletedMilliseconds, loaded = loadedMilliseconds,
            attachOwnerCompleted = attachOwnerCompletedMilliseconds, suggestionsStarted = suggestionsStartedMilliseconds,
            coreRequestsStarted = coreRequestsStartedMilliseconds, coreResponsesReady = coreResponsesReadyMilliseconds,
            coreEntriesCompleted = coreEntriesCompletedMilliseconds, backdropReady = backdropReadyMilliseconds, firstRendering = firstRenderingMilliseconds,
            initialBackdrop = initialBackdropMilliseconds, backdropVerificationCompleted = backdropVerificationCompletedMilliseconds,
            entranceFirstTick = entranceFirstTickMilliseconds,
            entranceClockCompleted = entranceClockCompletedMilliseconds, entranceFirstRendering = entranceFirstRenderingMilliseconds,
            entranceFirstRenderedOpacity }
    };
    private async Task LoadInitialEntries()
    {
        await LoadCoreEntries();
        coreEntriesCompletedMilliseconds = openClock.Elapsed.TotalMilliseconds;
    }
    private void ClosePalette(bool clearQuery = false, bool blur = false)
    {
        if (closing || closed) return;
        closing = true;
        try
        {
            inputBox.DismissPalette(Close, clearQuery, blur);
        }
        finally { if (!closed) closing = false; }
    }
    private void AttachOwner()
    {
        if (ownerAttached) return;
        ownerAttached = true;
        owner.LocationChanged += CloseForOwnerChange; owner.SizeChanged += CloseForOwnerSize;
        owner.StateChanged += CloseForOwnerChange; owner.IsVisibleChanged += CloseForOwnerVisibility;
        // The supplied AutoSuggestBox closes for an outside pointer, resize or
        // explicit choice/Escape. A focus/activation transition alone is not a
        // dismissal: assistive-technology clients may move keyboard focus.
        owner.PreviewMouseDown += OwnerPointerDown; owner.Closed += CloseForOwnerClosed;
        attachOwnerCompletedMilliseconds = openClock.Elapsed.TotalMilliseconds;
    }
    private void QueryChanged(object sender, TextChangedEventArgs e) { if (!closing && !closed) Filter(); }
    private void OwnerPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (new Rect(inputBox.RenderSize).Contains(e.GetPosition(inputBox))
            || new Rect(suggestionBorder.RenderSize).Contains(e.GetPosition(suggestionBorder))) return;
        ClosePalette();
    }
    private void CloseForOwnerChange(object? sender, EventArgs e) => ClosePalette();
    private void CloseForOwnerSize(object sender, SizeChangedEventArgs e) => ClosePalette();
    private void CloseForOwnerVisibility(object sender, DependencyPropertyChangedEventArgs e) { if (e.NewValue is false) ClosePalette(); }
    private void CloseForOwnerClosed(object? sender, EventArgs e) => Close();
    private void QueuePositionFlyout()
    {
        if (positionQueued || closing || closed || !IsLoaded) return;
        positionQueued = true;
        // ItemsChanged realizes containers during layout. Measuring the outer
        // stack directly from TextChanged can reuse the previous query's list.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            positionQueued = false;
            PositionFlyout();
        }));
    }
    private readonly record struct Placement(double Left, double Top, double Width, double MaxHeight);
    private static Placement Locate(GlobalSearchBox anchor, FrameworkElement host)
    {
        // Input and results share one presentation source and one DPI scale.
        // Keep CSS-equivalent coordinates in the client host's logical pixels.
        Point origin = anchor.TranslatePoint(new Point(0, anchor.ActualHeight), host);
        double availableWidth = Math.Max(1, host.ActualWidth - 16);
        double width = Math.Min(Math.Max(anchor.ActualWidth, 320), availableWidth);
        double left = Math.Clamp(origin.X, 8, Math.Max(8, host.ActualWidth - width - 8));
        double top = origin.Y + 4;
        double maxHeight = Math.Min(380, Math.Max(40, host.ActualHeight - top - 8));
        return new(left, top, width, maxHeight);
    }
    internal static Rect PreparationViewport(GlobalSearchBox anchor, FrameworkElement root)
    {
        if (anchor.ActualWidth <= 0 || anchor.ActualHeight <= 0) return Rect.Empty;
        var placement = Locate(anchor, root);
        // This is the real inside-border viewport. Reserving the legal maximum
        // height also covers later query results without rebuilding during entry.
        return new Rect(placement.Left + 1, placement.Top + 1,
            Math.Max(1, placement.Width - 2), Math.Max(1, placement.MaxHeight - 2));
    }
    private void PositionFlyout()
    {
        if (closing || closed || positioning) return;
        var started = Stopwatch.GetTimestamp();
        positioning = true;
        try
        {
            var placement = Locate(inputBox, overlayHost);
            double width = placement.Width, left = placement.Left, top = placement.Top;
            var measure = new Size(Math.Max(1, width - 10), double.PositiveInfinity);
            // The reference renders at most eight ordinary rows. Measure those
            // actual containers directly so an earlier list/scroll extent can
            // never keep a filtered one-row palette at its old height.
            double contentHeight = 0;
            foreach (var element in new UIElement[] { resultCount, empty })
            {
                if (element.Visibility != Visibility.Visible) continue;
                element.InvalidateMeasure(); element.Measure(measure);
                contentHeight += element.DesiredSize.Height;
            }
            foreach (ResultItem item in results.Items)
            {
                item.ApplyTemplate(); item.InvalidateMeasure(); item.Measure(measure);
                contentHeight += item.DesiredSize.Height;
            }
            results.InvalidateMeasure(); suggestions.InvalidateMeasure();
            double maxHeight = placement.MaxHeight;
            double panelHeight = Math.Min(maxHeight, contentHeight + 10);
            suggestionBorder.MaxHeight = maxHeight; suggestionBorder.Height = panelHeight;
            Width = width + 2 * ShadowInset; Height = panelHeight + 2 * ShadowInset;
            var bounds = new Rect(left - ShadowInset, top - ShadowInset, Width, Height);
            if (positionedBounds != bounds)
            {
                Canvas.SetLeft(this, bounds.X); Canvas.SetTop(this, bounds.Y);
                positionedBounds = bounds; positionCount++;
            }
        }
        finally { positioning = false; positionMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }

    private void RebuildEntries()
    {
        commandState = CommandState;
        entries.Clear();
        bool running = session.Status.Text("phase") == "running";
        AddCommand(running ? "cmd-stop" : "cmd-start", running ? "停止内核" : "启动内核", running ? "stop" : "play",
            running ? "desktop_stop" : "desktop_start", "core start stop 内核 启动 停止", () => DesktopReady,
            L.T(running ? "运行中" : "已停止"));
        entries.Add(new Entry("cmd-proxy", "command", "nav-proxies", L.T(session.Status.Bool("systemProxy") ? "关闭系统代理" : "开启系统代理"), "",
            "system proxy 系统代理", async () => await session.CommandAsync("desktop_set_system_proxy", new { enabled = !session.Status.Bool("systemProxy") }),
            () => DesktopReady && session.Status.Get("capabilities").Bool("systemProxy")));
        AddNavigation("cmd-tun", "command", "shield-lock", session.Status.Bool("tun") ? "关闭 TUN 模式" : "开启 TUN 模式", "TUN",
            "tun virtual adapter 虚拟网卡", new("home", Action: "toggle-tun"), () => DesktopReady && !session.IsDirty && session.Status.Get("capabilities").Bool("tun"));
        AddNavigation("cmd-import", "command", "arrow-import", "导入配置", "", "import yaml subscription 导入 订阅 配置", new("profiles", Action: "import"), () => DesktopReady && session.Status.Get("capabilities").Bool("configImport"));
        AddNavigation("cmd-test", "command", "speed", "全部测速", "Ctrl+T", "latency speed test 测速 延迟", new("proxies", Action: "test-all"), () => RuntimeReady);
        AddNavigation("cmd-edit", "command", "edit", "编辑配置文件", "", "yaml config editor 编辑 配置", new("profiles", Action: "edit"), () => session.Ready && !session.IsUpdating);
        AddCommand("cmd-restart", "重启内核", "arrow-sync", "desktop_restart", "core restart 重启 内核", () => DesktopReady && !session.IsDirty);
        foreach (string mode in new[] { "rule", "global", "direct" })
        {
            string title = mode switch { "rule" => "切换到规则模式", "global" => "切换到全局模式", _ => "切换到直连模式" };
            string detail = runtimeMode == mode ? L.T("当前模式") : L.ShortMode(mode);
            entries.Add(new Entry("mode-" + mode, "command", "arrow-swap", L.T(title), detail, "mode " + mode + " " + title,
                async () => await session.CommandAsync("desktop_patch_config", new { patch = new Dictionary<string, object> { ["mode"] = mode } }),
                () => RuntimeReady && !session.IsDirty));
        }
        entries.Add(new Entry("cmd-config-folder", "command", "folder-open", L.T("打开配置文件夹"), "", "config directory folder 配置 目录 文件夹",
            async () => await session.CommandAsync("desktop_open_directory", new { kind = "config" }), () => session.Ready && !session.IsUpdating));
        entries.Add(new Entry("cmd-log-folder", "command", "nav-logs", L.T("打开日志目录"), "", "logs directory folder 日志 目录 文件夹",
            async () => await session.CommandAsync("desktop_open_directory", new { kind = "logs" }), () => session.Ready && !session.IsUpdating));
        foreach (var (page, title, icon, keywords) in new[]
        {
            ("home", "主页", "nav-home", "home overview dashboard 首页 概览"), ("proxies", "代理", "nav-proxies", "proxies policy group node 代理 策略组 节点"),
            ("connections", "连接", "nav-connections", "connections traffic 连接 流量"), ("dns", "DNS", "nav-dns", "dns lookup domain 域名 查询"),
            ("rules", "规则", "nav-rules", "rules match 规则 匹配"), ("logs", "日志", "nav-logs", "logs errors 日志 错误"),
            ("profiles", "配置", "nav-profiles", "profiles config subscription yaml 配置 订阅"), ("settings", "设置", "nav-settings", "settings preferences 设置 偏好")
        }) AddNavigation("page-" + page, "page", icon, title, page == "home" ? "/" : "/" + page, keywords, new(page));
        entries.AddRange(coreEntries);
        foreach (var target in SearchTargets.Settings)
            AddNavigation("setting-" + target.Id, "setting", "setting", target.Title, L.T(target.Page == "profiles" ? "配置" : "设置"), target.Keywords,
                new(target.Page, Target: target.Id));
        Filter(preserveSelection: true);
    }

    private void AddCommand(string key, string title, string icon, string method, string keywords, Func<bool> enabled, string detail = "") =>
        entries.Add(new Entry(key, "command", icon, L.T(title), detail, keywords, async () => await session.CommandAsync(method), enabled));

    private void AddNavigation(string key, string category, string icon, string title, string detail, string keywords, NavigationTarget target, Func<bool>? enabled = null) =>
        entries.Add(new Entry(key, category, icon, L.T(title), detail, keywords, () => { session.Navigate(target); return Task.CompletedTask; }, enabled));

    private void Filter(bool preserveSelection = false)
    {
        if (closing || closed) return;
        string? selectedKey = preserveSelection && results.SelectedItem is ListBoxItem { Tag: Entry previous } ? previous.Key : null;
        string query = search.Text.Trim();
        var candidates = query.Length == 0 ? entries.Where(entry => entry.Category == "command").Take(6).ToArray()
            : entries.Where(entry => entry.Matches(query)).OrderByDescending(entry => entry.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var visible = candidates.Take(MaximumVisibleResults).ToArray();
        var appearance = visible.Select(entry => new ResultAppearance(entry.Key, entry.Category, entry.Icon, entry.Title, entry.Detail, entry.IsEnabled, L.IsEnglish)).ToArray();
        bool contentChanged = renderedQuery != query || !renderedResults.SequenceEqual(appearance);
        if (contentChanged)
        {
            results.Items.Clear();
            foreach (Entry entry in visible) results.Items.Add(RenderResult(entry, query));
            renderedQuery = query; renderedResults = appearance;
        }
        else
        {
            // Endpoint loading and status-only updates leave identical rows
            // in place, including their realized templates and UIA providers.
            // Their commands still follow the latest entry, never old data.
            for (int index = 0; index < visible.Length; index++)
                UpdateEntry((ResultItem)results.Items[index], visible[index]);
        }
        int selected = 0;
        if (selectedKey is not null)
            for (int index = 0; index < results.Items.Count; index++)
                if (((ListBoxItem)results.Items[index]).Tag is Entry entry && entry.Key == selectedKey) { selected = index; break; }
        if (results.Items.Count > 0) results.SelectedIndex = selected;
        resultCount.Text = L.T("试试这些");
        var headingVisibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool layoutChanged = contentChanged || resultCount.Visibility != headingVisibility;
        resultCount.Visibility = headingVisibility;
        string countDescription = candidates.Length > MaximumVisibleResults ? L.F("{0} 个结果，显示前 {1} 项；请细化关键词", candidates.Length, MaximumVisibleResults)
            : L.F("{0} 个结果", candidates.Length);
        AutomationProperties.SetItemStatus(results, countDescription);
        string emptyText = L.F("没有找到“{0}”。可以搜索节点、策略组、规则、设置项或命令。", query);
        var emptyVisibility = candidates.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        layoutChanged |= empty.Visibility != emptyVisibility || emptyVisibility == Visibility.Visible && empty.Text != emptyText;
        empty.Text = emptyText; empty.Visibility = emptyVisibility;
        state.Text = loading ? L.T("正在读取策略组、节点和规则…")
            : loadError is not null ? L.F("部分内容未能读取：{0}。按 F5 重试。", loadError)
            : !RuntimeReady ? L.T("启动内核后，可搜索策略组、节点和规则。页面与设置始终可搜索。")
            : L.T("选择结果可定位到页面、控件或规则；节点结果会定位到节点。");
        AutomationProperties.SetHelpText(search, state.Text);
        if (layoutChanged) QueuePositionFlyout();
    }

    private ListBoxItem RenderResult(Entry entry, string query)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = new FluentIcon(entry.Icon, 16) { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        glyph.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush"); row.Children.Add(glyph);
        var title = Ui.Text("", 14); title.TextTrimming = TextTrimming.CharacterEllipsis; title.TextWrapping = TextWrapping.NoWrap;
        title.LineHeight = 20; title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        AppendHighlight(title, entry.Title, query);
        var detail = Ui.Muted(entry.Detail); detail.TextWrapping = TextWrapping.NoWrap; detail.TextTrimming = TextTrimming.CharacterEllipsis;
        detail.LineHeight = 16; detail.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        detail.Visibility = entry.Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var text = Ui.Column(title, detail); Grid.SetColumn(text, 1); row.Children.Add(text);
        string category = L.IsEnglish ? entry.Category switch { "command" => "Command", "page" => "Page", "group" => "Group", "node" => "Node", "setting" => "Setting", _ => "Rule" }
            : entry.Category switch { "command" => "命令", "page" => "页面", "group" => "策略组", "node" => "节点", "setting" => "设置", _ => "规则" };
        var label = Ui.Muted(category, 12); label.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        label.TextWrapping = TextWrapping.NoWrap; label.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(label, 2); row.Children.Add(label);
        var item = new ResultItem { Execute = () => RunEntry(entry), CanExecute = () => entry.IsEnabled && !closing && !closed && !choosing,
            Content = row, Tag = entry, Style = (Style)templates["Result"],
            Opacity = entry.IsEnabled ? 1 : 0.5, ToolTip = entry.IsEnabled ? null : L.T("当前状态无法执行此命令，请等待正在进行的操作完成。") }.Id("SearchResult_" + entry.Key);
        AutomationProperties.SetName(item, entry.Title + " · " + category + (entry.Detail.Length > 0 ? " · " + entry.Detail : ""));
        AutomationProperties.SetHelpText(item, entry.IsEnabled ? "" : L.T("当前状态无法执行此命令，请等待正在进行的操作完成。"));
        item.MouseEnter += (_, _) => { results.SelectedItem = item; };
        item.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; results.SelectedItem = item; };
        item.PreviewMouseLeftButtonUp += async (_, e) => { e.Handled = true; await item.Execute(); };
        return item;
    }

    private void UpdateEntry(ResultItem item, Entry entry)
    {
        item.Tag = entry;
        item.Execute = () => RunEntry(entry);
        item.CanExecute = () => entry.IsEnabled && !closing && !closed && !choosing;
    }

    private static void AppendHighlight(TextBlock text, string title, string query)
    {
        int start = query.Length == 0 ? -1 : title.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (start < 0) { text.Inlines.Add(new Run(title)); return; }
        text.Inlines.Add(new Run(title[..start]));
        var highlight = new Run(title.Substring(start, query.Length));
        highlight.FontWeight = FontWeights.SemiBold;
        highlight.SetResourceReference(TextElement.ForegroundProperty, "AccentLinkBrush");
        text.Inlines.Add(highlight); text.Inlines.Add(new Run(title[(start + query.Length)..]));
    }

    private async Task LoadCoreEntries()
    {
        if (closed || loading || !RuntimeReady) return;
        string identity = ControllerIdentity;
        loading = true; loadError = null; Filter(preserveSelection: true);
        try
        {
            coreRequestsStartedMilliseconds = openClock.Elapsed.TotalMilliseconds;
            Task<JsonElement> proxies = session.ApiAsync(HttpMethod.Get, "proxies", cancellationToken: lifetime.Token);
            Task<JsonElement> rules = session.ApiAsync(HttpMethod.Get, "rules", cancellationToken: lifetime.Token);
            Task<JsonElement> configs = session.ApiAsync(HttpMethod.Get, "configs", cancellationToken: lifetime.Token);
            // Keep readable categories available even when a different endpoint fails.
            try { await Task.WhenAll(proxies, rules, configs); } catch (Exception) when (!lifetime.IsCancellationRequested) { }
            coreResponsesReadyMilliseconds = openClock.Elapsed.TotalMilliseconds;
            if (closed || lifetime.IsCancellationRequested || identity != ControllerIdentity || !RuntimeReady) return;
            coreEntries.Clear(); coreIdentity = identity;
            if (configs.IsCompletedSuccessfully) runtimeMode = configs.Result.Text("mode");
            if (proxies.IsCompletedSuccessfully)
            {
                foreach (var proxy in proxies.Result.Get("proxies").Fields())
                {
                    string name = proxy.Name; bool group = proxy.Value.Get("all").ValueKind == JsonValueKind.Array;
                    string detail = proxy.Value.Text("type");
                    if (group) detail += " · " + proxy.Value.Text("now", fallback: "—");
                    else
                    {
                        double delay = proxy.Value.Get("history").Items().LastOrDefault().Number("delay");
                        if (delay > 0) detail += " · " + delay.ToString("0.#") + " ms";
                    }
                    coreEntries.Add(new Entry((group ? "group-" : "node-") + name, group ? "group" : "node", group ? "nav-proxies" : "globe", name, detail,
                        (group ? "group policy 策略组 " : "node proxy 节点 ") + detail,
                        () => { session.Navigate(new NavigationTarget("proxies", Target: name, Action: group ? "focus-group" : "focus-node")); return Task.CompletedTask; }, () => RuntimeReady));
                }
            }
            if (rules.IsCompletedSuccessfully)
            {
                int index = 0;
                foreach (var rule in rules.Result.Get("rules").Items())
                {
                    string payload = rule.Text("payload"); int ruleIndex = index++;
                    if (payload.Length == 0) continue;
                    string detail = rule.Text("type") + " → " + rule.Text("proxy");
                    coreEntries.Add(new Entry("rule-" + ruleIndex, "rule", "nav-rules", payload, detail, "rule 规则 " + rule.Text("proxy"),
                        () => { session.Navigate(new NavigationTarget("rules", Query: payload)); return Task.CompletedTask; }, () => RuntimeReady));
                }
            }
            loadError = new[] { proxies, rules, configs }.FirstOrDefault(task => task.IsFaulted)?.Exception?.GetBaseException().Message;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) loadError = error.Message; }
        finally
        {
            loading = false;
            if (!closed) RebuildEntries();
        }
    }

    private async void OnStatusChanged(object? sender, EventArgs e)
    {
        if (closed) return;
        bool changed = commandState != CommandState;
        if ((!RuntimeReady || coreIdentity != ControllerIdentity) && coreIdentity.Length > 0)
        { coreEntries.Clear(); runtimeMode = ""; coreIdentity = ""; changed = true; }
        if (changed) RebuildEntries();
        if (RuntimeReady && coreIdentity.Length == 0) await LoadCoreEntries();
    }
    private void OnLanguageChanged() => RebuildEntries();

    internal bool HandleKey(KeyEventArgs e)
    {
        if (closing || closed) return false;
        if (e.Key == Key.Escape) { e.Handled = true; ClosePalette(clearQuery: true, blur: true); return true; }
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; FocusInput(); return true; }
        if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (entries.FirstOrDefault(entry => entry.Key == "cmd-test") is { } test) _ = RunEntry(test);
            return true;
        }
        if (e.Key == Key.F5) { e.Handled = true; _ = LoadCoreEntries(); return true; }
        if (e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            if (results.Items.Count == 0) return true;
            int step = e.Key == Key.Down ? 1 : -1;
            results.SelectedIndex = (results.SelectedIndex + step + results.Items.Count) % results.Items.Count;
            if (results.SelectedItem is ListBoxItem selected) selected.BringIntoView();
            return true;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (results.SelectedItem is ListBoxItem { Tag: Entry entry }) _ = RunEntry(entry);
            return true;
        }
        return false;
    }

    private async Task RunEntry(Entry entry)
    {
        if (choosing || closing || closed) return;
        if (!entry.IsEnabled) { state.Text = L.T("当前状态无法执行此命令，请等待正在进行的操作完成。"); session.Notify(state.Text, true); return; }
        choosing = true;
        try
        {
            // Close the palette before navigation so the destination receives keyboard focus.
            ClosePalette(clearQuery: true, blur: true);
            await entry.Run();
        }
        catch (Exception error) { session.Notify(error.Message, true); }
    }

}
