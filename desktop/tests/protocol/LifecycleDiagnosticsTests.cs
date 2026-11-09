using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mihomo.Desktop.Services;

internal static class LifecycleDiagnosticsTests
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "mihomo-ui-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RecordsAreBoundedAndRotated(root);
            await BackpressureDoesNotBlockProducers(root);
            await WriteFailureDoesNotFaultCallers(root);
            await NetworkEventsAreReadOnlyAndCoalesced(root);
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Native lifecycle diagnostics: bounded queue/exit, file rotation, write-failure isolation, network loss/recovery and disposal passed.");
    }

    private static async Task RecordsAreBoundedAndRotated(string root)
    {
        string directory = Path.Combine(root, "rotation");
        await using var log = new LifecycleDiagnostics(directory, maximumFileBytes: 1100);
        for (int index = 0; index < 10; index++)
            Require(log.Record("fixture_record", new { index, value = "中文" }), "Normal diagnostic was dropped");
        Require(await log.FlushAsync(TimeSpan.FromSeconds(3)), "Diagnostic writer did not flush");
        string[] files = Directory.GetFiles(directory);
        Require(files.Length == 2 && files.All(file => new FileInfo(file).Length <= 1100), "Diagnostic rotation is not bounded");
        var records = File.ReadAllLines(Path.Combine(directory, LifecycleDiagnostics.FileName)).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Require(records.Length > 0, "No diagnostic records were retained");
            foreach (var record in records)
            {
                Require(record.RootElement.GetProperty("uiInstance").GetString() == log.Instance, "UI instance was not attached");
                Require(record.RootElement.GetProperty("schemaVersion").GetInt32() == 1, "Diagnostic schema version is absent");
                Require(record.RootElement.GetProperty("timeMs").GetInt64() > 0 && record.RootElement.GetProperty("sequence").GetInt64() > 0, "Capture metadata is absent");
                Require(record.RootElement.GetProperty("data").GetProperty("value").GetString() == "中文", "UTF-8 diagnostics changed data");
            }
        }
        finally { foreach (var record in records) record.Dispose(); }
        Require(LifecycleDiagnostics.Code("https://token@example.invalid/config") == "unclassified", "Invalid diagnostic codes were echoed");
        Require(LifecycleDiagnostics.FormatVersion(new Version(0, 2, 8, 0)) == "0.2.8", "UI diagnostics do not use the product version");
    }

    private static async Task BackpressureDoesNotBlockProducers(string root)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var records = new ConcurrentQueue<byte[]>();
        var log = new LifecycleDiagnostics(root, capacity: 4, testWriter: line =>
        {
            entered.Set();
            release.Wait();
            records.Enqueue(line);
        });
        try
        {
            log.Record("first_record", new { });
            Require(entered.Wait(TimeSpan.FromSeconds(2)), "Test writer did not start");
            var elapsed = Stopwatch.StartNew();
            for (int index = 0; index < 1000; index++) log.Record("queued_record", new { index });
            Require(elapsed.Elapsed < TimeSpan.FromSeconds(1) && log.Dropped >= 996, "Diagnostic producers waited for stalled output");
            Require(!await log.FlushAsync(TimeSpan.FromMilliseconds(30)), "Saturated writer reported a false barrier");
            elapsed.Restart();
            await log.DisposeAsync();
            Require(elapsed.Elapsed < TimeSpan.FromSeconds(1), "A stalled diagnostic writer blocked exit");
        }
        finally
        {
            release.Set();
            await log.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Require(records.Any(line => JsonDocument.Parse(line).RootElement.GetProperty("droppedTotal").GetInt64() > 0), "Dropped diagnostic count was hidden");
    }

    private static async Task WriteFailureDoesNotFaultCallers(string root)
    {
        string directory = Path.Combine(root, "blocked-path");
        File.WriteAllText(directory, "this path is not a directory");
        await using var log = new LifecycleDiagnostics(directory);
        log.Record("before_recovery", new { });
        Require(!await log.FlushAsync(TimeSpan.FromSeconds(3)) && log.WriteFailures == 1, "Output failure was not observed");
        File.Delete(directory);
        log.Record("after_recovery", new { });
        await log.FlushAsync(TimeSpan.FromSeconds(3));
        using var recovered = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, LifecycleDiagnostics.FileName)));
        Require(recovered.RootElement.GetProperty("writeFailuresTotal").GetInt64() == 1, "Recovered output omitted its earlier failure");
    }

    private static async Task NetworkEventsAreReadOnlyAndCoalesced(string root)
    {
        var rows = new ConcurrentQueue<JsonElement>();
        await using var log = new LifecycleDiagnostics(root, testWriter: line => rows.Enqueue(JsonDocument.Parse(line).RootElement.Clone()));
        int probes = 0;
        string wifiId = "12222222-2222-2222-2222-222222222222";
        string tunId = "23333333-3333-3333-3333-333333333333";
        NetworkEvidence current = new(true, [new(wifiId, 3, "wifi", "up", true), new(tunId, 7, "ethernet", "up", false)]);
        using var observer = new NetworkDiagnostics(log, () => { Interlocked.Increment(ref probes); return Volatile.Read(ref current); }, subscribe: false);
        observer.Start();
        await WaitUntil(() => Volatile.Read(ref probes) == 1 && observer.IsIdle);
        await log.FlushAsync(TimeSpan.FromSeconds(2));
        Require(rows.Count == 1, "Initial network snapshot is missing");
        for (int index = 0; index < 20; index++) observer.Schedule(2);
        await WaitUntil(() => Volatile.Read(ref probes) >= 2 && observer.IsIdle);
        await log.FlushAsync(TimeSpan.FromSeconds(2));
        Require(probes == 2 && rows.Count == 2, "Repeated network notifications were not retained as one batch");
        var same = rows.Last();
        var sameData = same.GetProperty("data");
        Require(same.GetProperty("event").GetString() == "network_change_observed" && !sameData.GetProperty("stateChanged").GetBoolean(), "Same-state OS notifications were misrepresented as a changed snapshot");
        Require(sameData.GetProperty("notificationCount").GetInt32() == 20, "Coalesced notification count is incorrect");
        Require(sameData.GetProperty("firstNotificationMs").GetInt64() <= sameData.GetProperty("lastNotificationMs").GetInt64()
            && sameData.GetProperty("lastNotificationMs").GetInt64() <= same.GetProperty("timeMs").GetInt64(), "Network notifications lost their observation time");

        // Both OS notifications arrive before the delayed probe: the sample
        // returns to its original state, but the transient event must remain.
        Volatile.Write(ref current, new(false, [new(wifiId, 3, "wifi", "down", true), new(tunId, 7, "ethernet", "down", false)]));
        observer.Schedule(4, availability: false);
        Volatile.Write(ref current, new(true, [new(wifiId, 3, "wifi", "up", true), new(tunId, 7, "ethernet", "up", false)]));
        observer.Schedule(4, availability: true);
        await WaitUntil(() => Volatile.Read(ref probes) >= 3 && observer.IsIdle);
        await log.FlushAsync(TimeSpan.FromSeconds(2));
        var transient = rows.Last().GetProperty("data");
        Require(rows.Count == 3 && !transient.GetProperty("stateChanged").GetBoolean(), "A transient down/up notification batch disappeared");
        Require(transient.GetProperty("notificationCount").GetInt32() == 2
            && transient.GetProperty("availableNotified").GetBoolean()
            && transient.GetProperty("unavailableNotified").GetBoolean(), "Transient availability notifications were discarded");

        Volatile.Write(ref current, new(true, [new(wifiId, 3, "wifi", "down", true), new(tunId, 7, "ethernet", "up", false)]));
        observer.Schedule(2);
        await WaitUntil(() => Volatile.Read(ref probes) >= 4 && observer.IsIdle);
        await log.FlushAsync(TimeSpan.FromSeconds(2));
        var changed = rows.Last().GetProperty("data");
        Require(rows.Count == 4 && changed.GetProperty("networkAvailable").GetBoolean(), "Availability was incorrectly treated as Wi-Fi link state");
        Require(rows.Last().GetProperty("event").GetString() == "network_snapshot_changed" && changed.GetProperty("stateChanged").GetBoolean(), "Changed network state was not identified");
        Require(changed.GetProperty("interfaces")[0].GetProperty("status").GetString() == "down", "Wi-Fi loss did not retain per-interface evidence");
        Require(changed.GetProperty("interfaces")[1].GetProperty("status").GetString() == "up", "A healthy virtual interface was classified as down");

        // Notifications keep arriving faster than the coalescing interval.
        // Capture must still run while the stream is active, not after it ends.
        int beforeContinuous = probes;
        using (var stream = new CancellationTokenSource())
        {
            var notifications = Task.Run(async () =>
            {
                try
                {
                    while (!stream.IsCancellationRequested)
                    {
                        observer.Schedule(2);
                        await Task.Delay(25, stream.Token);
                    }
                }
                catch (OperationCanceledException) when (stream.IsCancellationRequested) { }
            });
            try { await WaitUntil(() => Volatile.Read(ref probes) >= beforeContinuous + 2); }
            finally { stream.Cancel(); await notifications; }
        }
        await WaitUntil(() => observer.IsIdle);
        observer.Dispose();
        int before = probes;
        observer.Schedule(2);
        await Task.Delay(350);
        Require(probes == before, "Disposed network observer still queried the OS");

        var normalized = NetworkDiagnostics.Normalize(new(true, [new("SSID=private-network", 9, "wifi", "up", false), new(wifiId.ToUpperInvariant(), 3, "wifi", "up", true)]));
        string json = JsonSerializer.Serialize(normalized);
        Require(!json.Contains("SSID", StringComparison.Ordinal) && normalized.Interfaces[1].DeviceId == wifiId, "Network evidence retained a non-GUID device label");
    }

    private static async Task WaitUntil(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!ready()) await Task.Delay(15, deadline.Token);
    }
}
