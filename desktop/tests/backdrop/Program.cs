using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Mihomo.Desktop.Controls;

internal static class Program
{
    private static readonly Size Size = new(288, 216);
    private static readonly Rect Bounds = new(5, 4, 270, 198);
    private static readonly List<string> Report = [];
    private const string EvidenceDirectory = "artifacts/verification/backdrop-contract";

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            int exit = 0;
            try { await VerifyAsync().WaitAsync(TimeSpan.FromSeconds(45)); Log("PASS: native backdrop snapshot pixel contracts."); }
            catch (Exception error) { Log(error.ToString()); exit = 1; }
            Directory.CreateDirectory(EvidenceDirectory); File.WriteAllLines(Path.Combine(EvidenceDirectory, "report.txt"), Report);
            File.WriteAllText(Path.Combine(EvidenceDirectory, "report.json"), JsonSerializer.Serialize(new { passed = exit == 0, dpi = new[] { 96, 120, 144 }, pixelTolerance = 0, results = Report }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(exit);
        };
        return app.Run();
    }

    private static async Task VerifyAsync()
    {
        foreach (double dpi in new[] { 96d, 120d, 144d })
        {
            var background = new SolidColorBrush(Color.FromArgb(210, 242, 247, 252));
            var ink = new SolidColorBrush(Color.FromRgb(28, 110, 218));
            var root = Fixture(ink, dpi / 96);
            var overlay = new Border { Width = 60, Height = 45, CornerRadius = new(8), Background = new SolidColorBrush(Color.FromArgb(145, 240, 60, 80)), Opacity = .71 };
            Layout(overlay);
            var layers = new[] {
                new FluentBackdropScene.Layer(root, new Rect(7.5, 6.25, 220, 160), new Rect(12.25, 9.5, 247.5, 180)),
                new FluentBackdropScene.Layer(overlay, new Rect(0, 0, 60, 45), new Rect(190, 120, 60, 45)),
                new FluentBackdropScene.Layer(new ScrollClipVisual(), new Rect(0, 0, 64, 48), new Rect(30, 150, 64, 48))
            };
            byte[] expected = Original(background, layers, dpi);
            Require(expected.Distinct().Count() > 32, "The reference fixture did not draw meaningful content.");
            var scene = Capture(background, layers);
            Equal(expected, await Worker(scene, dpi), dpi, $"composite-{dpi}");

            // Modify live resources, visual properties and layout after capture.
            ink.Color = Colors.LimeGreen; background.Color = Colors.Gold;
            root.Children[0].Opacity = .36; Canvas.SetLeft(root.Children[0], 39.75);
            overlay.CornerRadius = new(1); Layout(root); Layout(overlay);
            Equal(expected, await Worker(scene, dpi), dpi, $"immutable-{dpi}");
            byte[] changed = Original(background, layers, dpi);
            Require(!expected.AsSpan().SequenceEqual(changed), "The live mutation did not alter the original render.");
            Equal(changed, await Worker(Capture(background, layers), dpi), dpi, $"recapture-{dpi}");
            Log($"PASS {dpi} DPI: nested offset/transform/clip/opacity/mask/effect/glyph/bitmap, scroll clip, ordered layers, immutable snapshot and live recapture.");

            if (dpi == 96)
            {
                using var before = new CancellationTokenSource(); before.Cancel();
                await Canceled(() => Worker(scene, dpi, before.Token), "before submission");
                using var submitted = new CancellationTokenSource();
                var pending = FluentBackdropScene.RenderAsync(scene, 288, 216, dpi, dpi, new byte[288 * 216 * 4], submitted.Token);
                bool completedBeforeCancel = pending.IsCompleted;
                long cancelStarted = Stopwatch.GetTimestamp();
                submitted.Cancel();
                try {
                    var completed = await pending;
                    Require(completed.CopiedAt <= cancelStarted, "Capture completed after cancellation but was published.");
                    Log($"INFO: raster copy finished before cancellation; task completion already observed: {completedBeforeCancel}; no running-cancellation claim.");
                } catch (OperationCanceledException) { Log("PASS cancellation after submission (RTB itself need not be interruptible)."); }
                Equal(expected, await Worker(scene, dpi), dpi, "after-cancellation");
            }
        }
        var nested = new DrawingVisual();
        using (var draw = nested.RenderOpen()) draw.DrawRectangle(new VisualBrush(Fixture(Brushes.Blue, 1)), null, new Rect(0, 0, 100, 80));
        Unsupported(nested, "nested VisualBrush");
        nested.Opacity = 0;
        var hidden = new[] { new FluentBackdropScene.Layer(nested, new Rect(0, 0, 100, 80), new Rect(0, 0, 100, 80)) };
        Equal(Original(Brushes.White, hidden, 96), await Worker(Capture(Brushes.White, hidden), 96), 96, "hidden-observer");
        var threeD = new ContainerVisual(); threeD.Children.Add(new Viewport3DVisual());
        Unsupported(threeD, "3D child");
    }

    private sealed class ScrollClipVisual : DrawingVisual
    {
        internal ScrollClipVisual()
        {
            VisualScrollableAreaClip = new Rect(12.5, 8, 32, 24);
            using var draw = RenderOpen(); draw.DrawRectangle(Brushes.Violet, null, new Rect(0, 0, 64, 48));
            draw.DrawLine(new Pen(Brushes.White, 2), new Point(0, 0), new Point(64, 48));
        }
    }

    private static Canvas Fixture(Brush ink, double pixelsPerDip)
    {
        var root = new Canvas { Width = 240, Height = 180, Background = Brushes.Transparent, ClipToBounds = true };
        var transform = new TransformGroup(); transform.Children.Add(new ScaleTransform(.93, 1.04)); transform.Children.Add(new RotateTransform(4, 80, 60));
        var panel = new Canvas {
            Width = 210, Height = 145, Opacity = .79, RenderTransform = transform,
            Clip = new RectangleGeometry(new Rect(4.25, 2, 195, 135), 9, 9),
            OpacityMask = new LinearGradientBrush(Colors.White, Color.FromArgb(70, 255, 255, 255), 15)
        };
        Canvas.SetLeft(panel, 14.25); Canvas.SetTop(panel, 11.5); root.Children.Add(panel);
        panel.Children.Add(new Border { Width = 185, Height = 125, CornerRadius = new(11), Background = Brushes.LightCyan, BorderBrush = Brushes.Navy, BorderThickness = new(1.25) });
        var paint = new Ink(ink, pixelsPerDip) {
            Width = 176, Height = 98, SnapsToDevicePixels = true,
            Effect = new BlurEffect { Radius = 1.75, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality }
        };
        Canvas.SetLeft(paint, 9.5); Canvas.SetTop(paint, 8.25); panel.Children.Add(paint);
        RenderOptions.SetBitmapScalingMode(paint, BitmapScalingMode.HighQuality);
        TextOptions.SetTextFormattingMode(paint, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(paint, TextRenderingMode.Grayscale);
        // Real UI visibility must survive retained drawings from an earlier visible frame.
        var hidden = new Border { Width = 220, Height = 160, Background = Brushes.Magenta };
        var collapsed = new Border { Width = 210, Height = 150, Background = Brushes.Lime };
        root.Children.Add(hidden); root.Children.Add(collapsed); Layout(root);
        new RenderTargetBitmap(240, 180, 96, 96, PixelFormats.Pbgra32).Render(root);
        hidden.Visibility = Visibility.Hidden; collapsed.Visibility = Visibility.Collapsed;
        Layout(root); return root;
    }

    private sealed class Ink(Brush ink, double pixelsPerDip) : FrameworkElement
    {
        protected override void OnRender(DrawingContext draw)
        {
            draw.DrawRoundedRectangle(ink, new Pen(Brushes.White, 1.25), new Rect(2, 3, 168, 88), 7, 7);
            draw.PushClip(new EllipseGeometry(new Point(75, 46), 67, 38));
            draw.DrawLine(new Pen(Brushes.OrangeRed, 3.5), new Point(5, 86), new Point(165, 9));
            draw.DrawText(new FormattedText("Mihomo 代理", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 18, Brushes.Black, pixelsPerDip), new Point(8, 18));
            byte[] pixels = [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 0, 255, 0, 255, 255, 255, 255, 0, 255, 255];
            draw.DrawImage(BitmapSource.Create(3, 2, 96, 96, PixelFormats.Pbgra32, null, pixels, 12), new Rect(20, 53, 39, 25));
            draw.Pop();
        }
    }

    private static void Layout(FrameworkElement source)
    {
        source.Measure(new Size(source.Width, source.Height));
        source.Arrange(new Rect(0, 0, source.Width, source.Height)); source.UpdateLayout();
    }

    private static FluentBackdropScene.Scene Capture(Brush background, IReadOnlyList<FluentBackdropScene.Layer> layers)
    {
        Require(FluentBackdropScene.TryCapture(background, Size, Bounds, layers, out var scene, out string? reason), $"Supported scene rejected: {reason}");
        return scene ?? throw new Exception("Successful capture returned no scene.");
    }

    // Independent baseline: the existing UI-thread VisualBrush + RTB path.
    // No snapshot traversal, property copying or worker reconstruction is used.
    private static byte[] Original(Brush background, IReadOnlyList<FluentBackdropScene.Layer> layers, double dpi)
    {
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(background, null, new Rect(Size)); draw.PushClip(new RectangleGeometry(Bounds));
            foreach (var layer in layers)
                draw.DrawRectangle(new VisualBrush(layer.Source) {
                    AutoLayoutContent = false, ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute,
                    Viewbox = layer.Viewbox, Viewport = layer.Viewport, Stretch = Stretch.Fill,
                    AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, TileMode = TileMode.None
                }, null, layer.Viewport);
            draw.Pop();
        }
        int width = Pixels(Size.Width, dpi), height = Pixels(Size.Height, dpi);
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0); return pixels;
    }

    private static async Task<byte[]> Worker(FluentBackdropScene.Scene scene, double dpi, CancellationToken cancellation = default)
    {
        int width = Pixels(Size.Width, dpi), height = Pixels(Size.Height, dpi);
        var pixels = new byte[width * height * 4];
        var timing = await FluentBackdropScene.RenderAsync(scene, width, height, dpi, dpi, pixels, cancellation);
        Require(timing.RenderedAt >= timing.RenderStartedAt && timing.CopiedAt >= timing.RenderedAt, "Invalid worker timing order.");
        return pixels;
    }

    private static async Task Canceled(Func<Task<byte[]>> operation, string phase)
    {
        try { await operation(); }
        catch (OperationCanceledException) { Log($"PASS cancellation {phase}."); return; }
        throw new Exception($"Canceled capture was published ({phase}).");
    }

    private static void Unsupported(Visual source, string name)
    {
        var layers = new[] { new FluentBackdropScene.Layer(source, new Rect(0, 0, 100, 80), new Rect(0, 0, 100, 80)) };
        bool accepted = FluentBackdropScene.TryCapture(Brushes.White, Size, Bounds, layers, out var scene, out string? reason);
        Require(!accepted && scene is null && !string.IsNullOrWhiteSpace(reason), $"Unsupported {name} did not request fallback.");
        Log($"PASS fallback {name}: {reason}");
    }

    private static void Equal(byte[] expected, byte[] actual, double dpi, string name)
    {
        Require(expected.Length == actual.Length, $"{name}: wrong buffer length.");
        int different = 0, maximum = 0, first = -1;
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != actual[i]) { different++; maximum = Math.Max(maximum, Math.Abs(expected[i] - actual[i])); if (first < 0) first = i; }
        if (different == 0) { Log($"PASS {name}: all {expected.Length} BGRA bytes equal."); return; }
        Directory.CreateDirectory(EvidenceDirectory);
        Save(expected, dpi, Path.Combine(EvidenceDirectory, name + "-original.png")); Save(actual, dpi, Path.Combine(EvidenceDirectory, name + "-worker.png"));
        throw new Exception($"{name}: {different}/{expected.Length} BGRA channels differ; max delta {maximum}; first byte {first}. PNGs: {EvidenceDirectory}");
    }
    private static void Log(string message) { Report.Add(message); Console.WriteLine(message); }

    private static void Save(byte[] pixels, double dpi, string path)
    {
        int width = Pixels(Size.Width, dpi), height = Pixels(Size.Height, dpi);
        var bitmap = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Pbgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static int Pixels(double size, double dpi) => checked((int)Math.Round(size * dpi / 96));
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
