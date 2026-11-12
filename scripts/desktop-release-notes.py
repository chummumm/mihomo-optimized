#!/usr/bin/env python3
'''Render the native desktop release notes from the attested build manifest.'''
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
info = json.loads((root / 'artifacts/desktop/build-info.json').read_text())
version = info['desktopVersion']
print(f'''## Windows x64 桌面版 v{version}

本版优化启动、模式与 TUN 切换，并修复后台恢复健康后设置页仍保留旧红字的问题。配套内核升级为 **{info['coreVersion']}**，继续支持与 Linux 等平台相同来源的独立内核自动更新。

### 本版更新

- **旧状态提示自动清除**：设置页随新的健康状态清理旧运行错误；用户命令的失败结果独立保留。首次 TUN 采样显示检查中，真实过期或设备异常仍显示警告。
- **轻量设置直接更新**：规则 / 全局 / 直连、局域网开关、日志等级和代理端口变化使用内核支持的运行配置接口，不再重复准备整份资源。更新后回读确认并保存，端口实际监听成功后才切换系统代理；失败时恢复先前运行状态，无法确认恢复则停止自有内核并报告错误。
- **TUN 开关保留内核进程**：安装版在配套服务已经可用时，由同一服务管理普通模式和 TUN，开关 TUN 在原进程内应用。首次从普通进程进入服务仍需一次迁移，便携版普通模式继续由用户进程管理。设备身份检查与停止优先级继续生效。
- **减少启动重复工作**：实际内核负责启动时的配置解析、资源准备与就绪检查，去掉启动前额外的 `-t` 进程；运行中的整份重载仍执行预校验。界面去掉重复主题应用并合并重复配置读取，发行版预编译界面程序集，减少首次执行时的编译工作。
- **分阶段耗时可追查**：生命周期日志细分界面初始化、宿主进程启动、内核配置准备、进程创建和就绪等待；日志目录入口按实际的内核管理者选择。首次下载规则或 Geo 数据仍受资源与网络速度影响。
- **配套内核 1.0.33**：包含 1.0.32 的健康采样修复，提前启动后台采样；相同 TUN 配置的重载保留真实观察与原始时间戳，实际设备变更仍使旧快照失效。1.0.33 另修复 Windows SOCKS UDP 测试夹具的端口分配，保留原有全部断言。基于已同步的上游 v1.19.34 与 sing-tun v0.4.29。

### 下载与升级

- 安装版：`mihomo-desktop-{version}-windows-x64-setup.exe`。TUN 用户使用安装版。
- 便携版：`mihomo-desktop-{version}-windows-x64-portable.zip`。解压完整目录后运行 `mihomo-optimized.exe`。
- 支持 Windows 10 1809 及更新版本的 x64 系统，包含完整 .NET 运行环境，无需单独安装 .NET 或 WebView2。
- **从旧版升级请使用完整安装包**：本版宿主与服务使用通信协议 v4，需要一起升级界面、后台、服务和内核。先保存配置并从托盘退出旧程序，再覆盖安装；独立内核更新无法修复旧界面提示或升级仍在运行的旧服务。
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

本版的验证范围包括状态恢复后的提示清理、配置更新回读与回滚、启动与停止竞态、服务协议及运行模式、发布程序集预编译，以及 Windows 上的真实安装升级、普通 / TUN / 停止状态下的独立内核更新、设置连续切换、TUN 转发与清理。耗时记录来自具体的测试环境，不等同于每台电脑的启动承诺；具体通过情况以本次提交的 Windows Actions 记录和随附报告为准。

- 桌面：{info['desktopVersion']}；网页面板：{info['uiVersion']}；随包内核：{info['coreVersion']}。
- [桌面和网页源码](https://github.com/{info['repository']}/tree/{info['uiCommit']})
- [随包内核源码](https://github.com/{info['repository']}/tree/{info['coreCommit']})
- [Windows 使用说明与历史版本记录](https://github.com/{info['repository']}/blob/{info['uiCommit']}/desktop/README.zh-CN.md)

正式发行附带 `SHA256SUMS`、`build-info.json` 与 `.sigstore.json`。构建证明与 Windows Authenticode 发布者签名是不同机制；本发行没有 Windows Authenticode 发布者证书签名。桌面版使用独立的 `mihomo-desktop-v*` 标签，不改变正式内核的 Latest 入口。
''')
