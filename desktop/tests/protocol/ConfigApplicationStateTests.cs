using Mihomo.Desktop.Services;

internal static class ConfigApplicationStateTests
{
    public static void Run()
    {
        const string path = @"C:\Mihomo\config.yaml";
        var state = new ConfigApplicationState();
        state.ObserveDocument(path, "original");
        Require(!state.Pending && state.Failure.Length == 0, "Opening a document invented an application result.");

        // Save-only, close/reopen and reread must continue to offer application.
        state.ObserveDocument(path, "saved");
        state.Saved();
        state.ObserveDocument(path, "saved");
        Require(state.Pending, "Reading a saved document cleared its pending application.");

        state.ApplyFailed("Core validation failed");
        state.ObserveDocument(path.ToLowerInvariant(), "saved");
        Require(state.Pending && state.Failure == "Core validation failed", "Rereading the failed revision hid its application failure.");

        // External edits replace the failed input, but are not proof of a reload.
        state.ObserveDocument(path, "external-repair");
        Require(state.Pending && state.Failure.Length == 0, "An external repair must retain pending application without the old revision's error.");
        state.ApplyFailed("Transport unavailable");
        state.Applied();
        state.ObserveDocument(path, "external-repair");
        Require(!state.Pending && state.Failure.Length == 0, "An acknowledged application left a historical result in the editor.");

        state.Saved();
        state.ApplyFailed("Old file error");
        state.ObserveDocument(@"D:\Other\config.yaml", "other");
        Require(!state.Pending && state.Failure.Length == 0, "A different configuration inherited the previous file's application state.");

        // Reload can fail before the editor's first successful read.
        var unopened = new ConfigApplicationState();
        unopened.ApplyFailed("File application failed");
        unopened.ObserveDocument(path, "first-read");
        Require(unopened.Pending && unopened.Failure == "File application failed", "The first read acknowledged or hid a failed application.");
        Console.WriteLine("Config application state: save/read/reopen, failed revision, external repair and confirmed application passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
