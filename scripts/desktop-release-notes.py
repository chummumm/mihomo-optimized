#!/usr/bin/env python3
'''Render the native desktop release notes from the attested build manifest.'''
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
info = json.loads((root / 'artifacts/desktop/build-info.json').read_text())
version = info['desktopVersion']
print(f'''## Windows x64 桌面版 v{version}

本版修复点击“重启内核”后，TUN 的预期 GUID 与实际 GUID 相同，却报 `native_identity_missing` 并导致启动失败的问题。配套内核升级为 **{info['coreVersion']}**。

### 本版更新

- **修复关闭与重新创建之间的误判**：旧内核退出后，Windows 的 PnP 设备记录可能先消失，IP Helper 接口表暂时保留旧行。旧检查可能在 Wintun 开始创建前就失败；本版让符合产品 GUID、名称、有效 LUID / 接口索引及接口类型的单条记录进入一次原生创建，由 Windows 协调同一软件设备的生命周期。
- **保持准确的设备边界**：成功创建后仍核验实际持有句柄的 GUID。外部同名网卡、重复身份、已读取到但属性不符的设备和原生查询错误继续拒绝。显式 `tun.device` 保留，不改用备用名、不按名称打开接管，也不增加失败后的重复启动。
- **加入完整重启验收**：连续 20 次执行真实桌面重启入口，首轮实际调用主页按钮；逐轮验证旧进程先退出、新 PID 与核心实例、同名同 GUID 的 TUN、健康状态和实际转发。失败立即阻止发布，并保留清理前的状态、两种原生接口视图和日志。

### 下载与升级

- 安装版：`mihomo-desktop-{version}-windows-x64-setup.exe`。TUN 用户使用安装版。
- 便携版：`mihomo-desktop-{version}-windows-x64-portable.zip`。解压完整目录后运行 `mihomo-optimized.exe`。
- 支持 Windows 10 1809 及更新版本的 x64 系统，包含完整 .NET 运行环境，无需单独安装 .NET 或 WebView2。
- **0.2.11 用户**可以独立更新内核至 1.0.36 或更新版本获得此项修复，也可以使用 0.2.12 完整安装包覆盖升级。本版继续使用宿主 / 服务协议 v5。
- **从更早桌面版本升级请使用完整安装包**，同步更新界面、宿主、服务和配套内核。先保存配置并从托盘退出旧程序，再覆盖安装；独立内核更新不会升级仍在运行的旧宿主或服务。
- 配置仍位于 `%LOCALAPPDATA%\\Mihomo Optimized`，覆盖安装和卸载均保留用户配置。便携用户先退出旧程序，再使用完整新版便携目录。
- 安装版若提示需要桌面服务，可在设置页“桌面服务”区域安装；安装服务本身不会开启 TUN。首次使用仍需导入自己的 YAML 或订阅。

### 更新设置与范围

在 YAML 顶层配置，保存并应用后生效：

```yaml
core-auto-update: true
core-update-interval: 24
```

自动更新仅在桌面程序运行期间调度。设置页“应用程序更新”继续更新桌面程序、宿主、服务及随包组件；内核独立更新只替换 `mihomo-core.exe`。规则、订阅提供者和 Geo 数据继续使用内核的资源更新能力。

独立更新限定为本仓库正式发布的 Windows amd64 内核，不支持自定义下载地址、其他核心、预发布版本、自动降级或替换桌面服务。便携版的 TUN 仍需安装版提供受保护路径和服务。

### 来源与验证

内核独立更新固定从本仓库的 HTTPS 发布入口读取版本，再锁定对应标签下载和核对 `SHA256SUMS`；候选必须报告一致版本并支持桌面管理接口，才进入替换阶段。应用安装包更新继续使用固定 GitHub Actions 工作流的 OIDC / Sigstore 构建证明验证。

本版新增 PnP 记录缺席而 IP Helper 保留旧快照时的原生创建回归，以及 20 轮完整桌面重启门禁。原有本产品设备的真实占用、立即关闭重建、异常退出后恢复、外部同名保护、历史失败状态、TUN 局部更新回读和回滚、真实安装升级、普通 / TUN / 停止状态下的独立内核更新、TUN 转发与清理继续执行。具体通过情况以本次提交的 Windows Actions 记录和随附报告为准；自动化测试不替代 S3 / S4 / Modern Standby 或特定无线与过滤驱动的实机验证。

- 桌面：{info['desktopVersion']}；网页面板：{info['uiVersion']}；随包内核：{info['coreVersion']}。
- [桌面和网页源码](https://github.com/{info['repository']}/tree/{info['uiCommit']})
- [随包内核源码](https://github.com/{info['repository']}/tree/{info['coreCommit']})
- [Windows 使用说明与历史版本记录](https://github.com/{info['repository']}/blob/{info['uiCommit']}/desktop/README.zh-CN.md)

正式发行附带 `SHA256SUMS`、`build-info.json` 与 `.sigstore.json`。构建证明与 Windows Authenticode 发布者签名是不同机制；本发行没有 Windows Authenticode 发布者证书签名。桌面版使用独立的 `mihomo-desktop-v*` 标签，不改变正式内核的 Latest 入口。
''')
