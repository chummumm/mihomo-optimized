using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Mihomo.Desktop.Controls;

/// <summary>A local backdrop sampled only from explicitly supplied lower visual layers.</summary>
internal sealed class FluentBackdropFilter : Grid, IDisposable
{
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
    private readonly Border tint = new() { IsHitTestVisible = false };
    private readonly SourcePlane plane = new() { IsHitTestVisible = false };
    private readonly List<Layer> layers = [];
    private FrameworkElement? coordinateRoot;
    private Border? graph;
    private SaturationEffect? colorEffect;
    private bool listening, disposed, geometryDirty = true;
    private int passCount;
    private double passRadius = -1, padding, dpi = 1;
    private Rect clipBounds = Rect.Empty;
    private Size sourceSize;

    internal FluentBackdropFilter()
    {
        IsHitTestVisible = false; Focusable = false; ClipToBounds = true;
        Children.Add(sampling); Children.Add(tint);
        SetResourceReference(BaseBrushProperty, "AppBackgroundBrush");
        SetResourceReference(TintBrushProperty, "AcrylicBrush");
        Loaded += OnLoaded; Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibilityChanged; SizeChanged += OnSizeChanged;
    }

    /// <summary>
    /// The root defines the screen-space clipping viewport and is never implicitly sampled.
    /// Explicit sources may belong to different windows on this dispatcher, but cannot
    /// contain this filter or belong to its own output subtree.
    /// </summary>
    internal void SetSources(FrameworkElement root, params FrameworkElement[] sources)
    {
        Dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(root); ArgumentNullException.ThrowIfNull(sources);
        if (!ReferenceEquals(root.Dispatcher, Dispatcher))
            throw new ArgumentException("The backdrop viewport must use this dispatcher.", nameof(root));
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!ReferenceEquals(source.Dispatcher, Dispatcher))
                throw new ArgumentException("Backdrop layers must use this dispatcher.", nameof(sources));
            if (ReferenceEquals(source, this) || source.IsAncestorOf(this) || IsAncestorOf(source))
                throw new ArgumentException("A backdrop source cannot contain or belong to the filter.", nameof(sources));
        }
        foreach (var layer in layers) layer.Brush.Visual = null;
        layers.Clear(); coordinateRoot = root;
        foreach (var source in sources.Distinct()) layers.Add(new Layer(source));
        geometryDirty = true; UpdateSubscription();
    }

    internal object Describe() => new
    {
        available = FilterAvailable, diagnostic = FilterDiagnostic, sigma = Sigma, saturation = Saturation,
        gaussianPasses = passCount, radiusPerPass = Math.Max(0, passRadius), dpi = dpi * 96,
        sourceKind = "wpf-visual-layers", includesDesktopComposition = false,
        // WPF quantizes each device-space radius. Expose the effective value
        // instead of claiming exact CSS equivalence at every fractional DPI.
        effectiveSigma = passCount == 0 ? 0 : Math.Sqrt(passCount) * Math.Min(100, Math.Floor(Math.Floor(passRadius) * dpi)) / (3 * dpi),
        samplePadding = padding, sourceLayers = layers.Count,
    };

    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var filter = (FluentBackdropFilter)owner;
        filter.geometryDirty = true;
        filter.tint.Background = filter.TintBrush;
        filter.UpdateGeometry();
    }
    private void OnLoaded(object sender, RoutedEventArgs args) => UpdateSubscription();
    private void OnUnloaded(object sender, RoutedEventArgs args) => StopListening();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateSubscription();
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) { geometryDirty = true; UpdateGeometry(); }
    private void UpdateSubscription()
    {
        if (disposed || !IsLoaded || !IsVisible || coordinateRoot is null) { StopListening(); return; }
        if (!listening) { listening = true; CompositionTarget.Rendering += Rendering; }
        UpdateGeometry();
    }
    private void StopListening()
    {
        if (listening) { listening = false; CompositionTarget.Rendering -= Rendering; }
        sampling.Visibility = Visibility.Collapsed;
    }
    private void Rendering(object? sender, EventArgs args) => UpdateGeometry();
    private void Fallback(string reason)
    {
        FilterAvailable = false; FilterDiagnostic = reason;
        sampling.Visibility = Visibility.Collapsed; Background = BaseBrush ?? SystemColors.WindowBrush;
    }

    private void UpdateGeometry()
    {
        if (disposed || !IsLoaded || !IsVisible) return;
        tint.Background = TintBrush;
        if (geometryDirty && ActualWidth > 0 && ActualHeight > 0) Clip = Rounded(new Size(ActualWidth, ActualHeight), CornerRadius);
        if (coordinateRoot is null || layers.Count == 0 || PresentationSource.FromVisual(coordinateRoot) is null || PresentationSource.FromVisual(this) is null)
        { Fallback("source-not-attached"); return; }
        if (ActualWidth <= 0 || ActualHeight <= 0) { Fallback("surface-not-arranged"); return; }
        if (SystemParameters.HighContrast) { Fallback("high-contrast"); return; }
        var shader = SaturationEffect.Compiled;
        if (shader.Shader is null) { Fallback(shader.Diagnostic); return; }
        try
        {
            var scale = VisualTreeHelper.GetDpi(this);
            dpi = Math.Max(scale.DpiScaleX, scale.DpiScaleY);
            if (!double.IsFinite(dpi) || dpi is <= 0 or > 4) { Fallback("dpi-not-supported"); return; }
            // WPF's Gaussian uses sd = radius / 3 and clamps a scaled radius at
            // 100 physical pixels. Multiple passes add their variances, avoiding
            // a silently weaker blur on high-DPI monitors.
            int count = Sigma == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(Math.Pow(3 * Sigma * dpi / 99, 2)));
            double radius = count == 0 ? 0 : Math.Max(1, Math.Round(3 * Sigma / Math.Sqrt(count)));
            while (radius * dpi > 100) { count++; radius = Math.Max(1, Math.Round(3 * Sigma / Math.Sqrt(count))); }
            if (count != passCount || radius != passRadius || graph is null) BuildGraph(count, radius, shader.Shader);
            if (colorEffect is not null) colorEffect.Amount = Saturation;
            var size = new Size(ActualWidth + 2 * padding, ActualHeight + 2 * padding);
            var origin = PointToScreen(new Point());
            Rect ToLocal(FrameworkElement source)
            {
                var first = source.PointToScreen(new Point());
                var last = source.PointToScreen(new Point(source.ActualWidth, source.ActualHeight));
                return new Rect((first.X - origin.X) / scale.DpiScaleX + padding, (first.Y - origin.Y) / scale.DpiScaleY + padding,
                    Math.Max(0, (last.X - first.X) / scale.DpiScaleX), Math.Max(0, (last.Y - first.Y) / scale.DpiScaleY));
            }
            var bounds = ToLocal(coordinateRoot);
            bool changed = geometryDirty || sourceSize != size || clipBounds != bounds;
            foreach (var layer in layers)
            {
                if (ReferenceEquals(layer.Source, this) || layer.Source.IsAncestorOf(this) || IsAncestorOf(layer.Source)) { Fallback("recursive-source-rejected"); return; }
                var view = new Rect(0, 0, layer.Source.ActualWidth, layer.Source.ActualHeight);
                var port = layer.Source.IsVisible ? ToLocal(layer.Source) : Rect.Empty;
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
                if (graph is not null) { graph.Width = size.Width; graph.Height = size.Height; Canvas.SetLeft(graph, -padding); Canvas.SetTop(graph, -padding); }
                plane.Width = size.Width; plane.Height = size.Height;
                Clip = Rounded(new Size(ActualWidth, ActualHeight), CornerRadius);
                var drawing = new DrawingGroup();
                using (var context = drawing.Open())
                {
                    var area = new Rect(size);
                    context.DrawRectangle(BaseBrush ?? SystemColors.WindowBrush, null, area);
                    context.PushClip(new RectangleGeometry(bounds));
                    foreach (var layer in layers)
                        if (!layer.View.IsEmpty && layer.View.Width > 0 && layer.View.Height > 0 && !layer.Port.IsEmpty && layer.Port.Width > 0 && layer.Port.Height > 0)
                            context.DrawRectangle(layer.Brush, null, area);
                    context.Pop();
                }
                plane.Drawing = drawing; plane.InvalidateVisual();
            }
            Background = Brushes.Transparent; sampling.Visibility = Visibility.Visible;
            FilterAvailable = true; FilterDiagnostic = "live-visual-gaussian-srgb";
        }
        catch (InvalidOperationException) { Fallback("source-detached"); }
    }

    private void BuildGraph(int count, double radius, PixelShader shader)
    {
        if (plane.Parent is Border previous) previous.Child = null;
        sampling.Children.Clear(); UIElement next = plane;
        for (int index = 0; index < count; index++)
            next = new Border { Child = next, Effect = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality } };
        colorEffect = new SaturationEffect(shader) { Amount = Saturation };
        graph = new Border { Child = next, Effect = colorEffect };
        sampling.Children.Add(graph); passCount = count; passRadius = radius;
        padding = Math.Ceiling(count * radius); geometryDirty = true;
    }

    private static Geometry Rounded(Size size, CornerRadius corners)
    {
        double width = size.Width, height = size.Height;
        double tl = Math.Max(0, corners.TopLeft), tr = Math.Max(0, corners.TopRight), br = Math.Max(0, corners.BottomRight), bl = Math.Max(0, corners.BottomLeft);
        double factor = 1;
        foreach (var (sum, edge) in new[] { (tl + tr, width), (bl + br, width), (tl + bl, height), (tr + br, height) })
            if (sum > 0) factor = Math.Min(factor, edge / sum);
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

    public void Dispose()
    {
        if (disposed) return;
        Dispatcher.VerifyAccess(); disposed = true; StopListening();
        Loaded -= OnLoaded; Unloaded -= OnUnloaded; IsVisibleChanged -= OnVisibilityChanged; SizeChanged -= OnSizeChanged;
        foreach (var layer in layers) layer.Brush.Visual = null;
        layers.Clear(); coordinateRoot = null; plane.Drawing = null; sampling.Children.Clear(); graph = null;
        FilterAvailable = false; FilterDiagnostic = "disposed";
    }

    private sealed class SaturationEffect : ShaderEffect
    {
        internal sealed record Compilation(PixelShader? Shader, string Diagnostic);
        private static readonly Lazy<Compilation> compilation = new(Compile);
        internal static Compilation Compiled => compilation.Value;
        private static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(SaturationEffect), 0, SamplingMode.Bilinear);
        private static readonly DependencyProperty AmountProperty = DependencyProperty.Register(nameof(Amount), typeof(double), typeof(SaturationEffect), new UIPropertyMetadata(1.5d, PixelShaderConstantCallback(0)));
        internal double Amount { get => (double)GetValue(AmountProperty); set => SetValue(AmountProperty, value); }
        internal SaturationEffect(PixelShader shader)
        {
            PixelShader = shader; UpdateShaderValue(InputProperty); UpdateShaderValue(AmountProperty);
        }
        protected override Freezable CreateInstanceCore() => new SaturationEffect(PixelShader);
        private static Compilation Compile()
        {
            IBlob? code = null, errors = null;
            try
            {
                // CSS saturate() is an sRGB color matrix, including values above
                // one. Keep alpha premultiplied and clamp to the source alpha.
                const string source = "sampler2D input : register(s0); float amount : register(c0); float4 main(float2 uv : TEXCOORD) : COLOR { float4 c = tex2D(input, uv); float y = dot(c.rgb, float3(0.213, 0.715, 0.072)); c.rgb = clamp(y + amount * (c.rgb - y), 0, c.a); return c; }";
                var bytes = Encoding.ASCII.GetBytes(source);
                int status = D3DCompile(bytes, (nuint)bytes.Length, null, IntPtr.Zero, IntPtr.Zero, "main", "ps_2_0", 0, 0, out code, out errors);
                if (status < 0 || code is null) return new(null, "shader-compile-failed-" + status.ToString("X8"));
                nuint length = code.GetBufferSize();
                if (length is 0 or > 65536) return new(null, "shader-bytecode-invalid");
                var compiled = new byte[(int)length]; Marshal.Copy(code.GetBufferPointer(), compiled, 0, compiled.Length);
                var shader = new PixelShader(); using var stream = new MemoryStream(compiled, false); shader.SetStreamSource(stream); shader.Freeze();
                return new(shader, "available");
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or COMException or NotSupportedException or InvalidOperationException or IOException or ArgumentException)
            { return new(null, "shader-unavailable-" + error.GetType().Name); }
            finally
            {
                if (code is not null) Marshal.ReleaseComObject(code);
                if (errors is not null) Marshal.ReleaseComObject(errors);
            }
        }
        [ComImport, Guid("8BA5FB08-5195-40E2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IBlob
        {
            [PreserveSig] IntPtr GetBufferPointer();
            [PreserveSig] nuint GetBufferSize();
        }
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("d3dcompiler_47.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int D3DCompile(byte[] source, nuint length, string? name, IntPtr defines, IntPtr include, string entry, string profile, uint flags, uint effectFlags, out IBlob? code, out IBlob? errors);
    }
}
