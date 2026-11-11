using System.Text.Json;
using Mihomo.Desktop.Services;

internal static class CoreUpdateStateTests
{
    public static void Run()
    {
        ReadsTheActualHostPolicy();
        CountsDownUsingElapsedTime();
        RejectsInventedVersionsAndSchedules();
        DoesNotInferRequestSuccessFromVersion();
        Console.WriteLine("Core update observations: applied policy, host-owned task state, monotonic schedules, request identity, and malformed metadata passed.");
    }

    private static void ReadsTheActualHostPolicy()
    {
        var disabled = Read(values => { values["auto_update"] = false; values["status"] = "skipped"; values["next_check_in_ms"] = 5000; });
        Check(disabled.Available && !disabled.Active && disabled.NextCheck(DateTimeOffset.UtcNow, 1000) is null,
            "A disabled policy exposed a stale automatic schedule");
        foreach (var phase in new[] { "checking", "restarting" })
            Check(Read(values => values["status"] = phase).Active, "An active host task was presented as idle");
        Check(Read(values => { values["status"] = "pending"; values["request_id"] = "accepted"; }).Active,
            "An accepted queued task stopped polling or allowed another update write");
        foreach (var phase in new[] { "pending", "updated", "up_to_date", "skipped", "error" })
            Check(!Read(values => values["status"] = phase).Active, "A terminal host observation blocked the next explicit request");
    }

    private static void CountsDownUsingElapsedTime()
    {
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.FromHours(8));
        var state = Read(values => values["next_check_in_ms"] = 30000, requestedAt: 1000);
        Check(state.NextCheck(now, 11000) == now.AddSeconds(20), "Polling time was counted as extra waiting time");
        Check(state.NextCheck(now, 32000) < now, "A due schedule was silently moved into the future");
        Check(state.NextCheck(now, 500) == now.AddSeconds(30), "An earlier monotonic value increased the remaining wait");
        var missing = Read(values => { values["next_check_in_ms"] = null; values["auto_update_state"] = "waiting"; });
        Check(missing.NextCheck(now, 1000) is null, "An unscheduled task invented a time from its interval");
        var absolute = Read(values => { values.Remove("next_check_in_ms"); values["next_check_at"] = now.AddMinutes(3).ToString("O"); });
        Check(absolute.NextCheck(now, 1000) == now.AddMinutes(3), "An absolute host schedule changed its instant");
        var longInterval = Read(values => values["next_check_in_ms"] = 3651L * 86400000);
        Check(longInterval.NextCheck(now, 1000) == now.AddDays(3651), "A valid long interval was silently shortened");
        var unrepresentable = Read(values => values["next_check_in_ms"] = long.MaxValue);
        Check(unrepresentable.NextCheck(now, 1000) is null, "An out-of-range schedule invented an arbitrary future date");
    }

    private static void RejectsInventedVersionsAndSchedules()
    {
        foreach (var change in new Action<Dictionary<string, object?>>[]
        {
            values => values.Remove("auto_update"),
            values => values["auto_update"] = "true",
            values => values["status"] = "ready",
            values => values["kind"] = "ui",
            values => values["update_interval"] = 0,
            values => values["update_interval"] = "24",
            values => values["interval_unit"] = "seconds",
            values => values["current_version"] = 29,
            values => values["latest_version"] = new { value = "1.0.29" },
            values => values["next_check_in_ms"] = -1,
            values => values["next_check_in_ms"] = 1.5,
            values => values["request_id"] = 1
        })
        {
            try { Read(change); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Malformed host metadata was shown as a valid core update observation");
        }
        Check(!new CoreUpdateState().Available, "A missing host status invented an automatic-update policy");
    }

    private static void DoesNotInferRequestSuccessFromVersion()
    {
        var previous = Read(values => { values["status"] = "updated"; values["request_id"] = "previous"; values["current_version"] = "1.0.29"; });
        Check(previous.RequestId == "previous" && previous.RequestId != "new-request",
            "A previous successful task was attributed to a new request");
        var confirmed = Read(values => { values["status"] = "checking"; values["request_id"] = "new-request"; });
        Check(confirmed.RequestId == "new-request" && confirmed.Active, "An accepted request lost its identity while running");
    }

    private static CoreUpdateState Read(Action<Dictionary<string, object?>>? change = null, long requestedAt = 1000)
    {
        var values = new Dictionary<string, object?>
        {
            ["id"] = "kernel", ["kind"] = "kernel", ["name"] = "kernel", ["status"] = "pending",
            ["current_version"] = "1.0.28", ["latest_version"] = "1.0.29", ["auto_update"] = true,
            ["update_interval"] = 24, ["interval_unit"] = "hours", ["auto_update_state"] = "scheduled",
            ["next_check_in_ms"] = 30000, ["request_id"] = ""
        };
        change?.Invoke(values);
        return CoreUpdateState.Read(JsonSerializer.SerializeToElement(values), requestedAt);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
