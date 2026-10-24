using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Mihomo.Desktop.Controls;

internal static class Program
{
    private const string EvidenceDirectory = "artifacts/verification/shell-contract";
    private static readonly List<Check> Results = [];
    private sealed record Check(string Name, bool Passed, string Expected, string Actual);

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            Window? window = null;
            string? error = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                window = new Window {
                    Width = 300, Height = 180, Left = 40, Top = 40,
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false, Title = "Shell hit-test contract"
                };
                await VerifyAsync(window, timeout.Token).WaitAsync(timeout.Token);
            }
            catch (Exception failure) { error = failure.ToString(); Console.Error.WriteLine(error); }
            finally
            {
                timeout.Cancel();
                window?.Close();
                bool passed = error is null && Results.Count > 0 && Results.All(check => check.Passed);
                Directory.CreateDirectory(EvidenceDirectory);
                File.WriteAllText(Path.Combine(EvidenceDirectory, "report.json"), JsonSerializer.Serialize(new {
                    passed, timeoutSeconds = 15, motionEnabled = FluentMotion.Enabled,
                    method = "Window.InputHitTest on a shown WPF window; no UIA invocation or synthetic routed event",
                    results = Results, error
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(passed ? "PASS: shell transparent-background hit-test contracts." : "FAIL: shell hit-test contracts; see report.json.");
                app.Shutdown(passed ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task VerifyAsync(Window window, CancellationToken cancellation)
    {
        var surface = new Canvas { Name = "UnderlyingSurface", Background = Brushes.White };
        Border before = Target(surface, "BeforeLoaded", 10, 10);
        Border after = Target(surface, "AfterLoaded", 160, 10);
        Border themed = Target(surface, "Themed", 10, 100);
        window.Content = surface;
        window.Resources["ShellTestBrush"] = Brushes.Transparent;
        themed.SetResourceReference(ShellVisuals.BackgroundProperty, "ShellTestBrush");

        if (before.IsLoaded) throw new Exception("Pre-load fixture was already mounted.");
        // Use the same shared transparent brush as the production property's old default.
        ShellVisuals.SetBackground(before, Brushes.Transparent);
        window.Show();
        await FlushAsync(window);
        if (!before.IsLoaded || !after.IsLoaded) throw new Exception("The real WPF window did not load its fixtures.");
        Hit(window, before, before, "transparent-first-assignment-before-loaded");

        ShellVisuals.SetBackground(after, Brushes.Transparent);
        await FlushAsync(window);
        Hit(window, after, after, "transparent-first-assignment-after-loaded");
        Hit(window, themed, themed, "dynamic-resource-initial-transparent");

        // null intentionally leaves the empty border without a hit surface.
        after.SetValue(ShellVisuals.BackgroundProperty, null);
        await FlushAsync(window);
        Hit(window, after, surface, "explicit-null-passes-through");

        ShellVisuals.SetBackground(after, Brushes.DodgerBlue);
        await FlushAsync(window);
        Hit(window, after, after, "hover-color-hit-surface");
        ShellVisuals.SetBackground(after, Brushes.Transparent);
        await SettleTransparentAsync(window, after, cancellation);
        Hit(window, after, after, "hover-return-to-transparent");

        window.Resources["ShellTestBrush"] = Brushes.DarkSlateGray;
        await FlushAsync(window);
        Hit(window, themed, themed, "dynamic-resource-theme-color");
        window.Resources["ShellTestBrush"] = Brushes.Transparent;
        await SettleTransparentAsync(window, themed, cancellation);
        Hit(window, themed, themed, "dynamic-resource-return-to-transparent");

        themed.SetValue(ShellVisuals.BackgroundProperty, null);
        await FlushAsync(window);
        Hit(window, themed, surface, "null-after-theme-passes-through");
    }

    private static Border Target(Canvas surface, string name, double left, double top)
    {
        var border = new Border { Name = name, Width = 120, Height = 60 };
        Canvas.SetLeft(border, left); Canvas.SetTop(border, top); surface.Children.Add(border);
        return border;
    }

    private static async Task FlushAsync(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task SettleTransparentAsync(Window window, Border border, CancellationToken cancellation)
    {
        // Cover the real color animation when OS motion is enabled, and its disabled path otherwise.
        do
        {
            await FlushAsync(window);
            if (border.Background is SolidColorBrush { Color.A: 0 }) return;
            await Task.Delay(20, cancellation);
        } while (true);
    }

    private static void Hit(Window window, Border target, DependencyObject expected, string name)
    {
        // Center and empty padding must both hit; no child icon or text can mask a missing background.
        Point[] points = [new(60, 30), new(4, 4), new(116, 56)];
        var actual = points.Select(point => window.InputHitTest(target.TranslatePoint(point, window))).ToArray();
        bool passed = actual.All(hit => ReferenceEquals(hit, expected));
        string Describe(object? hit) => hit is FrameworkElement element ? $"{element.GetType().Name}({element.Name})" : hit?.GetType().Name ?? "null";
        var check = new Check(name, passed, Describe(expected), string.Join(", ", actual.Select(Describe)) + $"; background={target.Background?.ToString() ?? "null"}");
        Results.Add(check);
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {check.Name}: expected {check.Expected}; actual {check.Actual}");
    }
}
