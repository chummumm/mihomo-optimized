using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

/// <summary>A full-resolution backdrop sampled only from explicitly supplied lower visual layers.</summary>
internal sealed class FluentBackdropFilter : Grid, IDisposable
{
    internal sealed record PreparationTicket(long Generation, long RequiredRevision, long FrameRevision,
        double RequestedAtMilliseconds, double CaptureStartedAtMilliseconds, double CaptureCompletedAtMilliseconds);

    private sealed class ObserverScope
    {
        internal readonly List<WeakReference<FluentBackdropFilter>> Filters = [];
        internal bool Rearming;
    }
    private static readonly ConditionalWeakTable<Dispatcher, ObserverScope> ObserverScopes = new();
    internal static void PrewarmRendering() { if (FluentMotion.Enabled) FluentBackdropPixels.Prewarm(); }
    // Keep the diagnostic entry point used by the opt-in motion recorder; the
    // implementation has no shader/compiler or software-rendering STA anymore.
    internal static object DescribeCompilation() => new { completed = true, available = true, diagnostic = "full-resolution-simd-gaussian", renderer = FluentBackdropPixels.DescribeWarmup() };

    internal static readonly DependencyProperty TintBrushProperty = DependencyProperty.Register(nameof(TintBrush), typeof(Brush), typeof(FluentBackdropFilter), new PropertyMetadata(null, Changed));
    internal static readonly DependencyProperty BaseBrushProperty = DependencyProperty.Register(nameof(BaseBrush), typeof(Brush), typeof(FluentBackdropFilter), new PropertyMetadata(null, Changed));
    internal static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(FluentBackdropFilter), new PropertyMetadata(new CornerRadius(), Changed), value => value is CornerRadius corners && new[] { corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft }.All(number => double.IsFinite(number) && number >= 0));
    internal static readonly DependencyProperty SigmaProperty = DependencyProperty.Register(nameof(Sigma), typeof(double), typeof(FluentBackdropFilter), new PropertyMetadata(30d, Changed), value => value is double number && double.IsFinite(number) && number is >= 0 and <= 30);
    internal static readonly DependencyProperty SaturationProperty = DependencyProperty.Register(nameof(Saturation), typeof(double), typeof(FluentBackdropFilter), new PropertyMetadata(1.5d, Changed), value => value is double number && double.IsFinite(number) && number is >= 0 and <= 4);

    internal Brush? TintBrush { get => (Brush?)GetValue(TintBrushProperty); set => SetValue(TintBrushProperty, value); }
    internal Brush? BaseBrush { get => (Brush?)GetValue(BaseBrushProperty); set => SetValue(BaseBrushProperty, value); }
    internal CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    internal double Sigma { get => (double)GetValue(SigmaProperty); set => SetValue(SigmaProperty, value); }
    internal double Saturation { get => (double)GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }
    internal Thickness MotionOverscan { get; set; } = new(16);
    internal bool FilterAvailable { get; private set; }
    internal string FilterDiagnostic { get; private set; } = "source-not-attached";

    private sealed class Layer(FrameworkElement source)
    {
        internal FrameworkElement Source { get; } = source;
        internal VisualBrush Brush { get; } = new(source)
        {
            AutoLayoutContent = false, ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, TileMode = TileMode.None,
        };
        internal Rect View { get; set; } = Rect.Empty;
        internal Rect Port { get; set; } = Rect.Empty;
    }

    private sealed class SourcePlane : FrameworkElement
    {
        internal DrawingGroup? Drawing { get; set; }
        protected override void OnRender(DrawingContext context) { if (Drawing is not null) context.DrawDrawing(Drawing); }
    }

    // These zero-opacity observation resources have no rendered pixels. They
    // must not become another filter's lower layer (for example a modal toast
    // enumerating the owner's direct children), which would make a brush cycle.
    private sealed class PreparationPlane : Canvas
    {
        internal PreparationPlane()
        {
            Width = Height = 0; IsHitTestVisible = false;
            HorizontalAlignment = HorizontalAlignment.Left; VerticalAlignment = VerticalAlignment.Top;
            SetZIndex(this, int.MaxValue);
        }
    }

    private readonly Canvas sampling = new() { IsHitTestVisible = false, ClipToBounds = false };
    private readonly TranslateTransform sampleOffset = new();
    private readonly Image raster = new() { IsHitTestVisible = false, Stretch = Stretch.Fill };
    private readonly Border tint = new() { IsHitTestVisible = false };
    // Keep the VisualBrush resources on WPF's channel so its cyclic-brush
    // invalidation reaches SourceChanged even while the displayed bitmap is
    // unchanged. Opacity zero affects composition, not resource marshaling.
    // This plane is never captured as the source and performs no blur/effects.
    private readonly SourcePlane observer = new() { IsHitTestVisible = false, Opacity = 0 };
    private readonly DrawingVisual captureVisual = new();
    private readonly List<Layer> layers = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly ObserverScope observerScope;
    private readonly PreparationPlane preparationObserverHost = new();
    private readonly DispatcherTimer preparationTimer;
    private Panel? preparationHost;
    private Func<Rect>? preparationViewport;
    private bool preparationEnabled, presentationEnabled = true, renderingSubscribed, demanding;
    private FrameworkElement? coordinateRoot;
    private bool listening, disposed, geometryDirty = true, sourceDirty = true, capturing, updatingGeometry;
    private DispatcherOperation? captureOperation;
    private double dpiX = 1, dpiY = 1, padding;
    private Rect clipBounds = Rect.Empty, sampleWindow = Rect.Empty;
    private Size sourceSize, clipSize;
    private long generation;
    private long sourceRevision, publishedSourceRevision = -1, publishedGeneration = -1, readyRevision;
    private double publishedCaptureStartedAtMilliseconds, publishedCaptureCompletedAtMilliseconds;
    private byte[]? baseline, scratch;
    private CancellationTokenSource? processing;
    private TaskCompletionSource<bool> firstReady = NewReady();
    private int drawingRevision, sourceInvalidations, captures, identicalCaptures, filteredFrames, cancelledFrames, observerRearms;
    private double captureMilliseconds, compareMilliseconds, filterMilliseconds, retryAfterMilliseconds;
    private double queueMilliseconds, schedulingMilliseconds, convolutionMilliseconds, publishDispatchMilliseconds;
    private double lastCaptureAt = double.NegativeInfinity, preparationStartedAt, preparationReadyMilliseconds;
    private double? demandStartedAt;
    private double demandMilliseconds, captureQueueMilliseconds;
    private int demandCacheHits, captureQueuePromotions;
    private int preparedFrameReuses;
    private object? lastPreparationUse;
    private double? firstBitmapMilliseconds;
    private static TaskCompletionSource<bool> NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal FluentBackdropFilter()
    {
        observerScope = ObserverScopes.GetValue(Dispatcher, _ => new ObserverScope());
        observerScope.Filters.Add(new(this));
        preparationTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        preparationTimer.Tick += PreparationDue;
        IsHitTestVisible = false; Focusable = false; ClipToBounds = true;
        sampling.RenderTransform = sampleOffset;
        sampling.Children.Add(observer); sampling.Children.Add(raster);
        Children.Add(sampling); Children.Add(tint);
        SetResourceReference(BaseBrushProperty, "AppBackgroundBrush");
        SetResourceReference(TintBrushProperty, "AcrylicBrush");
        Loaded += OnLoaded; Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibilityChanged; SizeChanged += OnSizeChanged;
    }

    private bool Presenting => presentationEnabled && IsLoaded && IsVisible;
    private bool Preparing => preparationEnabled && preparationHost is { IsLoaded: true, IsVisible: true };
    private bool CanProcess => Presenting || Preparing;

    /// <summary>Prepare the real future viewport without constructing or displaying a popup HWND.</summary>
    internal void ConfigurePreparation(Panel host, Func<Rect> viewport)
    {
        Dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(viewport);
        if (!ReferenceEquals(host.Dispatcher, Dispatcher) || layers.Any(layer => ReferenceEquals(layer.Source, host) || layer.Source.IsAncestorOf(host)))
            throw new ArgumentException("The preparation observer must be outside every sampled layer.", nameof(host));
        if (preparationHost is not null) throw new InvalidOperationException("Backdrop preparation is already configured.");
        preparationHost = host; preparationViewport = viewport; presentationEnabled = false;
        sampling.Children.Remove(observer); preparationObserverHost.Children.Add(observer); host.Children.Add(preparationObserverHost);
        host.Loaded += PreparationHostChanged; host.Unloaded += PreparationHostChanged;
        host.IsVisibleChanged += PreparationVisibilityChanged; host.SizeChanged += PreparationSizeChanged;
        host.LayoutUpdated += PreparationLayoutChanged;
    }

    internal void SetPreparationEnabled(bool enabled)
    {
        if (disposed || preparationEnabled == enabled) return;
        preparationEnabled = enabled;
        if (enabled) { preparationStartedAt = clock.Elapsed.TotalMilliseconds; preparationReadyMilliseconds = 0; }
        UpdateSubscription();
    }

    internal void BeginPresentation() { presentationEnabled = true; UpdateSubscription(); }
    internal void EndPresentation() { if (preparationHost is not null) { presentationEnabled = false; UpdateSubscription(); } }
    internal void RefreshPreparation() { if (Preparing) UpdateGeometry(); }
    internal void InvalidatePreparation()
    {
        if (disposed) return;
        InvalidateRaster(clear: true);
        if (CanProcess) { UpdateGeometry(); QueueCapture(); }
    }

    private void PreparationDue(object? sender, EventArgs args) { preparationTimer.Stop(); QueueCapture(); }
    private void PreparationHostChanged(object sender, RoutedEventArgs args) => UpdateSubscription();
    private void PreparationVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateSubscription();
    private void PreparationSizeChanged(object sender, SizeChangedEventArgs args) { if (Preparing) UpdateGeometry(); }
    private void PreparationLayoutChanged(object? sender, EventArgs args) { if (Preparing && !Presenting) UpdateGeometry(); }

    /// <summary>The viewport is never implicitly sampled; every lower layer must be explicit and non-recursive.</summary>
    internal void SetSources(FrameworkElement root, params FrameworkElement[] sources)
    {
        Dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(root); ArgumentNullException.ThrowIfNull(sources);
        if (!ReferenceEquals(root.Dispatcher, Dispatcher)) throw new ArgumentException("The backdrop viewport must use this dispatcher.", nameof(root));
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source is PreparationPlane) continue;
            if (!ReferenceEquals(source.Dispatcher, Dispatcher)) throw new ArgumentException("Backdrop layers must use this dispatcher.", nameof(sources));
            if (ReferenceEquals(source, this) || source.IsAncestorOf(this) || IsAncestorOf(source) || source.IsAncestorOf(observer))
                throw new ArgumentException("A backdrop source cannot contain or belong to the filter.", nameof(sources));
        }
        ReleaseLayers(); coordinateRoot = root; sampleWindow = Rect.Empty; geometryDirty = true;
        foreach (var source in sources.Where(source => source is not PreparationPlane).Distinct())
        {
            var layer = new Layer(source); layer.Brush.Changed += SourceChanged; layers.Add(layer);
        }
        firstReady.TrySetResult(false); firstReady = NewReady();
        InvalidateRaster(clear: true); UpdateSubscription();
    }

    internal Task<PreparationTicket?> PrepareAsync() => PrepareCoreAsync(verifyPixels: true);

    // A popup can construct its HWND/layout while this same real-source
    // verification runs. Loaded validates that same request's frame against the
    // final geometry. Later live revisions must not turn this into a second
    // serial preparation; page/DPI/geometry changes still invalidate the ticket.
    internal async Task<bool> CompletePreparationAsync(Task<PreparationTicket?> preparation)
    {
        var ticket = await preparation;
        if (disposed || !CanProcess) return false;
        UpdateGeometry();
        if (ticket is not null && raster.Source is not null && FilterAvailable
            && generation == ticket.Generation && publishedGeneration == ticket.Generation
            && publishedSourceRevision >= ticket.FrameRevision)
        {
            preparedFrameReuses++; demandCacheHits++;
            lastPreparationUse = new
            {
                generation = ticket.Generation, requiredRevision = ticket.RequiredRevision,
                frameRevision = ticket.FrameRevision, currentSourceRevision = sourceRevision,
                sampledAfterRequestMilliseconds = ticket.CaptureStartedAtMilliseconds - ticket.RequestedAtMilliseconds,
                sampleDurationMilliseconds = ticket.CaptureCompletedAtMilliseconds - ticket.CaptureStartedAtMilliseconds,
                sampleAgeAtUseMilliseconds = clock.Elapsed.TotalMilliseconds - ticket.CaptureCompletedAtMilliseconds,
            };
            return true;
        }
        return await PrepareCoreAsync(verifyPixels: false) is not null;
    }

    private async Task<PreparationTicket?> PrepareCoreAsync(bool verifyPixels)
    {
        Dispatcher.VerifyAccess();
        if (disposed || !CanProcess) return null;
        double requestedAt = clock.Elapsed.TotalMilliseconds;
        bool verifySource = verifyPixels && !demanding;
        if (verifySource) demandStartedAt = clock.Elapsed.TotalMilliseconds;
        demanding = true; preparationTimer.Stop();
        UpdateGeometry();
        if (FilterDiagnostic is not ("preparing-full-resolution-backdrop" or "live-full-resolution-gaussian-srgb")) { demanding = false; return null; }
        // Keep a real pixel verification for each new presentation request.
        // The owner-side observer supplies later revisions while the popup
        // constructs; those changes still wait for their own completed sample.
        if (verifySource) { RearmObservers(); sourceDirty = true; sourceRevision++; }
        if (raster.Source is not null && publishedGeneration == generation && !sourceDirty && processing is null && publishedSourceRevision >= sourceRevision)
        {
            demandCacheHits++; CompleteDemand();
            return new(generation, sourceRevision, publishedSourceRevision, requestedAt,
                publishedCaptureStartedAtMilliseconds, publishedCaptureCompletedAtMilliseconds);
        }
        if (coordinateRoot is null || layers.Count == 0 || SystemParameters.HighContrast) { demanding = false; return null; }
        long requiredRevision = sourceRevision;
        readyRevision = requiredRevision;
        if (firstReady.Task.IsCompleted) firstReady = NewReady();
        QueueCapture();
        bool ready = await firstReady.Task;
        if (!ready || disposed || raster.Source is null || publishedGeneration != generation || publishedSourceRevision < requiredRevision) return null;
        return new(generation, requiredRevision, publishedSourceRevision, requestedAt,
            publishedCaptureStartedAtMilliseconds, publishedCaptureCompletedAtMilliseconds);
    }

    internal object Describe() => new
    {
        available = FilterAvailable, diagnostic = FilterDiagnostic, sigma = Sigma, saturation = Saturation,
        gaussianPasses = Sigma == 0 ? 0 : 1, radiusPerPass = 3 * Sigma, dpi = Math.Max(dpiX, dpiY) * 96,
        sourceKind = "wpf-visual-layers", includesDesktopComposition = false, effectiveSigma = Sigma,
        samplePadding = padding, sourceLayers = layers.Count, drawingRevision,
        cachedAtNativeResolution = raster.Source is not null, cacheRenderScale = 1,
        sampleWidth = sourceSize.Width, sampleHeight = sourceSize.Height,
        rasterWidth = raster.Source is BitmapSource bitmap ? bitmap.PixelWidth : 0,
        rasterHeight = raster.Source is BitmapSource bitmap2 ? bitmap2.PixelHeight : 0,
        sourceInvalidations, captures, identicalCaptures, filteredFrames, cancelledFrames, observerRearms,
        captureMilliseconds, compareMilliseconds, filterMilliseconds, firstBitmapMilliseconds,
        queueMilliseconds, schedulingMilliseconds, convolutionMilliseconds, publishDispatchMilliseconds,
        preparationEnabled, presenting = Presenting, preparationReadyMilliseconds,
        demandMilliseconds, demandCacheHits, captureQueueMilliseconds, captureQueuePromotions,
        preparedFrameReuses, lastPreparationUse,
        sourceRevision, publishedSourceRevision,
        processing = processing is not null, pendingSourceChange = sourceDirty,
    };

    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var filter = (FluentBackdropFilter)owner;
        filter.tint.Background = filter.TintBrush;
        if (args.Property == CornerRadiusProperty) filter.clipSize = default;
        else if (args.Property != TintBrushProperty)
        {
            filter.geometryDirty = true;
            if (args.Property == SigmaProperty) filter.sampleWindow = Rect.Empty;
            filter.InvalidateRaster(clear: false);
        }
        filter.UpdateGeometry();
    }
    private void OnLoaded(object sender, RoutedEventArgs args) => UpdateSubscription();
    // A prepared filter is transferred to/from the flyout HWND. The owner-side
    // observer remains loaded, so that transfer must not discard the bitmap.
    private void OnUnloaded(object sender, RoutedEventArgs args) => UpdateSubscription();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateSubscription();
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => UpdateGeometry();
    private void SourceChanged(object? sender, EventArgs args)
    {
        if (disposed || updatingGeometry || capturing || observerScope.Rearming) return;
        sourceInvalidations++; sourceRevision++; sourceDirty = true; retryAfterMilliseconds = 0; QueueCapture();
    }
    private void UpdateSubscription()
    {
        if (disposed || !CanProcess || coordinateRoot is null) { StopListening(); return; }
        listening = true;
        if (Presenting && !renderingSubscribed) { renderingSubscribed = true; CompositionTarget.Rendering += Rendering; }
        else if (!Presenting && renderingSubscribed) { renderingSubscribed = false; CompositionTarget.Rendering -= Rendering; }
        if (firstReady.Task.IsCompleted && raster.Source is null) firstReady = NewReady();
        UpdateGeometry();
    }
    private void StopListening()
    {
        listening = false; demanding = false; preparationTimer.Stop();
        captureOperation?.Abort(); captureOperation = null;
        if (renderingSubscribed) { renderingSubscribed = false; CompositionTarget.Rendering -= Rendering; }
        InvalidateRaster(clear: true); firstReady.TrySetResult(false);
        sampling.Visibility = Visibility.Collapsed;
    }
    private void Rendering(object? sender, EventArgs args) => UpdateGeometry();
    private void InvalidateRaster(bool clear)
    {
        generation++; sourceRevision++; processing?.Cancel(); baseline = scratch = null; sourceDirty = true;
        if (demanding) readyRevision = sourceRevision;
        if (clear)
        {
            raster.Source = null; publishedSourceRevision = publishedGeneration = -1; FilterAvailable = false;
            if (firstReady.Task.IsCompleted) firstReady = NewReady();
        }
    }
    private void Fallback(string reason)
    {
        if (raster.Source is not null || processing is not null || baseline is not null || FilterDiagnostic != reason)
            InvalidateRaster(clear: true);
        else sourceDirty = true;
        FilterAvailable = false; FilterDiagnostic = reason;
        sampling.Visibility = Visibility.Collapsed; Background = BaseBrush ?? SystemColors.WindowBrush;
        if (reason.StartsWith("capture-unavailable-", StringComparison.Ordinal) || reason.StartsWith("filter-unavailable-", StringComparison.Ordinal))
            retryAfterMilliseconds = clock.Elapsed.TotalMilliseconds + 500;
        demanding = false; firstReady.TrySetResult(false);
    }

    private void UpdateGeometry()
    {
        if (disposed || !CanProcess || updatingGeometry) return;
        tint.Background = TintBrush;
        var actual = new Size(ActualWidth, ActualHeight);
        if (actual != clipSize && actual.Width > 0 && actual.Height > 0) { Clip = Rounded(actual, CornerRadius); clipSize = actual; }
        if (coordinateRoot is null || layers.Count == 0 || PresentationSource.FromVisual(coordinateRoot) is null)
        { Fallback("source-not-attached"); return; }
        bool arrangedPresentation = Presenting && actual.Width > 0 && actual.Height > 0 && PresentationSource.FromVisual(this) is not null;
        if (!arrangedPresentation && !Preparing) { Fallback("surface-not-arranged"); return; }
        if (SystemParameters.HighContrast) { Fallback("high-contrast"); return; }
        updatingGeometry = true;
        try
        {
            var scale = VisualTreeHelper.GetDpi(arrangedPresentation ? this : preparationHost!);
            bool dpiChanged = dpiX != scale.DpiScaleX || dpiY != scale.DpiScaleY;
            dpiX = scale.DpiScaleX; dpiY = scale.DpiScaleY;
            if (!double.IsFinite(dpiX) || !double.IsFinite(dpiY) || dpiX is <= 0 or > 4 || dpiY is <= 0 or > 4) { Fallback("dpi-not-supported"); return; }
            double padX = Math.Ceiling(3 * Sigma * dpiX) / dpiX, padY = Math.Ceiling(3 * Sigma * dpiY) / dpiY;
            padding = Math.Max(padX, padY);
            var rootOrigin = coordinateRoot.PointToScreen(new Point());
            Rect viewport;
            if (arrangedPresentation)
            {
                var origin = PointToScreen(new Point());
                viewport = new Rect((origin.X - rootOrigin.X) / dpiX, (origin.Y - rootOrigin.Y) / dpiY, ActualWidth, ActualHeight);
            }
            else viewport = preparationViewport?.Invoke() ?? Rect.Empty;
            if (viewport.IsEmpty || viewport.Width <= 0 || viewport.Height <= 0) { Fallback("surface-not-arranged"); return; }
            var rootEnd = coordinateRoot.PointToScreen(new Point(coordinateRoot.ActualWidth, coordinateRoot.ActualHeight));
            var rootBounds = new Rect(0, 0, Math.Max(0, (rootEnd.X - rootOrigin.X) / dpiX), Math.Max(0, (rootEnd.Y - rootOrigin.Y) / dpiY));
            var required = Rect.Intersect(viewport, rootBounds);
            // Pixels wholly outside the owner's viewport are never presented.
            // Reserve the final visible navigation region instead of filtering
            // an additional full pane width while it slides in from off-screen.
            if (required.IsEmpty && Preparing) required = Rect.Intersect(preparationViewport!(), rootBounds);
            if (required.IsEmpty) { Fallback("surface-outside-source"); return; }
            required.Inflate(padX, padY);
            if (sampleWindow.IsEmpty || !sampleWindow.Contains(required) || dpiChanged)
            {
                // Reserve the complete Gaussian support plus the entrance path.
                // Align to physical pixels without shrinking or downsampling.
                var margin = Preparing ? new Thickness(16) : MotionOverscan;
                double left = Math.Floor((required.Left - Math.Max(0, margin.Left)) * dpiX) / dpiX;
                double top = Math.Floor((required.Top - Math.Max(0, margin.Top)) * dpiY) / dpiY;
                double right = Math.Ceiling((required.Right + Math.Max(0, margin.Right)) * dpiX) / dpiX;
                double bottom = Math.Ceiling((required.Bottom + Math.Max(0, margin.Bottom)) * dpiY) / dpiY;
                sampleWindow = new Rect(new Point(left, top), new Point(right, bottom)); geometryDirty = true;
            }
            var size = sampleWindow.Size;
            // The bitmap remains fixed in world space while the filter's visible
            // viewport moves. Its backdrop does not slide with the menu contents.
            sampleOffset.X = sampleWindow.X - viewport.X; sampleOffset.Y = sampleWindow.Y - viewport.Y;
            Rect ToLocal(FrameworkElement source)
            {
                var first = source.PointToScreen(new Point()); var last = source.PointToScreen(new Point(source.ActualWidth, source.ActualHeight));
                return new Rect((first.X - rootOrigin.X) / dpiX - sampleWindow.X, (first.Y - rootOrigin.Y) / dpiY - sampleWindow.Y,
                    Math.Max(0, (last.X - first.X) / dpiX), Math.Max(0, (last.Y - first.Y) / dpiY));
            }
            var bounds = ToLocal(coordinateRoot); var area = new Rect(size);
            bool changed = geometryDirty || sourceSize != size || clipBounds != bounds || dpiChanged;
            foreach (var layer in layers)
            {
                if (ReferenceEquals(layer.Source, this) || layer.Source.IsAncestorOf(this) || IsAncestorOf(layer.Source) || layer.Source.IsAncestorOf(observer)) { Fallback("recursive-source-rejected"); return; }
                var full = layer.Source.IsVisible ? ToLocal(layer.Source) : Rect.Empty;
                var port = Rect.Intersect(Rect.Intersect(full, area), bounds); var view = Rect.Empty;
                if (!port.IsEmpty && port.Width > 0 && port.Height > 0 && full.Width > 0 && full.Height > 0)
                    view = new Rect((port.X - full.X) * layer.Source.ActualWidth / full.Width, (port.Y - full.Y) * layer.Source.ActualHeight / full.Height,
                        port.Width * layer.Source.ActualWidth / full.Width, port.Height * layer.Source.ActualHeight / full.Height);
                if (view != layer.View || port != layer.Port)
                {
                    changed = true; layer.View = view; layer.Port = port;
                    if (!view.IsEmpty && view.Width > 0 && view.Height > 0 && !port.IsEmpty && port.Width > 0 && port.Height > 0)
                    { layer.Brush.Viewbox = view; layer.Brush.Viewport = port; }
                }
            }
            if (changed)
            {
                sourceSize = size; clipBounds = bounds; geometryDirty = false;
                observer.Width = raster.Width = size.Width; observer.Height = raster.Height = size.Height;
                var drawing = new DrawingGroup();
                using (var context = drawing.Open())
                {
                    context.DrawRectangle(BaseBrush ?? SystemColors.WindowBrush, null, area);
                    context.PushClip(new RectangleGeometry(bounds));
                    foreach (var layer in layers)
                        if (!layer.View.IsEmpty && layer.View.Width > 0 && layer.View.Height > 0 && !layer.Port.IsEmpty && layer.Port.Width > 0 && layer.Port.Height > 0)
                            context.DrawRectangle(layer.Brush, null, layer.Port);
                    context.Pop();
                }
                observer.Drawing = drawing; observer.InvalidateVisual();
                using (var context = captureVisual.RenderOpen()) context.DrawDrawing(drawing);
                drawingRevision++; retryAfterMilliseconds = 0; InvalidateRaster(clear: true);
            }
            Background = raster.Source is null ? BaseBrush ?? SystemColors.WindowBrush : Brushes.Transparent;
            sampling.Visibility = Visibility.Visible;
            FilterAvailable = raster.Source is not null;
            if (!FilterAvailable && firstReady.Task.IsCompleted && !firstReady.Task.Result) firstReady = NewReady();
            FilterDiagnostic = FilterAvailable ? "live-full-resolution-gaussian-srgb" : "preparing-full-resolution-backdrop";
        }
        catch (InvalidOperationException) { Fallback("source-detached"); }
        finally { updatingGeometry = false; }
        if (sourceDirty) QueueCapture();
    }

    private void QueueCapture()
    {
        if (disposed || !listening || !CanProcess || capturing || updatingGeometry || processing is not null || observer.Drawing is null || clock.Elapsed.TotalMilliseconds < retryAfterMilliseconds) return;
        // Initial presentation is urgent. Subsequent live sampling runs after
        // normal WPF presentation, so an RTB does not preempt the menu's next
        // animation frame. There is still only one queued/in-flight capture.
        var priority = demanding || Presenting && !FilterAvailable ? DispatcherPriority.Render : DispatcherPriority.Background;
        if (captureOperation is { Status: DispatcherOperationStatus.Pending } pending)
        {
            if (pending.Priority < priority) { pending.Priority = priority; captureQueuePromotions++; }
            return;
        }
        if (!Presenting && !demanding)
        {
            // Hidden preparation is not a second render loop. Source changes
            // coalesce into at most one capture per second; real input intent
            // bypasses this budget and waits for a current source revision.
            double delay = Math.Max(0, lastCaptureAt + 1000 - clock.Elapsed.TotalMilliseconds);
            if (delay > 0)
            {
                preparationTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delay)); preparationTimer.Start(); return;
            }
        }
        long queued = Stopwatch.GetTimestamp();
        captureOperation = Dispatcher.BeginInvoke(priority, new Action(() =>
        {
            captureOperation = null;
            if (disposed || !listening || !CanProcess || processing is not null || !sourceDirty) return;
            capturing = true;
            long timingSession = 0, timingStarted = 0, timingRendered = 0, timingCopied = 0, timingGeneration = 0, timingRevision = 0;
            try
            {
                captureQueueMilliseconds = Stopwatch.GetElapsedTime(queued).TotalMilliseconds;
                UpdateGeometry();
                if (FilterDiagnostic is not ("preparing-full-resolution-backdrop" or "live-full-resolution-gaussian-srgb")) return;
                sourceDirty = false;
                long revision = sourceRevision;
                int width = checked((int)Math.Round(sourceSize.Width * dpiX)), height = checked((int)Math.Round(sourceSize.Height * dpiY));
                int stride = checked(width * 4), length = checked(stride * height);
                if (width <= 0 || height <= 0) return;
                long captureStarted = Stopwatch.GetTimestamp(); lastCaptureAt = clock.Elapsed.TotalMilliseconds;
                double sampleStartedAt = lastCaptureAt;
                timingSession = NativeMotionCapture.TimingSession;
                if (timingSession != 0) { timingStarted = captureStarted; timingGeneration = generation; timingRevision = revision; }
                var snapshot = new RenderTargetBitmap(width, height, dpiX * 96, dpiY * 96, PixelFormats.Pbgra32);
                try { snapshot.Render(captureVisual); }
                finally
                {
                    if (timingSession != 0) timingRendered = Stopwatch.GetTimestamp();
                    RearmObservers();
                }
                if (scratch?.Length != length) scratch = new byte[length];
                snapshot.CopyPixels(scratch, stride, 0); captures++;
                if (timingSession != 0) timingCopied = Stopwatch.GetTimestamp();
                captureMilliseconds = Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds;
                double sampleCompletedAt = clock.Elapsed.TotalMilliseconds;
                long compareStarted = Stopwatch.GetTimestamp();
                bool identical = baseline is not null && baseline.AsSpan().SequenceEqual(scratch);
                compareMilliseconds = Stopwatch.GetElapsedTime(compareStarted).TotalMilliseconds;
                if (identical) { identicalCaptures++; CompleteReady(revision, sampleStartedAt, sampleCompletedAt); return; }
                // Only these two byte buffers are retained. While one is being
                // processed no second capture can mutate it or build a queue.
                (baseline, scratch) = (scratch, baseline);
                var cancellation = new CancellationTokenSource(); processing = cancellation;
                _ = PublishAsync(baseline!, width, height, dpiX, dpiY, Sigma, Saturation, generation, revision, sampleStartedAt, sampleCompletedAt, cancellation);
            }
            catch (Exception error) when (error is COMException or NotSupportedException or InvalidOperationException or ArgumentException or OverflowException)
            { Fallback("capture-unavailable-" + error.GetType().Name); }
            finally
            {
                capturing = false;
                if (timingSession != 0)
                    NativeMotionCapture.BackdropCapture(timingSession, this, timingGeneration, timingRevision,
                        timingStarted, timingRendered, timingCopied, Stopwatch.GetTimestamp());
            }
        }));
    }

    private async Task PublishAsync(byte[] pixels, int width, int height, double scaleX, double scaleY, double sigma, double saturation, long version, long revision,
        double sampleStartedAt, double sampleCompletedAt, CancellationTokenSource cancellation)
    {
        try
        {
            long started = Stopwatch.GetTimestamp();
            var result = await FluentBackdropPixels.ApplyMeasuredAsync(pixels, width, height, sigma * scaleX, sigma * scaleY, saturation, cancellation.Token);
            filterMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            queueMilliseconds = result.QueueMilliseconds; schedulingMilliseconds = result.SchedulingMilliseconds;
            convolutionMilliseconds = result.ConvolutionMilliseconds;
            publishDispatchMilliseconds = Stopwatch.GetElapsedTime(result.CompletedAt).TotalMilliseconds;
            if (disposed || !listening || !CanProcess || version != generation || cancellation.IsCancellationRequested) { cancelledFrames++; return; }
            var bitmap = BitmapSource.Create(width, height, scaleX * 96, scaleY * 96, PixelFormats.Pbgra32, null, result.Pixels, width * 4);
            bitmap.Freeze(); raster.Source = bitmap; filteredFrames++;
            firstBitmapMilliseconds ??= clock.Elapsed.TotalMilliseconds;
            Background = Brushes.Transparent; FilterAvailable = true; FilterDiagnostic = "live-full-resolution-gaussian-srgb";
            CompleteReady(revision, sampleStartedAt, sampleCompletedAt);
        }
        catch (OperationCanceledException) { cancelledFrames++; }
        catch (Exception error) when (error is COMException or NotSupportedException or InvalidOperationException or ArgumentException or OverflowException)
        { if (!disposed && version == generation) Fallback("filter-unavailable-" + error.GetType().Name); }
        finally
        {
            if (ReferenceEquals(processing, cancellation)) processing = null;
            cancellation.Dispose();
            if (!disposed && sourceDirty) QueueCapture();
        }
    }

    private void CompleteReady(long revision, double sampleStartedAt, double sampleCompletedAt)
    {
        publishedSourceRevision = revision; publishedGeneration = generation;
        publishedCaptureStartedAtMilliseconds = sampleStartedAt; publishedCaptureCompletedAtMilliseconds = sampleCompletedAt;
        if (preparationEnabled) preparationReadyMilliseconds = clock.Elapsed.TotalMilliseconds - preparationStartedAt;
        // Readiness means a real sample captured after the current request (or
        // a newer page/geometry invalidation). It must not wait for a live chart
        // to stop changing: later dirty frames continue through the live queue.
        if (revision >= readyRevision) { CompleteDemand(); firstReady.TrySetResult(true); }
    }

    private void CompleteDemand()
    {
        if (demanding && demandStartedAt is { } started) demandMilliseconds = clock.Elapsed.TotalMilliseconds - started;
        demanding = false; demandStartedAt = null;
    }

    private void RearmObservers()
    {
        // RenderTargetBitmap releases its synchronous WPF channel. WPF clears
        // NodeIsCyclicBrushRoot when that channel reaches zero, even if a visual
        // still has observers on the normal presentation channel. Rebind all our
        // observers together so the last old reference is released before the
        // first new one restores the marker. These property changes are not
        // source changes and must never schedule another capture themselves.
        var active = new List<FluentBackdropFilter>();
        observerScope.Filters.RemoveAll(reference => !reference.TryGetTarget(out var filter) || filter.disposed);
        foreach (var reference in observerScope.Filters)
            if (reference.TryGetTarget(out var filter)) active.Add(filter);
        var watched = active.SelectMany(filter => filter.layers).ToArray();
        observerScope.Rearming = true;
        try
        {
            foreach (var layer in watched) layer.Brush.Visual = null;
        }
        finally
        {
            try { foreach (var layer in watched) layer.Brush.Visual = layer.Source; }
            finally { observerScope.Rearming = false; }
        }
        observerRearms++;
    }

    internal static void RestoreLiveObservers()
    {
        if (!ObserverScopes.TryGetValue(Dispatcher.CurrentDispatcher, out var scope)) return;
        foreach (var reference in scope.Filters)
            if (reference.TryGetTarget(out var filter) && !filter.disposed) { filter.RearmObservers(); return; }
    }

    private static Geometry Rounded(Size size, CornerRadius corners)
    {
        double width = size.Width, height = size.Height;
        double tl = Math.Max(0, corners.TopLeft), tr = Math.Max(0, corners.TopRight), br = Math.Max(0, corners.BottomRight), bl = Math.Max(0, corners.BottomLeft);
        double factor = 1;
        foreach (var (sum, edge) in new[] { (tl + tr, width), (bl + br, width), (tl + bl, height), (tr + br, height) }) if (sum > 0) factor = Math.Min(factor, edge / sum);
        tl *= factor; tr *= factor; br *= factor; bl *= factor;
        var shape = new StreamGeometry();
        using (var context = shape.Open())
        {
            void Corner(Point end, double radius)
            {
                if (radius <= 0) context.LineTo(end, true, false);
                else context.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            context.BeginFigure(new Point(tl, 0), true, true);
            context.LineTo(new Point(width - tr, 0), true, false); Corner(new Point(width, tr), tr);
            context.LineTo(new Point(width, height - br), true, false); Corner(new Point(width - br, height), br);
            context.LineTo(new Point(bl, height), true, false); Corner(new Point(0, height - bl), bl);
            context.LineTo(new Point(0, tl), true, false); Corner(new Point(tl, 0), tl);
        }
        shape.Freeze(); return shape;
    }

    private void ReleaseLayers()
    {
        foreach (var layer in layers) { layer.Brush.Changed -= SourceChanged; layer.Brush.Visual = null; }
        layers.Clear(); observer.Drawing = null;
        using (captureVisual.RenderOpen()) { }
    }
    public void Dispose()
    {
        if (disposed) return;
        Dispatcher.VerifyAccess(); disposed = true; StopListening();
        Loaded -= OnLoaded; Unloaded -= OnUnloaded; IsVisibleChanged -= OnVisibilityChanged; SizeChanged -= OnSizeChanged;
        preparationTimer.Tick -= PreparationDue;
        if (preparationHost is not null)
        {
            preparationHost.Loaded -= PreparationHostChanged; preparationHost.Unloaded -= PreparationHostChanged;
            preparationHost.IsVisibleChanged -= PreparationVisibilityChanged; preparationHost.SizeChanged -= PreparationSizeChanged;
            preparationHost.LayoutUpdated -= PreparationLayoutChanged;
            preparationHost.Children.Remove(preparationObserverHost); preparationObserverHost.Children.Clear(); preparationHost = null;
        }
        ReleaseLayers(); coordinateRoot = null; sampling.Children.Clear();
        FilterAvailable = false; FilterDiagnostic = "disposed";
    }
}
