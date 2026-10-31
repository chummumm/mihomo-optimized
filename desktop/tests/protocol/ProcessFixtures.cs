using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Mihomo.Desktop.Services;

internal static class ProcessFixtures
{
    public static async Task HostAsync()
    {
        string? mode = Environment.GetEnvironmentVariable("MIHOMO_PROTOCOL_FIXTURE");
        if (mode is "slow" or "dispose") await Task.Delay(500);
        int count = 0;
        while (await Console.In.ReadLineAsync() is string line)
        {
            try
            {
                using var request = JsonDocument.Parse(line);
                long id = request.RootElement.GetProperty("id").GetInt64();
                string method = request.RootElement.GetProperty("method").GetString()!;
                if (method == "desktop_eof") return;
                if (method == "desktop_bad_frame")
                {
                    Console.WriteLine("invalid-json");
                    await Console.In.ReadToEndAsync();
                    return;
                }
                if (method == "desktop_hold") continue;
                if (method == "desktop_exit")
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { id, result = new { exiting = true } }));
                    return;
                }
                Console.WriteLine(JsonSerializer.Serialize(new { id, result = new { count = ++count, pid = Environment.ProcessId, noAutostart = Environment.GetCommandLineArgs().Contains("--no-autostart") } }));
            }
            catch (JsonException) { Console.WriteLine("invalid-json"); }
        }
    }

    private static string CopyAppHost(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        string assembly = Assembly.GetExecutingAssembly().GetName().Name!;
        string source = Path.Combine(AppContext.BaseDirectory, assembly + (OperatingSystem.IsWindows() ? ".exe" : ""));
        string destination = Path.Combine(directory, name);
        File.Copy(source, destination, true);
        if (directory != AppContext.BaseDirectory)
            foreach (string suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, assembly + suffix), Path.Combine(directory, assembly + suffix), true);
        return destination;
    }

    private static async Task ExpectCancelled(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new Exception("Caller cancellation was ignored");
    }

    private static async Task ExpectFailure(Task task, string? code = null)
    {
        try { await task; }
        catch (DesktopException error) when (code is null || error.Code == code) { return; }
        throw new Exception("Expected a failed desktop channel");
    }

    public static async Task TestHostAsync()
    {
        string executable = CopyAppHost(AppContext.BaseDirectory, "mihomo-desktop-host.exe");
        string? previous = Environment.GetEnvironmentVariable("MIHOMO_PROTOCOL_FIXTURE");
        try
        {
            Environment.SetEnvironmentVariable("MIHOMO_PROTOCOL_FIXTURE", "slow");
            await using (var host = new HostChannel())
            {
                host.Start();
                using var cancellation = new CancellationTokenSource(100);
                await ExpectCancelled(host.CallAsync("desktop_save_config", new { content = new string('a', 2 * 1024 * 1024), expectedRevision = "old" }, cancellation.Token));
                using var deadline = new CancellationTokenSource(5000);
                var reply = await host.CallAsync("desktop_status", null, deadline.Token);
                if (reply.GetProperty("count").GetInt32() != 2) throw new Exception("A cancelled frame corrupted the next request");
            }
            Environment.SetEnvironmentVariable("MIHOMO_PROTOCOL_FIXTURE", "normal");
            foreach (string method in new[] { "desktop_bad_frame", "desktop_eof" })
            {
                await using var host = new HostChannel();
                host.Start();
                using var deadline = new CancellationTokenSource(5000);
                await ExpectFailure(host.CallAsync(method, null, deadline.Token));
                await ExpectFailure(host.CallAsync("desktop_status", null, deadline.Token));
            }
            await using (var failed = new HostChannel())
            {
                failed.Start();
                using var deadline = new CancellationTokenSource(5000);
                var before = await failed.CallAsync("desktop_status", null, deadline.Token);
                using var oldProcess = Process.GetProcessById(before.GetProperty("pid").GetInt32());
                await ExpectFailure(failed.CallAsync("desktop_bad_frame", null, deadline.Token));
                await failed.DisposeAsync();
                if (!oldProcess.HasExited) throw new Exception("Channel disposal did not confirm old host exit");
                await using var replacement = new HostChannel();
                replacement.Start(allowAutostart: false);
                var after = await replacement.CallAsync("desktop_status", null, deadline.Token);
                if (!after.GetProperty("noAutostart").GetBoolean()) throw new Exception("Replacement host could autostart before reconciling user intent");
                await ExpectFailure(failed.CallAsync("desktop_status", null, deadline.Token));
            }
            await using (var host = new HostChannel())
            {
                int disconnected = 0;
                host.Disconnected += _ => Interlocked.Increment(ref disconnected);
                host.Start();
                using var deadline = new CancellationTokenSource(5000);
                var result = await host.CallAsync("desktop_exit", new { force = true }, deadline.Token);
                if (!result.GetProperty("exiting").GetBoolean()) throw new Exception("Exit reply was lost");
                await host.DisposeAsync();
                if (disconnected != 0) throw new Exception("Expected exit was reported as a lost connection");
            }
            await using (var host = new HostChannel())
            {
                host.Start();
                using var cancellation = new CancellationTokenSource();
                var calls = Enumerable.Range(0, 64).Select(_ => host.CallAsync("desktop_hold", null, cancellation.Token)).ToArray();
                await ExpectFailure(host.CallAsync("desktop_status", null, default), "desktop_busy");
                cancellation.Cancel();
                foreach (Task call in calls) await ExpectCancelled(call);
            }
            Environment.SetEnvironmentVariable("MIHOMO_PROTOCOL_FIXTURE", "dispose");
            await using (var host = new HostChannel())
            {
                host.Start();
                var sending = host.CallAsync("desktop_save_config", new { content = new string('a', 2 * 1024 * 1024), expectedRevision = "old" }, default);
                await Task.Delay(50);
                await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                await ExpectFailure(sending);
            }
            Console.WriteLine("Native child channel: cancelled large frame, bounded concurrency, malformed response, EOF, immediate exit reply and dispose during write passed.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MIHOMO_PROTOCOL_FIXTURE", previous);
            File.Delete(executable);
        }
    }

    [SupportedOSPlatform("windows")]
    public static async Task<int> BrokerAsync(string scope, string action)
    {
        try
        {
            using var broker = new InstanceBroker(scope);
            if (action == "server")
            {
                if (!broker.IsFirst) return 3;
                int calls = 0;
                broker.Listen(_ => Task.FromResult(Interlocked.Increment(ref calls) == 1));
                Console.WriteLine("READY");
                await Console.In.ReadLineAsync();
                return 0;
            }
            if (action == "unverified-server")
            {
                if (!broker.IsFirst) return 3;
                // This fixture deliberately does not authenticate its client.
                // It isolates the real client's server-image check: using two
                // authenticated endpoints races the server's Disconnect against
                // GetNamedPipeServerProcessId in the rejected client.
                using var pipe = new NamedPipeServerStream(BrokerPipeName(scope), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var connectionDeadline = new CancellationTokenSource(10000);
                Console.WriteLine("READY");
                await pipe.WaitForConnectionAsync(connectionDeadline.Token);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                string? command = await reader.ReadLineAsync(connectionDeadline.Token);
                Console.WriteLine(command is null ? "NO_COMMAND" : "UNEXPECTED_COMMAND");
                return command is null ? 0 : 6;
            }
            if (broker.IsFirst) return 4;
            using var deadline = new CancellationTokenSource(5000);
            return await broker.SendAsync("activate", deadline.Token) ? 0 : 5;
        }
        catch (UnauthorizedAccessException error) { Console.Error.WriteLine("REJECTED_PEER: " + error.Message); return 2; }
        catch (Exception error) { Console.Error.WriteLine(error); return 9; }
    }

    [SupportedOSPlatform("windows")]
    private static string BrokerPipeName(string scope)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.User!.Value + ":" + process.SessionId + ":" + scope)))[..24];
        return "MihomoOptimized.Native." + key;
    }

    private static Process StartBroker(string executable, string scope, string action)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--broker-fixture"); start.ArgumentList.Add(scope); start.ArgumentList.Add(action);
        return Process.Start(start) ?? throw new Exception("Cannot start broker fixture");
    }

    [SupportedOSPlatform("windows")]
    public static async Task TestBrokerAsync()
    {
        string ui = CopyAppHost(AppContext.BaseDirectory, "mihomo-optimized.exe");
        string otherDirectory = Path.Combine(Path.GetTempPath(), "MihomoBrokerTest-" + Guid.NewGuid().ToString("N"));
        string otherUi = CopyAppHost(otherDirectory, "mihomo-optimized.exe");
        using var deadline = new CancellationTokenSource(30000);
        try
        {
            string scope = Guid.NewGuid().ToString("N");
            using (var server = StartBroker(ui, scope, "server"))
            {
                try
                {
                    if (await server.StandardOutput.ReadLineAsync(deadline.Token) != "READY")
                        throw new Exception("Broker fixture did not start: " + await server.StandardError.ReadToEndAsync(deadline.Token));
                    using (var impostor = new NamedPipeClientStream(".", BrokerPipeName(scope), PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
                    {
                        await impostor.ConnectAsync(deadline.Token);
                        bool accepted = false;
                        try
                        {
                            using var writer = new StreamWriter(impostor, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                            using var reader = new StreamReader(impostor, leaveOpen: true);
                            await writer.WriteLineAsync("{\"command\":\"activate\"}");
                            accepted = await reader.ReadLineAsync(deadline.Token) is not null;
                        }
                        catch (IOException) { }
                        if (accepted) throw new Exception("A same-user process at the wrong image path reached the instance handler");
                    }
                    using var legitimate = StartBroker(ui, scope, "client");
                    await legitimate.WaitForExitAsync(deadline.Token);
                    if (legitimate.ExitCode != 0) throw new Exception($"Legitimate same-image broker exchange failed (exit {legitimate.ExitCode}): " + await legitimate.StandardError.ReadToEndAsync(deadline.Token));
                }
                finally
                {
                    server.StandardInput.Close();
                    await server.WaitForExitAsync(deadline.Token);
                }
            }
            Console.WriteLine("Native Windows instance broker: legitimate same-image client accepted; same-user wrong-image client rejected.");
            string foreignScope = Guid.NewGuid().ToString("N");
            using (var foreignServer = StartBroker(otherUi, foreignScope, "unverified-server"))
            {
                if (await foreignServer.StandardOutput.ReadLineAsync(deadline.Token) != "READY")
                    throw new Exception("Foreign-path broker fixture did not start: " + await foreignServer.StandardError.ReadToEndAsync(deadline.Token));
                using var client = StartBroker(ui, foreignScope, "client");
                await client.WaitForExitAsync(deadline.Token);
                string clientError = await client.StandardError.ReadToEndAsync(deadline.Token);
                string? observed = await foreignServer.StandardOutput.ReadLineAsync(deadline.Token);
                await foreignServer.WaitForExitAsync(deadline.Token);
                if (client.ExitCode != 2) throw new Exception($"Client did not reject a foreign-path server (exit {client.ExitCode}): {clientError}");
                if (observed != "NO_COMMAND" || foreignServer.ExitCode != 0)
                    throw new Exception($"Foreign-path server received a command or failed (observed {observed}, exit {foreignServer.ExitCode}): " + await foreignServer.StandardError.ReadToEndAsync(deadline.Token));
            }
            Console.WriteLine("Native Windows instance broker: client rejected another installation's server before transmitting any command.");
        }
        finally
        {
            File.Delete(ui);
            Directory.Delete(otherDirectory, true);
        }
    }
}
