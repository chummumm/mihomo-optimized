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
        private readonly List<Task> writes = [];
        private readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
        private readonly DispatcherTimer deadline = new() { Interval = TimeSpan.FromSeconds(20) };
        private readonly RoutedEventHandler click;
        private readonly System.Windows.Controls.SelectionChangedEventHandler selection;
        private readonly System.Windows.Input.KeyboardFocusChangedEventHandler focus;
        private long started;
        private int index;
        private string error = "";
        private bool finishing;
        public bool Finished { get; private set; }
        internal Recording(Window owner, string directory, string name, string trigger, string surface)
        {
            this.owner = owner; this.directory = directory; this.name = name; this.trigger = trigger; this.surface = surface;
            click = (_, e) => Trigger(e.OriginalSource as DependencyObject);
            selection = (_, e) => Trigger(e.OriginalSource as DependencyObject);
            focus = (_, e) => Trigger(e.OriginalSource as DependencyObject);
            owner.AddHandler(ButtonBase.ClickEvent, click, true);
            owner.AddHandler(ToggleButton.CheckedEvent, click, true); owner.AddHandler(ToggleButton.UncheckedEvent, click, true);
            owner.AddHandler(Selector.SelectionChangedEvent, selection, true);
            owner.AddHandler(UIElement.PreviewGotKeyboardFocusEvent, focus, true);
            timer.Tick += Capture;
            deadline.Tick += (_, _) => { error = "trigger_timeout"; Finish(); };
            deadline.Start();
        }
        internal object Status() => new { armed = started == 0 && !Finished, started = started != 0, finished = Finished, name, frames = frames.Count, error };
        private void Trigger(DependencyObject? item)
        {
            if (started != 0 || Finished) return;
            while (item is not null)
            {
                if (AutomationProperties.GetAutomationId(item) == trigger)
                {
                    started = Stopwatch.GetTimestamp(); Unhook(); deadline.Stop();
                    timer.Start(); return;
                }
                item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
            }
        }
        private void Unhook()
        {
            owner.RemoveHandler(ButtonBase.ClickEvent, click); owner.RemoveHandler(ToggleButton.CheckedEvent, click); owner.RemoveHandler(ToggleButton.UncheckedEvent, click); owner.RemoveHandler(Selector.SelectionChangedEvent, selection);
            owner.RemoveHandler(UIElement.PreviewGotKeyboardFocusEvent, focus);
        }
        private void Capture(object? sender, EventArgs e)
        {
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (index >= Schedule.Length || elapsed < Schedule[index]) return;
            try
            {
                var captured = NativeVisualCapture.Render(owner, surface);
                var bitmap = captured.Bitmap;
                int width = captured.Width, height = captured.Height;
                string file = name + "-" + index.ToString("D2") + ".png";
                frames.Add(new { file, requestedMilliseconds = Schedule[index], elapsedMilliseconds = Math.Round(elapsed, 3), width, height, dpi = captured.Dpi });
                writes.Add(Task.Run(() => { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(directory, file)); encoder.Save(stream); }));
                index++;
                if (index == Schedule.Length) Finish();
            }
            catch (Exception exception) { error = exception.GetType().Name; Finish(); }
        }
        private async void Finish()
        {
            if (Finished || finishing) return;
            finishing = true;
            timer.Stop(); deadline.Stop(); Unhook();
            try { await Task.WhenAll(writes); }
            catch (Exception exception) { error = exception.GetType().Name; }
            var report = new { name, trigger, surface, captureKind = "wpf-visual-tree", drivenBy = "independent-windows-uia", reducedMotion = !FluentMotion.Enabled, frames, error };
            try { File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception exception) { error = exception.GetType().Name; }
            Finished = true;
        }
    }
}
