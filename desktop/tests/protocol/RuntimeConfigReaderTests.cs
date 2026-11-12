using System.Text.Json;
using Mihomo.Desktop.Services;

internal static class RuntimeConfigReaderTests
{
    internal static async Task RunAsync()
    {
        await CoalesceOverlappingRefreshes();
        await ReadBackAfterAChange();
        await DiscardRetiredControllerResults();
        await IsolateCancellationAndRetryFailures();
        Console.WriteLine("Runtime config reads: overlapping refreshes coalesce, command readbacks are fresh, retired controllers cannot publish values/errors, and cancellation remains scoped.");
    }

    private sealed class Fixture
    {
        internal string Source = "host-a/controller-a/pid-1";
        internal readonly List<TaskCompletionSource<JsonElement>> Reads = [];
        internal readonly List<(string Value, bool AfterChange)> Applied = [];
        internal readonly RuntimeConfigReader Reader;

        internal Fixture()
        {
            Reader = new(() => Source, _ =>
            {
                var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                Reads.Add(completion);
                return completion.Task;
            }, (value, afterChange) => Applied.Add((value.GetProperty("mode").GetString()!, afterChange)));
        }
        internal void Complete(int index, string mode) => Reads[index].SetResult(JsonSerializer.SerializeToElement(new { mode }));
    }

    private static async Task CoalesceOverlappingRefreshes()
    {
        var fixture = new Fixture();
        Task first = fixture.Reader.RefreshAsync(default);
        Task duplicate = fixture.Reader.RefreshAsync(default);
        Check(fixture.Reads.Count == 1 && fixture.Reader.IsReading, "Overlapping page/status refreshes issued duplicate GETs");
        fixture.Complete(0, "rule");
        await Task.WhenAll(first, duplicate);
        Check(fixture.Applied.SequenceEqual(new[] { ("rule", false) }) && !fixture.Reader.IsReading,
            "A coalesced read was applied more than once or retained a busy state");
        Task later = fixture.Reader.RefreshAsync(default);
        Check(fixture.Reads.Count == 2, "A completed read became an indefinite cache");
        fixture.Complete(1, "global");
        await later;
    }

    private static async Task ReadBackAfterAChange()
    {
        foreach (bool oldCompletesFirst in new[] { true, false })
        {
            var fixture = new Fixture();
            Task old = fixture.Reader.RefreshAsync(default);
            Task readback = fixture.Reader.ReadAfterChangeAsync(default);
            Task observer = fixture.Reader.RefreshAsync(default);
            Check(fixture.Reads.Count == 2, "A command reused a pre-write GET or a concurrent observer duplicated its readback");
            if (oldCompletesFirst) { fixture.Complete(0, "rule"); await old; }
            fixture.Complete(1, "global");
            await Task.WhenAll(readback, observer);
            if (!oldCompletesFirst) { fixture.Complete(0, "rule"); await old; }
            Check(fixture.Applied.SequenceEqual(new[] { ("global", true) }), "A pre-write result replaced the confirmed command readback");
        }
    }

    private static async Task DiscardRetiredControllerResults()
    {
        var fixture = new Fixture();
        Task old = fixture.Reader.RefreshAsync(default);
        fixture.Source = "host-a/controller-a/pid-2"; // The endpoint can be reused by a replacement process.
        Task replacement = fixture.Reader.RefreshAsync(default);
        Check(fixture.Reads.Count == 2, "A replacement controller joined the retired process's request");
        fixture.Complete(1, "global");
        await replacement;
        fixture.Reads[0].SetException(new IOException("retired controller"));
        await Cancelled(old);
        Check(fixture.Applied.SequenceEqual(new[] { ("global", false) }), "A retired controller changed the new display or surfaced an old failure");

        Task pending = fixture.Reader.RefreshAsync(default);
        fixture.Source = "host-b/controller-b/pid-3";
        fixture.Complete(2, "direct");
        await pending;
        Check(fixture.Applied.Count == 1, "A source change without a replacement read still accepted the old result");
    }

    private static async Task IsolateCancellationAndRetryFailures()
    {
        var fixture = new Fixture();
        Task owner = fixture.Reader.RefreshAsync(default);
        using var waiterCancellation = new CancellationTokenSource();
        Task waiter = fixture.Reader.RefreshAsync(waiterCancellation.Token);
        waiterCancellation.Cancel();
        await Cancelled(waiter);
        fixture.Complete(0, "rule");
        await owner;
        Check(fixture.Applied.Count == 1, "Cancelling one waiter cancelled the shared owner");

        using var oldPage = new CancellationTokenSource();
        Task retiredPage = fixture.Reader.RefreshAsync(oldPage.Token);
        oldPage.Cancel();
        Task newPage = fixture.Reader.RefreshAsync(default);
        Check(fixture.Reads.Count == 3, "A reopened page joined a cancelled request");
        fixture.Complete(1, "direct");
        await Cancelled(retiredPage);
        fixture.Complete(2, "global");
        await newPage;
        Check(fixture.Applied.SequenceEqual(new[] { ("rule", false), ("global", false) }), "A cancelled page rendered late data");

        Task failed = fixture.Reader.RefreshAsync(default);
        fixture.Reads[3].SetException(new IOException("current controller"));
        try { await failed; throw new Exception("A current read failure was hidden"); }
        catch (IOException) { }
        Task retry = fixture.Reader.RefreshAsync(default);
        Check(fixture.Reads.Count == 5, "A failed read poisoned subsequent refreshes");
        fixture.Complete(4, "rule");
        await retry;
    }

    private static async Task Cancelled(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
