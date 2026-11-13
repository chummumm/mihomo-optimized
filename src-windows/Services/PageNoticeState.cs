using System.Text.Json;

namespace Mihomo.Desktop.Services;

internal enum PageNoticeKind { Information, Warning, Error }

internal sealed record PageNotice(string Text, PageNoticeKind Kind, object[]? Arguments = null)
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
    internal PageNotice LastOperation { get; private set; } = PageNotice.Empty;

    internal void ShowOperation(string text, bool error) =>
        Operation = new(text, error ? PageNoticeKind.Error : PageNoticeKind.Information);

    internal void ObserveRuntime(JsonElement status, DesktopRuntimeState runtime)
    {
        string text = runtime.RuntimeError;
        if (text.Length == 0 && (runtime.NeedsAttention || runtime.Phase == DesktopRuntimePhase.CheckingTun))
            text = runtime.Description;
        var kind = runtime.IsProblem ? PageNoticeKind.Error
            : runtime.NeedsAttention ? PageNoticeKind.Warning : PageNoticeKind.Information;
        Runtime = new(text, kind);
        ObserveLastOperation(runtime);
    }

    internal void ObserveLastOperation(DesktopRuntimeState runtime) => LastOperation = FailureNotice(runtime);

    internal static PageNotice FailureNotice(DesktopRuntimeState runtime)
    {
        if (runtime.LastOperationFailure is not { } failure) return PageNotice.Empty;
        string template = failure.Operation switch
        {
            "tun_change" when failure.RequestedTun && !runtime.TunRequested && runtime.Phase == DesktopRuntimePhase.Running
                => "上次开启 TUN 失败（{0}），当前 TUN 已关闭。{1}",
            "tun_change" when !failure.RequestedTun && runtime.TunRequested && runtime.Phase == DesktopRuntimePhase.Running
                => "上次关闭 TUN 失败（{0}），当前 TUN 仍开启。{1}",
            "tun_change" => failure.RequestedTun ? "上次开启 TUN 失败（{0}）。{1}" : "上次关闭 TUN 失败（{0}）。{1}",
            "config_reload" => "上次配置重载失败（{0}）。{1}",
            "config_patch" => "上次配置修改失败（{0}）。{1}",
            _ => "上次启动失败（{0}）。{1}"
        };
        return new(template, PageNoticeKind.Warning,
            [failure.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), failure.Message]);
    }
}
