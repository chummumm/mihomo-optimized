using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Pages;

namespace Mihomo.Desktop.Services;

/// <summary>Read-only evidence from real WPF visuals, including same-window search results and owned dialogs.</summary>
internal static class NativeVisualCapture
{
    internal sealed record Frame(BitmapSource Bitmap, int Width, int Height, double Dpi);
    internal sealed record ScreenRegion(int X, int Y, int Width, int Height, double Dpi,
        int VisibleX, int VisibleY, int VisibleWidth, int VisibleHeight);

    internal static void ResizeForCapture(Window window, double width, double height)
    {
        // A hosted Windows desktop can be smaller than the default window.
        // Keep the real HWND wholly inside its monitor's working area before
        // recording; the reference fixture uses the resulting client size.
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(window);
        int pixelsWide = Math.Min(area.Width, (int)Math.Round(width * dpi.DpiScaleX));
        int pixelsHigh = Math.Min(area.Height, (int)Math.Round(height * dpi.DpiScaleY));
        if (!SetWindowPos(handle, IntPtr.Zero, area.Left + (area.Width - pixelsWide) / 2,
            area.Top + (area.Height - pixelsHigh) / 2, pixelsWide, pixelsHigh, 0x0014))
            throw new System.ComponentModel.Win32Exception();
    }

    // Resolve WPF geometry on the dispatcher once; the screen read itself runs
    // independently so recording does not render or block the application's UI.
    internal static ScreenRegion ScreenBounds(Window owner, string surface)
    {
        var target = Surface(owner, surface);
        var dpi = VisualTreeHelper.GetDpi(target);
        var origin = target.PointToScreen(new Point());
        int x = (int)Math.Round(origin.X), y = (int)Math.Round(origin.Y);
        int width = (int)Math.Ceiling(target.ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(target.ActualHeight * dpi.DpiScaleY);
        if (width is < 1 or > 4000 || height is < 1 or > 3000) throw new InvalidOperationException("Capture bounds are invalid.");
        var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
        var visible = System.Drawing.Rectangle.Intersect(new System.Drawing.Rectangle(x, y, width, height), desktop);
        if (visible.Width < 1 || visible.Height < 1) throw new InvalidOperationException("The recording surface is outside the desktop.");
        return new(x, y, width, height, dpi.PixelsPerInchX, visible.X - x, visible.Y - y, visible.Width, visible.Height);
    }

    internal static Frame CaptureScreen(ScreenRegion bounds)
    {
        using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.Black);
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try
            {
                IntPtr destination = graphics.GetHdc();
                try
                {
                    // CAPTUREBLT includes layered native dialogs and flyouts.
                    if (!BitBlt(destination, bounds.VisibleX, bounds.VisibleY, bounds.VisibleWidth, bounds.VisibleHeight,
                        screen, bounds.X + bounds.VisibleX, bounds.Y + bounds.VisibleY, 0x00CC0020 | 0x40000000))
                        throw new System.ComponentModel.Win32Exception();
                }
                finally { graphics.ReleaseHdc(destination); }
            }
            finally { _ = ReleaseDC(IntPtr.Zero, screen); }
        }
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bounds.Width, bounds.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        byte[] pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
        try
        {
            int stride = bounds.Width * 4;
            for (int row = 0; row < bounds.Height; row++)
                System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), pixels, row * stride, stride);
        }
        finally { bitmap.UnlockBits(data); }
        // GDI's RGB32 fourth byte is undefined, not an alpha channel.
        for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var image = BitmapSource.Create(bounds.Width, bounds.Height, bounds.Dpi, bounds.Dpi, PixelFormats.Bgra32, null, pixels, bounds.Width * 4);
        image.Freeze();
        return new(image, bounds.Width, bounds.Height, bounds.Dpi);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);

    internal static Frame Render(Window owner, string surface)
    {
        FrameworkElement target = Surface(owner, surface);
        target.UpdateLayout();
        if (surface == "search" && !ContainsSearchFlyout(owner)) throw new InvalidOperationException("Open the real search flyout before capturing it.");
        var dpi = VisualTreeHelper.GetDpi(target);
        int width = (int)Math.Ceiling(target.ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(target.ActualHeight * dpi.DpiScaleY);
        if (width is < 1 or > 4000 || height is < 1 or > 3000) throw new InvalidOperationException("Capture bounds are invalid.");
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // RTB releases a synchronous WPF channel, including the owner's
        // transparent live observers. Preserve their normal presentation-channel
        // subscription after taking evidence, without another capture or filter.
        try { bitmap.Render(target); }
        finally { FluentBackdropFilter.RestoreLiveObservers(); }
        var overlays = owner.OwnedWindows.OfType<FluentDialog>().Where(window => window.IsVisible).Cast<Window>().ToList();
        // Search is already in the owner's visual tree and presentation channel.
        if (surface == "tray") overlays.Clear();
        foreach (var flyout in overlays)
        {
            if (flyout.Content is not FrameworkElement content) throw new InvalidOperationException("Owned surface content is unavailable.");
            content.UpdateLayout();
            var popupDpi = VisualTreeHelper.GetDpi(content);
            var popup = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(content.ActualWidth * popupDpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(content.ActualHeight * popupDpi.DpiScaleY)), popupDpi.PixelsPerInchX, popupDpi.PixelsPerInchY, PixelFormats.Pbgra32);
            try { popup.Render(content); }
            finally { FluentBackdropFilter.RestoreLiveObservers(); }
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
        string captureKind = args.Text("capture") == "screen" ? "screen-pixels" : "wpf-visual-tree";
        var frame = captureKind == "screen-pixels" ? CaptureScreen(ScreenBounds(owner, surface)) : Render(owner, surface);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(frame.Bitmap));
        using (var file = File.Create(path)) png.Save(file);
        var publicUpdate = update.Fields().Where(field => field.Name is "currentVersion" or "available" or "canInstall" or "version" or "releaseUrl" or "body").ToDictionary(field => field.Name, field => field.Value.Clone());
        return new { path, captureKind, surface, material, width = frame.Width, height = frame.Height, dpi = frame.Dpi, capturedAt = DateTimeOffset.UtcNow, appearance = Appearance(), backdrops = Backdrops(owner), elements = Geometry(Surface(owner, surface)), streams = Readings(Surface(owner, surface)), update = publicUpdate };
    }

    private static object[] Backdrops(Window owner)
    {
        var filters = new List<object>();
        void Visit(DependencyObject node)
        {
            if (node is FluentBackdropFilter { IsVisible: true } filter)
                filters.Add(new { id = AutomationProperties.GetAutomationId(filter), state = filter.Describe() });
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) Visit(VisualTreeHelper.GetChild(node, index));
        }
        Visit(owner);
        foreach (Window window in owner.OwnedWindows) if (window.IsVisible) Visit(window);
        return filters.ToArray();
    }

    private static FrameworkElement Surface(Window owner, string surface) => surface switch
    {
        "main" or "search" => owner,
        "tray" => Find("TrayFlyout")?.Content as FrameworkElement ?? throw new InvalidOperationException("Open the real tray flyout before capturing it."),
        _ => throw new ArgumentException("Unknown capture surface.")
    };
    private static Window? Find(string id) => Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsVisible && AutomationProperties.GetAutomationId(window) == id);
    private static bool ContainsSearchFlyout(DependencyObject node)
    {
        if (node is SearchFlyout { IsVisible: true }) return true;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            if (ContainsSearchFlyout(VisualTreeHelper.GetChild(node, index))) return true;
        return false;
    }
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
