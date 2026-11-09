using System.Text.Json;

namespace Mihomo.Desktop.Services;

/// <summary>Names and deliberate recovery actions come from the accepted host snapshot.</summary>
internal sealed record TunDeviceState
{
    public bool Available { get; init; }
    public string? ConfiguredName { get; init; }
    public string? EffectiveName { get; init; }
    public string ManagedName { get; init; } = "";
    public string ConfigRevision { get; init; } = "";
    public bool Managed { get; init; }
    public bool HasConflict { get; init; }
    public bool OfferManaged { get; init; }
    public bool CanUseManaged { get; init; }
    public bool CanUseConfigured { get; init; }

    public bool MatchesRevision(string expectedRevision) => ConfigRevision.Length > 0
        && string.Equals(ConfigRevision, expectedRevision, StringComparison.Ordinal);

    public static TunDeviceState Read(JsonElement status, bool ready, bool dirty, bool updating, string? busyCommand = null)
    {
        var device = Get(status, "tunDevice");
        var configured = Get(device, "configuredName");
        var effective = Get(device, "effectiveName");
        string managedName = Text(device, "managedName");
        string revision = Text(device, "configRevision");
        bool available = device.ValueKind == JsonValueKind.Object
            && configured.ValueKind is JsonValueKind.String or JsonValueKind.Null
            && effective.ValueKind is JsonValueKind.String or JsonValueKind.Null
            && Get(device, "managed").ValueKind is JsonValueKind.True or JsonValueKind.False
            && Get(device, "canUseManaged").ValueKind is JsonValueKind.True or JsonValueKind.False
            && !string.IsNullOrWhiteSpace(managedName);
        string? configuredName = configured.ValueKind == JsonValueKind.String ? configured.GetString() : null;
        string? effectiveName = effective.ValueKind == JsonValueKind.String ? effective.GetString() : null;
        bool managed = Get(device, "managed").ValueKind == JsonValueKind.True;
        bool conflict = Text(status, "errorCode") == "tun_device_conflict";
        bool offerManaged = available && conflict && !managed && Get(status, "tun").ValueKind == JsonValueKind.True
            && !string.Equals(configuredName, managedName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(effectiveName, managedName, StringComparison.OrdinalIgnoreCase);
        var runtime = DesktopRuntimeState.Read(status, busyCommand);
        bool canChange = available && ready && !dirty && !updating && string.IsNullOrEmpty(busyCommand)
            && !runtime.IsWorking && runtime.Phase != DesktopRuntimePhase.StopUnconfirmed
            && Text(status, "phase") is "running" or "error" or "stopped"
            && Text(status, "operation").Length == 0
            && Text(status, "processState") is "alive" or "exited"
            && Get(status, "statusStale").ValueKind == JsonValueKind.False
            && !string.IsNullOrWhiteSpace(revision);

        return new TunDeviceState
        {
            Available = available,
            ConfiguredName = configuredName,
            EffectiveName = effectiveName,
            ManagedName = managedName,
            ConfigRevision = revision,
            Managed = managed,
            HasConflict = conflict,
            OfferManaged = offerManaged,
            CanUseManaged = canChange && offerManaged && Get(device, "canUseManaged").ValueKind == JsonValueKind.True,
            CanUseConfigured = canChange && managed
        };
    }

    private static JsonElement Get(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var item) ? item : default;
    private static string Text(JsonElement value, string key) => Get(value, key) is { ValueKind: JsonValueKind.String } item
        ? item.GetString() ?? "" : "";
}
