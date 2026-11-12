using System.Text.Json;

namespace Mihomo.Desktop.Services;

internal enum PageNoticeKind { Information, Warning, Error }

internal sealed record PageNotice(string Text, PageNoticeKind Kind)
{
    internal static PageNotice Empty { get; } = new("", PageNoticeKind.Information);
    internal bool IsVisible => Text.Length > 0;
}

/// <summary>
/// Runtime observations replace only their own notice. A background recovery
/// must not acknowledge a failed page operation on the user's behalf.
/// </summary>
internal sealed class PageNoticeState
{
    internal PageNotice Operation { get; private set; } = PageNotice.Empty;
    internal PageNotice Runtime { get; private set; } = PageNotice.Empty;

    internal void ShowOperation(string text, bool error) =>
        Operation = new(text, error ? PageNoticeKind.Error : PageNoticeKind.Information);

    internal void ObserveRuntime(JsonElement status, DesktopRuntimeState runtime)
    {
        string text = status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty("lastError", out var error) && error.ValueKind == JsonValueKind.String
            ? error.GetString() ?? "" : "";
        if (text.Length == 0 && (runtime.NeedsAttention || runtime.Phase == DesktopRuntimePhase.CheckingTun))
            text = runtime.Description;
        var kind = runtime.IsProblem ? PageNoticeKind.Error
            : runtime.NeedsAttention ? PageNoticeKind.Warning : PageNoticeKind.Information;
        Runtime = new(text, kind);
    }
}
