using System.Text.Json;
using Mihomo.Desktop.Services;

internal static class PageNoticeStateTests
{
    internal static void Run()
    {
        ClearRecoveredRuntimeNotice();
        PreserveOperationFailures();
        DistinguishInitialTunObservation();
        KeepTunFailureSeparateFromLiveHealth();
        RejectInvalidOperationFailures();
        Console.WriteLine("Page notices: current health, timestamped operation failures, unfulfilled TUN intent, and ordered clearing remain independent.");
    }

    private static void ClearRecoveredRuntimeNotice()
    {
        var notices = new PageNoticeState();
        var order = new DesktopStatusOrder();
        void Observe(JsonElement status)
        {
            if (order.TryAccept(status)) notices.ObserveRuntime(status, DesktopRuntimeState.Read(status));
        }

        // This is the reported sequence: the same live PID first publishes an
        // unavailable observation, then healthy with no lastError.
        var unavailable = Status(40, health: "unavailable", phase: "recovering",
            errorCode: "tun_health_unavailable", message: "TUN 状态快照待刷新，保留内核并继续检查");
        Observe(unavailable);
        Check(notices.Runtime.IsVisible && notices.Runtime.Kind == PageNoticeKind.Warning,
            "A genuinely unavailable TUN observation lost its current warning");
        Observe(Status(41));
        Check(!notices.Runtime.IsVisible, "The recovered runtime retained the old status message");
        Observe(unavailable);
        Check(!notices.Runtime.IsVisible, "A delayed old observation restored the cleared warning");

        Observe(Status(42, health: "failed", phase: "error", errorCode: "tun_recovery_failed", message: "TUN 恢复失败"));
        Check(notices.Runtime.Kind == PageNoticeKind.Error, "A terminal runtime failure lost its error presentation");
        Observe(Status(43, phase: "stopped", process: "exited", control: "unknown", tun: false));
        Check(!notices.Runtime.IsVisible, "A confirmed new stopped state retained the old runtime failure");
    }

    private static void PreserveOperationFailures()
    {
        var notices = new PageNoticeState();
        const string failure = "配置校验未通过";
        notices.ShowOperation(failure, true);
        foreach (var status in new[]
        {
            Status(1, health: "unavailable", errorCode: "tun_health_unavailable", message: "正在等待最新快照"),
            Status(2),
            Status(3, health: "checking")
        })
        {
            notices.ObserveRuntime(status, DesktopRuntimeState.Read(status));
            Check(notices.Operation.Text == failure && notices.Operation.Kind == PageNoticeKind.Error,
                "A background observation acknowledged a failed user command");
        }

        Check(notices.Runtime.IsVisible && notices.Runtime.Kind == PageNoticeKind.Information,
            "The current runtime observation was hidden behind the independent command failure");
        notices.ShowOperation("", false);
        Check(!notices.Operation.IsVisible && notices.Runtime.IsVisible,
            "Completing a later page operation erased the independent runtime observation");
        var ready = Status(4);
        notices.ObserveRuntime(ready, DesktopRuntimeState.Read(ready));
        Check(!notices.Runtime.IsVisible && !notices.Operation.IsVisible,
            "Completed operation and runtime notices remained visible");
    }

    private static void DistinguishInitialTunObservation()
    {
        var pending = Status(1, health: "checking");
        var runtime = DesktopRuntimeState.Read(pending);
        Check(runtime.Phase == DesktopRuntimePhase.CheckingTun && runtime.RuntimeAvailable && runtime.StopRequired
            && !runtime.CanStart && !runtime.IsWorking && !runtime.NeedsAttention && !runtime.IsProblem,
            "Initial TUN sampling claimed health, reported failure, or blocked a ready controller");
        Check(runtime.TunDescription == "正在检查 TUN", "Initial sampling was presented as enabled and healthy");
        var notices = new PageNoticeState();
        notices.ObserveRuntime(pending, runtime);
        Check(notices.Runtime.Text == runtime.Description && notices.Runtime.Kind == PageNoticeKind.Information,
            "Initial sampling did not expose its checking state");

        foreach (var status in new[]
        {
            Status(2, health: "unavailable"),
            Status(3, health: "checking", stale: true),
            Status(4, health: "checking", errorCode: "tun_health_unavailable"),
            Status(5, health: "checking", control: "unreachable"),
            Status(6, health: "checking", phase: "error", errorCode: "other_failure")
        })
        {
            var uncertain = DesktopRuntimeState.Read(status);
            notices.ObserveRuntime(status, uncertain);
            Check(uncertain.Phase == DesktopRuntimePhase.Uncertain && uncertain.NeedsAttention
                && notices.Runtime.IsVisible && notices.Runtime.Kind == PageNoticeKind.Warning,
                "An unavailable, stale, or failed observation was hidden as an initial TUN check");
        }
        Check(DesktopRuntimeState.Read(Status(7, health: "checking", errorCode: "tun_recovery_failed")).IsProblem,
            "Initial sampling concealed a terminal TUN failure");
        Check(DesktopRuntimeState.Read(pending, "desktop_stop").Phase == DesktopRuntimePhase.Stopping,
            "A pending observation overrode the user's Stop");
        Check(DesktopRuntimeState.Read(Status(8, health: "disabled")).Phase == DesktopRuntimePhase.TunDegraded,
            "A genuinely disabled requested TUN was silently reclassified as checking");
    }

    private static void KeepTunFailureSeparateFromLiveHealth()
    {
        var order = new DesktopStatusOrder();
        var notices = new PageNoticeState();
        var failure = OperationFailure();
        void Observe(JsonElement status)
        {
            if (order.TryAccept(status)) notices.ObserveRuntime(status, DesktopRuntimeState.Read(status));
        }
        var rejectedEnable = Status(10, health: "disabled", tun: false, failure: failure);
        var runtime = DesktopRuntimeState.Read(rejectedEnable);
        Check(runtime.Phase == DesktopRuntimePhase.Running && runtime.RuntimeAvailable && !runtime.IsProblem,
            "A rejected TUN enable marked the confirmed healthy proxy as failed");
        Check(runtime.TunEnableUnfulfilled && !runtime.TunRequested
            && runtime.TunDescription == "已关闭 · 上次开启失败"
            && runtime.LastOperationFailure?.OccurredAt.ToUnixTimeMilliseconds() == 1760000000123,
            "The failed request or its original time was lost, or TUN was shown as enabled");
        Observe(rejectedEnable);
        Check(!notices.Runtime.IsVisible && notices.LastOperation.IsVisible
            && notices.LastOperation.Kind == PageNoticeKind.Warning
            && notices.LastOperation.Arguments is { Length: 2 } arguments
            && arguments[1].Equals("fixture native owner is busy"),
            "The historical failure was hidden or presented as current runtime health");

        // Neither an ordinary health observation nor an unrelated scalar write
        // acknowledges the rejected enable request.
        Observe(Status(11, health: "disabled", tun: false, failure: failure));
        notices.ShowOperation("", false);
        Observe(Status(12, health: "disabled", tun: false, failure: failure, intentRevision: 2));
        Check(notices.LastOperation.IsVisible && !notices.Runtime.IsVisible && !notices.Operation.IsVisible,
            "A poll or unrelated successful operation acknowledged the failed TUN request");

        // A later successful TUN action explicitly clears the host failure.
        Observe(Status(13, intentRevision: 3));
        Check(!notices.LastOperation.IsVisible, "A successful TUN retry retained the old failure");
        Observe(Status(14, health: "disabled", tun: false, failure: failure));
        Observe(rejectedEnable);
        Check(!notices.LastOperation.IsVisible, "A delayed failed operation replaced the newer successful intent");

        var unresolved = Status(15, health: "unavailable", phase: "error", control: "unknown", tun: false,
            failure: failure, errorCode: "tun_rollback_unconfirmed", message: "rollback not confirmed", intentRevision: 4);
        Observe(unresolved);
        var uncertain = DesktopRuntimeState.Read(unresolved);
        Check(notices.Runtime.IsVisible && notices.LastOperation.IsVisible
            && uncertain.NeedsAttention && uncertain.TunDescription == "状态待确认"
            && notices.LastOperation.Text == "上次开启 TUN 失败（{0}）。{1}",
            "Historical failure concealed an unconfirmed current runtime state");
    }

    private static void RejectInvalidOperationFailures()
    {
        foreach (var change in new Action<Dictionary<string, object?>>[]
        {
            value => value["occurredAt"] = "yesterday",
            value => value["occurredAt"] = long.MaxValue,
            value => value["occurredAt"] = 0,
            value => value["requestedTun"] = "true",
            value => value.Remove("appliedTun"),
            value => value["intentRevision"] = "1",
            value => value["intentRevision"] = 2,
            value => value["operation"] = "unknown",
            value => value["message"] = ""
        })
        {
            var failure = OperationFailure(); change(failure);
            var runtime = DesktopRuntimeState.Read(Status(1, tun: false, health: "disabled", failure: failure));
            Check(runtime.LastOperationFailure is null && !runtime.TunEnableUnfulfilled
                && !PageNoticeState.FailureNotice(runtime).IsVisible,
                "Malformed or not-yet-accepted operation data invented a failed TUN request");
        }
    }

    private static Dictionary<string, object?> OperationFailure() => new()
    {
        ["operation"] = "tun_change", ["code"] = "tun_device_busy", ["message"] = "fixture native owner is busy",
        ["occurredAt"] = 1760000000123L, ["requestedTun"] = true, ["appliedTun"] = false, ["intentRevision"] = 1UL
    };

    private static JsonElement Status(int sequence, string health = "healthy", string phase = "running",
        string process = "alive", string control = "ready", bool tun = true, bool stale = false,
        string? errorCode = null, string? message = null, object? failure = null, ulong intentRevision = 1) => JsonSerializer.SerializeToElement(new
        {
            statusInstance = "host-fixture", statusSequence = sequence, intentRevision,
            phase, processState = process, controlState = control, tunHealth = health, tun,
            desiredRunning = phase != "stopped", pid = process == "alive" ? 5740 : (int?)null,
            errorCode, lastError = message, statusStale = stale, lastOperationFailure = failure,
            controller = process == "alive" ? new { url = "http://127.0.0.1:1234" } : null
        });

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
