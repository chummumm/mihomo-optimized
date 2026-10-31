namespace Mihomo.Desktop.Services;

/// <summary>User intent survives a failed channel; cached observations cannot undo a later Stop.</summary>
internal sealed class HostRecoveryIntent
{
    public bool? DesiredRunning { get; private set; }
    public long Revision { get; private set; }

    public void ObserveInitial(bool desiredRunning) => DesiredRunning ??= desiredRunning;
    public void Request(bool running) { DesiredRunning = running; Revision++; }
    public void Invalidate() => Revision++;
    public bool CanResume(long revision) => DesiredRunning == true && Revision == revision;
}
