using System.Text.Json;
using Mihomo.Desktop.Services;

internal static class TunDeviceStateTests
{
    public static void Run()
    {
        ReadsNamesWithoutManualEligibility();
        KeepsSelectionSeparateFromReadiness();
        RejectsIncompleteMetadata();
        KeepsTheConfigurationRevisionBound();
        Console.WriteLine("TUN device status: configured names without a fixed fallback, separate readiness, and ordered source revisions passed.");
    }

    private static void ReadsNamesWithoutManualEligibility()
    {
        var state = Read(Snapshot());
        Check(state.Available && !state.Managed && state.ConfiguredName == "utun" && state.EffectiveName == "utun"
            && state.ManagedName == "MihomoDesktop" && state.ConfigRevision == "revision-a",
            "Reading names required manual eligibility or inferred a different selection");

        foreach (string? defaultName in new[] { null, "" })
        {
            var defaults = Read(Snapshot(device: values =>
            {
                values["configuredName"] = defaultName;
                values["effectiveName"] = defaultName;
            }));
            Check(defaults.Available && defaults.ConfiguredName == defaultName && defaults.EffectiveName == defaultName
                && !defaults.Managed, "An implicit core name was rejected or replaced by an invented desktop name");
        }

        var legacyEligibility = Read(Snapshot(device: values => values["canUseManaged"] = true));
        Check(legacyEligibility == state, "A retired action eligibility field changed read-only device information");
        var withoutFallback = Read(Snapshot(device: values =>
        {
            values.Remove("managed"); values.Remove("managedName");
        }));
        Check(withoutFallback.Available && !withoutFallback.Managed && withoutFallback.ConfiguredName == "utun"
            && withoutFallback.EffectiveName == "utun" && withoutFallback.ManagedName.Length == 0,
            "Configured-name information still required the retired fixed-name fallback");
    }

    private static void KeepsSelectionSeparateFromReadiness()
    {
        var deciding = Snapshot(status: values =>
        {
            values["phase"] = "recovering"; values["operation"] = "recovering_tun_device";
        });
        Check(DesktopRuntimeState.Read(deciding).Phase == DesktopRuntimePhase.RecoveringTunDevice
            && Read(deciding).EffectiveName == "utun" && !Read(deciding).Managed,
            "The UI invented the target name before the host selected it");

        JsonElement Selected(Action<Dictionary<string, object?>>? status = null) => Snapshot(status: status, device: values =>
        {
            values["managed"] = true;
            values["effectiveName"] = "MihomoDesktop";
        });
        foreach (var snapshot in new[]
        {
            Selected(),
            Selected(values => { values["phase"] = "recovering"; values["operation"] = "recovering_tun_device"; }),
            Selected(values => { values["processState"] = "unknown"; values["statusStale"] = true; }),
            Selected(values => { values["phase"] = "stopped"; values["desiredRunning"] = false; values["tun"] = false; })
        })
        {
            var device = Read(snapshot);
            Check(device.Available && device.Managed && device.ConfiguredName == "utun" && device.EffectiveName == "MihomoDesktop",
                "A runtime transition overwrote or hid the saved name selection");
            Check(!DesktopRuntimeState.Read(snapshot).RuntimeAvailable,
                "A saved device selection was treated as evidence that the core was ready");
        }
    }

    private static void RejectsIncompleteMetadata()
    {
        foreach (var change in new Action<Dictionary<string, object?>>[]
        {
            values => values["managed"] = "false",
            values => values.Remove("configuredName"),
            values => values["configuredName"] = 1,
            values => values.Remove("effectiveName"),
            values => values["effectiveName"] = new { invalid = true },
            values => values.Remove("configRevision"),
            values => values["configRevision"] = " "
        }) Check(!Read(Snapshot(device: change)).Available, "Malformed metadata exposed invented device information");
        Check(!Read(Snapshot(status: values => values.Remove("tunDevice"))).Available,
            "A host without device metadata exposed new device information");
    }

    private static void KeepsTheConfigurationRevisionBound()
    {
        var order = new DesktopStatusOrder();
        var first = Snapshot(device: values =>
        {
            values["managed"] = true; values["effectiveName"] = "MihomoDesktop";
        });
        Check(order.TryAccept(first), "Initial device observation was rejected");
        var next = Snapshot(status: values => values["statusSequence"] = 6UL, device: values =>
        {
            values["configRevision"] = "revision-b";
            values["configuredName"] = "new-config-name";
            values["effectiveName"] = "new-config-name";
        });
        Check(order.TryAccept(next) && !order.TryAccept(first), "A delayed recovery replaced the latest source revision");
        var state = Read(next);
        Check(state.ConfigRevision == "revision-b" && state.ConfiguredName == "new-config-name"
            && state.EffectiveName == "new-config-name" && !state.Managed,
            "A source change carried over the old name selection");
    }

    private static TunDeviceState Read(JsonElement status) => TunDeviceState.Read(status);
    private static JsonElement Snapshot(Action<Dictionary<string, object?>>? status = null, Action<Dictionary<string, object?>>? device = null)
    {
        var names = new Dictionary<string, object?>
        {
            ["configuredName"] = "utun", ["effectiveName"] = "utun", ["managedName"] = "MihomoDesktop",
            ["managed"] = false, ["configRevision"] = "revision-a"
        };
        device?.Invoke(names);
        var values = new Dictionary<string, object?>
        {
            ["phase"] = "error", ["processState"] = "exited", ["controlState"] = "unknown", ["operation"] = null,
            ["desiredRunning"] = true, ["tun"] = true, ["statusStale"] = false, ["errorCode"] = "tun_device_conflict",
            ["lastError"] = "adapter utun already exists with active, foreign or unverified ownership",
            ["statusInstance"] = "host-fixture", ["statusSequence"] = 5UL, ["intentRevision"] = 1UL, ["tunDevice"] = names
        };
        status?.Invoke(values);
        return JsonSerializer.SerializeToElement(values);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
