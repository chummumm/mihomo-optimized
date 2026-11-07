using System.Text.Json;
using Mihomo.Desktop.Services;

internal static class DesktopStatusTests
{
    public static void Run()
    {
        RejectDelayedObservations();
        SeparateProcessAndTunHealth();
        Console.WriteLine("Desktop status: ordered host observations, Stop intent, TUN uncertainty/failure, live controller access, and confirmed exit passed.");
    }

    private static void RejectDelayedObservations()
    {
        var order = new DesktopStatusOrder();
        Check(order.TryAccept(Snapshot("host-a", 4, 1)), "Initial observation was rejected");
        Check(order.TryAccept(Snapshot("host-a", 5, 2, phase: "stopping")), "Stop intent was rejected");
        Check(!order.TryAccept(Snapshot("host-a", 4, 1, phase: "running")), "Delayed Start reply undid Stop");
        Check(!order.TryAccept(Snapshot("host-a", 6, 1)), "A newer publication restored an older intent");
        Check(!order.TryAccept(Snapshot("host-a", 5, 2, observedAt: 99999)), "Reading cached status manufactured freshness");
        Check(!order.TryAccept(Snapshot("host-retired", 100, 100)), "A payload changed the authenticated host instance");
        Check(order.TryAccept(Snapshot("host-a", 6, 2, phase: "stopped")), "Confirmed Stop was rejected after stale replies");

        JsonElement legacy = JsonSerializer.SerializeToElement(new { phase = "running" });
        Check(!order.TryAccept(legacy), "An unversioned response replaced a versioned observation");
        foreach (var invalid in new object[]
        {
            new { statusInstance = "host-a", statusSequence = 7 },
            new { statusInstance = "host-a", statusSequence = "7", intentRevision = 2 },
            new { statusInstance = "", statusSequence = 7, intentRevision = 2 },
            new { statusInstance = "host-a", statusSequence = -1, intentRevision = 2 },
            new { statusInstance = "host-a", statusSequence = 7, intentRevision = 2.5 }
        }) Check(!order.TryAccept(JsonSerializer.SerializeToElement(invalid)), "Malformed observation ordering was accepted");

        order.Reset(); // Only AppSession replacing its HostChannel may do this.
        Check(order.TryAccept(legacy), "Legacy initial status was rejected");
        Check(order.TryAccept(Snapshot("host-b", 1, 0)), "A confirmed replacement host could not publish");
        Check(!order.TryAccept(Snapshot("host-a", 100, 100)), "A queued old-channel observation replaced the new host");
        Check(order.TryAccept(Snapshot("host-b", ulong.MaxValue - 1, ulong.MaxValue - 1)), "64-bit revisions lost precision");
        Check(order.TryAccept(Snapshot("host-b", ulong.MaxValue, ulong.MaxValue)), "Adjacent 64-bit revisions compared incorrectly");
        Check(!order.TryAccept(Snapshot("host-b", ulong.MaxValue - 1, ulong.MaxValue)), "An older 64-bit sequence was accepted");
    }

    private static JsonElement Snapshot(string instance, ulong sequence, ulong intent, string phase = "running", ulong observedAt = 1234) =>
        JsonSerializer.SerializeToElement(new { statusInstance = instance, statusSequence = sequence, intentRevision = intent, phase, observedAt });

    private static void SeparateProcessAndTunHealth()
    {
        var uncertain = DesktopRuntimeState.Read(Status(phase: "error", tunHealth: "unavailable"));
        Equal(DesktopRuntimePhase.Uncertain, uncertain.Phase);
        Check(uncertain.ProcessAlive && uncertain.StopRequired && !uncertain.IsProblem && !uncertain.CanStart,
            "An unknown TUN observation was reported as an exited process");
        Check(uncertain.RuntimeAvailable, "TUN uncertainty withdrew an independently ready controller");
        Check(uncertain.TunRequested, "A health observation changed the requested TUN toggle");

        var repairing = DesktopRuntimeState.Read(Status(phase: "recovering", operation: "recovering_tun", tunHealth: "degraded"));
        Equal(DesktopRuntimePhase.RepairingTun, repairing.Phase);
        Check(repairing.IsWorking && repairing.StopRequired && repairing.RuntimeAvailable,
            "TUN repair disabled Stop or hid a live controller");
        Equal("正在恢复 TUN", repairing.TunDescription);
        var busyRepair = DesktopRuntimeState.Read(Status(phase: "recovering", control: "busy", operation: "recovering_tun", tunHealth: "unavailable"));
        Check(busyRepair.Phase == DesktopRuntimePhase.RepairingTun && busyRepair.RuntimeAvailable && busyRepair.StopRequired,
            "A responsive busy executor hid its controller during TUN repair");
        var busyObservation = DesktopRuntimeState.Read(Status(phase: "recovering", control: "busy", tunHealth: "unavailable"));
        Check(busyObservation.Phase == DesktopRuntimePhase.Uncertain && busyObservation.RuntimeAvailable,
            "A temporarily busy health observation hid the responsive controller");

        foreach (string code in new[] { "tun_recovery_failed", "tun_recovery_needs_restart", "tun_restart_required" })
        {
            var failed = DesktopRuntimeState.Read(Status(phase: "error", errorCode: code, tunHealth: "degraded"));
            Equal(DesktopRuntimePhase.TunFailed, failed.Phase);
            Check(failed.ProcessAlive && failed.StopRequired && failed.TunRequested, "TUN failure lost process or user intent");
        }
        Equal(DesktopRuntimePhase.TunDegraded, DesktopRuntimeState.Read(Status(tunHealth: "conflict")).Phase);
        Equal(DesktopRuntimePhase.TunDegraded, DesktopRuntimeState.Read(Status(tunHealth: "disabled")).Phase);
        Equal(DesktopRuntimePhase.Running, DesktopRuntimeState.Read(Status(tun: false, tunHealth: "disabled")).Phase);

        var unreachable = DesktopRuntimeState.Read(Status(control: "unreachable"));
        Equal(DesktopRuntimePhase.Uncertain, unreachable.Phase);
        Check(!unreachable.RuntimeAvailable && unreachable.StopRequired, "Unreachable local control was treated as process death");
        var stale = DesktopRuntimeState.Read(Status(stale: true));
        Equal(DesktopRuntimePhase.Uncertain, stale.Phase);
        Check(!stale.RuntimeAvailable && stale.StopRequired, "A stale observation was presented as ready");

        var stopped = Status(phase: "stopped", process: "exited", control: "unknown", desired: false, pid: null, controller: false);
        Check(DesktopRuntimeState.Read(stopped).CanStart, "A confirmed stopped core could not start");
        var idleStopped = DesktopRuntimeState.Read(Status(phase: "stopped", process: "exited", control: "unknown", desired: false, pid: null, controller: false, stale: true));
        Check(idleStopped.CanStart && idleStopped.Phase == DesktopRuntimePhase.Stopped,
            "An idle confirmed Stop expired as if it were an unobserved live process");
        foreach (string command in new[] { "desktop_start", "desktop_restart", "desktop_stop" })
        {
            var pending = DesktopRuntimeState.Read(stopped, command);
            Check(pending.StopRequired && !pending.CanStart, "Startup or Stop lost its intent before the next status reply");
        }
        Equal(DesktopRuntimePhase.Stopping, DesktopRuntimeState.Read(Status(operation: "recovering_tun"), "desktop_stop").Phase);
        var unconfirmed = DesktopRuntimeState.Read(Status(phase: "error", process: "unknown", control: "unknown",
            desired: false, errorCode: "core_termination_unconfirmed", operation: "stopping", controller: false));
        Equal(DesktopRuntimePhase.StopUnconfirmed, unconfirmed.Phase);
        Check(!unconfirmed.CanStart && unconfirmed.StopRequired && !unconfirmed.RuntimeAvailable,
            "An unconfirmed stop allowed a second core or claimed completion");

        var exited = DesktopRuntimeState.Read(Status(phase: "error", process: "exited", control: "unknown", pid: null, controller: false));
        Equal(DesktopRuntimePhase.Exited, exited.Phase);
        Check(exited.CanStart && !exited.ProcessAlive, "A confirmed exited core could not be retried");

        var legacy = JsonSerializer.SerializeToElement(new { phase = "running", controller = new { url = "http://127.0.0.1:1234" } });
        var legacyRuntime = DesktopRuntimeState.Read(legacy);
        Equal(DesktopRuntimePhase.Running, legacyRuntime.Phase);
        Check(legacyRuntime.RuntimeAvailable && legacyRuntime.StopRequired, "Legacy ready status compatibility regressed");
    }

    private static JsonElement Status(string phase = "running", string process = "alive", string control = "ready",
        string tunHealth = "healthy", bool tun = true, bool desired = true, uint? pid = 123, string? operation = null,
        string? errorCode = null, bool stale = false, bool controller = true) => JsonSerializer.SerializeToElement(new
        {
            phase, processState = process, controlState = control, tunHealth, tun, desiredRunning = desired,
            pid, operation, errorCode, statusStale = stale,
            controller = controller ? new { url = "http://127.0.0.1:1234", secret = "fixture" } : null
        });

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) where T : notnull =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; received {actual}");
}
