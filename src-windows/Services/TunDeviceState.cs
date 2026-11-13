using System.Text.Json;

namespace Mihomo.Desktop.Services;

/// <summary>Read-only name selections from the accepted host snapshot, not native device readiness.</summary>
internal sealed record TunDeviceState
{
    public bool Available { get; init; }
    public string? ConfiguredName { get; init; }
    public string? EffectiveName { get; init; }
    public string ManagedName { get; init; } = "";
    public string ConfigRevision { get; init; } = "";
    public bool Managed { get; init; }

    public static TunDeviceState Read(JsonElement status)
    {
        var device = Get(status, "tunDevice");
        var configured = Get(device, "configuredName");
        var effective = Get(device, "effectiveName");
        string managedName = Text(device, "managedName");
        string revision = Text(device, "configRevision");
        bool available = device.ValueKind == JsonValueKind.Object
            && configured.ValueKind is JsonValueKind.String or JsonValueKind.Null
            && effective.ValueKind is JsonValueKind.String or JsonValueKind.Null
            && Get(device, "managed").ValueKind is JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False
            && !string.IsNullOrWhiteSpace(revision);
        if (!available) return new TunDeviceState();
        string? configuredName = configured.ValueKind == JsonValueKind.String ? configured.GetString() : null;
        string? effectiveName = effective.ValueKind == JsonValueKind.String ? effective.GetString() : null;
        bool managed = Get(device, "managed").ValueKind == JsonValueKind.True;

        return new TunDeviceState
        {
            Available = available,
            ConfiguredName = configuredName,
            EffectiveName = effectiveName,
            ManagedName = managedName,
            ConfigRevision = revision,
            Managed = managed
        };
    }

    private static JsonElement Get(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var item) ? item : default;
    private static string Text(JsonElement value, string key) => Get(value, key) is { ValueKind: JsonValueKind.String } item
        ? item.GetString() ?? "" : "";
}
