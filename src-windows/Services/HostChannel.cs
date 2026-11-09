using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mihomo.Desktop.Services;

public sealed class DesktopException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The private child pipe carries commands; no local TCP listener or shared secret file.</summary>
internal sealed class HostChannel : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal const int MaximumRequestBytes = 4 * 1024 * 1024 * 6 + 4096;
    private sealed record PendingCall(string Method, TaskCompletionSource<JsonElement> Completion);
    private readonly ConcurrentDictionary<long, PendingCall> pending = new();
    private readonly SemaphoreSlim requests = new(64, 64);
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly LifecycleDiagnostics? diagnostics;
    private string? hostInstance;
    private int? hostPid;
    private int exitRecorded;
    private Process? process;
    private Task? reader;
    private Exception? failure;
    private long sequence;
    private readonly object disposeGate = new();
    private Task? disposal;
    private volatile bool closing;
    public event Action<string, JsonElement>? Event;
    public event Action<Exception>? Disconnected;
    internal string ChannelId { get; } = Guid.NewGuid().ToString("N");
    internal string? HostInstance => Volatile.Read(ref hostInstance);
    internal int? HostPid => hostPid;

    public HostChannel(LifecycleDiagnostics? diagnostics = null)
    {
        this.diagnostics = diagnostics;
        lifetimeToken = lifetime.Token;
    }

    public void Start(bool allowAutostart = true, string origin = "initial_start")
    {
        if (process is not null) throw new InvalidOperationException("后台已经启动");
        string executable = Path.Combine(AppContext.BaseDirectory, "mihomo-desktop-host.exe");
        if (!File.Exists(executable)) throw new DesktopException("host_missing", "缺少 mihomo-desktop-host.exe，请重新安装完整客户端。");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add("--stdio");
        start.ArgumentList.Add("--parent-pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!allowAutostart) start.ArgumentList.Add("--no-autostart");
        process = Process.Start(start) ?? throw new DesktopException("host_start_failed", "无法启动桌面后台");
        hostPid = process.Id;
        Process ownedProcess = process;
        try
        {
            process.Exited += (_, _) => RecordExit(ownedProcess);
            process.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Record("host_exit_observer_unavailable", ("errorType", error.GetType().Name));
        }
        Record("host_process_started", ("allowAutostart", allowAutostart), ("origin", LifecycleDiagnostics.Code(origin)));
        reader = ReadAsync(process, lifetimeToken);
        // Diagnostics can contain paths; drain stderr without forwarding potentially sensitive text into the UI.
        _ = DrainAsync(process.StandardError, lifetimeToken);
    }

    public async Task<JsonElement> CallAsync(string method, object? arguments, CancellationToken cancellationToken, HostCallContext? context = null)
    {
        long began = Stopwatch.GetTimestamp();
        long? id = null;
        bool acquired = false;
        bool sent = false;
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var child = process ?? throw new DesktopException("host_unavailable", "桌面后台尚未连接");
            if (Volatile.Read(ref failure) is Exception disconnected) throw disconnected;
            if (closing || child.HasExited) throw new DesktopException("host_unavailable", "桌面后台已退出，请重新打开应用");
            if (!requests.Wait(0)) throw new DesktopException("desktop_busy", "待处理操作过多，请稍后再试");
            acquired = true;
            id = Interlocked.Increment(ref sequence);
            if (!pending.TryAdd(id.Value, new PendingCall(method, completion))) throw new InvalidOperationException();
            var request = JsonSerializer.Serialize(new { id, method, @params = arguments ?? new { } }, JsonOptions);
            // Match Rust's bound, including the Windows CRLF frame terminator.
            if (Encoding.UTF8.GetByteCount(request) + 2 > MaximumRequestBytes)
                throw new DesktopException("request_too_large", "操作内容过大");
            await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref failure) is Exception unavailable) throw unavailable;
                if (closing) throw new DesktopException("host_unavailable", "桌面后台正在退出");
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Once a frame starts, caller cancellation must not leave a
                    // partial JSON line for the next request to append to.
                    await child.StandardInput.WriteLineAsync(request.AsMemory(), lifetimeToken).ConfigureAwait(false);
                    await child.StandardInput.FlushAsync(lifetimeToken).ConfigureAwait(false);
                    sent = true;
                    if (IsLifecycleCommand(method)) RecordRequest("host_request_sent", id, method, context, "sent", null, true, began);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
                {
                    var broken = new DesktopException("host_disconnected", "桌面后台通信中断；请退出后重新打开应用。");
                    Fault(broken, "request_write", error.GetType().Name);
                    throw broken;
                }
            }
            finally { writes.Release(); }
            var result = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (IsLifecycleCommand(method)) RecordRequest("host_request_result", id, method, context, "succeeded", null, sent, began);
            return result;
        }
        catch (Exception error)
        {
            RecordRequest("host_request_result", id, method, context, error is OperationCanceledException ? "cancelled" : "failed", LifecycleDiagnostics.ErrorCode(error), sent, began);
            throw;
        }
        finally
        {
            if (id is long requestId) pending.TryRemove(requestId, out _);
            if (acquired) requests.Release();
        }
    }

    private async Task ReadAsync(Process child, CancellationToken cancellationToken)
    {
        Exception errorResult = new DesktopException("host_disconnected", "桌面后台已断开；请退出后重新打开应用。");
        string reason = "pipe_eof";
        string? errorType = null;
        try
        {
            var frames = new BoundedLineReader(child.StandardOutput, 32 * 1024 * 1024);
            while (await frames.ReadAsync(cancellationToken).ConfigureAwait(false) is string line)
            {
                using var document = JsonDocument.Parse(line);
                JsonElement message = document.RootElement;
                if (message.TryGetProperty("event", out var name))
                {
                    var data = message.TryGetProperty("data", out var value) ? value.Clone() : JsonSerializer.SerializeToElement(new { });
                    if (name.GetString() == "desktop-status") ObserveHostInstance(data);
                    Event?.Invoke(name.GetString() ?? "", data);
                    continue;
                }
                if (!message.TryGetProperty("id", out var responseId) || !responseId.TryGetInt64(out long id) || !pending.TryGetValue(id, out var call)) continue;
                if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                    call.Completion.TrySetException(new DesktopException(Text(error, "code", "operation_failed"), Text(error, "message", "操作失败")));
                else
                {
                    var result = message.TryGetProperty("result", out var value) ? value.Clone() : JsonSerializer.SerializeToElement<object?>(null);
                    ObserveHostInstance(result);
                    if (call.Method is "desktop_exit" or "desktop_install_update" && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("exiting", out var leaving) && leaving.ValueKind == JsonValueKind.True)
                        ExpectExit();
                    call.Completion.TrySetResult(result);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { reason = "reader_cancelled"; }
        catch (Exception error) { reason = "protocol_read"; errorType = error.GetType().Name; errorResult = new DesktopException("host_protocol_error", "桌面后台通信中断：" + error.GetType().Name); }
        finally { RecordExit(child); Fault(errorResult, reason, errorType); }
    }

    private void CloseInput(string reason)
    {
        // Dispose the pipe itself, not StreamWriter: Dispose/Close would flush
        // buffered data and can race an in-flight WriteLineAsync or block here.
        Record("host_input_closed", ("reason", reason), ("expectedExit", closing));
        try { process?.StandardInput.BaseStream.Dispose(); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void Fault(Exception error, string reason, string? errorType)
    {
        if (Interlocked.CompareExchange(ref failure, error, null) is not null) return;
        Record("host_channel_fault", ("reason", reason), ("code", LifecycleDiagnostics.ErrorCode(error)), ("errorType", errorType), ("expectedExit", closing), ("processState", ProcessState()));
        lifetime.Cancel();
        CloseInput("channel_fault");
        foreach (var request in pending.Values) request.Completion.TrySetException(error);
        if (!closing) Disconnected?.Invoke(error);
    }

    private static async Task DrainAsync(StreamReader stream, CancellationToken cancellationToken)
    {
        try { var buffer = new char[2048]; while (await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) != 0) { } }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    internal static string Text(JsonElement element, string key, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    public void ExpectExit()
    {
        closing = true;
        Record("host_exit_expected", ("processState", ProcessState()));
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate) return new ValueTask(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        long began = Stopwatch.GetTimestamp();
        string outcome = "not_started";
        string? errorCode = null;
        bool killIssued = false;
        Record("host_dispose_started", ("processState", ProcessState()), ("expectedExit", closing));
        closing = true;
        lifetime.Cancel();
        CloseInput("dispose");
        try
        {
            if (process is not null)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                    outcome = "exited";
                }
                catch (OperationCanceledException)
                {
                    // The independent proxy watchdog and update monitor must survive host failure.
                    Record("host_termination_requested", ("reason", "dispose_deadline"), ("processState", ProcessState()));
                    try { process.Kill(entireProcessTree: false); killIssued = true; }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        Record("host_termination_failed", ("errorType", error.GetType().Name), ("processState", ProcessState()));
                    }
                    using var confirmation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await process.WaitForExitAsync(confirmation.Token).ConfigureAwait(false); outcome = killIssued ? "exit_confirmed_after_force" : "exit_confirmed"; }
                    catch (OperationCanceledException)
                    {
                        outcome = "exit_unconfirmed";
                        // Keep the owned handle and the fault latch. A caller must
                        // not start a replacement while the old host may be alive.
                        throw new DesktopException("host_termination_unconfirmed", "无法确认旧桌面后台已退出，已停止自动恢复。");
                    }
                }
                RecordExit(process);
                if (reader is not null) try { await reader.ConfigureAwait(false); } catch (OperationCanceledException) { }
                process.Dispose();
            }
            lifetime.Dispose();
        }
        catch (Exception error)
        {
            if (outcome != "exit_unconfirmed") outcome = "failed";
            errorCode = LifecycleDiagnostics.ErrorCode(error);
            throw;
        }
        finally
        {
            Record("host_dispose_finished", ("outcome", outcome), ("code", errorCode), ("killIssued", killIssued), ("elapsedMs", (long)Stopwatch.GetElapsedTime(began).TotalMilliseconds));
        }
    }

    private void ObserveHostInstance(JsonElement value)
    {
        string identity = Text(value, "statusInstance");
        if (identity.Length is 0 or > 96 || !System.Linq.Enumerable.All(identity, character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) return;
        string? previous = Interlocked.CompareExchange(ref hostInstance, identity, null);
        if (previous is null) Record("host_instance_identified");
        else if (previous != identity) Record("host_instance_changed", ("observedInstance", identity));
    }

    internal string ProcessState()
    {
        try { return process is null ? "not_started" : process.HasExited ? "exited" : "alive"; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return "unknown"; }
    }

    private void RecordExit(Process child)
    {
        try
        {
            if (!child.HasExited) return;
            int exitCode = child.ExitCode;
            if (Interlocked.Exchange(ref exitRecorded, 1) == 0)
                Record("host_process_exit", ("exitCode", exitCode), ("expectedExit", closing));
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private void RecordRequest(string name, long? id, string method, HostCallContext? context, string outcome, string? code, bool sent, long began)
    {
        Record(name, ("requestId", id), ("command", KnownCommand(method)), ("origin", LifecycleDiagnostics.Code(context?.Origin ?? "unspecified")),
            ("uiIntentRevision", context?.IntentRevision), ("commandGeneration", context?.CommandGeneration),
            ("outcome", outcome), ("code", code), ("sent", sent), ("elapsedMs", (long)Stopwatch.GetElapsedTime(began).TotalMilliseconds));
    }

    private static bool IsLifecycleCommand(string method) => method is "desktop_start" or "desktop_stop" or "desktop_restart" or "desktop_reload_config"
        or "desktop_set_tun" or "desktop_set_system_proxy" or "desktop_install_service" or "desktop_uninstall_service" or "desktop_exit"
        or "desktop_install_update" or "desktop_patch_config" or "desktop_save_config" or "desktop_import_config" or "desktop_import_config_url";
    private static string KnownCommand(string method) => IsLifecycleCommand(method) || method is "desktop_status" or "desktop_set_dirty" or "desktop_check_update" ? method : "other_command";

    private void Record(string name, params (string Key, object? Value)[] fields)
    {
        if (diagnostics is null) return;
        var data = new Dictionary<string, object?> { ["channelId"] = ChannelId, ["hostInstance"] = HostInstance, ["hostPid"] = hostPid };
        foreach (var field in fields) data[field.Key] = field.Value;
        diagnostics.Record(name, data);
    }
}

internal static class BoundedLine
{
    // Single-message pipe exchanges have no following frame to retain.
    public static Task<string?> ReadAsync(StreamReader reader, int maximum, CancellationToken cancellationToken) =>
        new BoundedLineReader(reader, maximum).ReadAsync(cancellationToken);
}

internal sealed class BoundedLineReader(StreamReader reader, int maximum)
{
    private readonly char[] buffer = new char[8192];
    private int offset;
    private int available;

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        while (true)
        {
            if (offset == available)
            {
                available = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                offset = 0;
                if (available == 0) return text.Length == 0 ? null : throw new InvalidDataException("Incomplete IPC frame");
            }
            int newline = Array.IndexOf(buffer, '\n', offset, available - offset);
            int end = newline < 0 ? available : newline;
            int count = end - offset;
            if (text.Length + count > maximum) throw new InvalidDataException("IPC frame too large");
            text.Append(buffer, offset, count);
            offset = newline < 0 ? end : end + 1;
            if (newline >= 0) return text.ToString().TrimEnd('\r');
        }
    }
}
