using System.Net;
using System.Text.Json;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

internal sealed record ProxyLatency(double Delay, double Mean, string Stamp);
internal sealed record ProxyNodeInfo(string Name, string Type, bool Udp, bool? Alive, IReadOnlyList<ProxyLatency> History, string Now, IReadOnlyList<string> Members)
{
    public bool IsGroup => Members.Count > 0 || Type is "Selector" or "URLTest" or "Fallback" or "LoadBalance";
    public bool CanTest => Name is not ("DIRECT" or "REJECT" or "REJECT-DROP" or "PASS") && Type is not ("Direct" or "Reject" or "RejectDrop" or "Pass");
    public double Delay => History.LastOrDefault()?.Delay ?? 0;
    public bool Failed => Alive == false || (History.Count > 0 && Delay <= 0);
    public string Tone => Failed ? "bad" : Delay <= 0 ? "idle" : Delay <= 260 ? "good" : Delay <= 600 ? "warn" : "bad";
    public string DelayLabel => Failed ? L.T("超时") : Delay > 0 ? $"{Delay:0.#} ms" : "—";
    public static ProxyNodeInfo From(string name, JsonElement value) => new(name, value.Text("type"), value.Bool("udp"), value.Get("alive").ValueKind is JsonValueKind.True or JsonValueKind.False ? value.Bool("alive") : null,
        value.Get("history").Items().TakeLast(10).Select(item => new ProxyLatency(item.Number("delay"), item.Number("meanDelay"), item.Text("time") + ":" + item.Number("delay"))).ToArray(), value.Text("now"), value.Get("all").Items().Select(item => item.Text()).ToArray());
    public static string? ResolveExit(string name, IReadOnlyDictionary<string, ProxyNodeInfo> nodes)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (name.Length > 0)
        {
            if (!visited.Add(name)) return null;
            if (!nodes.TryGetValue(name, out var next) || !next.IsGroup) return name;
            name = next.Now;
        }
        return null;
    }
}

internal sealed record TopologyNode(string Id, int Column, string Kind, string Title, string Subtitle, string Tone, string? Target = null, string? Stamp = null, int? GroupColor = null);
internal sealed record TopologyLink(string From, string To, double Rate, string Kind);
internal sealed record TopologyModel(IReadOnlyList<TopologyNode> Nodes, IReadOnlyList<TopologyLink> Links, int Rows, int Connections)
{
    public static readonly TopologyModel Empty = new([], [], 3, 0);
}

/// <summary>Builds the same ranked source → policy → exit map as the supplied design.</summary>
internal sealed class ProxyTopologyModelBuilder
{
    private Dictionary<string, (double Upload, double Download)> previous = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> rates = new(StringComparer.Ordinal);
    private long? sampledAt;
    private JsonElement snapshot;
    private sealed record Tally(double Rate, int Count);

    public void Clear() { previous.Clear(); rates.Clear(); sampledAt = null; snapshot = default; }
    public void Sample(JsonElement value)
    {
        var now = Environment.TickCount64;
        var elapsed = sampledAt.HasValue ? Math.Max(.05, (now - sampledAt.Value) / 1000d) : 0;
        var current = new Dictionary<string, (double Upload, double Download)>(StringComparer.Ordinal);
        rates.Clear();
        foreach (var connection in value.Get("connections").Items())
        {
            var id = connection.Text("id"); var upload = connection.Number("upload"); var download = connection.Number("download");
            current[id] = (upload, download);
            rates[id] = elapsed > 0 && previous.TryGetValue(id, out var old) ? (Math.Max(0, upload - old.Upload) + Math.Max(0, download - old.Download)) / elapsed : 0;
        }
        previous = current; sampledAt = now; snapshot = value;
    }

    public TopologyModel Build(IReadOnlyDictionary<string, ProxyNodeInfo> proxies, IEnumerable<ProxyNodeInfo> configuredGroups)
    {
        var configured = configuredGroups.ToArray();
        var groupColors = configured.Select((item, index) => (item.Name, Index: index % 9)).ToDictionary(item => item.Name, item => item.Index, StringComparer.Ordinal);
        var sources = new Dictionary<string, Tally>(StringComparer.Ordinal);
        var policies = new Dictionary<string, Tally>(StringComparer.Ordinal);
        var exits = new Dictionary<string, Tally>(StringComparer.Ordinal);
        var pairs = new Dictionary<(string From, string To), TopologyLink>();
        var active = 0;
        foreach (var connection in snapshot.Get("connections").Items())
        {
            if (connection.Bool("completed")) continue;
            active++; var rate = rates.GetValueOrDefault(connection.Text("id"));
            var chain = connection.Get("chains").Items().Select(item => item.Text()).ToArray();
            var exit = chain.FirstOrDefault() ?? "DIRECT"; var outer = chain.LastOrDefault() ?? exit;
            var source = SourceKey(connection); Add(sources, source, rate); Add(exits, exit, rate);
            if (outer != exit && proxies.TryGetValue(outer, out var group) && group.IsGroup)
            {
                Add(policies, outer, rate); Pair("s:" + source, "g:" + outer, rate, Kind(exit)); Pair("g:" + outer, "x:" + exit, rate, Kind(exit));
            }
            else Pair("s:" + source, "x:" + exit, rate, Kind(exit));
        }
        var groups = Rank(policies);
        if (groups.Count == 0) groups = configured.Take(6).Select(item => new KeyValuePair<string, Tally>(item.Name, new Tally(0, 0))).ToList();
        foreach (var entry in groups.Take(6))
        {
            var exit = ProxyNodeInfo.ResolveExit(entry.Key, proxies);
            if (exit is null) continue;
            exits.TryAdd(exit, new Tally(0, 0));
            if (!pairs.ContainsKey(("g:" + entry.Key, "x:" + exit))) Pair("g:" + entry.Key, "x:" + exit, 0, Kind(exit));
        }

        var nodes = new List<TopologyNode>(); var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        Place(0, "s:", Rank(sources), (name, tally) => new("s:" + name, 0, "source", name == "internal" ? "mihomo" : name == "unknown" ? L.T("未知来源") : name[7..], tally.Rate >= 1 ? Ui.Bytes(tally.Rate) + "/s" : L.F("{0} 个连接", tally.Count), tally.Rate >= 1 ? "live" : "quiet"));
        Place(1, "g:", groups, (name, _) => new("g:" + name, 1, "group", name, "→ " + (proxies.GetValueOrDefault(name)?.Now ?? ""), name == "GLOBAL" ? "quiet" : "live", name, GroupColor: groupColors.TryGetValue(name, out var color) ? color : null));
        Place(2, "x:", Rank(exits), (name, _) =>
        {
            proxies.TryGetValue(name, out var proxy); var kind = Kind(name);
            var subtitle = kind == "direct" ? L.T("直接连接") : kind == "reject" ? L.T("拒绝连接") : proxy?.History.Count > 0 ? proxy.DelayLabel : proxy?.Type ?? "";
            return new("x:" + name, 2, "exit", name, subtitle, kind == "direct" ? "quiet" : kind == "reject" ? "bad" : proxy?.Tone ?? "idle", kind == "proxy" && proxy is not null ? name : null, proxy?.History.LastOrDefault()?.Stamp);
        });
        var links = new Dictionary<(string From, string To), TopologyLink>();
        var present = nodes.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var link in pairs.Values)
        {
            var from = aliases.GetValueOrDefault(link.From, link.From); var to = aliases.GetValueOrDefault(link.To, link.To);
            if (!present.Contains(from) || !present.Contains(to)) continue;
            links[(from, to)] = links.TryGetValue((from, to), out var old) ? old with { Rate = old.Rate + link.Rate } : link with { From = from, To = to };
        }
        return new(nodes, links.Values.ToArray(), Math.Max(3, Enumerable.Range(0, 3).Max(column => nodes.Count(item => item.Column == column))), active);

        string Kind(string exit) => exit is "DIRECT" || proxies.GetValueOrDefault(exit)?.Type == "Direct" ? "direct" : exit is "REJECT" or "REJECT-DROP" || proxies.GetValueOrDefault(exit)?.Type is "Reject" or "RejectDrop" ? "reject" : "proxy";
        void Pair(string from, string to, double rate, string kind) => pairs[(from, to)] = pairs.TryGetValue((from, to), out var old) ? old with { Rate = old.Rate + rate } : new(from, to, rate, kind);
        void Place(int column, string prefix, List<KeyValuePair<string, Tally>> ranked, Func<string, Tally, TopologyNode> make)
        {
            var limit = column == 0 ? 5 : 6; var kept = ranked.Count > limit + 1 ? limit : ranked.Count;
            foreach (var entry in ranked.Take(kept)) nodes.Add(make(entry.Key, entry.Value));
            var rest = ranked.Skip(kept).ToArray(); if (rest.Length == 0) return;
            var id = "more:" + column; var rate = rest.Sum(item => item.Value.Rate);
            nodes.Add(new(id, column, "more", L.F("其他 {0} 项", rest.Length), rate >= 1 ? Ui.Bytes(rate) + "/s" : L.F("{0} 个连接", rest.Sum(item => item.Value.Count)), "quiet"));
            foreach (var entry in rest) aliases[prefix + entry.Key] = id;
        }
    }
    private static void Add(Dictionary<string, Tally> map, string key, double rate) => map[key] = map.TryGetValue(key, out var value) ? new(value.Rate + rate, value.Count + 1) : new(rate, 1);
    private static List<KeyValuePair<string, Tally>> Rank(Dictionary<string, Tally> values) => values.OrderByDescending(item => item.Value.Rate).ThenByDescending(item => item.Value.Count).ToList();
    private static string SourceKey(JsonElement connection)
    {
        if (connection.Bool("dns")) return "internal";
        var metadata = connection.Get("metadata"); var source = metadata.Text("sourceIP"); var split = source.Split('%', 2);
        if (IPAddress.TryParse(split[0], out var address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) && !(address.IsIPv4MappedToIPv6 && address.MapToIPv4().Equals(IPAddress.Any)))
            return "device:" + address + (split.Length == 2 ? "%" + split[1] : "");
        return metadata.Text("type") == "Inner" ? "internal" : "unknown";
    }
}
