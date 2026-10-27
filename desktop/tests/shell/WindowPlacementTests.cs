using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Mihomo.Desktop.Services;

internal static class WindowPlacementTests
{
    internal static async Task VerifyAsync(Action<string, bool, string, string> record, CancellationToken cancellation)
    {
        var window = new Window {
            Width = 1240, Height = 820, MinWidth = 500, MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false, Title = "Window placement contract"
        };
        WindowChrome.SetWindowChrome(window, new WindowChrome {
            CaptionHeight = 48, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(8), UseAeroCaptionButtons = false
        });
        var root = new Grid { Background = Brushes.White };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition());
        var title = new Border { Background = Brushes.AliceBlue };
        root.Children.Add(title); window.Content = root;
        using var placement = new WindowPlacement(window);
        (WindowPixelBounds Bounds, WindowPixelBounds Work)? firstVisible = null;
        HwndSource? source = null;
        IntPtr CaptureShown(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // WPF's IsVisible can change while the HWND is still being built.
            // Observe the actual native show, before queued recovery can hide
            // a bad initial placement from this assertion.
            if (message == 0x0018 && wParam != IntPtr.Zero && firstVisible is null) firstVisible = Read(window); // WM_SHOWWINDOW
            return IntPtr.Zero;
        }
        window.SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source?.AddHook(CaptureShown);
        };
        try
        {
            window.Show();
            await FlushAsync(cancellation);
            if (firstVisible is not { } first) throw new Exception("The placement fixture never became visible.");
            record("window-first-visible-work-area", Inside(first.Bounds, first.Work), first.Work.ToString(), first.Bounds.ToString());
            CheckTitle(window, title, record, "window-first-render-title-visible");
            var initial = Read(window);
            double dpiScale = GetDpiForWindow(new WindowInteropHelper(window).Handle) / 96.0;
            record("window-minimum-fits-work-area", window.MinWidth * dpiScale <= initial.Work.Width + .5 && window.MinHeight * dpiScale <= initial.Work.Height + .5,
                $"minimum <= {initial.Work.Width} x {initial.Work.Height} px", $"{window.MinWidth * dpiScale} x {window.MinHeight * dpiScale} px");

            // An ordinary restore keeps the user's position instead of
            // recentering on every Show or activation.
            var chosen = initial.Bounds with {
                Left = initial.Work.Left + (initial.Work.Width - initial.Bounds.Width) / 3,
                Top = initial.Work.Top + (initial.Work.Height - initial.Bounds.Height) / 4
            };
            Move(window, chosen.Left, chosen.Top);
            placement.EnsureVisible();
            await FlushAsync(cancellation);
            var restored = Read(window);
            record("window-valid-position-preserved", restored.Bounds == chosen, chosen.ToString(), restored.Bounds.ToString());

            window.Hide();
            Move(window, chosen.Left, initial.Work.Top - 32);
            window.Show();
            await FlushAsync(cancellation);
            var recovered = Read(window);
            record("window-hidden-position-recovered", Inside(recovered.Bounds, recovered.Work), recovered.Work.ToString(), recovered.Bounds.ToString());
            CheckTitle(window, title, record, "window-reopened-title-visible");
        }
        finally
        {
            if (source is { IsDisposed: false }) source.RemoveHook(CaptureShown);
            window.Close();
        }
    }

    private static async Task FlushAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        cancellation.ThrowIfCancellationRequested();
    }

    private static void CheckTitle(Window window, FrameworkElement title, Action<string, bool, string, string> record, string name)
    {
        var work = Read(window).Work;
        var topLeft = title.PointToScreen(new Point());
        var bottomRight = title.PointToScreen(new Point(title.ActualWidth, title.ActualHeight));
        bool inside = title.ActualHeight == 48 && topLeft.X >= work.Left - .5 && topLeft.Y >= work.Top - .5
            && bottomRight.X <= work.Right + .5 && bottomRight.Y <= work.Bottom + .5;
        record(name, inside, $"48 DIP title within {work}", $"{topLeft} to {bottomRight}; height={title.ActualHeight}");
    }

    private static bool Inside(WindowPixelBounds bounds, WindowPixelBounds work) => bounds.Width > 0 && bounds.Height > 0
        && bounds.Left >= work.Left && bounds.Top >= work.Top && bounds.Right <= work.Right && bounds.Bottom <= work.Bottom;

    private static (WindowPixelBounds Bounds, WindowPixelBounds Work) Read(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(handle, out var bounds)) throw new Win32Exception();
        var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor)) throw new Win32Exception();
        return (bounds.Value, monitor.Work.Value);
    }

    private static void Move(Window window, int x, int y)
    {
        if (!SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, x, y, 0, 0, 0x0015)) throw new Win32Exception();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        internal readonly WindowPixelBounds Value => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
}
