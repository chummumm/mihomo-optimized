using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Mihomo.Desktop.Services;

/// <summary>Preserves Windows 11's native maximize hover/Snap behavior for the drawn caption.</summary>
internal sealed class NativeTitleBar : IDisposable
{
    private readonly Window window;
    private readonly Button maximize;
    private readonly Action toggle;
    private HwndSource? source;
    private bool pressed;
    public NativeTitleBar(Window window, Button maximize, Action toggle)
    {
        this.window = window; this.maximize = maximize; this.toggle = toggle;
        window.SourceInitialized += Initialize;
        window.Deactivated += Deactivated;
    }
    private void Initialize(object? sender, EventArgs args)
    {
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.AddHook(HandleMessage);
    }
    private void Deactivated(object? sender, EventArgs args) { pressed = false; maximize.Tag = null; }
    private bool Contains(IntPtr point)
    {
        if (!maximize.IsVisible || !maximize.IsEnabled || maximize.ActualWidth <= 0) return false;
        long value = point.ToInt64();
        var local = maximize.PointFromScreen(new Point(unchecked((short)(value & 0xffff)), unchecked((short)((value >> 16) & 0xffff))));
        return new Rect(maximize.RenderSize).Contains(local);
    }
    private IntPtr HandleMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0084 && Contains(lParam)) // WM_NCHITTEST, HTMAXBUTTON
        {
            handled = true;
            return new IntPtr(9);
        }
        if (message == 0x00A0) // WM_NCMOUSEMOVE: leave DWM free to show the Snap flyout.
        {
            bool over = wParam.ToInt32() == 9 && Contains(lParam);
            maximize.Tag = over ? pressed ? "pressed" : "hover" : null;
            if (over)
            {
                var track = new TrackMouse { Size = (uint)Marshal.SizeOf<TrackMouse>(), Flags = 0x12, Window = windowHandle };
                _ = TrackMouseEvent(ref track);
            }
        }
        if (message == 0x02A2) { maximize.Tag = null; } // WM_NCMOUSELEAVE
        if (message == 0x00A1 && wParam.ToInt32() == 9) // WM_NCLBUTTONDOWN
        {
            pressed = true; maximize.Tag = "pressed"; handled = true;
        }
        if (message == 0x00A2 && pressed) // WM_NCLBUTTONUP
        {
            pressed = false; maximize.Tag = null; handled = true;
            if (Contains(lParam)) toggle();
        }
        return IntPtr.Zero;
    }
    public void Dispose() { window.SourceInitialized -= Initialize; window.Deactivated -= Deactivated; source?.RemoveHook(HandleMessage); }
    [StructLayout(LayoutKind.Sequential)] private struct TrackMouse { public uint Size, Flags; public IntPtr Window; public uint HoverTime; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TrackMouseEvent(ref TrackMouse value);
}
