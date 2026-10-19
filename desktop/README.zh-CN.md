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

TUN 使用你导入配置中的 DNS 策略，要求 `dns.enable: true`。配置里的 `tun.enable` 由桌面开关控制。Linux 的透明代理、iptables 和 TCP SYN 主动探测参数不适用于 Windows；发现不兼容项时，先在配置编辑器中修复。

## 配置与故障恢复

内核启动失败时，主界面仍可以打开配置编辑器和日志目录。保存配置使用内容修订号检查，避免覆盖另一个编辑器刚保存的文件。保存和重载是两步：保存失败不会覆盖旧文件；内核不接受重载时会保留先前运行配置。

控制端口和认证密钥由桌面程序临时生成，不从网页 URL 或浏览器存储读取。控制接口只监听本机回环地址。导入的配置不能改变此控制通道，也不能启用内核自己的二进制或 UI 更新。

## 更新

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

`.github/workflows/windows-desktop.yml` 包含 Windows 编译、真实内核 HTTP 代理回环测试，以及安装后的 WebView2 界面、配置修复、系统代理恢复、服务和隔离 TUN 流量检查。测试在一次性 GitHub Windows runner 中运行；不同物理网卡、睡眠恢复、企业网络策略和长期运行仍需要实际机器验证。

源码仓库：https://github.com/chummumm/mihomo-optimized

UI / 桌面源码位于 `clash-dashboard`，内核来源以随包 `build-info.json` 中的 `coreCommit` 为准。各组件许可证随安装包放在 `resources/licenses`。
