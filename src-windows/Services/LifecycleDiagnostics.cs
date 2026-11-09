using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Mihomo.Desktop.Services;

/// <summary>UI-owned lifecycle evidence. Producers never wait for the filesystem.</summary>
internal sealed class LifecycleDiagnostics : IAsyncDisposable
{
    internal const string FileName = "ui-recovery.log";
    internal const string PreviousFileName = "ui-recovery.previous.log";
    internal static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mihomo Optimized", "logs");
    private sealed record Entry(long TimeMs, long Sequence, long ElapsedMs, string Event, object Data);
    private sealed record Work(Entry? Entry, TaskCompletionSource<bool>? Barrier);
    private readonly Channel<Work> queue;
    private readonly Task worker;
    private readonly string directory;
    private readonly long maximumFileBytes;
    private readonly Action<byte[]>? testWriter;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly string appVersion = FormatVersion(typeof(LifecycleDiagnostics).Assembly.GetName().Version);
    private long sequence;
    private long dropped;
    private long writeFailures;
    private int disposed;
    public string Instance { get; } = Guid.NewGuid().ToString("N");
    internal long Dropped => Interlocked.Read(ref dropped);
    internal long WriteFailures => Interlocked.Read(ref writeFailures);
    internal Task Completion => worker;

    internal LifecycleDiagnostics(string directory, int capacity = 256, long maximumFileBytes = 1024 * 1024, Action<byte[]>? testWriter = null)
    {
        if (capacity < 1 || maximumFileBytes < 256) throw new ArgumentOutOfRangeException();
        this.directory = directory;
        this.maximumFileBytes = maximumFileBytes;
        this.testWriter = testWriter;
        queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        worker = Task.Run(WriteLoopAsync);
    }

    // All callers pass fixed categories and small snapshots, never request
    // arguments, controller credentials, paths, exception messages or URLs.
    internal bool Record(string name, object fields)
    {
        if (Volatile.Read(ref disposed) != 0) return false;
        var entry = new Entry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Interlocked.Increment(ref sequence),
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, Code(name), fields);
        if (queue.Writer.TryWrite(new Work(entry, null))) return true;
        Interlocked.Increment(ref dropped);
        return false;
    }

    internal async Task<bool> FlushAsync(TimeSpan limit)
    {
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.Writer.TryWrite(new Work(null, barrier))) return false;
        try { return await barrier.Task.WaitAsync(limit).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var work in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (work.Barrier is not null)
            {
                work.Barrier.TrySetResult(WriteFailures == 0);
                continue;
            }
            var entry = work.Entry!;
            try
            {
                byte[] line = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 1, timeMs = entry.TimeMs, sequence = entry.Sequence, elapsedMs = entry.ElapsedMs,
                    @event = entry.Event, processId = Environment.ProcessId, uiInstance = Instance,
                    appVersion, droppedTotal = Dropped, writeFailuresTotal = WriteFailures, data = entry.Data
                }, HostChannel.JsonOptions);
                if (line.Length > 16 * 1024 || line.Length + 1 > maximumFileBytes)
                {
                    Interlocked.Increment(ref dropped);
                    continue;
                }
                if (testWriter is not null) testWriter(line);
                else Append(line);
            }
            catch (Exception)
            {
                // Disk and serialization failures cannot fault the UI or become
                // another host-recovery trigger. A later record exposes the count.
                Interlocked.Increment(ref writeFailures);
            }
        }
    }

    private void Append(byte[] line)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);
        if (File.Exists(path) && new FileInfo(path).Length + line.Length + 1 > maximumFileBytes)
            File.Move(path, Path.Combine(directory, PreviousFileName), true);
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
        file.Write(line);
        file.WriteByte((byte)'\n');
    }

    internal static string Code(string? value) => value is { Length: > 0 and <= 96 }
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
        ? value : "unclassified";

    internal static string ErrorCode(Exception error) => error is DesktopException desktop ? Code(desktop.Code)
        : error is OperationCanceledException ? "operation_cancelled" : "unexpected_exception";

    internal static string FormatVersion(Version? version) => version is null ? "unknown"
        : version.Build >= 0 ? version.ToString(3) : version.ToString();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        queue.Writer.TryComplete();
        try { await worker.WaitAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false); }
        catch (TimeoutException) { /* A stalled disk must not hold application exit. */ }
    }
}

/// <summary>Local-only context; it is deliberately absent from the IPC request schema.</summary>
internal sealed record HostCallContext(string Origin, long? IntentRevision = null, long? CommandGeneration = null);
