#!/usr/bin/env python3
'''Render the native desktop release notes from the attested build manifest.'''
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
info = json.loads((root / 'artifacts/desktop/build-info.json').read_text())
version = info['desktopVersion']
print(f'''随包内核 **{info['coreVersion']}** · 网页面板 **{info['uiVersion']}** · Windows x64

## 本次更新

- 客户端交给内置 DNS 的解析请求不再显示为普通连接；查询来源、域名和结果继续保留在 DNS 查询中，真实 DNS 上游连接继续显示。
- 连接详情路径末端的“域名”标签改为“地址”。列表中的 DNS 类型与 TPROXY 等类型统一文字样式，移除独立边框。
- DNS 概览增加“清理缓存”按钮，采用扫帚图标、悬停与按压反馈，以及一次短扫动。按钮文案固定，成功后不弹提示。
- 修正覆盖安装后遗留旧版 .NET 诊断组件的问题，仅清理已确认的旧组件文件，保留用户配置。

清理覆盖主解析、代理、直连、默认上游与 bootstrap 的回答缓存；FakeIP 映射和 DNS 历史统计保留。

## 下载与升级

- [安装版](https://github.com/{info['repository']}/releases/download/mihomo-desktop-v{version}/mihomo-desktop-{version}-windows-x64-setup.exe)：TUN 用户请选择安装版。
- [便携版](https://github.com/{info['repository']}/releases/download/mihomo-desktop-v{version}/mihomo-desktop-{version}-windows-x64-portable.zip)：退出旧程序后，使用完整新版目录。

从托盘退出旧程序后覆盖安装，用户配置保留。需要完整桌面更新才能获得本版界面改动；独立内核更新只更新内核。宿主 / 服务协议继续为 v5。

[安装、配置与验证指南](https://github.com/{info['repository']}/blob/{info['uiCommit']}/desktop/README.zh-CN.md) · [桌面与网页源码](https://github.com/{info['repository']}/tree/{info['uiCommit']}) · [内核源码](https://github.com/{info['repository']}/tree/{info['coreCommit']})
''')
