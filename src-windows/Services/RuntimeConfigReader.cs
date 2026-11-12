using System.Text.Json;

namespace Mihomo.Desktop.Services;

/// <summary>
/// Coalesces overlapping page refreshes for one controller. A command's readback
/// always starts a new read and invalidates older results, even for the same URL.
/// Callbacks resume on the requesting UI context; controller credentials remain
/// an opaque, in-memory identity and are never recorded in diagnostics.
/// </summary>
internal sealed class RuntimeConfigReader(
    Func<string> currentSource,
    Func<CancellationToken, Task<JsonElement>> read,
    Action<JsonElement, bool> apply)
{
    private sealed record Request(string Source, bool AfterChange, CancellationToken Cancellation,
        TaskCompletionSource<bool> Completion);
    private readonly object gate = new();
    private Request? pending;

    internal bool IsReading
    {
        get { lock (gate) return pending is { } request && !request.Completion.Task.IsCompleted && !request.Cancellation.IsCancellationRequested; }
    }

    internal Task RefreshAsync(CancellationToken cancellation) => Start(false, cancellation);
    internal Task ReadAfterChangeAsync(CancellationToken cancellation) => Start(true, cancellation);

    private Task Start(bool afterChange, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Request request;
        lock (gate)
        {
            string source = currentSource();
            if (!afterChange && pending is { } existing && existing.Source == source
                && !existing.Completion.Task.IsCompleted && !existing.Cancellation.IsCancellationRequested)
                return existing.Completion.Task.WaitAsync(cancellation);
            request = new(source, afterChange, cancellation, new(TaskCreationOptions.RunContinuationsAsynchronously));
            pending = request;
        }
        _ = ReadAsync(request);
        return request.Completion.Task;
    }

    private async Task ReadAsync(Request request)
    {
        try
        {
            var result = await read(request.Cancellation);
            request.Cancellation.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (ReferenceEquals(pending, request) && request.Source == currentSource())
                    apply(result, request.AfterChange);
            }
            request.Completion.TrySetResult(true);
        }
        catch (OperationCanceledException) { request.Completion.TrySetCanceled(); }
        catch (Exception error)
        {
            lock (gate)
            {
                if (ReferenceEquals(pending, request) && request.Source == currentSource())
                    request.Completion.TrySetException(error);
                else request.Completion.TrySetCanceled();
            }
        }
        finally
        {
            lock (gate) if (ReferenceEquals(pending, request)) pending = null;
        }
    }
}
