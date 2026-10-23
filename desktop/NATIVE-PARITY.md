# 最终 Fluent 设计 → Windows 原生实现

设计依据是用户提供的 `mihomo-fluent-redesign(1).zip` 中 `files/` 的最终源文件及 patch。本表记录原生实现入口和验收路径；网页端文件仍作为设计依据，不参与 Windows 运行。Windows 使用 C# WPF，系统集成保留现有 Rust host 和固定 Go 内核。

| 原稿 | 原生入口 | 对应行为 |
| --- | --- | --- |
| Shell/TitleBar、Sidebar、Shell/nav-mode | MainWindow、NativeTitleBar | 48px 标题栏、返回、窗口按钮、拖动/缩放/贴靠、展开/紧凑/覆盖式导航、Escape 关闭覆盖导航 |
| Shell/appearance、styles/tokens、components/Icon/Motion | AppSession、NativeBackdrop、FluentIcon、FluentMotion | 系统主题与强调色、Mica/Acrylic、原稿 Fluent 图标、状态和页面过渡、减少动画/高对比度/透明效果回退 |
| Shell/CoreCard | MainWindow CoreCard | 内核状态、版本、启停/重启/日志/配置快捷入口 |
| Shell/GlobalSearch、search-targets、Settings/focus-target | SearchWindow、SearchTargets、NavigationTarget | 命令/页面/组/节点/设置/规则检索、匹配高亮、键盘操作、设置定位和短暂高亮、代理定位、规则筛选 |
| Home/index | HomePage、ProxyExitSnapshot、ExitSummaryList | 运行状态、常用开关、模式、实时/累计流量、4 个出口摘要、点击设置及策略组 |
| platform/TrayFlyout、tray.css | TrayController | 主窗口入口、系统代理/TUN/LAN 开关、三种模式、3 个出口、实时速率及 sparkline、内核电源、独立托盘菜单 |
| Proxies/index、Group、Proxy、Provider | ProxiesPage、ProxyNodeTile、ProxyPreferences | 过滤/Escape、Ctrl+T 与进度、组展开持久化、节点选择、关联连接关闭、延迟历史/排序/UDP、提供者更新和健康检查 |
| Proxies/Topology | ProxyTopologyModel、ProxyTopologyControl | 真实连接差分速率、来源→组→出口、曲线粒子与悬停、节点/组点击定位、滚动或隐藏时节制渲染 |
| Connections/Throughput、Devices 和原连接页面 | ConnectionsPage、TrafficChart、RouteDisplay | 60 秒实测历史、悬停值、曲线平移、来源过滤与速率份额、完整连接列、排序/列宽、实时详情、关闭、暂停和有界历史 |
| DNS 页面与最终样式 | DnsPage、DnsTrendChart、DnsPageIndex | 统计/完整分钟趋势/热门来源、精确筛选、50/100 条分页、第一页/末页/指定页、原子锚点、上游与真实出口明细 |
| Rules/index 与 Provider | RulesPage | 顺序规则、虚拟化列表、全文筛选/Escape、全局搜索带入查询、规则集合更新 |
| Logs 与最终全局样式 | LogsPage | 默认/Debug/Info/Warn/Error/Silent、暂停暂存/继续、清空、跟随、数量与级别颜色 |
| Profiles/index、Settings/ConfigEditor | ProfilesPage、ConfigCodeEditor | 当前配置摘要、文件/URL/粘贴/拖放、虚拟行号、光标与字节限制、Tab 缩进与键盘导航、Ctrl+S、原换行保留、只读/冲突、保存与重载分离、草稿确认、资源调度 |
| Settings/index、i18n | SettingsPage、L.* | 即时中文/英文、主题、系统/代理/服务设置、三个端口、探测 URL、目录入口、可信应用更新和进度 |

## 验收

- `desktop/tests/protocol`：stdio 边界、取消/并发、退出、Windows 单实例身份校验，以及 DNS 原子分页索引的稀疏 ID、UInt64 边界、空页、非法/旧版元数据。
- `desktop/tests/windows_smoke.py` 与 `uia-query.ps1`：实际安装程序、普通权限 WPF 界面、原生 UIA 控件操作及截图；同时保留系统代理、TUN、服务身份、资源引导和异常退出恢复检查。
- `desktop/tests/upgrade_smoke.py`：真实旧发行升级、卸载残留修复、配置和所有者保留、安装/便携逐文件一致性。
- `.github/workflows/windows-desktop.yml`：通过全部 Windows 检查后，由 `clash-dashboard` 受信工作流生成构建证明并发布 `mihomo-desktop-v0.2.0`。PR 构建不发布正式 Release。

WPF 视觉树截图来自实际运行的控件。系统合成的 Mica/Acrylic 需在支持透明效果的 Windows 11 桌面观察；不支持材料的系统使用原稿规定的实色回退。YAML 编辑器依照原稿的纯文本编辑模型实现，不将原稿没有的代码补全列作已实现功能。
