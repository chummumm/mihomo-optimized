using System.Text.Json;

namespace Mihomo.Desktop.Services;

/// <summary>Presentation choices, stored separately from the core configuration.</summary>
internal sealed class ProxyPreferences
{
    private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mihomo Optimized", "native-proxies.json");
    public bool TopologyOpen { get; set; } = true;
    public HashSet<string>? OpenGroups { get; set; }

    public ProxyPreferences()
    {
        try
        {
            if (!File.Exists(path)) return;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.ValueKind != JsonValueKind.Object) return;
            if (json.RootElement.TryGetProperty("topologyOpen", out var open) && open.ValueKind is JsonValueKind.True or JsonValueKind.False) TopologyOpen = open.GetBoolean();
            if (json.RootElement.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                OpenGroups = groups.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Take(200).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { topologyOpen = TopologyOpen, groups = OpenGroups?.Take(200).ToArray() }));
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Keep the current session usable when preferences cannot be written. */ }
    }
}
