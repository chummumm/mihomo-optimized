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

### 本版界面与动效校准

- 按最终 Fluent 原稿恢复页面留白、卡片尺寸、字体与行高、半透明背景和边框，校准主页、代理、连接、DNS、规则、日志、配置与设置的实际布局。
- 搜索改为标题栏内真实输入和独立结果浮层；设置采用原稿分段选择与开关，连接详情在表格内展开，配置编辑器采用原稿子页面。
- 配置导入恢复文件、粘贴、URL 三模式预览和导入后重载；未保存提示、首次开启 TUN 的说明同步使用原稿布局。
- 搜索、导航等局部浮层补齐背景模糊、饱和度、半透明填充和阴影；短通知、内核恢复页与可关闭的更新提示按原稿显示。
- 页面、导航指示条、控件状态、选中框、速率和折线采用原稿的时序与缓动，并遵从系统减少动画设置。
- 验收固定原稿源版本，在同一 Windows、窗口尺寸、DPI、语言和安全测试数据下渲染对照画面；原生动画由独立 UI Automation 操作触发，后台采集实际桌面连续帧和时间，避免录制阻塞界面。

### 原生 Fluent 功能

- 自定义标题栏、返回与三种导航布局；Windows 11 支持时使用 Mica / Acrylic，遵守系统高对比度、透明效果和动画设置。
- 主页 4 个、托盘 3 个当前出口摘要，包含实际节点与延迟，点击定位策略组；实时流量和常用开关均接入真实内核状态。
- Ctrl+K 搜索命令、页面、策略组、节点、设置和规则；命中内容高亮，设置项和代理目标准确定位。中文 / English 即时切换，主题跟随系统或手动选择。
- 原生实时流量拓扑、节点延迟历史、排序、提供者更新与健康检查；Ctrl+T 批量测速和进度反馈，可在切换节点时断开关联连接。
- 连接列表提供来源筛选、双向速率、60 秒吞吐历史、分组统计、实时详情和关闭操作；DNS 提供趋势、查询分页跳转和实际出口明细，规则和日志保留筛选、更新、暂停与清空操作。
- 配置文件、URL、粘贴和拖放导入；原生 YAML 编辑器具备虚拟行号、缩进、行列位置、Ctrl+S、查找、换行格式保留、修订冲突和未保存保护；保存成功但重载失败可单独重试。
- 模式、局域网和代理端口通过桌面后台保存、同步系统代理并回读确认；测速地址和界面偏好持久保存，资源更新呈现真实任务及调度状态。
- 关闭主窗口收起到托盘；退出先停止自有内核并恢复本程序设置的系统代理。UI 与后台使用进程绑定的本地通道，没有新增远程管理端口。

### 保留的运行与更新能力

- 内核继续使用本项目 {info['coreVersion']}，保留 `resource-bootstrap` 代理组引导下载、缓存和原有资源期限。
- 冷启动资源准备期间，状态读取仍可及时响应；只有内核身份、认证、实际监听和就绪全部确认后，才按保存的偏好应用系统代理。
- 保留服务身份检查、普通权限界面控制受限 TUN 服务、代理异常恢复及启动失败清理。
- 应用更新继续校验固定 GitHub Actions 工作流的构建证明，更新监护等待原生 UI 与后台退出后重启主程序；原有安装包和缓存清理继续保留。
- 安装器保留对旧版卸载后残留助手的处理，直接查询 SCM 并等待固定服务和助手退出。正常卸载先移除服务、防火墙规则和助手，再按精确清单删除程序文件。
- NSIS 安装包与便携包使用同一份原生运行目录；`build-info.json` 记录每个运行文件的 SHA-256，包括 .NET 程序集与后台程序。

### 验证与来源

Windows 工作流继续要求真实内核与资源引导测试、原生 Rust 回归、0.1.1 卸载后重装、运行中服务覆盖升级、完整卸载和配置保留。安装后的验收改用真实 WPF UI 与 Windows UI Automation，保留普通权限令牌、冷启动、配置修复、系统代理、TUN 流量、服务身份权限和崩溃清理检查。同时通过真实 UI Automation 操作验证语言/主题切换、全局搜索定位、图形拓扑、编辑器和窄窗导航，并保留实际运行界面截图、原稿对照画面与动画连续帧。具体结果以本次提交的 Actions 记录为准。

- 桌面：{info['desktopVersion']}；网页面板：{info['uiVersion']}；内核：{info['coreVersion']}。
- [桌面和网页源码](https://github.com/{info['repository']}/tree/{info['uiCommit']})
- [固定内核源码](https://github.com/{info['repository']}/tree/{info['coreCommit']})
- [资源引导配置](https://github.com/{info['repository']}/blob/{info['coreCommit']}/docs/resource-bootstrap.md)

正式发行附带 `SHA256SUMS`、`build-info.json` 与 `.sigstore.json`。GitHub 构建证明用于验证来源；本发行没有 Windows Authenticode 发布者证书签名。桌面版使用独立的 `mihomo-desktop-v*` 标签，不改变服务端内核的 Latest 入口。
''')
