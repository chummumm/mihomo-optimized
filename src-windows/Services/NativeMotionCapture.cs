using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mihomo.Desktop.Controls;

namespace Mihomo.Desktop.Services;

/// <summary>Opt-in acceptance recording of real routed UI actions. It never drives the control being measured.</summary>
internal static class NativeMotionCapture
{
    private static Recording? current;
    internal static object Arm(Window owner, JsonElement args)
    {
        if (current is { Finished: false }) throw new InvalidOperationException("A motion recording is already armed.");
        var directory = Path.GetFullPath(args.Text("directory"));
        string name = args.Text("name"), trigger = args.Text("trigger");
        if (name.Length is < 1 or > 60 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) || trigger.Length is < 1 or > 120)
            throw new ArgumentException("Invalid recording identifier.");
        Directory.CreateDirectory(directory);
        current = new Recording(owner, directory, name, trigger, args.Text("surface", fallback: "main"));
        return new { armed = true, name, trigger, reducedMotion = !FluentMotion.Enabled };
    }
    internal static object Status() => current?.Status() ?? new { armed = false, finished = true };

    private sealed class Recording
    {
        private static readonly double[] Schedule = [0, 16, 40, 83, 125, 167, 220, 250, 300, 333, 460, 700, 900, 1400, 1800, 2800];
        private readonly Window owner;
        private readonly string directory, name, trigger, surface;
        private readonly List<object> frames = [];
        private readonly NativeVisualCapture.ScreenRegion bounds;
        private readonly bool reducedMotion;
        private readonly DispatcherTimer deadline = new() { Interval = TimeSpan.FromSeconds(20) };
        private readonly RoutedEventHandler click;
        private readonly System.Windows.Controls.SelectionChangedEventHandler selection;
        private readonly System.Windows.Input.KeyboardFocusChangedEventHandler focus;
        private long started;
        private int frameCount;
        private string error = "";
        private object? diagnostics;
        private volatile bool finished;
        public bool Finished => finished;
        internal Recording(Window owner, string directory, string name, string trigger, string surface)
        {
            this.owner = owner; this.directory = directory; this.name = name; this.trigger = trigger; this.surface = surface;
            bounds = NativeVisualCapture.ScreenBounds(owner, surface);
            reducedMotion = !FluentMotion.Enabled;
            click = (_, e) => Trigger(e.OriginalSource as DependencyObject);
            selection = (_, e) => Trigger(e.OriginalSource as DependencyObject);
            focus = (_, e) => Trigger(e.OriginalSource as DependencyObject);
            owner.AddHandler(ButtonBase.ClickEvent, click, true);
            owner.AddHandler(ToggleButton.CheckedEvent, click, true); owner.AddHandler(ToggleButton.UncheckedEvent, click, true);
            owner.AddHandler(Selector.SelectionChangedEvent, selection, true);
            owner.AddHandler(UIElement.PreviewGotKeyboardFocusEvent, focus, true);
            deadline.Tick += (_, _) => { error = "trigger_timeout"; deadline.Stop(); Unhook(); SaveReport(); };
            deadline.Start();
        }
        internal object Status() => new { armed = started == 0 && !Finished, started = started != 0, finished = Finished, name, frames = Volatile.Read(ref frameCount), error };
        private void Trigger(DependencyObject? item)
        {
            if (started != 0 || Finished) return;
            while (item is not null)
            {
                if (AutomationProperties.GetAutomationId(item) == trigger)
                {
                    started = Stopwatch.GetTimestamp(); Unhook(); deadline.Stop();
                    _ = Task.Run(RecordScreenAsync); return;
                }
                item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
            }
        }
        private void Unhook()
        {
            owner.RemoveHandler(ButtonBase.ClickEvent, click); owner.RemoveHandler(ToggleButton.CheckedEvent, click); owner.RemoveHandler(ToggleButton.UncheckedEvent, click); owner.RemoveHandler(Selector.SelectionChangedEvent, selection);
            owner.RemoveHandler(UIElement.PreviewGotKeyboardFocusEvent, focus);
        }
        private async Task RecordScreenAsync()
        {
            var images = new List<(string File, BitmapSource Bitmap)>();
            try
            {
                for (int index = 0; index < Schedule.Length; index++)
                {
                    double remaining = Schedule[index] - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining)).ConfigureAwait(false);
                    double before = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var captured = NativeVisualCapture.CaptureScreen(bounds);
                    double after = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    string file = name + "-" + index.ToString("D2") + ".png";
                    frames.Add(new { file, requestedMilliseconds = Schedule[index], elapsedMilliseconds = Math.Round((before + after) / 2, 3),
                        captureMilliseconds = Math.Round(after - before, 3), width = captured.Width, height = captured.Height, dpi = captured.Dpi });
                    images.Add((file, captured.Bitmap));
                    Volatile.Write(ref frameCount, frames.Count);
                }
                if (owner is MainWindow main)
                    diagnostics = await owner.Dispatcher.InvokeAsync(main.DescribeMotionDiagnostics, DispatcherPriority.Background);
                // Encoding is deferred until the interaction is finished, so
                // compression does not compete with the real UI's rendering.
                foreach (var image in images)
                {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image.Bitmap));
                    using var stream = File.Create(Path.Combine(directory, image.File)); encoder.Save(stream);
                }
            }
            catch (Exception exception) { error = exception.GetType().Name; }
            SaveReport();
        }
        private void SaveReport()
        {
            var report = new { name, trigger, surface, captureKind = "screen-pixels", drivenBy = "independent-windows-uia",
                recordedBy = "background-desktop-capture", reducedMotion, screen = bounds, frames, error, diagnostics,
                caveat = "Only the visible desktop rectangle contains screen pixels; off-desktop areas are black. No WPF layout or render is requested by the recorder." };
            try { File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception exception) { error = exception.GetType().Name; }
            finished = true;
        }
    }
}
