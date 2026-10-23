using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop;

public partial class App : Application
{
    private InstanceBroker? instance;
    private AutomationServer? automation;
    private AppSession? session;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (e.Args.FirstOrDefault() is "--install" or "--uninstall" or "--stop-for-update" or "--start-if-installed")
            {
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "mihomo-desktop-host.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
                foreach (string argument in e.Args) start.ArgumentList.Add(argument);
                using var helper = Process.Start(start) ?? throw new InvalidOperationException("无法启动维护程序");
                await helper.WaitForExitAsync();
                Shutdown(helper.ExitCode);
                return;
            }
            instance = new InstanceBroker();
            if (!instance.IsFirst)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                bool accepted = await instance.SendAsync(e.Args.Contains("--quit") ? "quit" : "activate", deadline.Token);
                Shutdown(accepted ? 0 : 2);
                return;
            }
            if (e.Args.Contains("--quit")) { Shutdown(); return; }
            session = new AppSession();
            session.ApplyTheme();
            var window = new MainWindow(session);
            MainWindow = window;
            instance.Listen(async command => await Dispatcher.InvokeAsync(async () =>
            {
                if (command == "activate") { window.ShowMain(); return true; }
                bool accepted = await window.PrepareExitAsync();
                if (accepted) _ = FinishRemoteExitAsync(window);
                return accepted;
            }).Task.Unwrap());
            if (!e.Args.Contains("--tray")) window.Show();
            else { window.Show(); window.HideToTray(); }
            int automationIndex = Array.IndexOf(e.Args, "--automation-pipe");
            if (automationIndex >= 0)
            {
                if (automationIndex + 1 >= e.Args.Length) throw new ArgumentException("Missing automation pipe name");
                automation = new AutomationServer(e.Args[automationIndex + 1], session, (method, args) => Dispatcher.InvokeAsync(() => window.AutomationAsync(method, args)).Task.Unwrap());
                automation.Start();
            }
            try { await session.StartAsync(); }
            catch (Exception error) { session.Notify(error.Message, true); window.ShowMain(); }
        }
        catch (Exception error)
        {
            // An installer waits for --quit's exit code; a hidden modal dialog
            // would leave that maintenance invocation blocked indefinitely.
            if (!e.Args.Contains("--quit")) MessageBox.Show(L.T(error.Message), "Mihomo Optimized", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static async Task FinishRemoteExitAsync(MainWindow window)
    {
        // Allow the single-instance pipe to acknowledge before tearing it down.
        await Task.Delay(150);
        await window.FinishExitAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        automation?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
