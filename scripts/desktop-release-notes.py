#!/usr/bin/env python3
'''Render the native desktop release notes from the attested build manifest.'''
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
info = json.loads((root / 'artifacts/desktop/build-info.json').read_text())
version = info['desktopVersion']
print(f'''## Windows x64 桌面版 v{version}

本版加入 **Windows 内核独立更新**，配套内核升级为 **{info['coreVersion']}**。Windows 现在沿用 Linux 等平台的 `core-auto-update` 与 `core-update-interval` 设置，从本仓库相同的正式内核发布入口取得新版，无需等待下一次桌面应用发版。

### 本版更新

- **沿用现有 YAML 设置**：`core-auto-update: true` 开启自动检查并安装新版，`core-update-interval` 按小时设置间隔，默认 24 小时；未启用时仍可手动检查。调度由桌面后台负责，切换页面或收起到托盘不会中止已接受的任务。
- **内核与资源状态分别显示**：配置页“内核、规则与 Geo 数据”区域显示当前内核版本、检查结果和自动更新状态。“检查并更新”同时处理内核与现有资源任务；内核停止时也能独立更新内核，更新后保留停止状态。
- **准备完成后再切换**：下载、SHA-256 校验、版本与桌面控制接口检查期间保留当前内核。需要替换时才短暂重启；启动检查失败会尝试恢复旧内核，停止指令优先于更新恢复。切换期间已有连接可能需要重新建立。
- **安装版与便携版均支持**：安装版由配套 Windows 服务写入受保护目录，即使当前没有开启 TUN，独立更新也需要安装并运行新版服务；便携版在自己的可写目录中完成替换。更新保留原有 TUN 开关和配置。
- **配套内核 1.0.29**：首次安装即可使用该版本；后续独立更新按正式版本号比较，不因打包版本带有 `-desktop` 标记而重复更新或降级。

### 下载与升级

- 安装版：`mihomo-desktop-{version}-windows-x64-setup.exe`。TUN 用户使用安装版。
- 便携版：`mihomo-desktop-{version}-windows-x64-portable.zip`。解压完整目录后运行 `mihomo-optimized.exe`。
- 支持 Windows 10 1809 及更新版本的 x64 系统，包含完整 .NET 运行环境，无需单独安装 .NET 或 WebView2。
- **从旧版升级请使用完整安装包**：本版宿主与服务使用通信协议 v3，需要一起升级界面、后台、服务和内核。先保存配置并从托盘退出旧程序，再覆盖安装；仅替换单个 EXE 或重开界面不能更新仍在运行的旧服务。
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

本版的验证范围包括版本比较、压缩包与校验和拒绝、下载取消和清理、文件替换与回滚，以及 Windows 上的真实候选和服务更新流程。具体通过情况以本次提交的 Windows Actions 记录和随附报告为准。

- 桌面：{info['desktopVersion']}；网页面板：{info['uiVersion']}；随包内核：{info['coreVersion']}。
- [桌面和网页源码](https://github.com/{info['repository']}/tree/{info['uiCommit']})
- [随包内核源码](https://github.com/{info['repository']}/tree/{info['coreCommit']})
- [Windows 使用说明与历史版本记录](https://github.com/{info['repository']}/blob/{info['uiCommit']}/desktop/README.zh-CN.md)

正式发行附带 `SHA256SUMS`、`build-info.json` 与 `.sigstore.json`。构建证明与 Windows Authenticode 发布者签名是不同机制；本发行没有 Windows Authenticode 发布者证书签名。桌面版使用独立的 `mihomo-desktop-v*` 标签，不改变正式内核的 Latest 入口。
''')
