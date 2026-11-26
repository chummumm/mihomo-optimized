#!/usr/bin/env python3
'''Render the native desktop release notes from the attested build manifest.'''
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
info = json.loads((root / 'artifacts/desktop/build-info.json').read_text())
version = info['desktopVersion']
print(f'''随包内核 **{info['coreVersion']}** · 网页面板 **{info['uiVersion']}** · Windows x64

## 本次更新

- 修复部分本地数据盘全新安装时，预检查误报 `service_untrusted_parent` 的问题；准确区分真实卷根、普通上级目录和程序目录的权限。
- 修复安装、卸载错误详情中的中文乱码，并在上级目录确实不能安全使用时显示具体路径。
- 主界面、内核、后台、桌面服务、更新校验器和随包 .NET 依赖全部安装到用户指定目录。
- 增加保留新格式化 NTFS 默认权限、卷根 Modify 权限及中文错误传递的实际验收，保留跨盘 TUN、20 次连续重启和正常卸载检查。

## 下载与安装

- [安装版](https://github.com/{info['repository']}/releases/download/mihomo-desktop-v{version}/mihomo-desktop-{version}-windows-x64-setup.exe)：TUN 用户请选择安装版。
- [便携版](https://github.com/{info['repository']}/releases/download/mihomo-desktop-v{version}/mihomo-desktop-{version}-windows-x64-portable.zip)：退出旧程序后，使用完整新版目录。

使用本版完整桌面安装包执行新安装，在向导中选择所需目录。安装完成后在设置中安装桌面服务并开启 TUN；正常卸载保留配置、订阅、用户偏好和未知文件。宿主 / 服务协议继续为 v5。

用户配置、日志和更新缓存保存在 `%LOCALAPPDATA%\\Mihomo Optimized`，服务运行数据保存在 `%PROGRAMDATA%\\Mihomo Optimized\\service`；这些运行数据与安装目录中的程序文件分开保存。

自定义目录须位于支持权限保护的本地固定磁盘。安装器不会修改整个磁盘或已有共享父目录的权限；真正可被普通用户替换的上级目录仍会明确拒绝。本次不加入旧版安装迁移或旧路径接管。

[安装、配置与验证指南](https://github.com/{info['repository']}/blob/{info['uiCommit']}/desktop/README.zh-CN.md) · [桌面与网页源码](https://github.com/{info['repository']}/tree/{info['uiCommit']}) · [内核源码](https://github.com/{info['repository']}/tree/{info['coreCommit']})
''')
