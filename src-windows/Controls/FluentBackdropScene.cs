using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Mihomo.Desktop.Controls;

/// <summary>Copies retained drawing state on its owner, then rasterizes only that immutable state on one STA.</summary>
internal static class FluentBackdropScene
{
    internal readonly record struct Layer(Visual Source, Rect Viewbox, Rect Viewport);
    internal readonly record struct Result(long RenderStartedAt, long RenderedAt, long CopiedAt);

    internal sealed class Scene
    {
        private readonly Brush background;
        private readonly Size size;
        private readonly Rect bounds;
        private readonly FrozenLayer[] layers;

        internal Scene(Brush background, Size size, Rect bounds, IReadOnlyList<Layer> layers)
        {
            var capture = new Capture();
            this.background = capture.Freeze(background)!;
            this.size = size; this.bounds = bounds;
            var captured = new List<FrozenLayer>(layers.Count);
            foreach (var layer in layers)
            {
                if (!Finite(layer.Viewbox) || !Finite(layer.Viewport))
                    throw new UnsupportedSceneException("invalid-layer-geometry");
                if (layer.Viewbox.IsEmpty || layer.Viewport.IsEmpty) continue;
                if (layer.Source is null) throw new UnsupportedSceneException("missing-layer-source");
                layer.Source.Dispatcher.VerifyAccess();
                captured.Add(new(capture.Read(layer.Source, 0), layer.Viewbox, layer.Viewport));
            }
            this.layers = captured.ToArray();
        }

        internal Result Render(int width, int height, double dpiX, double dpiY, byte[] pixels,
            int stride, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            long startedAt = Stopwatch.GetTimestamp();
            var visual = new DrawingVisual();
            var brushes = new List<VisualBrush>(layers.Length);
            try
            {
                using (DrawingContext context = visual.RenderOpen())
                {
                    context.DrawRectangle(background, null, new Rect(size));
                    context.PushClip(new RectangleGeometry(bounds));
                    foreach (var layer in layers)
                    {
                        var brush = new VisualBrush(Restore(layer.Source, token))
                        {
                            AutoLayoutContent = false,
                            ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute,
                            Viewbox = layer.Viewbox, Viewport = layer.Viewport,
                            Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, TileMode = TileMode.None,
                        };
                        brushes.Add(brush);
                        context.DrawRectangle(brush, null, layer.Viewport);
                    }
                    context.Pop();
                }
                token.ThrowIfCancellationRequested();
                var bitmap = new RenderTargetBitmap(width, height, dpiX, dpiY, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                long renderedAt = Stopwatch.GetTimestamp();
                token.ThrowIfCancellationRequested();
                bitmap.CopyPixels(pixels, stride, 0);
                long copiedAt = Stopwatch.GetTimestamp();
                token.ThrowIfCancellationRequested();
                return new(startedAt, renderedAt, copiedAt);
            }
            finally
            {
                // Only worker-owned visuals enter the synchronous render channel. Clear its cyclic brush
                // roots promptly; original UI observers never disconnect or need rearming here.
                foreach (var brush in brushes) brush.Visual = null;
                using DrawingContext context = visual.RenderOpen();
            }
        }
    }

    private sealed record FrozenLayer(Node Source, Rect Viewbox, Rect Viewport);
    private sealed record Node(DrawingGroup? Drawing, Node[] Children, Vector Offset, Transform? Transform,
        Geometry? Clip, double Opacity, Brush? OpacityMask, Effect? Effect, CacheMode? CacheMode,
        DoubleCollection? GuidelinesX, DoubleCollection? GuidelinesY, Rect? ScrollableAreaClip,
        EdgeMode EdgeMode, BitmapScalingMode BitmapScalingMode, ClearTypeHint ClearTypeHint,
        TextRenderingMode TextRenderingMode, TextHintingMode TextHintingMode);

    private sealed class UnsupportedSceneException(string reason) : Exception(reason);

    /// <summary>
    /// GetDrawing returns only the node's content. Preserve the visual hierarchy as well as its local
    /// properties: flattening opacity, or reapplying a UIElement's layout properties, changes pixels.
    /// </summary>
    internal static bool TryCapture(Brush background, Size size, Rect bounds, IReadOnlyList<Layer> layers,
        out Scene? scene, out string? reason)
    {
        scene = null; reason = null;
        if (background is null || layers is null || !double.IsFinite(size.Width) || !double.IsFinite(size.Height)
            || size.Width <= 0 || size.Height <= 0 || !Finite(bounds) || layers.Count > 32)
        {
            reason = "invalid-scene-geometry"; return false;
        }
        try
        {
            scene = new Scene(background, size, bounds, layers);
            return true;
        }
        catch (UnsupportedSceneException error) { reason = error.Message; }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or ArgumentException
            or OverflowException or TypeInitializationException or System.Runtime.InteropServices.COMException)
        {
            reason = error.InnerException is UnsupportedSceneException unsupported
                ? unsupported.Message : "snapshot-" + error.GetType().Name;
        }
        return false;
    }

    private static bool Finite(Rect value) => value.IsEmpty || (double.IsFinite(value.X) && double.IsFinite(value.Y)
        && double.IsFinite(value.Width) && double.IsFinite(value.Height));

    private sealed class Capture
    {
        private const int MaximumVisuals = 16384, MaximumResources = 65536, MaximumDepth = 128;
        private readonly Dictionary<Freezable, Freezable> frozen = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Freezable> checkedResources = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Freezable> checkingResources = new(ReferenceEqualityComparer.Instance);
        private int visualCount, resourceCount;

        internal Node Read(Visual source, int depth)
        {
            source.Dispatcher.VerifyAccess();
            if (++visualCount > MaximumVisuals || depth > MaximumDepth)
                throw new UnsupportedSceneException("visual-tree-limit");
            if (source is Viewport3DVisual or HwndHost)
                throw new UnsupportedSceneException("unsupported-visual-" + source.GetType().Name);

            var offset = VisualTreeHelper.GetOffset(source);
            double opacity = VisualTreeHelper.GetOpacity(source);
            if (!double.IsFinite(offset.X) || !double.IsFinite(offset.Y) || !double.IsFinite(opacity))
                throw new UnsupportedSceneException("invalid-visual-state");
            var transform = Freeze(VisualTreeHelper.GetTransform(source));
            var clip = Freeze(VisualTreeHelper.GetClip(source));
            var effect = Freeze(VisualTreeHelper.GetEffect(source));
#pragma warning disable CS0618, WPF0001
            if (VisualTreeHelper.GetBitmapEffect(source) is not null || VisualTreeHelper.GetBitmapEffectInput(source) is not null)
                throw new UnsupportedSceneException("legacy-bitmap-effect");
#pragma warning restore CS0618, WPF0001
            var scrollClip = VisualState.ScrollableAreaClip(source);
            if (scrollClip is Rect rectangle && !Finite(rectangle))
                throw new UnsupportedSceneException("invalid-scrollable-clip");

            DrawingGroup? drawing;
            Node[] children;
            Brush? mask;
            if (opacity == 0)
            {
                // IsVisible is not sufficient: an unattached VisualBrush source may legitimately draw.
                // A zero-opacity branch has no pixels, but its bounds still participate in parent masks
                // and brush bounds. Keep those bounds without copying its live observation brushes.
                Rect contentBounds = VisualTreeHelper.GetDescendantBounds(source);
                if (!Finite(contentBounds)) throw new UnsupportedSceneException("nonfinite-invisible-bounds");
                drawing = null;
                if (!contentBounds.IsEmpty)
                {
                    drawing = new DrawingGroup();
                    using (DrawingContext context = drawing.Open())
                        context.DrawRectangle(Brushes.Transparent, null, contentBounds);
                    drawing.Freeze();
                }
                children = []; mask = null;
            }
            else
            {
                drawing = Freeze(VisualTreeHelper.GetDrawing(source));
                mask = Freeze(VisualTreeHelper.GetOpacityMask(source));
                int count = VisualTreeHelper.GetChildrenCount(source);
                if (count > MaximumVisuals - visualCount) throw new UnsupportedSceneException("visual-tree-limit");
                children = new Node[count];
                for (int index = 0; index < count; index++)
                {
                    if (VisualTreeHelper.GetChild(source, index) is not Visual child)
                        throw new UnsupportedSceneException("visual-3d-child");
                    children[index] = Read(child, depth + 1);
                }
            }
            return new(drawing, children, offset, transform, clip, opacity, mask, effect,
                Freeze(VisualTreeHelper.GetCacheMode(source)), Freeze(VisualTreeHelper.GetXSnappingGuidelines(source)),
                Freeze(VisualTreeHelper.GetYSnappingGuidelines(source)), scrollClip, VisualTreeHelper.GetEdgeMode(source),
                VisualState.BitmapScalingMode(source), VisualState.ClearTypeHint(source),
                VisualState.TextRenderingMode(source), VisualState.TextHintingMode(source));
        }

        internal T? Freeze<T>(T? resource) where T : Freezable
        {
            if (resource is null) return null;
            if (frozen.TryGetValue(resource, out var previous)) return (T)previous;
            CheckResource(resource, 0);
            // This captures current animation values and shares already frozen descendants. In particular,
            // it does not copy every pixel of immutable BitmapSources again on the UI thread.
            var copy = (T)resource.GetCurrentValueAsFrozen();
            if (!copy.IsFrozen) throw new UnsupportedSceneException("resource-not-frozen");
            frozen.Add(resource, copy);
            return copy;
        }

        private void CheckResource(Freezable resource, int depth)
        {
            if (checkedResources.Contains(resource)) return;
            if (depth > MaximumDepth || ++resourceCount > MaximumResources)
                throw new UnsupportedSceneException("drawing-resource-limit");
            if (resource is VisualBrush or BitmapCacheBrush)
                throw new UnsupportedSceneException("drawing-live-visual-brush");
            if (resource is VideoDrawing or D3DImage)
                throw new UnsupportedSceneException("drawing-live-media");
            if (!checkingResources.Add(resource)) throw new UnsupportedSceneException("drawing-resource-cycle");
            try
            {
                // Collections store children outside dependency properties. Value collections (points,
                // doubles, etc.) need no recursion; all reference resources still pass the freeze contract.
                if (resource is IEnumerable collection && resource is not DoubleCollection and not PointCollection
                    and not VectorCollection and not Int32Collection)
                    foreach (object? item in collection) CheckValue(item, depth + 1);
                var values = resource.GetLocalValueEnumerator();
                while (values.MoveNext()) CheckValue(resource.GetValue(values.Current.Property), depth + 1);
                checkedResources.Add(resource);
            }
            finally { checkingResources.Remove(resource); }
        }

        private void CheckValue(object? value, int depth)
        {
            if (value is Freezable child) CheckResource(child, depth);
            else if (value is DispatcherObject)
                throw new UnsupportedSceneException("drawing-thread-affine-resource");
        }
    }

    // The public attached properties are not the actual retained state for every Visual subclass.
    // These documented protected Visual properties are read by cached delegates, then set by our
    // DrawingVisual subclass. Binding failure is an explicit synchronous-path fallback, never a default.
    private static class VisualState
    {
        internal static readonly Func<Visual, Rect?> ScrollableAreaClip = Getter<Rect?>("VisualScrollableAreaClip");
        internal static readonly Func<Visual, BitmapScalingMode> BitmapScalingMode = Getter<BitmapScalingMode>("VisualBitmapScalingMode");
        internal static readonly Func<Visual, ClearTypeHint> ClearTypeHint = Getter<ClearTypeHint>("VisualClearTypeHint");
        internal static readonly Func<Visual, TextRenderingMode> TextRenderingMode = Getter<TextRenderingMode>("VisualTextRenderingMode");
        internal static readonly Func<Visual, TextHintingMode> TextHintingMode = Getter<TextHintingMode>("VisualTextHintingMode");

        private static Func<Visual, T> Getter<T>(string name)
        {
            var getter = typeof(Visual).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetMethod;
            if (getter is null) throw new UnsupportedSceneException("visual-state-api-unavailable-" + name);
            return getter.CreateDelegate<Func<Visual, T>>();
        }
    }

    private sealed class SnapshotVisual : DrawingVisual
    {
        internal SnapshotVisual(Node state)
        {
            Offset = state.Offset; Transform = state.Transform; Clip = state.Clip; Opacity = state.Opacity;
            OpacityMask = state.OpacityMask; Effect = state.Effect; CacheMode = state.CacheMode;
            XSnappingGuidelines = state.GuidelinesX; YSnappingGuidelines = state.GuidelinesY;
            VisualScrollableAreaClip = state.ScrollableAreaClip; VisualEdgeMode = state.EdgeMode;
            VisualBitmapScalingMode = state.BitmapScalingMode; VisualClearTypeHint = state.ClearTypeHint;
            VisualTextRenderingMode = state.TextRenderingMode; VisualTextHintingMode = state.TextHintingMode;
            if (state.Drawing is not null)
            {
                using DrawingContext context = RenderOpen();
                context.DrawDrawing(state.Drawing);
            }
        }
    }

    private static SnapshotVisual Restore(Node node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = new SnapshotVisual(node);
        foreach (var child in node.Children) result.Children.Add(Restore(child, token));
        return result;
    }

    private sealed class Worker
    {
        internal readonly TaskCompletionSource<Dispatcher> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ThreadId;
        internal string? Failure;
        internal Worker()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "Mihomo backdrop raster" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        private void Run()
        {
            try
            {
                Volatile.Write(ref ThreadId, Environment.CurrentManagedThreadId);
                Ready.TrySetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception error)
            {
                Volatile.Write(ref Failure, error.GetType().Name);
                Ready.TrySetException(error);
            }
        }
    }

    private static readonly Lazy<Worker> RasterWorker = new(() => new Worker());
    private static int pending, completed, cancelled, failed;

    internal static void Prewarm() => _ = RasterWorker.Value.Ready.Task;
    internal static object DescribeWorker() => new
    {
        started = RasterWorker.IsValueCreated,
        threadId = RasterWorker.IsValueCreated ? Volatile.Read(ref RasterWorker.Value.ThreadId) : 0,
        ready = RasterWorker.IsValueCreated && RasterWorker.Value.Ready.Task.IsCompletedSuccessfully,
        failure = RasterWorker.IsValueCreated ? Volatile.Read(ref RasterWorker.Value.Failure) : null,
        pending = Volatile.Read(ref pending), completed = Volatile.Read(ref completed),
        cancelled = Volatile.Read(ref cancelled), failed = Volatile.Read(ref failed),
    };

    /// <summary>The caller must retain exclusive buffer ownership until this task settles, including cancellation.</summary>
    internal static async Task<Result> RenderAsync(Scene scene, int width, int height, double dpiX, double dpiY,
        byte[] pixels, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || !double.IsFinite(dpiX) || !double.IsFinite(dpiY) || dpiX <= 0 || dpiY <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Raster dimensions and DPI must be positive and finite.");
        int stride = checked(width * 4);
        if (pixels.Length < checked(stride * height)) throw new ArgumentException("Raster buffer is too small.", nameof(pixels));
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref pending);
        try
        {
            var dispatcher = await RasterWorker.Value.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var result = await dispatcher.InvokeAsync(() => scene.Render(width, height, dpiX, dpiY, pixels, stride,
                cancellationToken), DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref completed);
            return result;
        }
        catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
        catch { Interlocked.Increment(ref failed); throw; }
        finally { Interlocked.Decrement(ref pending); }
    }

}
