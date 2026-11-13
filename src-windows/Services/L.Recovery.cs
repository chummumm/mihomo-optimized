namespace Mihomo.Desktop.Services;

public static partial class L
{
    static partial void RegisterRecovery(Dictionary<string, string> catalog)
    {
        catalog["正在连接桌面服务…"] = "Connecting to desktop service…";
        catalog["正在恢复"] = "Recovering";
        catalog["正在恢复…"] = "Recovering…";
        catalog["正在恢复内核…"] = "Recovering the core…";
        catalog["正在恢复本机内核"] = "Recovering the local core";
        catalog["正在恢复内核"] = "Recovering the core";
        catalog["正在应用配置"] = "Applying configuration";
        catalog["正在重启内核"] = "Restarting the core";
        catalog["正在恢复 TUN"] = "Recovering TUN";
        catalog["正在检查 TUN"] = "Checking TUN";
        catalog["内核已就绪，正在确认 TUN 状态。"] = "The core is ready. Checking the TUN status.";
        catalog["状态待确认"] = "Waiting for status";
        catalog["TUN 需要处理"] = "TUN needs attention";
        catalog["TUN 恢复失败"] = "TUN recovery failed";
        catalog["停止尚未完成"] = "Stop not yet confirmed";
        catalog["内核已退出"] = "The core exited";
        catalog["等待内核就绪"] = "Waiting for the core";
        catalog["等待本机状态更新"] = "Waiting for local status";
        catalog["正在校验配置、准备所需资源并等待内核就绪，可随时停止。"] = "Validating configuration, preparing resources, and waiting for the core. You can stop at any time.";
        catalog["正在停止旧内核并准备重新启动，可随时停止。"] = "Stopping the previous core before restarting. You can stop at any time.";
        catalog["正在应用已保存的配置，可随时停止内核。"] = "Applying the saved configuration. You can stop the core at any time.";
        catalog["内核进程仍在运行，正在恢复本机 TUN 设备。"] = "The core process is still running while its local TUN device is being recovered.";
        catalog["内核进程仍在运行，正在等待最新的本机健康状态。"] = "The core process is still running. Waiting for an updated local health observation.";
        catalog["暂时无法确认内核状态，正在等待本机服务的最新结果。"] = "The core status is not yet confirmed. Waiting for an updated result from the local service.";
        catalog["内核进程仍在运行，TUN 设备需要处理。请查看详细信息或日志。"] = "The core process is still running, but TUN needs attention. Review the details or logs.";
        catalog["TUN 恢复未完成。请查看详细信息或日志；可停止内核后重试。"] = "TUN recovery did not complete. Review the details or logs; you can stop the core and retry.";
        catalog["尚未确认旧内核退出，暂不启动新内核。请查看详细信息或日志。"] = "The previous core has not been confirmed stopped, so another core will not start. Review the details or logs.";
        catalog["已确认内核进程退出。请查看错误信息或日志，再重试启动。"] = "The core process has exited. Review the error or logs before trying to start it again.";
        catalog["正在核验本地内核与网络设备，可随时停止恢复。"] = "Checking the local core and network device. You can stop recovery at any time.";
        catalog["正在读取本机内核状态，请稍候。"] = "Reading the local core status. Please wait.";
        catalog["正在启动本机内核"] = "Starting the local core";
        catalog["正在校验配置、准备所需资源并等待内核就绪。首次启动可能需要更长时间，请稍候。"] = "Validating configuration, preparing required resources, and waiting for the core to become ready. The first start may take longer. Please wait.";
        catalog["正在停止本机内核"] = "Stopping the local core";
        catalog["正在停止代理连接并恢复系统设置，请稍候。"] = "Stopping proxy connections and restoring system settings. Please wait.";
        catalog["本机内核尚未运行"] = "The local core is not running";
        catalog["已有配置时，可从顶部启动内核；首次使用请在设置中导入完整配置。"] = "Start the core from the top bar if a configuration is already saved. For first use, import a complete configuration in Settings.";
        catalog["本机内核运行遇到问题"] = "The local core encountered a problem";
        catalog["请查看错误信息或日志，再重试启动；需要时可在设置中编辑配置。"] = "Review the error or logs, then try starting again. You can edit the configuration in Settings if needed.";
        catalog["无法读取本机状态"] = "Could not read the local status";
        catalog["无法连接桌面服务。请刷新状态，或完全退出后再次启动应用。"] = "Could not connect to the desktop service. Refresh its status, or exit the app fully and start it again.";
        catalog["启动内核"] = "Start core";
        catalog["刷新状态"] = "Refresh status";
        catalog["打开设置"] = "Open settings";
        catalog["正在自动恢复 TUN 接口"] = "Automatically recovering the TUN interface";
        catalog["正在自动恢复 TUN"] = "Automatically recovering TUN";
        catalog["正在自动恢复 TUN 接口，可随时停止。原配置文件保持不变。"] = "Automatically recovering the TUN interface. You can stop at any time. The configuration file is unchanged.";
        catalog["TUN 网卡名称冲突"] = "TUN adapter name conflict";
        catalog["网卡名称冲突"] = "Adapter name conflict";
        catalog["TUN 接口未能启动：此名称已被另一张网卡使用。请查看详细信息或日志。"] = "The TUN interface could not start: another adapter uses this name. Review the details or logs.";
        catalog["TUN 网卡名称"] = "TUN adapter name";
        catalog["配置名称：{0}"] = "Configured name: {0}";
        catalog["使用名称：{0}"] = "Effective name: {0}";
        catalog["准备使用名称：{0}"] = "Preparing to use: {0}";
        catalog["内核默认名称"] = "Core default";
        catalog["尚未确定"] = "Not yet known";
        catalog["使用配置中的网卡名称。若开启失败，保留失败详情，并显示当前实际状态。"] = "Uses the configured adapter name. If enabling fails, the failure details remain available alongside the current state.";
        catalog["已关闭 · 上次开启失败"] = "Off · last enable failed";
        catalog["上次开启 TUN 失败（{0}），当前 TUN 已关闭。{1}"] = "Last TUN enable failed ({0}). TUN is currently off. {1}";
        catalog["上次关闭 TUN 失败（{0}），当前 TUN 仍开启。{1}"] = "Last TUN disable failed ({0}). TUN is still on. {1}";
        catalog["上次开启 TUN 失败（{0}）。{1}"] = "Last TUN enable failed ({0}). {1}";
        catalog["上次关闭 TUN 失败（{0}）。{1}"] = "Last TUN disable failed ({0}). {1}";
        catalog["上次配置重载失败（{0}）。{1}"] = "Last configuration reload failed ({0}). {1}";
        catalog["上次配置修改失败（{0}）。{1}"] = "Last configuration change failed ({0}). {1}";
        catalog["上次启动失败（{0}）。{1}"] = "Last start failed ({0}). {1}";
    }
}
