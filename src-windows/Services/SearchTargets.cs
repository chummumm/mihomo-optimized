namespace Mihomo.Desktop.Services;

/// <summary>Shared, stable destinations for settings cards and the command palette.</summary>
internal static class SearchTargets
{
    internal sealed record Setting(string Id, string Title, string Keywords, string Page = "settings");

    internal static readonly Setting[] Settings =
    [
        new("set-theme", "应用主题", "theme appearance dark light system 主题 外观 深色 浅色 跟随系统"),
        new("set-language", "语言", "language English Chinese system 语言 英文 中文 跟随系统"),
        new("set-autostart", "开机时启动", "autostart login startup tray 开机 自启 托盘"),
        new("set-system-proxy", "设置为系统代理", "system proxy Windows 系统代理"),
        new("set-mode", "代理模式", "mode rule global direct 模式 规则 全局 直连"),
        new("set-lan", "允许来自局域网的连接", "lan allow-lan wifi 局域网 共享"),
        new("set-ports", "混合代理端口", "port mixed http socks 端口 混合"),
        new("set-probe", "测速 URL", "probe url latency speed test 测速 探测 延迟"),
        new("set-tun", "TUN 模式", "tun virtual adapter 虚拟网卡"),
        new("set-service", "系统服务", "service administrator tun 服务 管理员 安装 卸载"),
        new("desktop-app-update", "桌面应用", "update version release 更新 版本 发行 安装"),
        new("set-about", "关于 Mihomo Optimized", "about version github project 关于 版本 项目"),
        new("set-config-folder", "配置文件夹", "config directory folder 配置 目录 文件夹"),
        new("set-log-folder", "日志文件夹", "log directory folder 日志 目录 文件夹"),
        new("set-resources", "资源与规则集", "geo geoip geosite rule provider 规则集 资源", "profiles")
    ];
}
