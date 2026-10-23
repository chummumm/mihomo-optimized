using System.Net.Http;
using System.Text.Json;
using Mihomo.Desktop.Controls;

namespace Mihomo.Desktop.Services;

public sealed record ProxyExit(string Group, string Type, string? Exit, double Delay)
{
    public bool Manual => Type == "Selector";
    public string Tone => Delay <= 0 ? "TextSecondaryBrush" : Delay <= 260 ? "SuccessBrush" : Delay <= 600 ? "WarningBrush" : "DangerBrush";
}

public sealed record ProxyExitSnapshot(IReadOnlyList<ProxyExit> Groups)
{
    public static ProxyExitSnapshot Empty { get; } = new(Array.Empty<ProxyExit>());
    public ProxyExit? Main => Groups.FirstOrDefault(group => group.Manual) ?? Groups.FirstOrDefault();
    public IEnumerable<ProxyExit> Summaries => Groups.Where(group => group.Type is "Selector" or "URLTest" or "Fallback");
    public static async Task<ProxyExitSnapshot> ReadAsync(AppSession session, CancellationToken token)
    {
        var proxies = session.ApiAsync(HttpMethod.Get, "proxies", cancellationToken: token);
        var providers = session.ApiAsync(HttpMethod.Get, "providers/proxies", cancellationToken: token);
        await Task.WhenAll(proxies, providers);
        return Parse(await proxies, await providers);
    }
    public static ProxyExitSnapshot Parse(JsonElement response, JsonElement providerResponse)
    {
        var proxies = response.Get("proxies").Fields().ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
        var nodes = new Dictionary<string, JsonElement>(proxies, StringComparer.Ordinal);
        foreach (var provider in providerResponse.Get("providers").Fields())
            foreach (var node in provider.Value.Get("proxies").Items())
                if (node.Text("name") is { Length: > 0 } name) nodes[name] = node;
        var order = proxies.TryGetValue("GLOBAL", out var global) ? global.Get("all").Items().Select(node => node.Text()).ToArray() : [];
        if (order.Length == 0) order = proxies.Keys.ToArray();
        var groups = new List<ProxyExit>();
        foreach (var name in order.Distinct(StringComparer.Ordinal))
        {
            if (name == "GLOBAL" || !proxies.TryGetValue(name, out var group) || group.Text("type") is not ("Selector" or "URLTest" or "Fallback" or "LoadBalance")) continue;
            string? exit = group.Text("now");
            var visited = new HashSet<string>(StringComparer.Ordinal) { name };
            while (!string.IsNullOrEmpty(exit) && proxies.TryGetValue(exit, out var nested) && nested.Get("all").ValueKind == JsonValueKind.Array)
            {
                if (!visited.Add(exit)) { exit = null; break; }
                exit = nested.Text("now");
            }
            if (string.IsNullOrEmpty(exit)) exit = null;
            double delay = exit is not null && nodes.TryGetValue(exit, out var leaf) ? leaf.Get("history").Items().LastOrDefault().Number("delay") : 0;
            groups.Add(new ProxyExit(name, group.Text("type"), exit, delay));
        }
        return new ProxyExitSnapshot(groups);
    }
}
