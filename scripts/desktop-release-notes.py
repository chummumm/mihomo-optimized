#!/usr/bin/env python3
"""Render the native desktop release notes from the attested build manifest."""
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
info = json.loads((root / 'artifacts/desktop/build-info.json').read_text())
version = info['desktopVersion']
print(f'''## Windows x64 桌面版 v{version}

Windows 界面改为 C# WPF 原生控件与 Fluent 主题。安装版和便携版包含完整 .NET 运行环境，不再使用 Tauri 或 WebView2。内核、系统代理、TUN 服务及更新继续由现有 Rust 后台管理；网页面板保持 {info['uiVersion']}。

### 下载与升级

- 安装版：`mihomo-desktop-{version}-windows-x64-setup.exe`。TUN 用户使用安装版。
- 便携版：`mihomo-desktop-{version}-windows-x64-portable.zip`。解压完整目录后运行 `mihomo-optimized.exe`。
- 支持 Windows 10 1809 及更新版本的 x64 系统，无需单独安装 .NET 或 WebView2。
- 从旧版升级时，先保存配置并从托盘退出旧程序，再运行完整安装包。配置仍位于 `%LOCALAPPDATA%\\Mihomo Optimized`，覆盖安装和卸载均保留用户配置。
- 首次使用在配置页导入自己的 YAML 或订阅，然后启动内核并按需开启系统代理 / TUN。

### 原生界面

- 主页集中显示内核状态、运行模式、常用开关和实时流量；代理、连接、DNS、规则、日志、配置与设置使用原生控件。
- 配置支持文件、URL、粘贴导入和 YAML 编辑，保留修订冲突检查及未保存提示；内核无法启动时仍可编辑配置。
- 原生窗口保留系统标题栏、缩放与贴靠行为，支持跟随系统、浅色和深色主题。
- 托盘提供快速操作，关闭主窗口收起到托盘；退出先停止自有内核并恢复本程序设置的系统代理。
- UI 与后台之间使用进程绑定的本地通道。后台仅接受同目录、同用户且确实为其父进程的配套 UI；没有新增远程管理端口。

### 保留的运行与更新能力

- 内核继续使用本项目 {info['coreVersion']}，保留 `resource-bootstrap` 代理组引导下载、缓存和原有资源期限。
- 冷启动资源准备期间，状态读取仍可及时响应；只有内核身份、认证、实际监听和就绪全部确认后，才按保存的偏好应用系统代理。
- 保留服务身份检查、普通权限界面控制受限 TUN 服务、代理异常恢复及启动失败清理。
- 应用更新继续校验固定 GitHub Actions 工作流的构建证明，更新监护等待原生 UI 与后台退出后重启主程序；原有安装包和缓存清理继续保留。
- 安装器保留对旧版卸载后残留助手的处理，直接查询 SCM 并等待固定服务和助手退出。正常卸载先移除服务、防火墙规则和助手，再按精确清单删除程序文件。
- NSIS 安装包与便携包使用同一份原生运行目录；`build-info.json` 记录每个运行文件的 SHA-256，包括 .NET 程序集与后台程序。

### 验证与来源

Windows 工作流继续要求真实内核与资源引导测试、原生 Rust 回归、0.1.1 卸载后重装、运行中服务覆盖升级、完整卸载和配置保留。安装后的验收改用真实 WPF UI 与 Windows UI Automation，保留普通权限令牌、冷启动、配置修复、系统代理、TUN 流量、服务身份权限和崩溃清理检查。具体结果以本次提交的 Actions 记录为准。

- 桌面：{info['desktopVersion']}；网页面板：{info['uiVersion']}；内核：{info['coreVersion']}。
- [桌面和网页源码](https://github.com/{info['repository']}/tree/{info['uiCommit']})
- [固定内核源码](https://github.com/{info['repository']}/tree/{info['coreCommit']})
- [资源引导配置](https://github.com/{info['repository']}/blob/{info['coreCommit']}/docs/resource-bootstrap.md)

正式发行附带 `SHA256SUMS`、`build-info.json` 与 `.sigstore.json`。GitHub 构建证明用于验证来源；本发行没有 Windows Authenticode 发布者证书签名。桌面版使用独立的 `mihomo-desktop-v*` 标签，不改变服务端内核的 Latest 入口。
''')
