using System.Diagnostics;
using System.Text.Json;
using System.Windows;

namespace Mihomo.Desktop.Services;

/// <summary>Opt-in acceptance diagnostics contain exception types and code locations, never messages or configuration.</summary>
internal static class NativeFaultDiagnostics
{
    internal static void Attach(Application app, string[] arguments)
    {
        if (!arguments.Contains("--automation-pipe", StringComparer.Ordinal)) return;
        int index = Array.IndexOf(arguments, "--automation-diagnostics");
        if (index < 0 || index + 1 >= arguments.Length) return;
        string output = Path.GetFullPath(arguments[index + 1]);
        int recorded = 0;
        void Record(Exception exception, string source)
        {
            if (Interlocked.Exchange(ref recorded, 1) != 0) return;
            try
            {
                var chain = new List<object>();
                for (Exception? current = exception; current is not null && chain.Count < 8; current = current.InnerException)
                {
                    var frames = new StackTrace(current, false).GetFrames()?.Take(32).Select(frame =>
                    {
                        var method = frame.GetMethod();
                        return new { type = method?.DeclaringType?.FullName, method = method?.Name, offset = frame.GetILOffset() };
                    });
                    chain.Add(new { type = current.GetType().FullName, hresult = current.HResult, frames });
                }
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, JsonSerializer.Serialize(new { source, time = DateTimeOffset.UtcNow, exceptions = chain }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception) { /* Diagnostics must never replace the actual process failure. */ }
        }
        app.DispatcherUnhandledException += (_, args) => Record(args.Exception, "dispatcher");
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { if (args.ExceptionObject is Exception exception) Record(exception, "unhandled"); };
    }
}
