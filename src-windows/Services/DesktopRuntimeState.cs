using System.Text.Json;

namespace Mihomo.Desktop.Services;

internal enum DesktopRuntimePhase
{
    Connecting,
    Stopped,
    Starting,
    Running,
    CheckingTun,
    Reloading,
    Restarting,
    Recovering,
    RepairingTun,
    RecoveringTunDevice,
    Uncertain,
    TunDegraded,
    TunFailed,
    TunDeviceConflict,
    Stopping,
    StopUnconfirmed,
    Exited,
    Error
}

/// <summary>
/// Presentation and actions use independent process, controller and TUN observations.
/// An unavailable health observation never proves that the owned process exited.
/// </summary>
internal sealed record DesktopRuntimeState
{
    public required DesktopRuntimePhase Phase { get; init; }
    public bool ProcessAlive { get; init; }
    public bool RuntimeAvailable { get; init; }
    public bool StopRequired { get; init; }
    // The host's "tun" field is the last confirmed applied mode.
    public bool TunRequested { get; init; }
    public bool TunDeviceRecoveryPending { get; init; }
    public string RuntimeError { get; init; } = "";
    public DesktopOperationFailure? LastOperationFailure { get; init; }
    public bool TunEnableUnfulfilled => !TunRequested && LastOperationFailure is { RequestedTun: true };

    public bool IsWorking => Phase is DesktopRuntimePhase.Connecting or DesktopRuntimePhase.Starting
        or DesktopRuntimePhase.Reloading or DesktopRuntimePhase.Restarting or DesktopRuntimePhase.Recovering
        or DesktopRuntimePhase.RepairingTun or DesktopRuntimePhase.RecoveringTunDevice or DesktopRuntimePhase.Stopping
        || Phase == DesktopRuntimePhase.Uncertain && TunDeviceRecoveryPending;
    public bool IsProblem => Phase is DesktopRuntimePhase.TunFailed or DesktopRuntimePhase.StopUnconfirmed
        or DesktopRuntimePhase.TunDeviceConflict or DesktopRuntimePhase.Exited or DesktopRuntimePhase.Error;
    public bool NeedsAttention => IsProblem || Phase is DesktopRuntimePhase.Uncertain or DesktopRuntimePhase.TunDegraded;
    public bool CanStart => !StopRequired && Phase is DesktopRuntimePhase.Stopped or DesktopRuntimePhase.Exited
        or DesktopRuntimePhase.TunDeviceConflict or DesktopRuntimePhase.Error;

    public string Label => Phase switch
    {
        DesktopRuntimePhase.Stopped => "未运行",
        DesktopRuntimePhase.Starting => "正在启动",
        DesktopRuntimePhase.Running => "运行中",
        DesktopRuntimePhase.CheckingTun => "正在检查 TUN",
        DesktopRuntimePhase.Reloading => "正在应用配置",
        DesktopRuntimePhase.Restarting => "正在重启内核",
        DesktopRuntimePhase.Recovering => "正在恢复内核",
        DesktopRuntimePhase.RepairingTun => "正在恢复 TUN",
        DesktopRuntimePhase.RecoveringTunDevice => "正在自动恢复 TUN 接口",
        DesktopRuntimePhase.Uncertain => "状态待确认",
        DesktopRuntimePhase.TunDegraded => "TUN 需要处理",
        DesktopRuntimePhase.TunFailed => "TUN 恢复失败",
        DesktopRuntimePhase.TunDeviceConflict => "TUN 网卡名称冲突",
        DesktopRuntimePhase.Stopping => "正在停止",
        DesktopRuntimePhase.StopUnconfirmed => "停止尚未完成",
        DesktopRuntimePhase.Exited => "内核已退出",
        DesktopRuntimePhase.Error => "内核需要处理",
        _ => "正在连接…"
    };

    public string Description => Phase switch
    {
        DesktopRuntimePhase.Starting => "正在校验配置、准备所需资源并等待内核就绪，可随时停止。",
        DesktopRuntimePhase.Restarting => "正在停止旧内核并准备重新启动，可随时停止。",
        DesktopRuntimePhase.Reloading => "正在应用已保存的配置，可随时停止内核。",
        DesktopRuntimePhase.CheckingTun => "内核已就绪，正在确认 TUN 状态。",
        DesktopRuntimePhase.Recovering => "正在核验本地内核与网络设备，可随时停止恢复。",
        DesktopRuntimePhase.RepairingTun => "内核进程仍在运行，正在恢复本机 TUN 设备。",
        DesktopRuntimePhase.RecoveringTunDevice => "正在自动恢复 TUN 接口，可随时停止。原配置文件保持不变。",
        DesktopRuntimePhase.Uncertain => ProcessAlive
            ? "内核进程仍在运行，正在等待最新的本机健康状态。"
            : "暂时无法确认内核状态，正在等待本机服务的最新结果。",
        DesktopRuntimePhase.TunDegraded => "内核进程仍在运行，TUN 设备需要处理。请查看详细信息或日志。",
        DesktopRuntimePhase.TunFailed => "TUN 恢复未完成。请查看详细信息或日志；可停止内核后重试。",
        DesktopRuntimePhase.TunDeviceConflict => "TUN 接口未能启动：此名称已被另一张网卡使用。请查看详细信息或日志。",
        DesktopRuntimePhase.Stopping => "正在停止代理连接并恢复系统设置，请稍候。",
        DesktopRuntimePhase.StopUnconfirmed => "尚未确认旧内核退出，暂不启动新内核。请查看详细信息或日志。",
        DesktopRuntimePhase.Exited => "已确认内核进程退出。请查看错误信息或日志，再重试启动。",
        DesktopRuntimePhase.Error => "请查看错误信息或日志，再重试启动；需要时可在设置中编辑配置。",
        DesktopRuntimePhase.Connecting => "正在读取本机内核状态，请稍候。",
        _ => "已有配置时，可从顶部启动内核；首次使用请在设置中导入完整配置。"
    };

    public string TunDescription => !TunRequested
        ? Phase is DesktopRuntimePhase.Uncertain or DesktopRuntimePhase.StopUnconfirmed ? "状态待确认"
            : TunEnableUnfulfilled ? "已关闭 · 上次开启失败" : "已关闭"
        : Phase switch
    {
        DesktopRuntimePhase.RepairingTun => "正在恢复 TUN",
        DesktopRuntimePhase.RecoveringTunDevice => "正在自动恢复 TUN",
        DesktopRuntimePhase.CheckingTun => "正在检查 TUN",
        DesktopRuntimePhase.TunFailed => "TUN 恢复失败",
        DesktopRuntimePhase.TunDeviceConflict => "网卡名称冲突",
        DesktopRuntimePhase.TunDegraded => "TUN 需要处理",
        DesktopRuntimePhase.Uncertain or DesktopRuntimePhase.StopUnconfirmed => "状态待确认",
        DesktopRuntimePhase.Stopped or DesktopRuntimePhase.Exited or DesktopRuntimePhase.Error => "内核启动后生效",
        DesktopRuntimePhase.Starting or DesktopRuntimePhase.Restarting => "等待内核就绪",
        DesktopRuntimePhase.Stopping => "正在停止",
        _ => "已开启"
    };

    public static DesktopRuntimeState Read(JsonElement status, string? command = null)
    {
        string phase = Text(status, "phase", "connecting");
        string process = Text(status, "processState");
        string control = Text(status, "controlState");
        string operation = Text(status, "operation");
        string tunHealth = Text(status, "tunHealth");
        string error = Text(status, "errorCode");
        bool tunRequested = Boolean(status, "tun");
        bool alive = process == "alive" || process.Length == 0 && phase == "running";
        bool desired = Boolean(status, "desiredRunning");
        bool ownsProcess = alive || HasPid(status);
        // A confirmed Stop has no live process whose health can age. Expiring
        // that observation would disable Start after sitting idle for 30s.
        bool confirmedStopped = phase == "stopped" && process == "exited" && !ownsProcess && !desired && operation.Length == 0;
        bool stale = Boolean(status, "statusStale") && !confirmedStopped;
        // A rejected enable is an operation result, not a fault in an otherwise
        // confirmed healthy proxy. Older snapshots could retain its error code.
        bool disabledProxyReady = alive && !tunRequested && phase == "running" && control == "ready"
            && tunHealth == "disabled" && !stale && operation.Length == 0;
        bool historicalTunConflict = disabledProxyReady && error == "tun_device_conflict";
        bool stopPending = command == "desktop_stop";
        bool starting = command is "desktop_start" or "desktop_restart";
        bool recoveringTunDevice = operation == "recovering_tun_device";
        bool stopUnconfirmed = error == "core_termination_unconfirmed";
        // The host owns automatic recovery even between the old process exiting
        // and its replacement starting. That gap must not expose another Start.
        bool stopRequired = ownsProcess || starting || stopPending || recoveringTunDevice || stopUnconfirmed
            || operation == "stopping"
            || phase is "starting" or "recovering" or "stopping"
            || desired && process != "exited";

        DesktopRuntimePhase state;
        if (stopPending) state = DesktopRuntimePhase.Stopping;
        else if (stopUnconfirmed) state = DesktopRuntimePhase.StopUnconfirmed;
        else if (operation == "stopping" || phase == "stopping") state = DesktopRuntimePhase.Stopping;
        else if (recoveringTunDevice) state = stale || process is not ("alive" or "exited")
            ? DesktopRuntimePhase.Uncertain : DesktopRuntimePhase.RecoveringTunDevice;
        else if (command == "desktop_restart" || operation == "restarting") state = DesktopRuntimePhase.Restarting;
        else if (command == "desktop_start" || operation == "starting" || phase == "starting") state = DesktopRuntimePhase.Starting;
        else if (stale) state = DesktopRuntimePhase.Uncertain;
        else if (operation == "recovering_tun") state = DesktopRuntimePhase.RepairingTun;
        else if (command == "desktop_reload_config" || operation == "reloading") state = DesktopRuntimePhase.Reloading;
        else if (error == "tun_device_conflict" && !historicalTunConflict && phase != "recovering" && (alive || process == "exited")) state = DesktopRuntimePhase.TunDeviceConflict;
        else if (alive && tunRequested && (error is "tun_recovery_failed" or "tun_recovery_needs_restart" or "tun_restart_required" || tunHealth == "failed")) state = DesktopRuntimePhase.TunFailed;
        else if (alive && tunRequested && (tunHealth is "degraded" or "conflict" or "disabled" || error is "tun_not_repairable" or "tun_ownership_conflict")) state = DesktopRuntimePhase.TunDegraded;
        else if (alive && (tunRequested && (tunHealth == "unavailable" || error == "tun_health_unavailable") || control is "unknown" or "unreachable" or "busy")) state = DesktopRuntimePhase.Uncertain;
        else if (alive && phase == "error") state = DesktopRuntimePhase.Uncertain;
        // Initial sampling keeps a ready controller usable. Existing errors
        // and stale observations retain their attention state above.
        else if (alive && tunRequested && tunHealth == "checking") state = error.Length == 0
            ? DesktopRuntimePhase.CheckingTun : DesktopRuntimePhase.Uncertain;
        else if (phase == "recovering") state = DesktopRuntimePhase.Recovering;
        else if (process == "exited" && phase == "error") state = DesktopRuntimePhase.Exited;
        else if (process == "unknown" && phase != "connecting") state = DesktopRuntimePhase.Uncertain;
        else state = phase switch
        {
            "running" => DesktopRuntimePhase.Running,
            "stopped" => DesktopRuntimePhase.Stopped,
            "error" => DesktopRuntimePhase.Error,
            _ => DesktopRuntimePhase.Connecting
        };

        bool hasController = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("controller", out var controller)
            && controller.ValueKind == JsonValueKind.Object && Text(controller, "url").Length > 0;
        bool runtimeAvailable = hasController && alive && !stale && !recoveringTunDevice
            && (control is "ready" or "busy" || control.Length == 0 && phase == "running")
            && state is not (DesktopRuntimePhase.Starting or DesktopRuntimePhase.Restarting or DesktopRuntimePhase.RecoveringTunDevice
                or DesktopRuntimePhase.Stopping or DesktopRuntimePhase.StopUnconfirmed);

        return new DesktopRuntimeState
        {
            Phase = state,
            ProcessAlive = alive,
            RuntimeAvailable = runtimeAvailable,
            StopRequired = stopRequired,
            TunRequested = tunRequested,
            TunDeviceRecoveryPending = recoveringTunDevice,
            RuntimeError = historicalTunConflict ? "" : Text(status, "lastError"),
            LastOperationFailure = DesktopOperationFailure.Read(status)
        };
    }

    private static bool HasPid(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetUInt32(out uint id) && id > 0;
    private static bool Boolean(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement value, string key, string fallback = "") => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? fallback : fallback;
}

/// <summary>An accepted operation result, independent of the latest health observation.</summary>
internal sealed record DesktopOperationFailure(string Operation, string Code, string Message,
    DateTimeOffset OccurredAt, bool RequestedTun, bool AppliedTun, ulong IntentRevision)
{
    internal static DesktopOperationFailure? Read(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty("lastOperationFailure", out var failure) || failure.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty("intentRevision", out var current) || current.ValueKind != JsonValueKind.Number || !current.TryGetUInt64(out ulong currentRevision)
            || !failure.TryGetProperty("intentRevision", out var intent) || intent.ValueKind != JsonValueKind.Number || !intent.TryGetUInt64(out ulong revision)
            || revision > currentRevision
            || !failure.TryGetProperty("operation", out var operation) || operation.ValueKind != JsonValueKind.String
            || operation.GetString() is not ("tun_change" or "config_reload" or "config_patch" or "start")
            || !failure.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(code.GetString())
            || !failure.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(message.GetString())
            || !failure.TryGetProperty("occurredAt", out var timestamp) || timestamp.ValueKind != JsonValueKind.Number || !timestamp.TryGetInt64(out long milliseconds)
            || milliseconds <= 0 || milliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            || !failure.TryGetProperty("requestedTun", out var requested) || requested.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !failure.TryGetProperty("appliedTun", out var applied) || applied.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;
        return new(operation.GetString()!, code.GetString()!, message.GetString()!,
            DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), requested.GetBoolean(), applied.GetBoolean(), revision);
    }
}
