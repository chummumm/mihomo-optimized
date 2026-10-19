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

### 本次修复与升级

- 修复选择“先卸载再安装”后提示 `Unable to stop the Mihomo desktop service` 的问题。新安装器直接查询并停止固定的 Windows 服务，不再调用可能缺少配套文件的残留旧助手。
- 正确处理服务已不存在、正在停止、并发停止及等待删除的状态；等待旧助手文件解除占用后替换，并在失败时显示具体阶段和系统错误码。
- 从 0.1.0 / 0.1.1 升级时，先从托盘退出旧程序，再运行此完整安装包。即使旧版升级已在上述报错处中断，也可直接重试此安装包，无需删除用户配置。若安装时选择先卸载，首次开启 TUN 需要重新批准一次服务安装。
- 保留普通权限界面开启 TUN 的“系统错误：5”修复。服务仅向登记用户授予查询进程身份所需的有限权限，保留双向身份验证。
- 安装完成后普通双击启动即可；TUN 仍需安装版，便携包适合普通系统代理使用。

### 构建来源

- 桌面：{info['desktopVersion']}；UI：{info['uiVersion']}；内核：{info['coreVersion']}。
- [桌面与 UI 源码](https://github.com/{info['repository']}/tree/{info['uiCommit']})
- [内核对应源码](https://github.com/{info['repository']}/tree/{info['coreCommit']})
- 配置目录：`%LOCALAPPDATA%\\Mihomo Optimized`。关闭窗口收起到托盘，托盘退出会恢复代理并停止内核。

此发行已通过 Windows runner 中的编译、服务状态回归、内核通信及真实 0.1.1 卸载后重新安装测试。升级回归主动保留旧助手，确认旧助手因缺少配套文件失败，再验证新版成功安装且用户配置保留；随后通过 WebView2、系统代理恢复和隔离 TUN 流量测试。界面以中等完整性、管理员组未启用的令牌运行，并检查服务身份可查询而终止、读写内存、复制句柄及修改权限仍被拒绝。完整测试记录可在此提交的 Windows desktop Actions 运行中查看。

安装包使用 GitHub Actions 构建证明供应用内更新验签；未配置 Windows Authenticode 发布者证书。缺少 WebView2 时安装程序需要联网安装运行环境。不同物理网卡、睡眠恢复及企业网络环境需进一步实机使用验证。

本项目已准备 SignPath Foundation 免费开源代码签名申请材料，当前仍处于申请准备阶段，尚未提交或获批，本发行未使用 SignPath 证书签名。[代码签名政策草稿与隐私说明](https://github.com/chummumm/mihomo-optimized/blob/codex/signpath-oss-prep/docs/desktop-signing/code-signing-policy.draft.md)记录拟议的签名范围、审批流程和第三方来源。GitHub 构建证明与 Windows 发布者签名用途不同，当前下载仍可能出现 SmartScreen 信誉提示。

请阅读附件 `README.zh-CN.md`；`SHA256SUMS`、`build-info.json` 和 `.sigstore.json` 分别提供完整性校验、来源记录和构建证明。
''')
