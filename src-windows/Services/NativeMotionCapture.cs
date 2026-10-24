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
    private static long nextRecording;
    // The shared QPC origin belongs to the real routed input event. These
    // bounded probes are inert unless an opt-in recording is currently active.
    internal static long TimingSession => current is { } recording && recording.AcceptTiming ? recording.Id : 0;
    internal static void Mark(string phase, string? detail = null) => current?.Mark(phase, detail);
    internal static void Mark(long session, string phase)
    {
        if (current is { } recording && recording.Id == session) recording.Mark(phase, null);
    }
    internal static bool SearchRendering(long session, int instance, double opacity, double translateY) =>
        current is { } recording && recording.Id == session && recording.SearchRendering(instance, opacity, translateY);
    internal static void BackdropCapture(long session, FrameworkElement filter, long generation, long revision,
        long started, long rendered, long copied, long completed, string execution, long sceneCopied) => current?.BackdropCapture(session,
            AutomationProperties.GetAutomationId(filter), generation, revision, started, rendered, copied, completed, execution, sceneCopied);
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
        private const double TimingWindowMilliseconds = 600;
        private static readonly double[] Schedule = [0, 16, 40, 83, 125, 167, 220, 250, 300, 333, 460, 700, 900, 1400, 1800, 2800];
        internal long Id { get; } = Interlocked.Increment(ref nextRecording);
        private readonly Window owner;
        private readonly string directory, name, trigger, surface;
        private readonly List<object> frames = [];
        private readonly object timingLock = new();
        private readonly List<object> phases = [], renderings = [], captures = [];
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
        internal bool AcceptTiming => TryTimingTimestamp(out _);
        private bool TryTimingTimestamp(out long timestamp)
        {
            timestamp = 0;
            long origin = Volatile.Read(ref started);
            if (finished || origin == 0) return false;
            timestamp = Stopwatch.GetTimestamp();
            return Stopwatch.GetElapsedTime(origin, timestamp).TotalMilliseconds <= TimingWindowMilliseconds;
        }
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
        internal void Mark(string phase, string? detail)
        {
            if (!TryTimingTimestamp(out long timestamp)) return;
            lock (timingLock)
                if (phases.Count < 64) phases.Add(new { phase, detail, timestamp,
                    elapsedMilliseconds = Stopwatch.GetElapsedTime(started, timestamp).TotalMilliseconds });
        }
        internal bool SearchRendering(int instance, double opacity, double translateY)
        {
            if (!TryTimingTimestamp(out long timestamp)) return false;
            lock (timingLock)
            {
                if (renderings.Count >= 32) return false;
                renderings.Add(new { instance, timestamp, elapsedMilliseconds = Stopwatch.GetElapsedTime(started, timestamp).TotalMilliseconds, opacity, translateY });
                return renderings.Count < 32;
            }
        }
        internal void BackdropCapture(long session, string filter, long generation, long revision,
            long captureStarted, long rendered, long copied, long completed, string execution, long sceneCopied)
        {
            if (finished || session != Id || started == 0 || captureStarted < started
                || Stopwatch.GetElapsedTime(started, captureStarted).TotalMilliseconds > TimingWindowMilliseconds) return;
            lock (timingLock)
            {
                if (captures.Count >= 16) return;
                captures.Add(new { filter, generation, revision, execution, startedTimestamp = captureStarted,
                    sceneCopiedTimestamp = sceneCopied == 0 ? (long?)null : sceneCopied,
                    renderedTimestamp = rendered == 0 ? (long?)null : rendered,
                    copiedTimestamp = copied == 0 ? (long?)null : copied, completedTimestamp = completed,
                    startedMilliseconds = Stopwatch.GetElapsedTime(started, captureStarted).TotalMilliseconds,
                    sceneCopiedMilliseconds = sceneCopied == 0 ? (double?)null : Stopwatch.GetElapsedTime(started, sceneCopied).TotalMilliseconds,
                    renderedMilliseconds = rendered == 0 ? (double?)null : Stopwatch.GetElapsedTime(started, rendered).TotalMilliseconds,
                    copiedMilliseconds = copied == 0 ? (double?)null : Stopwatch.GetElapsedTime(started, copied).TotalMilliseconds,
                    completedMilliseconds = Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds });
            }
        }
        private object Timing()
        {
            lock (timingLock) return new { clock = "Stopwatch", frequency = Stopwatch.Frequency,
                triggerTimestamp = started, windowMilliseconds = TimingWindowMilliseconds,
                phases = phases.ToArray(), renderings = renderings.ToArray(), backdropCaptures = captures.ToArray() };
        }
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
                recordedBy = "background-desktop-capture", reducedMotion, screen = bounds, frames, error, diagnostics, timing = Timing(),
                caveat = "Only the visible desktop rectangle contains screen pixels; off-desktop areas are black. No WPF layout or render is requested by the recorder." };
            try { File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception exception) { error = exception.GetType().Name; }
            finished = true;
        }
    }
}
