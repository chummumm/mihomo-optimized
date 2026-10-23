using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Mihomo.Desktop.Services;

/// <summary>Requests real DWM materials while preserving Windows accessibility and fallback rules.</summary>
internal sealed class NativeBackdrop : IDisposable
{
    private readonly Window window;
    private readonly System.Windows.Controls.Panel surface;
    private readonly AppSession session;
    private readonly bool transient;
    public string Material { get; private set; } = "solid";
    public NativeBackdrop(Window window, System.Windows.Controls.Panel surface, AppSession session, bool transient = false)
    {
        this.window = window; this.surface = surface; this.session = session; this.transient = transient;
        window.SourceInitialized += SourceInitialized;
        window.Activated += SourceInitialized;
        window.StateChanged += SourceInitialized;
        session.ThemeChanged += Apply;
        SystemParameters.StaticPropertyChanged += SystemChanged;
    }
    private void SourceInitialized(object? sender, EventArgs args) => Apply();
    private void SystemChanged(object? sender, PropertyChangedEventArgs args) => window.Dispatcher.InvokeAsync(Apply);
    public void Apply()
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        bool dark = window.TryFindResource("AppBackgroundBrush") is SolidColorBrush brush && brush.Color.R < 128;
        int theme = dark ? 1 : 0, corner = 2;
        _ = DwmSetWindowAttribute(handle, 20, ref theme, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
        bool transparency = !SystemParameters.HighContrast;
        try { transparency &= Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) is not int value || value != 0; }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { transparency = false; }
        bool composition = DwmIsCompositionEnabled(out var composed) == 0 && composed;
        int backdrop = transparency && composition ? transient ? 3 : 2 : 1;
        // DWMSBT_MAINWINDOW (Mica) and DWMSBT_TRANSIENTWINDOW (Desktop Acrylic)
        // are the supported system-backdrop API from Windows 11 build 22621.
        bool applied = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
            && DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int)) == 0 && backdrop != 1;
        if (applied)
        {
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            applied = DwmExtendFrameIntoClientArea(handle, ref margins) == 0;
        }
        Material = applied ? transient ? "acrylic" : "mica" : "solid";
        if (applied)
        {
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            window.Background = Brushes.Transparent;
            // DWM supplies the material; only transient windows need the design's tint.
            surface.Background = transient ? new SolidColorBrush(dark ? Color.FromArgb(84, 32, 32, 32) : Color.FromArgb(56, 243, 243, 243)) : Brushes.Transparent;
        }
        else
        {
            var margins = new Margins();
            _ = DwmExtendFrameIntoClientArea(handle, ref margins);
            window.SetResourceReference(Window.BackgroundProperty, "AppBackgroundBrush");
            surface.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "AppBackgroundBrush");
        }
    }
    public void Dispose()
    {
        window.SourceInitialized -= SourceInitialized; window.Activated -= SourceInitialized; window.StateChanged -= SourceInitialized;
        session.ThemeChanged -= Apply; SystemParameters.StaticPropertyChanged -= SystemChanged;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
}
