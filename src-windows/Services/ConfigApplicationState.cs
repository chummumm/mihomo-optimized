namespace Mihomo.Desktop.Services;

/// <summary>Tracks an acknowledged file write separately from applying it to the core.</summary>
internal sealed class ConfigApplicationState
{
    private string path = "", revision = "";

    public bool Pending { get; private set; }
    public string Failure { get; private set; } = "";

    public void ObserveDocument(string documentPath, string documentRevision)
    {
        bool differentFile = path.Length > 0 && !StringComparer.OrdinalIgnoreCase.Equals(path, documentPath);
        if (differentFile) Applied();
        // A new file revision can invalidate the old validation error. Reading
        // it still does not acknowledge that the core applied the saved file.
        else if (revision.Length > 0 && revision != documentRevision) Failure = "";
        path = documentPath;
        revision = documentRevision;
    }

    public void Saved()
    {
        Pending = true;
        Failure = "";
    }

    public void Applied()
    {
        Pending = false;
        Failure = "";
    }

    public void ApplyFailed(string message)
    {
        Pending = true;
        Failure = message;
    }
}
