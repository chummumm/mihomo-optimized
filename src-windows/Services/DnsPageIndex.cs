using System.Globalization;
using System.Text.Json;

namespace Mihomo.Desktop.Services;

/// <summary>Validates the atomic, filtered page boundaries returned by the core.</summary>
public sealed record DnsPageIndex(int Total, string Anchor, string[] Cursors)
{
    public static DnsPageIndex? Read(JsonElement data, int limit)
    {
        if (limit is not (50 or 100) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("total", out var totalValue) || totalValue.ValueKind != JsonValueKind.Number || !totalValue.TryGetInt32(out int total) || total < 0 ||
            !data.TryGetProperty("retained", out var retainedValue) || retainedValue.ValueKind != JsonValueKind.Number || !retainedValue.TryGetInt32(out int retained) || total > retained ||
            !data.TryGetProperty("anchor_id", out var anchorValue) || anchorValue.ValueKind != JsonValueKind.String ||
            !data.TryGetProperty("page_cursors", out var cursorValues) || cursorValues.ValueKind != JsonValueKind.Array) return null;
        string anchor = anchorValue.GetString()!;
        if ((total > 0 && anchor.Length == 0) || (anchor.Length > 0 && !ReadId(anchor, out _))) return null;
        int count = Math.Max(1, (int)Math.Ceiling((double)total / limit));
        if (cursorValues.GetArrayLength() != count) return null;
        var cursors = new string[count];
        ulong upper = anchor.Length == 0 ? 0 : ulong.Parse(anchor, CultureInfo.InvariantCulture);
        ulong? previous = null; int index = 0;
        foreach (var cursorValue in cursorValues.EnumerateArray())
        {
            if (cursorValue.ValueKind != JsonValueKind.String) return null;
            string cursor = cursorValue.GetString()!;
            if (index == 0 ? cursor != "" : !ReadId(cursor, out var number) || number > upper || (previous.HasValue && number >= previous.Value)) return null;
            if (index > 0) previous = ulong.Parse(cursor, CultureInfo.InvariantCulture);
            cursors[index++] = cursor;
        }
        return new DnsPageIndex(total, anchor, cursors);
    }
    private static bool ReadId(string value, out ulong id)
    {
        id = 0;
        return value.Length is > 0 and <= 20 && value[0] != '0' && value.All(c => c is >= '0' and <= '9') && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }
}
