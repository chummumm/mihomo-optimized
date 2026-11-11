# sing-tun

Simple transparent proxy library.

For Linux, Windows, macOS and iOS.

## Mihomo Optimized patches

This pinned copy includes the Windows ownership fixes and deferred TCP
handshakes documented in [MIHOMO_PATCH.md](MIHOMO_PATCH.md). Pending mips/gvisor
connections reject failed outbounds with RST, while an explicit drop leaves the
SYN unanswered. Sniffing can accept a connection early; the pinned mipstack may
then emit FIN followed by RST on an abortive close. This is an existing stack
boundary, not a promise to reproduce the remote operating system's error.

## License

```
Copyright (C) 2022 by nekohasekai <contact-sagernet@sekai.icu>

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <http://www.gnu.org/licenses/>.
```
