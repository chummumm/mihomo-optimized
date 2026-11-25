#!/usr/bin/env python3
'''Render the native desktop release notes from the attested build manifest.'''
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
info = json.loads((root / 'artifacts/desktop/build-info.json').read_text())
version = info['desktopVersion']
print(f'''随包内核 **{info['coreVersion']}** · 网页面板 **{info['uiVersion']}** · Windows x64

## 本次更新

- 修复安装到其他本地硬盘或自定义目录后，TUN 服务无法启动、卸载中止的问题。
- 安装器统一登记实际路径并保护新建程序目录，桌面更新和内核更新沿用同一安装身份。
- 卸载使用安装包自带的维护程序；未安装过服务、配套程序缺失时也能清理已确认的本产品残留。
- 增加中文、空格路径及独立 NTFS 磁盘上的安装、TUN、修复和卸载验收。

## 下载与升级

- [安装版](https://github.com/{info['repository']}/releases/download/mihomo-desktop-v{version}/mihomo-desktop-{version}-windows-x64-setup.exe)：TUN 用户请选择安装版。
- [便携版](https://github.com/{info['repository']}/releases/download/mihomo-desktop-v{version}/mihomo-desktop-{version}-windows-x64-portable.zip)：退出旧程序后，使用完整新版目录。

从托盘退出旧程序后，在原安装位置运行完整安装包覆盖修复，配置、订阅与用户偏好保留。此修复需要完整桌面安装包，独立更新内核无法修复旧安装器和服务助手。宿主 / 服务协议继续为 v5。

自定义目录须位于支持权限保护的本地固定磁盘。上级目录权限不安全，或已有程序目录允许普通用户修改时，会在写入前说明原因并拒绝覆盖。

原位置无法覆盖修复时，退出托盘程序，运行新版安装包并附加 `/REMOVE` 参数清理旧安装，再重新安装；配置与未知文件保留，不需要先运行失效的旧卸载器。

[安装、配置与验证指南](https://github.com/{info['repository']}/blob/{info['uiCommit']}/desktop/README.zh-CN.md) · [桌面与网页源码](https://github.com/{info['repository']}/tree/{info['uiCommit']}) · [内核源码](https://github.com/{info['repository']}/tree/{info['coreCommit']})
''')
