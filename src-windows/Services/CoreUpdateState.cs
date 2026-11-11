using System.Text.Json;

namespace Mihomo.Desktop.Services;

/// <summary>A host-owned core update observation, separate from controller resource tasks.</summary>
internal sealed record CoreUpdateState
{
    public bool Available { get; init; }
    public JsonElement Item { get; init; }
    public long RequestedAt { get; init; }
    public bool Active => Available && (Text(Item, "status") is "checking" or "restarting"
        || Text(Item, "status") == "pending" && RequestId.Length > 0);
    public string RequestId => Text(Item, "request_id");

    public static CoreUpdateState Read(JsonElement item, long requestedAt)
    {
        if (item.ValueKind != JsonValueKind.Object || Text(item, "id") != "kernel"
            || Text(item, "kind") != "kernel"
            || Text(item, "status") is not ("pending" or "checking" or "restarting" or "updated" or "up_to_date" or "skipped" or "error")
            || Get(item, "auto_update").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !TryInteger(Get(item, "update_interval"), out var hours) || hours <= 0)
            throw new InvalidDataException("The desktop core update status is invalid.");

        foreach (var name in new[] { "name", "current_version", "latest_version", "request_id", "reason", "error", "stage", "message", "auto_update_state", "interval_unit", "next_check", "next_check_at" })
            if (Get(item, name).ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
                throw new InvalidDataException("The desktop core update status contains invalid text.");
        var remaining = Get(item, "next_check_in_ms");
        if (remaining.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            && (!TryInteger(remaining, out var milliseconds) || milliseconds < 0))
            throw new InvalidDataException("The desktop core update schedule is invalid.");
        if (Text(item, "interval_unit") is not ("" or "hours"))
            throw new InvalidDataException("The desktop core update interval must use hours.");

        return new CoreUpdateState { Available = true, Item = item.Clone(), RequestedAt = requestedAt };
    }

    public DateTimeOffset? NextCheck(DateTimeOffset now, long monotonicNow)
    {
        if (!Available || Get(Item, "auto_update").ValueKind != JsonValueKind.True) return null;
        if (TryInteger(Get(Item, "next_check_in_ms"), out var remaining))
        {
            try { return now.AddMilliseconds(remaining - (double)Math.Max(0, monotonicNow - RequestedAt)); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return DateTimeOffset.TryParse(Text(Item, "next_check_at"), out var next)
            || DateTimeOffset.TryParse(Text(Item, "next_check"), out next) ? next.ToLocalTime() : null;
    }

    private static JsonElement Get(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var item) ? item : default;
    private static string Text(JsonElement value, string key) => Get(value, key) is { ValueKind: JsonValueKind.String } item
        ? item.GetString() ?? "" : "";
    private static bool TryInteger(JsonElement value, out long number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number);
    }
}
