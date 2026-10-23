using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Pages;

namespace Mihomo.Desktop.Services;

/// <summary>Read-only evidence from real WPF visuals, including an owned search list at its actual position.</summary>
internal static class NativeVisualCapture
{
    internal sealed record Frame(RenderTargetBitmap Bitmap, int Width, int Height, double Dpi);

    internal static Frame Render(Window owner, string surface)
    {
        FrameworkElement target = Surface(owner, surface);
        target.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(target);
        int width = (int)Math.Ceiling(target.ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(target.ActualHeight * dpi.DpiScaleY);
        if (width is < 1 or > 4000 || height is < 1 or > 3000) throw new InvalidOperationException("Capture bounds are invalid.");
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(target);
        var overlays = owner.OwnedWindows.OfType<FluentDialog>().Where(window => window.IsVisible).Cast<Window>().ToList();
        if (surface == "search") overlays.Add(Find("GlobalSearchWindow") ?? throw new InvalidOperationException("Open the real search flyout before capturing it."));
        if (surface == "tray") overlays.Clear();
        foreach (var flyout in overlays)
        {
            if (flyout.Content is not FrameworkElement content) throw new InvalidOperationException("Owned surface content is unavailable.");
            content.UpdateLayout();
            var popupDpi = VisualTreeHelper.GetDpi(content);
            var popup = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(content.ActualWidth * popupDpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(content.ActualHeight * popupDpi.DpiScaleY)), popupDpi.PixelsPerInchX, popupDpi.PixelsPerInchY, PixelFormats.Pbgra32);
            popup.Render(content);
            var origin = target.PointToScreen(new Point());
            var position = content.PointToScreen(new Point());
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawImage(bitmap, new Rect(0, 0, target.ActualWidth, target.ActualHeight));
                dc.DrawImage(popup, new Rect((position.X - origin.X) / dpi.DpiScaleX, (position.Y - origin.Y) / dpi.DpiScaleY, content.ActualWidth, content.ActualHeight));
            }
            bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
        }
        bitmap.Freeze();
        return new Frame(bitmap, width, height, dpi.PixelsPerInchX);
    }

    internal static object Save(Window owner, JsonElement args, string material, JsonElement update)
    {
        string path = Path.GetFullPath(args.Text("path"));
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Screenshot must be a PNG file.");
        string surface = args.Text("surface", fallback: "main");
        var frame = Render(owner, surface);
        string captureKind = args.Text("capture") == "screen" ? "screen-pixels" : "wpf-visual-tree";
        if (captureKind == "screen-pixels")
        {
            var origin = Surface(owner, surface).PointToScreen(new Point());
            using var bitmap = new System.Drawing.Bitmap(frame.Width, frame.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen((int)Math.Round(origin.X), (int)Math.Round(origin.Y), 0, 0, new System.Drawing.Size(frame.Width, frame.Height), System.Drawing.CopyPixelOperation.SourceCopy);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        else
        {
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(frame.Bitmap));
            using var file = File.Create(path); png.Save(file);
        }
        var publicUpdate = update.Fields().Where(field => field.Name is "currentVersion" or "available" or "canInstall" or "version" or "releaseUrl" or "body").ToDictionary(field => field.Name, field => field.Value.Clone());
        return new { path, captureKind, surface, material, width = frame.Width, height = frame.Height, dpi = frame.Dpi, capturedAt = DateTimeOffset.UtcNow, appearance = Appearance(), elements = Geometry(Surface(owner, surface)), streams = Readings(Surface(owner, surface)), update = publicUpdate };
    }

    private static FrameworkElement Surface(Window owner, string surface) => surface switch
    {
        "main" or "search" => owner,
        "tray" => Find("TrayFlyout")?.Content as FrameworkElement ?? throw new InvalidOperationException("Open the real tray flyout before capturing it."),
        _ => throw new ArgumentException("Unknown capture surface.")
    };
    private static Window? Find(string id) => Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsVisible && AutomationProperties.GetAutomationId(window) == id);
    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private static object Appearance() => new
    {
        mica = false,
        accent = new { light = Hex(SystemColors.AccentColorDark1), dark = Hex(SystemColors.AccentColorLight2), lightText = Hex(SystemColors.AccentColorDark2), darkText = Hex(SystemColors.AccentColorLight3) },
        bodyFont = Application.Current.TryFindResource("BodyFont")?.ToString(),
        displayFont = Application.Current.TryFindResource("DisplayFont")?.ToString(),
        highContrast = SystemParameters.HighContrast,
        reducedMotion = !FluentMotion.Enabled
    };
    private static object[] Geometry(FrameworkElement root)
    {
        var items = new List<object>();
        void Visit(DependencyObject node)
        {
            if (items.Count >= 5000) return;
            if (node is FrameworkElement element && element.IsVisible && AutomationProperties.GetAutomationId(element) is { Length: > 0 } id)
            {
                try
                {
                    var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                    items.Add(new { id, x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height });
                }
                catch (InvalidOperationException) { }
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(root); return items.ToArray();
    }
    private static object Readings(FrameworkElement root)
    {
        object traffic = new { up = 0d, down = 0d, samples = Array.Empty<object>() };
        var logs = new List<object>();
        void Visit(DependencyObject node)
        {
            if (node is UIElement { IsVisible: false }) return;
            if (node is TrafficChart chart) traffic = chart.CaptureReadings();
            if (node is ItemsControl list && AutomationProperties.GetAutomationId(list) == "LogTable")
                foreach (var row in list.Items.OfType<LogsPage.LogRow>().TakeLast(2000)) logs.Add(new { time = row.Time, type = row.Level, payload = row.Message });
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(root); return new { traffic, logs };
    }
}
