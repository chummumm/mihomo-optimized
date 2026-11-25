#!/usr/bin/env python3
'''Render the native desktop release notes from the attested build manifest.'''
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
info = json.loads((root / 'artifacts/desktop/build-info.json').read_text())
version = info['desktopVersion']
print(f'''随包内核 **{info['coreVersion']}** · 网页面板 **{info['uiVersion']}** · Windows x64

## 本次更新

- 修复新安装到其他本地硬盘或自定义目录后，TUN 服务无法启动、卸载中止的问题。
- 安装器统一登记实际路径并保护新建程序目录，桌面更新和内核更新沿用同一安装身份。
- 卸载使用安装包自带的维护程序；本版安装未启用过服务或缺失配套程序时，也能按受保护的安装记录正常卸载。
- 增加中文、空格路径及独立 NTFS 磁盘上的新安装、TUN 启动与重启、实际转发和卸载验收。

## 下载与安装

- [安装版](https://github.com/{info['repository']}/releases/download/mihomo-desktop-v{version}/mihomo-desktop-{version}-windows-x64-setup.exe)：TUN 用户请选择安装版。
- [便携版](https://github.com/{info['repository']}/releases/download/mihomo-desktop-v{version}/mihomo-desktop-{version}-windows-x64-portable.zip)：退出旧程序后，使用完整新版目录。

使用本版完整桌面安装包执行新安装，在向导中选择所需目录。安装完成后在设置中安装桌面服务并开启 TUN；正常卸载保留配置、订阅、用户偏好和未知文件。宿主 / 服务协议继续为 v5。

自定义目录须位于支持权限保护的本地固定磁盘。上级目录权限不安全，或已有非空程序目录允许普通用户修改时，会在写入前说明原因并拒绝安装。

[安装、配置与验证指南](https://github.com/{info['repository']}/blob/{info['uiCommit']}/desktop/README.zh-CN.md) · [桌面与网页源码](https://github.com/{info['repository']}/tree/{info['uiCommit']}) · [内核源码](https://github.com/{info['repository']}/tree/{info['coreCommit']})
''')
