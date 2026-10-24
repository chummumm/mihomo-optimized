namespace Mihomo.Desktop.Services;

public static partial class L
{
    static partial void RegisterRecovery(Dictionary<string, string> catalog)
    {
        catalog["正在连接桌面服务…"] = "Connecting to desktop service…";
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
    }
}
