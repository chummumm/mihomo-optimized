using System.Text;
using System.Text.Json;
using Mihomo.Desktop.Services;

if (args.FirstOrDefault() == "--stdio") { await ProcessFixtures.HostAsync(); return; }
if (args.FirstOrDefault() == "--broker-fixture" && OperatingSystem.IsWindows())
{
    Environment.ExitCode = await ProcessFixtures.BrokerAsync(args[1], args[2]);
    return;
}

static StreamReader Reader(string text) => new(new MemoryStream(Encoding.UTF8.GetBytes(text)), Encoding.UTF8);
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; received {actual}");
}
static async Task Reject(Func<Task> operation)
{
    try { await operation(); } catch (InvalidDataException) { return; }
    throw new Exception("Malformed protocol frame was accepted");
}

string first = JsonSerializer.Serialize(new { id = 1, method = "desktop_save_config", @params = new { content = "# 中文\nproxies: []\n", expectedRevision = "old" } }, HostChannel.JsonOptions);
using (var reader = Reader(first + "\r\n{\"id\":2}\n"))
{
    var frames = new BoundedLineReader(reader, 10000);
    using var message = JsonDocument.Parse((await frames.ReadAsync(default))!);
    Equal("# 中文\nproxies: []\n", message.RootElement.GetProperty("params").GetProperty("content").GetString());
    Equal("old", message.RootElement.GetProperty("params").GetProperty("expectedRevision").GetString());
    Equal("{\"id\":2}", await frames.ReadAsync(default));
    Equal<string?>(null, await frames.ReadAsync(default));
}
using (var reader = Reader(new string('x', 20000) + "\nend\n"))
{
    var frames = new BoundedLineReader(reader, 20000);
    Equal(20000, (await frames.ReadAsync(default))!.Length);
    Equal("end", await frames.ReadAsync(default));
}
using (var reader = Reader("12345\n"))
    await Reject(async () => { await new BoundedLineReader(reader, 4).ReadAsync(default); });
using (var reader = Reader("{\"method\":\"desktop_exit\"}"))
    await Reject(async () => { await new BoundedLineReader(reader, 100).ReadAsync(default); });
using (var reader = Reader("complete\nincomplete"))
{
    var frames = new BoundedLineReader(reader, 100);
    Equal("complete", await frames.ReadAsync(default));
    await Reject(async () => { await frames.ReadAsync(default); });
}
Console.WriteLine("Native client protocol: Unicode/escaped YAML, combined and split frames, exact limits, oversized and incomplete EOF passed.");
await ProcessFixtures.TestHostAsync();
if (OperatingSystem.IsWindows()) await ProcessFixtures.TestBrokerAsync();
