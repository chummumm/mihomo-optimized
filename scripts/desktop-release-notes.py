#!/usr/bin/env python3
'''Render the native desktop release notes from the attested build manifest.'''
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
info = json.loads((root / 'artifacts/desktop/build-info.json').read_text())
version = info['desktopVersion']
print(f'''## Windows x64 桌面版 v{version}

本版修复 Windows TUN 的原生设备恢复与历史失败状态，并为单独开关 TUN 增加局部更新路径。配套内核升级为 **{info['coreVersion']}**，继续支持与 Linux 等平台相同来源的独立内核自动更新。

### 本版更新

- **自有 TUN 由原生生命周期恢复**：配套内核按实际 GUID 与产品属性确认设备身份；不再要求自有设备先变成 phantom 才允许重新创建。关闭后的异步移除由 Windows 协调，真实占用和查询失败保留具体原因。外部同名设备仍受保护，不按名称打开接管。
- **取消固定备用名重启**：停止在笼统冲突后自动改用 MihomoDesktop 并重启。显式 tun.device 保持用户意图，历史备用名映射在安全的生命周期边界退役。
- **上次失败与当前健康分开**：TUN 开启失败但已恢复普通代理时，界面显示当前 TUN 未启用及带时间的上次失败，避免旧冲突一直覆盖正常运行状态。普通代理健康不代表 TUN 已修复，成功重试或明确关闭才清理相应操作失败。
- **TUN 专用快速切换**：其他配置未改变时，向同一个内核局部应用完整有效的 TUN 参数并回读确认，省去额外配置校验进程和整份资源重载。统一运行配置与内核默认参数，避免首次开启误报失败或多次开关后同源重载又重建网卡；显式设备名、路由和非默认参数仍严格核对。失败回滚、取消与退出确认屏障保留；首次转入服务仍需要一次迁移。
- **创建失败可直接定位阶段**：错误携带本次设备身份、判定分支与原生错误码，客户端关联操作时间和实际运行状态。修正 Wintun 清理函数的原生参数及 Close API 返回值处理。
- **控制端口原子绑定**：内核直接申请并持有实际控制端口，客户端核对本次启动标识、进程与核心实例后再完成认证。启动、自动恢复和内核替换不再探测端口后释放重绑；绑定或交接记录发布失败会及时清理已创建资源。
- **保留已有快速路径**：模式、日志等级、端口等轻量设置、普通网络变化以及等价 TUN 配置的重载继续复用现有资源。规则、DNS、代理协议和 LAN 预连接能力保持。

### 下载与升级

- 安装版：`mihomo-desktop-{version}-windows-x64-setup.exe`。TUN 用户使用安装版。
- 便携版：`mihomo-desktop-{version}-windows-x64-portable.zip`。解压完整目录后运行 `mihomo-optimized.exe`。
- 支持 Windows 10 1809 及更新版本的 x64 系统，包含完整 .NET 运行环境，无需单独安装 .NET 或 WebView2。
- **从旧版升级请使用完整安装包**：本版宿主与服务使用通信协议 v5，明确区分 TUN 请求和普通配置重载，需要一起升级界面、后台、服务和支持启动交接的 1.0.35 及更新内核。先保存配置并从托盘退出旧程序，再覆盖安装；独立内核更新无法修复旧界面提示或升级仍在运行的旧服务。
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

本版的验证范围包括本产品设备的真实占用、立即关闭重建、异常退出后恢复、外部同名保护、历史失败状态、TUN 局部更新回读和回滚，以及 Windows 上的真实安装升级、普通 / TUN / 停止状态下的独立内核更新、连续切换、TUN 转发与清理。耗时记录来自具体的测试环境，不等同于每台电脑的启动承诺；具体通过情况以本次提交的 Windows Actions 记录和随附报告为准。自动化测试不替代 S3 / S4 / Modern Standby 或特定无线与过滤驱动的实机验证。

- 桌面：{info['desktopVersion']}；网页面板：{info['uiVersion']}；随包内核：{info['coreVersion']}。
- [桌面和网页源码](https://github.com/{info['repository']}/tree/{info['uiCommit']})
- [随包内核源码](https://github.com/{info['repository']}/tree/{info['coreCommit']})
- [Windows 使用说明与历史版本记录](https://github.com/{info['repository']}/blob/{info['uiCommit']}/desktop/README.zh-CN.md)

正式发行附带 `SHA256SUMS`、`build-info.json` 与 `.sigstore.json`。构建证明与 Windows Authenticode 发布者签名是不同机制；本发行没有 Windows Authenticode 发布者证书签名。桌面版使用独立的 `mihomo-desktop-v*` 标签，不改变正式内核的 Latest 入口。
''')
