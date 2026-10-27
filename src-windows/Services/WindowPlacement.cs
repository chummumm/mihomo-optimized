using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Mihomo.Desktop.Services;

/// <summary>Fits startup and restored windows to their monitor without replacing WPF's DPI, drag, or Snap handling.</summary>
internal sealed class WindowPlacement : IDisposable
{
    private readonly Window window;
    private readonly Action? metricsChanged;
    private readonly double preferredWidth, preferredHeight, preferredMinimumWidth, preferredMinimumHeight;
    private HwndSource? source;
    private IntPtr lastMonitor;
    private bool pending, applying, moving, reconcileAfterMove, disposed;

    internal WindowPlacement(Window window, Action? metricsChanged = null)
    {
        this.window = window; this.metricsChanged = metricsChanged;
        preferredWidth = window.Width; preferredHeight = window.Height;
        preferredMinimumWidth = window.MinWidth; preferredMinimumHeight = window.MinHeight;
        window.SourceInitialized += Initialize;
        window.StateChanged += StateChanged;
        window.IsVisibleChanged += VisibilityChanged;
        window.Closed += Closed;
    }

    private void Initialize(object? sender, EventArgs args)
    {
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.AddHook(HandleMessage);
        // CenterScreen selected the initial monitor. Own the final position now,
        // before Show, so WPF cannot center an oversized window above that screen.
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        Reconcile(initial: true);
    }

    private void StateChanged(object? sender, EventArgs args) => Schedule();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) { if (window.IsVisible) Schedule(); }
    private void Closed(object? sender, EventArgs args) => Dispose();

    private IntPtr HandleMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case 0x0231: moving = true; break; // WM_ENTERSIZEMOVE
            case 0x0232: // WM_EXITSIZEMOVE: preserve ordinary user drags and Snap placement.
                moving = false;
                if (reconcileAfterMove) { reconcileAfterMove = false; Schedule(); }
                break;
            case 0x02E0: // WM_DPICHANGED: let WPF consume the suggested rectangle first.
            case 0x007E: // WM_DISPLAYCHANGE
            case 0x001A: // WM_SETTINGCHANGE, including taskbar/work-area changes.
                Schedule(); break;
            case 0x0047: // WM_WINDOWPOSCHANGED also covers keyboard moves between monitors.
                if (!applying && MonitorFromWindow(handle, 2) != lastMonitor) Schedule();
                break;
        }
        // In particular, do not consume DPI, non-client, or maximize messages.
        return IntPtr.Zero;
    }

    private void Schedule()
    {
        if (disposed || pending || applying || window.Dispatcher.HasShutdownStarted) return;
        if (moving) { reconcileAfterMove = true; return; }
        pending = true;
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            pending = false;
            if (moving) reconcileAfterMove = true;
            else if (!disposed) Reconcile(initial: false);
        }));
    }

    internal void EnsureVisible() => Reconcile(initial: false);

    private void Reconcile(bool initial)
    {
        if (disposed || applying || window.WindowState == WindowState.Minimized || !TryContext(window, out var context)) return;
        applying = true;
        try
        {
            lastMonitor = context.Monitor;
            LimitMinimum(window, context, preferredMinimumWidth, preferredMinimumHeight);
            if (window.WindowState == WindowState.Normal)
            {
                var bounds = context.Bounds with {
                    Width = Math.Max(context.Bounds.Width, (int)Math.Ceiling(window.MinWidth * context.Scale)),
                    Height = Math.Max(context.Bounds.Height, (int)Math.Ceiling(window.MinHeight * context.Scale))
                };
                if (initial)
                    bounds = bounds with {
                        Width = WindowPlacementGeometry.Pixels(preferredWidth, context.Scale, context.WorkArea.Width),
                        Height = WindowPlacementGeometry.Pixels(preferredHeight, context.Scale, context.WorkArea.Height)
                    };
                var fitted = WindowPlacementGeometry.Fit(bounds, context.WorkArea, center: initial);
                if (initial) { window.Width = fitted.Width / context.Scale; window.Height = fitted.Height / context.Scale; }
                if (!Apply(window, context, fitted)) Trace.TraceWarning("Could not keep the main window inside its monitor: {0}", Marshal.GetLastWin32Error());
            }
            // Maximize remains native; only refresh the existing client inset.
            metricsChanged?.Invoke();
        }
        finally { applying = false; }
    }

    internal static void ResizeWithinWorkArea(Window window, double width, double height)
    {
        if (!TryContext(window, out var context)) throw new Win32Exception();
        LimitMinimum(window, context, window.MinWidth, window.MinHeight);
        var requested = context.Bounds with {
            Width = Math.Max(WindowPlacementGeometry.Pixels(width, context.Scale, context.WorkArea.Width), (int)Math.Ceiling(window.MinWidth * context.Scale)),
            Height = Math.Max(WindowPlacementGeometry.Pixels(height, context.Scale, context.WorkArea.Height), (int)Math.Ceiling(window.MinHeight * context.Scale))
        };
        if (!Apply(window, context, WindowPlacementGeometry.Fit(requested, context.WorkArea, center: true))) throw new Win32Exception();
    }

    internal static Thickness MaximizedClientInsets(Window window)
    {
        var origin = new NativePoint();
        if (!TryContext(window, out var context) || !GetClientRect(context.Handle, out var client)
            || !ClientToScreen(context.Handle, ref origin)) return SystemParameters.WindowResizeBorderThickness;
        // WindowChrome extends content into the native frame. Measure the
        // portion outside this monitor's work area, including any padded
        // border, rather than guessing a global/system-DPI frame thickness.
        var visible = WindowPlacementGeometry.VisibleInsets(new(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top), context.WorkArea);
        return new(visible.Left / context.Scale, visible.Top / context.Scale, visible.Right / context.Scale, visible.Bottom / context.Scale);
    }

    private static void LimitMinimum(Window window, Context context, double preferredWidth, double preferredHeight)
    {
        // WPF's hard minimum otherwise expands a fitted HWND again on a small
        // high-DPI display. Keep the original minimum for when it fits again.
        window.MinWidth = WindowPlacementGeometry.Minimum(preferredWidth, context.WorkArea.Width, context.Scale);
        window.MinHeight = WindowPlacementGeometry.Minimum(preferredHeight, context.WorkArea.Height, context.Scale);
    }

    private static bool Apply(Window window, Context context, WindowPixelBounds fitted)
    {
        if (context.Bounds == fitted) return true;
        if (context.Bounds.Width != fitted.Width) window.Width = fitted.Width / context.Scale;
        if (context.Bounds.Height != fitted.Height) window.Height = fitted.Height / context.Scale;
        return SetWindowPos(context.Handle, IntPtr.Zero, fitted.Left, fitted.Top, fitted.Width, fitted.Height, 0x0014);
    }

    private readonly record struct Context(IntPtr Handle, IntPtr Monitor, WindowPixelBounds Bounds, WindowPixelBounds WorkArea, double Scale);

    private static bool TryContext(Window window, out Context context)
    {
        context = default;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rectangle)) return false;
        IntPtr monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || info.Work.Right <= info.Work.Left || info.Work.Bottom <= info.Work.Top) return false;
        uint dpi = GetDpiForWindow(handle);
        if (dpi == 0) return false;
        context = new(handle, monitor, rectangle.Bounds, info.Work.Bounds, dpi / 96.0);
        return true;
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        window.SourceInitialized -= Initialize; window.StateChanged -= StateChanged;
        window.IsVisibleChanged -= VisibilityChanged; window.Closed -= Closed;
        if (source is { IsDisposed: false }) source.RemoveHook(HandleMessage);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        internal readonly WindowPixelBounds Bounds => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
}
