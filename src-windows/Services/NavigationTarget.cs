namespace Mihomo.Desktop.Services;

/// <summary>A page and the specific content or command selected in the shell.</summary>
public sealed record NavigationTarget(string Page, string? Target = null, string? Query = null, string? Action = null);
