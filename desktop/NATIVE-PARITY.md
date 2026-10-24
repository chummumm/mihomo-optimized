# 最终 Fluent 设计 → Windows 原生实现

设计依据是用户提供的 `mihomo-fluent-redesign(1).zip` 中 `files/` 的最终源文件及 patch。本表记录原生实现入口和验收路径；网页端文件仍作为设计依据，不参与 Windows 运行。Windows 使用 C# WPF，系统集成保留现有 Rust host 和固定 Go 内核。

## 0.2.1 视觉校准

原稿的完整前端通过 `visual-reference.json` 固定到基础提交和补丁 SHA-256，包含压缩包没有重复携带的 Logs 等基础页面。Windows 验收在同一 runner 上分别渲染原稿和安装后的 WPF 程序，使用同样的窗口尺寸、DPI、语言、主题强调色和无凭据测试数据。原稿仅作为测试基准，不进入安装包或便携包。

这一轮按原稿恢复透明色叠加、文字和行高、控件边框、卡片尺寸、页面留白、内容宽度断点，以及每页的标题、默认操作和滚动范围。全局搜索输入属于主标题栏，结果列表在同一个原生窗口的顶层展开，共用既有呈现通道；连接详情在表格内部展开，配置编辑器使用原稿的独立子页面结构。

导入配置使用原稿的文件/粘贴/URL 三模式预览，成功后重新加载；编辑器未保存提示保持在原稿内联区域。首次开启 TUN 的说明使用原稿内容对话框。16 个静态场景包含这些操作后的界面，7 个交互动效另行保存实际时间与连续帧，分别覆盖紧凑导航展开和窄窗导航滑入。

共享动画使用原稿的 cubic-bezier 曲线及 83/167/250ms 时长；页面进入为 300ms，导航指示条、节点数值、折线和定位高亮保留各自的时序。界面仍遵从系统减少动画设置。验收临时开启一次性 runner 的客户端动画，并在清理时恢复原值。

局部浮层仅采样明确指定的下层视觉，按实际 DPI 保留原始分辨率。界面线程复制当前绘制内容和视觉属性为不可变场景，后台 STA 重建相同的 VisualBrush 映射并读取像素，再在后台进行 Gaussian 模糊和预乘 alpha 的 sRGB 饱和度处理，最后叠加原稿的半透明颜色、边框和前景。包含不能冻结的资源时保留原有同步采样路径，并记录原因。像素没有变化时复用结果；真实内容变化触发更新，尺寸、来源或 DPI 改变时取消过期计算。快照时间记录真实来源观察时刻，后台完成时间不会让旧内容变成新采样。

缓存保留进入动效的完整采样范围，背景保持世界坐标，不随前景一起滑动。同一来源、几何和 DPI 的有效准备帧可以立即承载原有 250ms 过渡，同时继续验证当前像素；没有有效准备帧时等待实际滤镜图像。搜索浮层不属于任何采样源，阴影预留区域不拦截标题栏输入。截图元数据记录实际滤镜状态、DPI、采样范围、界面快照耗时和后台渲染耗时，系统合成材料仍须按实际机器核对。

| 原稿 | 原生入口 | 对应行为 |
| --- | --- | --- |
| Shell/TitleBar、Sidebar、Shell/nav-mode | MainWindow、NativeTitleBar | 默认 1240×820、最小 500×560，48px 标题栏、返回、窗口按钮、拖动/缩放/贴靠、展开/紧凑/覆盖式导航、Escape 关闭覆盖导航 |
| Shell/appearance、styles/tokens、components/Icon/Motion | AppSession、NativeBackdrop、FluentIcon、FluentMotion | 系统主题与强调色、Mica/Acrylic、原稿 Fluent 图标、状态和页面过渡、减少动画/高对比度/透明效果回退 |
| Shell/CoreCard | MainWindow CoreCard | 内核状态、版本、启停/重启/日志/配置快捷入口 |
| Shell/GlobalSearch、search-targets、Settings/focus-target | SearchFlyout、SearchTargets、NavigationTarget | 命令/页面/组/节点/设置/规则检索、匹配高亮、键盘操作、设置定位和短暂高亮、代理定位、规则筛选 |
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
- `desktop/tests/backdrop`：以原始 UI 视觉的 VisualBrush/RTB 为基准，逐像素检查后台场景的变换、裁剪、透明度、效果、文本和不同 DPI，并验证冻结后的来源隔离、取消和不支持资源回退。
- `desktop/tests/windows_smoke.py` 与 `uia-query.ps1`：实际安装程序、普通权限 WPF 界面、原生 UIA 控件操作及截图；验证空连接表能实际横向滚到末列、首列保持冻结并恢复起点，同时保留系统代理、TUN、服务身份、资源引导和异常退出恢复检查。
- `desktop/tests/prepare-visual-reference.py` 与 `capture-reference.cjs`：恢复并渲染固定原稿，使用安装验收导出的安全数据和实际页面滚动位置；输出实际尺寸、DPI、主题、字体及关键元素几何信息。测试浏览器禁止访问本地夹具以外的网络目标。
- `NativeMotionCapture`：由独立 Windows UIA 操作触发真实控件事件，后台读取实际桌面像素，保存页面、选中项、开关、搜索、编辑器和窄窗导航的连续帧、实际采样时间、采集耗时与屏幕可见区域。录制不调用 WPF 布局或渲染，PNG 编码延后到交互结束；它只录制，不替代或驱动控件行为。静态图与动画帧是人工对照证据，生成图片本身不等于逐像素一致性判定。
- `desktop/tests/upgrade_smoke.py`：真实旧发行升级、卸载残留修复、配置和所有者保留、安装/便携逐文件一致性。
- `.github/workflows/windows-desktop.yml`：通过全部 Windows 检查后，由 `clash-dashboard` 受信工作流生成构建证明并发布 `mihomo-desktop-v0.2.1`。PR 构建不发布正式 Release。

WPF 视觉树截图来自实际运行的控件。系统合成的 Mica/Acrylic 需在支持透明效果的 Windows 11 桌面观察；不支持材料的系统使用原稿规定的实色回退。YAML 编辑器依照原稿的纯文本编辑模型实现，不将原稿没有的代码补全列作已实现功能。

如果 UI 验收中途失败，工作流也会用已保存的安全数据渲染已有原稿场景，并明确标记为不完整诊断。它不会将失败的 Windows 验收改成通过；完整成功仍要求全部 16 个静态场景和 7 个交互录制。
