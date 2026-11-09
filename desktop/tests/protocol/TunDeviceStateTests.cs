using System.Text.Json;
using Mihomo.Desktop.Services;

internal static class TunDeviceStateTests
{
    public static void Run()
    {
        RequiresTypedConflictAndHostEligibility();
        RejectsUnsafeActionStates();
        RestoresOnlyAnActiveSelection();
        RejectsIncompleteMetadata();
        KeepsTheConfigurationRevisionBound();
        Console.WriteLine("TUN device choices: typed conflict, explicit eligibility, current revision, reversible selection, and stale/busy/dirty/update guards passed.");
    }

    private static void RequiresTypedConflictAndHostEligibility()
    {
        var state = Read(Snapshot());
        Check(state.Available && state.HasConflict && state.OfferManaged && state.CanUseManaged && !state.CanUseConfigured,
            "A current verified recovery choice was not offered");
        Check(!state.Managed && state.ConfiguredName == "utun" && state.EffectiveName == "utun" && state.ManagedName == "MihomoDesktop",
            "Reading the recovery choice changed or inferred a selected name");

        foreach (string? code in new[] { null, "core_start", "tun_ownership_conflict" })
        {
            var untyped = Read(Snapshot(status: values => values["errorCode"] = code));
            Check(!untyped.HasConflict && !untyped.OfferManaged && !untyped.CanUseManaged,
                "An error message or a different error code authorized a name change");
        }
        var unavailable = Read(Snapshot(device: values => values["canUseManaged"] = false));
        Check(unavailable.OfferManaged && !unavailable.CanUseManaged,
            "A temporary host eligibility denial enabled the action or removed its explanation");
        var alreadyNamed = Read(Snapshot(device: values => values["configuredName"] = "mihomodesktop"));
        Check(!alreadyNamed.OfferManaged && !alreadyNamed.CanUseManaged,
            "The recovery action offered the same existing name with different casing");

        foreach (string? defaultName in new[] { null, "" })
        {
            var defaults = Read(Snapshot(device: values =>
            {
                values["configuredName"] = defaultName;
                values["effectiveName"] = defaultName;
            }));
            Check(defaults.Available && defaults.CanUseManaged && defaults.ConfiguredName == defaultName && !defaults.Managed,
                "An implicit core name was rejected or silently replaced by a desktop name");
        }
    }

    private static void RejectsUnsafeActionStates()
    {
        var status = Snapshot();
        Check(!TunDeviceState.Read(status, ready: false, dirty: false, updating: false).CanUseManaged,
            "A disconnected host allowed recovery");
        Check(!TunDeviceState.Read(status, ready: true, dirty: true, updating: false).CanUseManaged,
            "Unsaved configuration allowed recovery");
        Check(!TunDeviceState.Read(status, ready: true, dirty: false, updating: true).CanUseManaged,
            "An application update allowed recovery");
        foreach (string command in new[] { "desktop_start", "desktop_stop", "desktop_reload_config", "desktop_use_managed_tun_device", "desktop_use_configured_tun_device" })
            Check(!TunDeviceState.Read(status, true, false, false, command).CanUseManaged, "An in-flight command allowed overlapping recovery");
        foreach (var change in new Action<Dictionary<string, object?>>[]
        {
            values => values["statusStale"] = true,
            values => values.Remove("statusStale"),
            values => values["statusStale"] = "false",
            values => values["processState"] = "unknown",
            values => values["operation"] = "reloading",
            values => values["operation"] = "stopping",
            values => values["phase"] = "starting",
            values => values["phase"] = "recovering",
            values => values["tun"] = false
        }) Check(!Read(Snapshot(status: change)).CanUseManaged, "An unsafe or stale observation enabled recovery");
    }

    private static void RestoresOnlyAnActiveSelection()
    {
        JsonElement Selected(Action<Dictionary<string, object?>>? status = null) => Snapshot(status: status, device: values =>
        {
            values["managed"] = true;
            values["effectiveName"] = "MihomoDesktop";
            values["canUseManaged"] = false;
        });
        var selected = Read(Selected());
        Check(selected.Managed && !selected.OfferManaged && !selected.CanUseManaged && selected.CanUseConfigured,
            "The stored desktop selection was not reversible");
        Check(selected.ConfiguredName == "utun" && selected.EffectiveName == "MihomoDesktop",
            "The original name was replaced by the effective name in the displayed state");

        var stopped = Read(Selected(values =>
        {
            values["phase"] = "stopped"; values["desiredRunning"] = false; values["tun"] = false; values["errorCode"] = null;
        }));
        Check(stopped.CanUseConfigured, "Restoring the configured name unnecessarily required a running TUN");
        Check(!Read(Selected(values => values["statusStale"] = true)).CanUseConfigured,
            "A stale selection allowed restoration");
        Check(!TunDeviceState.Read(Selected(), true, true, false).CanUseConfigured,
            "Unsaved configuration allowed restoration");
        Check(!TunDeviceState.Read(Selected(), true, false, true).CanUseConfigured,
            "An application update allowed restoration");
        Check(!Read(Snapshot()).CanUseConfigured, "A configuration without an override offered restoration");
    }

    private static void RejectsIncompleteMetadata()
    {
        foreach (var change in new Action<Dictionary<string, object?>>[]
        {
            values => values.Remove("managed"),
            values => values["managed"] = "false",
            values => values.Remove("canUseManaged"),
            values => values["configuredName"] = 1,
            values => values["effectiveName"] = new { invalid = true },
            values => values["managedName"] = " ",
            values => values["configRevision"] = null,
            values => values["configRevision"] = "",
            values => values["configRevision"] = 9
        })
        {
            var state = Read(Snapshot(device: change));
            Check(!state.CanUseManaged && !state.CanUseConfigured, "Incomplete recovery metadata authorized a command");
        }
        var legacy = Read(Snapshot(status: values => values.Remove("tunDevice")));
        Check(!legacy.Available && !legacy.CanUseManaged && !legacy.CanUseConfigured,
            "A legacy host without device metadata enabled new actions");
    }

    private static void KeepsTheConfigurationRevisionBound()
    {
        var order = new DesktopStatusOrder();
        var first = Snapshot();
        Check(order.TryAccept(first), "Initial device observation was rejected");
        var next = Snapshot(status: values => values["statusSequence"] = 6UL, device: values =>
        {
            values["configRevision"] = "revision-b";
            values["configuredName"] = "new-config-name";
            values["effectiveName"] = "new-config-name";
        });
        Check(order.TryAccept(next) && !order.TryAccept(first), "A delayed conflict replaced the latest source revision");
        var state = Read(next);
        Check(state.MatchesRevision("revision-b") && !state.MatchesRevision("revision-a") && !state.MatchesRevision(""),
            "An action from an older displayed configuration matched the latest snapshot");
        Check(state.ConfiguredName == "new-config-name" && !state.Managed, "A source change carried over the old selection");
    }

    private static TunDeviceState Read(JsonElement status) => TunDeviceState.Read(status, ready: true, dirty: false, updating: false);
    private static JsonElement Snapshot(Action<Dictionary<string, object?>>? status = null, Action<Dictionary<string, object?>>? device = null)
    {
        var choice = new Dictionary<string, object?>
        {
            ["configuredName"] = "utun", ["effectiveName"] = "utun", ["managedName"] = "MihomoDesktop",
            ["managed"] = false, ["canUseManaged"] = true, ["configRevision"] = "revision-a"
        };
        device?.Invoke(choice);
        var values = new Dictionary<string, object?>
        {
            ["phase"] = "error", ["processState"] = "exited", ["controlState"] = "unknown", ["operation"] = null,
            ["desiredRunning"] = true, ["tun"] = true, ["statusStale"] = false, ["errorCode"] = "tun_device_conflict",
            ["lastError"] = "adapter utun already exists with active, foreign or unverified ownership",
            ["statusInstance"] = "host-fixture", ["statusSequence"] = 5UL, ["intentRevision"] = 1UL, ["tunDevice"] = choice
        };
        status?.Invoke(values);
        return JsonSerializer.SerializeToElement(values);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
