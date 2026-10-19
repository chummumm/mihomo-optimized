# Mihomo Optimized Windows 桌面版

主界面沿用本仓库的 React UI，包含连接、DNS、代理、规则、日志和配置编辑页面。桌面宿主为 Tauri 2，配套的是本项目的 Mihomo 二开内核。

## 下载与启动

- `mihomo-desktop-<版本>-windows-x64-setup.exe`：Windows x64 安装包。安装到 Program Files，适合日常使用及 TUN。
- `mihomo-desktop-<版本>-windows-x64-portable.zip`：解压整个目录后运行 `mihomo-optimized.exe`，适合普通系统代理使用。TUN 需要使用安装版的受保护程序路径。
- 配置与桌面设置保存在 `%LOCALAPPDATA%\Mihomo Optimized`。便携包是免安装的程序分发形式，用户数据仍保存在此目录。
- 界面使用 Microsoft WebView2。安装包在缺少运行环境时自动下载引导安装；离线电脑请先准备 WebView2 Runtime。

首次启动后，在设置中导入 Clash / Mihomo YAML 文件，或粘贴配置订阅地址，然后启动内核。默认示例配置只有直连规则，不能替代你自己的节点或订阅。

## 桌面操作

关闭窗口会收起到托盘；托盘可以重新打开界面、停止代理，或真正退出。退出时，程序先恢复由它设置的系统代理，再关闭配套内核。如果系统代理在运行期间被别的程序修改，会保留那个新设置。

普通系统代理不需要让整个界面以管理员身份运行。启用 TUN 时，设置页面会安装受限 Windows 服务并请求一次管理员授权；服务只管理本项目的固定内核。可在设置中卸载服务。服务失去桌面程序心跳后会停止 TUN 内核，防止关闭界面进程后仍遗留接管网络的进程。

安装 TUN 服务时会为受保护安装目录中的 `mihomo-core.exe` 配置 TCP 入站防火墙例外，供 `mixed` / `system` 协议栈使用；升级保留并修复此规则，卸载服务时清理本程序规则。

TUN 使用你导入配置中的 DNS 策略，要求 `dns.enable: true`。配置里的 `tun.enable` 由桌面开关控制。Linux 的透明代理、iptables 和 TCP SYN 主动探测参数不适用于 Windows；发现不兼容项时，先在配置编辑器中修复。

## 配置与故障恢复

内核启动失败时，主界面仍可以打开配置编辑器和日志目录。保存配置使用内容修订号检查，避免覆盖另一个编辑器刚保存的文件。保存和重载是两步：保存失败不会覆盖旧文件；内核不接受重载时会保留先前运行配置。

控制端口和认证密钥由桌面程序临时生成，不从网页 URL 或浏览器存储读取。控制接口只监听本机回环地址。导入的配置不能改变此控制通道，也不能启用内核自己的二进制或 UI 更新。

## 更新

### 从 0.1.0 / 0.1.1 升级

0.1.2 修复选择“先卸载再安装”后出现 `Unable to stop the Mihomo desktop service` 的升级故障。旧卸载器可能删除主程序和内核、留下仍被占用的服务助手；新版安装器直接查询并停止 Windows 中登记的服务，不再执行这个残留旧助手。服务已不存在时继续安装，服务正在停止时等待完成，并等待旧助手文件解除占用后再替换。无法完成时会显示具体阶段与系统错误码。

正常卸载应用时，服务助手也会一起卸载：先退出主界面、停止并移除服务及本程序的防火墙规则，再等待助手退出并确认助手文件删除成功，随后删除其余程序文件。若助手仍被占用且等待超时，卸载会报告具体错误并中止，保留主程序与内核供重试；用户配置保留。

如果旧版升级已在这里中断，退出仍在运行的 Mihomo，直接重新运行 0.1.2 完整安装包即可修复程序文件；不需要删除用户配置目录。

新版保留 0.1.1 的 TUN 错误 5 修复：服务只向登记的用户授予有限进程查询权限，界面与服务的路径及用户身份验证仍然执行，解决普通权限界面开启 TUN 时无法验证 SYSTEM 服务进程的问题。

先从托盘退出旧程序，再运行新版完整安装包。覆盖安装会停止旧服务、替换程序并启动新服务；仅替换文件或重开界面不能保证旧服务已更新。若安装时选择先卸载，首次开启 TUN 需要重新批准一次服务安装。安装完成后普通双击启动程序即可。

### 后续更新

设置中的桌面更新统一更新界面、宿主及配套内核。更新包必须通过本仓库指定 GitHub Actions 工作流的 OIDC / Sigstore 构建签名验证；使用随包提供的官方 GitHub CLI 验证器离线验签，无需用户登录 GitHub。规则、订阅提供者和 Geo 数据仍通过内核的原有更新入口管理。

安装版可以在设置中安装更新。便携版的更新按钮打开对应发行页面：请下载完整安装包，或先退出程序再解压新版便携包，用户配置会保留。安装更新失败时会记录错误并尝试重新打开应用；若安装文件已部分写入，请使用完整安装包修复。

桌面版使用独立的 `mihomo-desktop-v*` 标签。它不会替代服务端内核发布使用的 GitHub latest 标记。首次发行没有可升级版本时，检查更新会显示当前版本。

`SHA256SUMS` 可用于核对下载完整性。`build-info.json` 记录桌面、UI、内核版本和确切来源提交；同一 Release 中的 `.sigstore.json` 是安装包的构建签名。构建签名与 Windows Authenticode 发布者证书是两种不同机制；本项目当前未配置商业发布者证书。

## 从源码构建

使用 Windows x64、Visual Studio C++ Build Tools、WebView2、Node.js 22、pnpm 8.15.9、Rust 1.99.1 和项目使用的 MetaCubeX Go 1.26 工具链。

1. 检出 `clash-dashboard` 分支，运行 `pnpm install --frozen-lockfile`。
2. 将 `desktop/core.json` 指定提交检出到 `core-src`。
3. 运行 `python scripts/prepare-desktop.py --core-dir core-src`。
4. 运行 `pnpm exec tauri build --ci --target x86_64-pc-windows-msvc --bundles nsis`。
5. 运行 `python scripts/package-desktop.py`，产物位于 `artifacts/desktop`。

开发界面使用 `pnpm desktop:dev`，开发地址固定为 `http://localhost:3000`。普通网页构建继续使用 `pnpm build`。

## 验证与源码

`.github/workflows/windows-desktop.yml` 包含 Windows 编译、真实内核 HTTP 代理回环测试，以及真实 0.1.1 卸载后重新安装的回归：主动让旧助手在卸载时残留，确认旧助手会报错，再验证新版安装器能完成升级并保留用户配置。还会在服务运行时覆盖安装，执行新版应用的完整卸载，确认助手与其余程序文件、服务、防火墙规则和卸载注册记录均已移除而配置保留，然后重新安装。随后检查 WebView2 界面、配置修复、系统代理恢复、服务和隔离 TUN 流量。界面使用同一用户的中等完整性令牌，管理员组设为仅拒绝、没有启用调试特权；服务测试同时核对有限查询可用、终止及读写内存等更高权限被拒绝。测试在一次性 GitHub Windows runner 中运行，未覆盖独立标准用户账户；不同物理网卡、睡眠恢复、企业网络策略和长期运行仍需要实际机器验证。

源码仓库：https://github.com/chummumm/mihomo-optimized

UI / 桌面源码位于 `clash-dashboard`，内核来源以随包 `build-info.json` 中的 `coreCommit` 为准。各组件许可证随安装包放在 `resources/licenses`。
