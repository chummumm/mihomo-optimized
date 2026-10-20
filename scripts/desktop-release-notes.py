#!/usr/bin/env python3
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
info = json.loads((root / 'artifacts/desktop/build-info.json').read_text())
version = info['desktopVersion']
print(f'''## Windows x64 桌面版 v{version}

主界面移植本项目现有 UI，内置本项目二开 Mihomo 内核，增加托盘、系统代理、登录启动、配置导入与故障修复，以及受限 Windows TUN 服务。

### 下载

- 安装版：`mihomo-desktop-{version}-windows-x64-setup.exe`，TUN 用户请选择此文件。
- 免安装程序包：`mihomo-desktop-{version}-windows-x64-portable.zip`，解压整个目录后运行 `mihomo-optimized.exe`。
- 首次使用在设置中导入自己的 YAML 配置或订阅，然后启动内核并按需开启系统代理 / TUN。

### 本次更新

- 内置新内核 {info['coreVersion']}。`resource-bootstrap.proxy` 可以直接引用已有 `select`、`fallback`、`url-test`、`load-balance`、内置 `GLOBAL` 和嵌套组，沿用原策略、过滤、本地订阅内容及可读取的选择缓存，不新增节点保存方式。
- 手动组保持选择，自动组在当前资源 GET 中按自身策略重试失败路径；一次性探测、连接、TLS、HTTP 状态和读响应体共同遵守原有预算。初始化 provider 显式指定的 `proxy` 也统一从独立引导图解析，不借用旧内核或未就绪的常规组；空组或候选耗尽不静默直连。
- 修复服务状态查询短暂失败后，重新确认内核运行却遗漏恢复系统代理偏好的问题。内核启动与资源准备不要求预先打开 Windows 系统代理；保留用户偏好，在认证就绪后才接管，准备或失败期间保持可恢复状态。
- 资源默认共用 180 秒总预算；普通 provider 请求 60 秒、Geo 请求 120 秒、连接 10 秒均受剩余时间限制。Windows 在配置检查和正式启动之间传递剩余预算，默认操作 210 秒、服务通信 240 秒；重载恢复和清理也使用同一个操作期限。无法确认恢复时清理自有内核，若仍未确认退出会明确报告并保留进程身份和配置修复入口。
- 顶栏保留唯一启动 / 停止入口，设置内保留重启与刷新状态，编辑与重新加载合并在同一配置区域。桌面应用更新与内核资源更新使用明确且独立的标题、按钮与提示；内核离线时仍可编辑、导入配置和检查应用更新。
- 每次启动桌面程序 5 秒后安排一次后台应用更新检查。内核启停、配置写入等操作繁忙时保持待检查，操作结束后再执行，不再因繁忙超过 30 秒而跳过。手动检查消费本轮自动检查，并复用正在进行的请求；切页、重显窗口不会重查，退出取消待检查。
- 发现新版本后显示可关闭提示；检查失败不影响代理。只有你点击安装更新后，安装版才下载、验证并启动安装程序；便携版仍打开对应发行页面。0.1.2 及更早版本需先手动检查或使用完整安装包升级。

在已有 YAML 顶层添加 `resource-bootstrap: {{proxy: "Proxy", timeout: 180}}` 即可选择原有代理组；也可填写具体节点名。直接指定订阅节点时再加 `proxy-provider`，组的订阅成员继续使用原组的 `use` 等字段。首次没有任何本地节点或订阅内容时仍需准备节点信息。完整示例与限制见[启动资源下载说明](https://github.com/{info['repository']}/blob/{info['coreCommit']}/docs/resource-bootstrap.md)。

### 升级、卸载及既有修复

- 保留 0.1.2 对“先卸载再安装”后提示 `Unable to stop the Mihomo desktop service` 的修复。新安装器直接查询并停止固定的 Windows 服务，不再调用可能缺少配套文件的残留旧助手。
- 正确处理服务已不存在、正在停止、并发停止及等待删除的状态；等待旧助手文件解除占用后替换，并在失败时显示具体阶段和系统错误码。
- 正常卸载应用时一并移除服务和助手：先退出主界面并移除服务，再等待助手文件删除成功，随后清理其余程序文件；用户配置保留。助手删除失败时会明确报错并中止，不再留下助手却显示卸载成功。
- 从旧版升级时，先从托盘退出旧程序，再运行此完整安装包。即使旧版升级已在上述报错处中断，也可直接重试此安装包，无需删除用户配置。若安装时选择先卸载，首次开启 TUN 需要重新批准一次服务安装。
- 保留普通权限界面开启 TUN 的“系统错误：5”修复。服务仅向登记用户授予查询进程身份所需的有限权限，保留双向身份验证。
- 安装完成后普通双击启动即可；TUN 仍需安装版，便携包适合普通系统代理使用。

### 构建来源

- 桌面：{info['desktopVersion']}；UI：{info['uiVersion']}；内核：{info['coreVersion']}。
- [桌面与 UI 源码](https://github.com/{info['repository']}/tree/{info['uiCommit']})
- [内核对应源码](https://github.com/{info['repository']}/tree/{info['coreCommit']})
- 配置目录：`%LOCALAPPDATA%\\Mihomo Optimized`。关闭窗口收起到托盘，托盘退出会恢复代理并停止内核。

此发行已通过 Windows runner 中的编译、服务状态回归、内核通信及真实 0.1.1 卸载后重新安装测试。升级回归主动保留旧助手，确认旧助手因缺少配套文件失败，再验证新版成功安装且用户配置保留；还验证服务运行时覆盖安装、新版完整卸载后助手及所有程序文件消失、服务和防火墙规则清理，以及保留配置后的重装。随后通过 WebView2、系统代理恢复和隔离 TUN 流量测试；还明确验证系统代理关闭时的冷资源启动、认证就绪后才应用已保存代理偏好，以及服务身份查询短暂失败后的同进程恢复。界面以中等完整性、管理员组未启用的令牌运行，并检查服务身份可查询而终止、读写内存、复制句柄及修改权限仍被拒绝。完整测试记录可在此提交的 Windows desktop Actions 运行中查看。

安装包及便携包提供 GitHub Actions 构建证明，应用内更新使用它验证安装包；本发行未使用 Windows Authenticode 发布者证书签名。缺少 WebView2 时安装程序需要联网安装运行环境。不同物理网卡、睡眠恢复及企业网络环境需进一步实机使用验证。

本项目已准备 SignPath Foundation 免费开源代码签名申请材料，当前仍处于申请准备阶段，尚未提交或获批，本发行未使用 SignPath 证书签名。[代码签名政策草稿与隐私说明](https://github.com/chummumm/mihomo-optimized/blob/codex/signpath-oss-prep/docs/desktop-signing/code-signing-policy.draft.md)记录拟议的签名范围、审批流程和第三方来源。GitHub 构建证明与 Windows 发布者签名用途不同，当前下载仍可能出现 SmartScreen 信誉提示。

请阅读附件 `README.zh-CN.md`；`SHA256SUMS`、`build-info.json` 和 `.sigstore.json` 分别提供完整性校验、来源记录和构建证明。
''')
