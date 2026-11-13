using System.Text.Json;
using Mihomo.Desktop.Services;

internal static class DesktopStatusTests
{
    public static void Run()
    {
        RejectDelayedObservations();
        SeparateProcessAndTunHealth();
        SeparateTunDeviceConflicts();
        ObserveAutomaticTunDeviceRecovery();
        Console.WriteLine("Desktop status: ordered host observations, automatic TUN recovery, Stop intent, TUN uncertainty/failure, live controller access, and confirmed exit passed.");
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

    private static void SeparateTunDeviceConflicts()
    {
        var exited = DesktopRuntimeState.Read(Status(phase: "error", process: "exited", control: "unknown",
            pid: null, controller: false, errorCode: "tun_device_conflict"));
        Equal(DesktopRuntimePhase.TunDeviceConflict, exited.Phase);
        Check(exited.IsProblem && exited.CanStart && !exited.ProcessAlive && exited.TunRequested,
            "A confirmed stopped adapter conflict lost the error or TUN intent");

        var alive = DesktopRuntimeState.Read(Status(phase: "error", errorCode: "tun_device_conflict"));
        Equal(DesktopRuntimePhase.TunDeviceConflict, alive.Phase);
        Check(alive.ProcessAlive && alive.StopRequired && !alive.CanStart,
            "An adapter conflict was treated as proof that a live core exited");

        var unknown = DesktopRuntimeState.Read(Status(phase: "error", process: "unknown", control: "unknown",
            desired: false, pid: null, controller: false, errorCode: "tun_device_conflict"));
        Equal(DesktopRuntimePhase.Uncertain, unknown.Phase);
        Check(!unknown.CanStart, "A conflict with unknown process state allowed another core to start");
        Equal(DesktopRuntimePhase.Uncertain, DesktopRuntimeState.Read(Status(stale: true, errorCode: "tun_device_conflict")).Phase);

        Equal(DesktopRuntimePhase.Stopping, DesktopRuntimeState.Read(Status(errorCode: "tun_device_conflict"), "desktop_stop").Phase);

        var rolledBack = DesktopRuntimeState.Read(Status(tun: false, tunHealth: "disabled", errorCode: "tun_device_conflict"));
        Equal(DesktopRuntimePhase.Running, rolledBack.Phase);
        Check(rolledBack.RuntimeAvailable && !rolledBack.IsProblem && rolledBack.RuntimeError.Length == 0,
            "An old enable conflict replaced the current healthy proxy state after TUN rollback");
        Equal(DesktopRuntimePhase.TunDeviceConflict, DesktopRuntimeState.Read(Status(
            phase: "error", tun: false, tunHealth: "unavailable", errorCode: "tun_device_conflict")).Phase);
        Equal(DesktopRuntimePhase.Uncertain, DesktopRuntimeState.Read(Status(
            tun: false, tunHealth: "disabled", stale: true, errorCode: "tun_device_conflict")).Phase);
    }

    private static void ObserveAutomaticTunDeviceRecovery()
    {
        foreach (string process in new[] { "alive", "exited" })
        {
            var status = Status(phase: "recovering", process: process, pid: process == "alive" ? 123U : null,
                operation: "recovering_tun_device", errorCode: "tun_device_conflict");
            foreach (string? command in new[] { null, "desktop_start", "desktop_restart", "desktop_set_tun" })
            {
                var recovering = DesktopRuntimeState.Read(status, command);
                Equal(DesktopRuntimePhase.RecoveringTunDevice, recovering.Phase);
                Check(recovering.IsWorking && recovering.TunDeviceRecoveryPending && recovering.StopRequired
                    && !recovering.CanStart && !recovering.RuntimeAvailable && recovering.TunRequested,
                    "Automatic recovery lost Stop or exposed a controller before the replacement was ready");
                Check(recovering.ProcessAlive == (process == "alive"), "Automatic recovery invented process liveness");
                Equal("正在自动恢复 TUN", recovering.TunDescription);
                Check(!recovering.Description.Contains("进程仍在运行", StringComparison.Ordinal),
                    "Automatic device recovery claimed the stopped process was alive");
            }
            Equal(DesktopRuntimePhase.Stopping, DesktopRuntimeState.Read(status, "desktop_stop").Phase);
        }

        foreach (var status in new[]
        {
            Status(phase: "recovering", process: "unknown", pid: null, operation: "recovering_tun_device"),
            Status(phase: "recovering", operation: "recovering_tun_device", stale: true),
            JsonSerializer.SerializeToElement(new
            {
                phase = "running", controlState = "ready", operation = "recovering_tun_device",
                controller = new { url = "http://127.0.0.1:1234" }
            })
        })
        {
            var uncertain = DesktopRuntimeState.Read(status);
            Equal(DesktopRuntimePhase.Uncertain, uncertain.Phase);
            Check(uncertain.IsWorking && uncertain.StopRequired && !uncertain.CanStart && !uncertain.RuntimeAvailable,
                "An uncertain automatic recovery enabled another action or lost Stop");
        }

        foreach (string? operation in new[] { null, "stopping", "recovering_tun_device" })
        {
            var unconfirmed = DesktopRuntimeState.Read(Status(phase: "error", process: "unknown", pid: null,
                desired: false, operation: operation, errorCode: "core_termination_unconfirmed"));
            Equal(DesktopRuntimePhase.StopUnconfirmed, unconfirmed.Phase);
            Check(unconfirmed.StopRequired && !unconfirmed.CanStart && !unconfirmed.IsWorking && !unconfirmed.RuntimeAvailable,
                "Unconfirmed cleanup was hidden behind automatic recovery or lost its Stop action");
        }
        var stopping = DesktopRuntimeState.Read(Status(phase: "error", process: "exited", pid: null,
            desired: false, operation: "stopping"));
        Check(stopping.Phase == DesktopRuntimePhase.Stopping && stopping.StopRequired && !stopping.CanStart,
            "An admitted Stop without a PID exposed Start before completion");

        var ready = DesktopRuntimeState.Read(Status());
        Check(ready.Phase == DesktopRuntimePhase.Running && !ready.IsWorking && !ready.TunDeviceRecoveryPending
            && ready.RuntimeAvailable, "Successful recovery retained its pending transition");
        var failed = DesktopRuntimeState.Read(Status(phase: "error", process: "exited", pid: null,
            controller: false, errorCode: "tun_device_conflict"));
        Check(failed.Phase == DesktopRuntimePhase.TunDeviceConflict && failed.IsProblem && !failed.IsWorking
            && !failed.TunDeviceRecoveryPending && failed.CanStart, "Terminal recovery failure was presented as ongoing recovery");
        var stopped = DesktopRuntimeState.Read(Status(phase: "stopped", process: "exited", pid: null,
            desired: false, controller: false));
        Check(stopped.Phase == DesktopRuntimePhase.Stopped && !stopped.IsWorking && !stopped.StopRequired
            && stopped.CanStart, "Completed Stop retained the automatic recovery intent");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) where T : notnull =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; received {actual}");
}
