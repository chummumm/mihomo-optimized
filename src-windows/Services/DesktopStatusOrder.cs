using System.Text.Json;

namespace Mihomo.Desktop.Services;

/// <summary>
/// Rejects delayed command replies and observations from a retired host instance.
/// Reset only after replacing the actual host channel, never from a status payload.
/// </summary>
internal sealed class DesktopStatusOrder
{
    private string? instance;
    private ulong sequence;
    private ulong intentRevision;

    public void Reset() { instance = null; sequence = intentRevision = 0; }

    public bool TryAccept(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object) return false;
        bool hasInstance = status.TryGetProperty("statusInstance", out var identity);
        bool hasSequence = status.TryGetProperty("statusSequence", out var version);
        bool hasIntent = status.TryGetProperty("intentRevision", out var intent);
        // A complete legacy snapshot is useful before upgrading the host. Once
        // versioned observations arrive, an unversioned reply cannot replace one.
        if (!hasInstance && !hasSequence && !hasIntent) return instance is null;
        if (!hasInstance || !hasSequence || !hasIntent || identity.ValueKind != JsonValueKind.String
            || version.ValueKind != JsonValueKind.Number || intent.ValueKind != JsonValueKind.Number
            || !version.TryGetUInt64(out ulong nextSequence) || !intent.TryGetUInt64(out ulong nextIntent)) return false;
        string? nextInstance = identity.GetString();
        if (string.IsNullOrEmpty(nextInstance) || nextInstance.Length > 256) return false;
        if (instance is not null && (instance != nextInstance || nextSequence <= sequence || nextIntent < intentRevision)) return false;
        instance = nextInstance;
        sequence = nextSequence;
        intentRevision = nextIntent;
        return true;
    }
}
