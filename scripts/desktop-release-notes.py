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

### 构建来源

- 桌面：{info['desktopVersion']}；UI：{info['uiVersion']}；内核：{info['coreVersion']}。
- [桌面与 UI 源码](https://github.com/{info['repository']}/tree/{info['uiCommit']})
- [内核对应源码](https://github.com/{info['repository']}/tree/{info['coreCommit']})
- 配置目录：`%LOCALAPPDATA%\\Mihomo Optimized`。关闭窗口收起到托盘，托盘退出会恢复代理并停止内核。

此发行已通过 Windows runner 中的编译、内核通信、安装后 WebView2、系统代理恢复和隔离 TUN 流量测试。完整测试记录可在此提交的 Windows desktop Actions 运行中查看。

安装包使用 GitHub Actions 构建证明供应用内更新验签；未配置 Windows Authenticode 发布者证书。缺少 WebView2 时安装程序需要联网安装运行环境。不同物理网卡、睡眠恢复及企业网络环境需进一步实机使用验证。

请阅读附件 `README.zh-CN.md`；`SHA256SUMS`、`build-info.json` 和 `.sigstore.json` 分别提供完整性校验、来源记录和构建证明。
''')
