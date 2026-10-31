using Mihomo.Desktop.Services;

internal static class HostRecoveryIntentTests
{
    public static void Run()
    {
        var intent = new HostRecoveryIntent();
        intent.ObserveInitial(true);
        long recovery = intent.Revision;
        if (!intent.CanResume(recovery)) throw new Exception("The authenticated initial running intent was lost");
        intent.Request(false);
        intent.ObserveInitial(true); // A stale status reply from the old channel.
        if (intent.CanResume(recovery) || intent.DesiredRunning != false)
            throw new Exception("A stale recovery overrode the user's Stop");
        intent.Request(true);
        long latest = intent.Revision;
        if (!intent.CanResume(latest) || intent.CanResume(recovery))
            throw new Exception("A new Start revived an old recovery generation");
        intent.Invalidate(); // Update/configuration supersedes an in-flight start.
        if (intent.CanResume(latest)) throw new Exception("A superseded recovery remained valid");
        var stopBeforeHandshake = new HostRecoveryIntent();
        stopBeforeHandshake.Request(false);
        stopBeforeHandshake.ObserveInitial(true);
        if (stopBeforeHandshake.DesiredRunning != false)
            throw new Exception("An initial handshake undid Stop during channel replacement");
        Console.WriteLine("Host recovery intent: Stop wins, stale status/generation rejected, fresh Start and pre-handshake Stop passed.");
    }
}
