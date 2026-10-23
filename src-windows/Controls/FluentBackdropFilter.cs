using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Mihomo.Desktop.Controls;

/// <summary>A full-resolution backdrop sampled only from explicitly supplied lower visual layers.</summary>
internal sealed class FluentBackdropFilter : Grid, IDisposable
{
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
    private FrameworkElement? coordinateRoot;
    private bool listening, disposed, geometryDirty = true, sourceDirty = true, captureQueued, capturing, updatingGeometry;
    private double dpiX = 1, dpiY = 1, padding;
    private Rect clipBounds = Rect.Empty, sampleWindow = Rect.Empty;
    private Size sourceSize, clipSize;
    private long generation;
    private byte[]? baseline, scratch;
    private CancellationTokenSource? processing;
    private TaskCompletionSource<bool> firstReady = NewReady();
    private int drawingRevision, sourceInvalidations, captures, identicalCaptures, filteredFrames, cancelledFrames;
    private double captureMilliseconds, compareMilliseconds, filterMilliseconds, retryAfterMilliseconds;
    private double? firstBitmapMilliseconds;
    private static TaskCompletionSource<bool> NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal FluentBackdropFilter()
    {
        IsHitTestVisible = false; Focusable = false; ClipToBounds = true;
        sampling.RenderTransform = sampleOffset;
        sampling.Children.Add(observer); sampling.Children.Add(raster);
        Children.Add(sampling); Children.Add(tint);
        SetResourceReference(BaseBrushProperty, "AppBackgroundBrush");
        SetResourceReference(TintBrushProperty, "AcrylicBrush");
        Loaded += OnLoaded; Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibilityChanged; SizeChanged += OnSizeChanged;
    }

    /// <summary>The viewport is never implicitly sampled; every lower layer must be explicit and non-recursive.</summary>
    internal void SetSources(FrameworkElement root, params FrameworkElement[] sources)
    {
        Dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(root); ArgumentNullException.ThrowIfNull(sources);
        if (!ReferenceEquals(root.Dispatcher, Dispatcher)) throw new ArgumentException("The backdrop viewport must use this dispatcher.", nameof(root));
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!ReferenceEquals(source.Dispatcher, Dispatcher)) throw new ArgumentException("Backdrop layers must use this dispatcher.", nameof(sources));
            if (ReferenceEquals(source, this) || source.IsAncestorOf(this) || IsAncestorOf(source))
                throw new ArgumentException("A backdrop source cannot contain or belong to the filter.", nameof(sources));
        }
        ReleaseLayers(); coordinateRoot = root; sampleWindow = Rect.Empty; geometryDirty = true;
        foreach (var source in sources.Distinct())
        {
            var layer = new Layer(source); layer.Brush.Changed += SourceChanged; layers.Add(layer);
        }
        firstReady.TrySetResult(false); firstReady = NewReady();
        InvalidateRaster(clear: true); UpdateSubscription();
    }

    internal async Task<bool> PrepareAsync()
    {
        Dispatcher.VerifyAccess();
        if (disposed || !IsLoaded || !IsVisible) return false;
        UpdateGeometry();
        if (raster.Source is not null) return true;
        if (coordinateRoot is null || layers.Count == 0 || SystemParameters.HighContrast) return false;
        QueueCapture();
        return await firstReady.Task;
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
        sourceInvalidations, captures, identicalCaptures, filteredFrames, cancelledFrames,
        captureMilliseconds, compareMilliseconds, filterMilliseconds, firstBitmapMilliseconds,
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
    private void OnUnloaded(object sender, RoutedEventArgs args) => StopListening();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateSubscription();
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => UpdateGeometry();
    private void SourceChanged(object? sender, EventArgs args)
    {
        if (disposed || updatingGeometry || capturing) return;
        sourceInvalidations++; sourceDirty = true; retryAfterMilliseconds = 0; QueueCapture();
    }
    private void UpdateSubscription()
    {
        if (disposed || !IsLoaded || !IsVisible || coordinateRoot is null) { StopListening(); return; }
        if (!listening) { listening = true; CompositionTarget.Rendering += Rendering; }
        if (firstReady.Task.IsCompleted && raster.Source is null) firstReady = NewReady();
        sourceDirty = true; UpdateGeometry();
    }
    private void StopListening()
    {
        if (listening) { listening = false; CompositionTarget.Rendering -= Rendering; }
        InvalidateRaster(clear: true); firstReady.TrySetResult(false);
        sampling.Visibility = Visibility.Collapsed;
    }
    private void Rendering(object? sender, EventArgs args) => UpdateGeometry();
    private void InvalidateRaster(bool clear)
    {
        generation++; processing?.Cancel(); baseline = scratch = null; sourceDirty = true;
        if (clear)
        {
            raster.Source = null; FilterAvailable = false;
            if (firstReady.Task.IsCompleted) firstReady = NewReady();
        }
    }
    private void Fallback(string reason)
    {
        InvalidateRaster(clear: true);
        FilterAvailable = false; FilterDiagnostic = reason;
        sampling.Visibility = Visibility.Collapsed; Background = BaseBrush ?? SystemColors.WindowBrush;
        if (reason.StartsWith("capture-unavailable-", StringComparison.Ordinal) || reason.StartsWith("filter-unavailable-", StringComparison.Ordinal))
            retryAfterMilliseconds = clock.Elapsed.TotalMilliseconds + 500;
        firstReady.TrySetResult(false);
    }

    private void UpdateGeometry()
    {
        if (disposed || !IsLoaded || !IsVisible || updatingGeometry) return;
        tint.Background = TintBrush;
        var actual = new Size(ActualWidth, ActualHeight);
        if (actual != clipSize && actual.Width > 0 && actual.Height > 0) { Clip = Rounded(actual, CornerRadius); clipSize = actual; }
        if (coordinateRoot is null || layers.Count == 0 || PresentationSource.FromVisual(coordinateRoot) is null || PresentationSource.FromVisual(this) is null)
        { Fallback("source-not-attached"); return; }
        if (actual.Width <= 0 || actual.Height <= 0) { Fallback("surface-not-arranged"); return; }
        if (SystemParameters.HighContrast) { Fallback("high-contrast"); return; }
        updatingGeometry = true;
        try
        {
            var scale = VisualTreeHelper.GetDpi(this);
            bool dpiChanged = dpiX != scale.DpiScaleX || dpiY != scale.DpiScaleY;
            dpiX = scale.DpiScaleX; dpiY = scale.DpiScaleY;
            if (!double.IsFinite(dpiX) || !double.IsFinite(dpiY) || dpiX is <= 0 or > 4 || dpiY is <= 0 or > 4) { Fallback("dpi-not-supported"); return; }
            double padX = Math.Ceiling(3 * Sigma * dpiX) / dpiX, padY = Math.Ceiling(3 * Sigma * dpiY) / dpiY;
            padding = Math.Max(padX, padY);
            var origin = PointToScreen(new Point()); var rootOrigin = coordinateRoot.PointToScreen(new Point());
            var viewport = new Rect((origin.X - rootOrigin.X) / dpiX, (origin.Y - rootOrigin.Y) / dpiY, ActualWidth, ActualHeight);
            var required = viewport; required.Inflate(padX, padY);
            if (sampleWindow.IsEmpty || !sampleWindow.Contains(required) || dpiChanged)
            {
                // Reserve the complete Gaussian support plus the entrance path.
                // Align to physical pixels without shrinking or downsampling.
                var margin = MotionOverscan;
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
                if (ReferenceEquals(layer.Source, this) || layer.Source.IsAncestorOf(this) || IsAncestorOf(layer.Source)) { Fallback("recursive-source-rejected"); return; }
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
        if (disposed || !listening || !IsLoaded || !IsVisible || capturing || updatingGeometry || captureQueued || processing is not null || observer.Drawing is null || clock.Elapsed.TotalMilliseconds < retryAfterMilliseconds) return;
        captureQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            captureQueued = false;
            if (disposed || !listening || !IsVisible || processing is not null || !sourceDirty) return;
            capturing = true;
            try
            {
                UpdateGeometry();
                if (FilterDiagnostic is not ("preparing-full-resolution-backdrop" or "live-full-resolution-gaussian-srgb")) return;
                sourceDirty = false;
                int width = checked((int)Math.Round(sourceSize.Width * dpiX)), height = checked((int)Math.Round(sourceSize.Height * dpiY));
                int stride = checked(width * 4), length = checked(stride * height);
                if (width <= 0 || height <= 0) return;
                long captureStarted = Stopwatch.GetTimestamp();
                var snapshot = new RenderTargetBitmap(width, height, dpiX * 96, dpiY * 96, PixelFormats.Pbgra32);
                snapshot.Render(captureVisual);
                if (scratch?.Length != length) scratch = new byte[length];
                snapshot.CopyPixels(scratch, stride, 0); captures++;
                captureMilliseconds = Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds;
                long compareStarted = Stopwatch.GetTimestamp();
                bool identical = baseline is not null && baseline.AsSpan().SequenceEqual(scratch);
                compareMilliseconds = Stopwatch.GetElapsedTime(compareStarted).TotalMilliseconds;
                if (identical) { identicalCaptures++; return; }
                // Only these two byte buffers are retained. While one is being
                // processed no second capture can mutate it or build a queue.
                (baseline, scratch) = (scratch, baseline);
                var cancellation = new CancellationTokenSource(); processing = cancellation;
                _ = PublishAsync(baseline!, width, height, dpiX, dpiY, Sigma, Saturation, generation, cancellation);
            }
            catch (Exception error) when (error is COMException or NotSupportedException or InvalidOperationException or ArgumentException or OverflowException)
            { Fallback("capture-unavailable-" + error.GetType().Name); }
            finally { capturing = false; }
        }));
    }

    private async Task PublishAsync(byte[] pixels, int width, int height, double scaleX, double scaleY, double sigma, double saturation, long version, CancellationTokenSource cancellation)
    {
        try
        {
            long started = Stopwatch.GetTimestamp();
            byte[] filtered = await FluentBackdropPixels.ApplyAsync(pixels, width, height, sigma * scaleX, sigma * scaleY, saturation, cancellation.Token);
            filterMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (disposed || !listening || !IsVisible || version != generation || cancellation.IsCancellationRequested) { cancelledFrames++; return; }
            var bitmap = BitmapSource.Create(width, height, scaleX * 96, scaleY * 96, PixelFormats.Pbgra32, null, filtered, width * 4);
            bitmap.Freeze(); raster.Source = bitmap; filteredFrames++;
            firstBitmapMilliseconds ??= clock.Elapsed.TotalMilliseconds;
            Background = Brushes.Transparent; FilterAvailable = true; FilterDiagnostic = "live-full-resolution-gaussian-srgb";
            firstReady.TrySetResult(true);
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
        ReleaseLayers(); coordinateRoot = null; sampling.Children.Clear();
        FilterAvailable = false; FilterDiagnostic = "disposed";
    }
}
