using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Mihomo.Desktop.Services;

/// <summary>Opt-in, same-user acceptance-test pipe. Production starts expose no automation pipe.</summary>
internal sealed class AutomationServer : IDisposable
{
    private readonly string name;
    private readonly AppSession session;
    private readonly Func<string, JsonElement, Task<object?>> native;
    private readonly CancellationTokenSource lifetime = new();

    public AutomationServer(string name, AppSession session, Func<string, JsonElement, Task<object?>> native)
    {
        if (name.Length is < 1 or > 100 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new ArgumentException("Automation pipe name must use 1–100 letters, digits or hyphens.", nameof(name));
        this.name = name; this.session = session; this.native = native;
    }

    public void Start()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("Cannot identify the automation pipe owner");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        // Use the account SID explicitly; CurrentUserOnly uses token ownership,
        // which can differ between the filtered GUI and its test driver.
        var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 8192, 8192, security);
        _ = ListenAsync(pipe);
    }

    private async Task ListenAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(lifetime.Token);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 8192, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 8192, true) { AutoFlush = true };
                using var slots = new SemaphoreSlim(32);
                using var writes = new SemaphoreSlim(1);
                var requests = new List<Task>();
                var frames = new BoundedLineReader(reader, 32 * 1024 * 1024);
                try
                {
                    while (pipe.IsConnected && !lifetime.IsCancellationRequested)
                    {
                        string? line = await frames.ReadAsync(lifetime.Token);
                        if (line is null) break;
                        await slots.WaitAsync(lifetime.Token);
                        requests.RemoveAll(task => task.IsCompleted);
                        requests.Add(HandleAsync(line, writer, slots, writes));
                    }
                }
                finally { await Task.WhenAll(requests); }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or JsonException) { }
            finally { if (pipe.IsConnected) try { pipe.Disconnect(); } catch (IOException) { } }
        }
    }

    private async Task HandleAsync(string line, StreamWriter writer, SemaphoreSlim slots, SemaphoreSlim writes)
    {
        long id = 0;
        object reply;
        bool exitAfterReply = false;
        try
        {
            using var request = JsonDocument.Parse(line);
            id = request.RootElement.GetProperty("id").GetInt64();
            string method = HostChannel.Text(request.RootElement, "method");
            JsonElement args = request.RootElement.TryGetProperty("params", out var parameters) ? parameters.Clone() : JsonSerializer.SerializeToElement(new { });
            object? result;
            if (method.StartsWith("native_", StringComparison.Ordinal))
                result = await native(method, args);
            else if (method.StartsWith("desktop_", StringComparison.Ordinal))
                result = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => session.CommandAsync(method, args, lifetime.Token)).Task.Unwrap();
            else throw new DesktopException("unknown_method", "Unknown automation method");
            exitAfterReply = method == "desktop_exit" && result is JsonElement response && response.ValueKind == JsonValueKind.Object && response.TryGetProperty("exiting", out var exiting) && exiting.ValueKind == JsonValueKind.True;
            reply = new { id, result };
        }
        catch (Exception error) { reply = new { id, error = new { code = error is DesktopException desktop ? desktop.Code : "test_failed", message = error.Message } }; }
        try
        {
            await writes.WaitAsync(lifetime.Token);
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(reply, HostChannel.JsonOptions).AsMemory(), lifetime.Token); }
            finally { writes.Release(); }
            if (exitAfterReply) await native("native_finish_exit", JsonSerializer.SerializeToElement(new { }));
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { slots.Release(); }
    }

    public void Dispose() { lifetime.Cancel(); }
}
