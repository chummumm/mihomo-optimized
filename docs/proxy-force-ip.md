# 代理目标 IP 与 DNS 地址族策略

内核 `1.0.23` 增加 issue [#61](https://github.com/chummumm/mihomo-optimized/issues/61) 的 `proxy-force-ip`。它默认关闭。开启后，在实际选中的具体代理节点发送协议目标之前，使用本地解析得到的真实 IP；规则、嗅探和连接日志继续保留域名。DIRECT 保持原有行为。

```yaml
proxy-force-ip: true

proxies:
  - name: v4-proxy
    type: socks5
    server: proxy.example.com
    port: 1080
    target-ip-family: ipv4

dns:
  enable: true
  enhanced-mode: normal
  nameserver:
    - 1.1.1.1
  answer-ip-family-policy:
    "+.v4-only.example": ipv4
    "dual.v4-only.example": any
```

以上节点和域名用于说明字段，需替换为实际配置。这里有三个独立控制项：

| 字段 | 作用 | 默认值 |
| --- | --- | --- |
| 顶层 `proxy-force-ip` | 代理协议的业务目标使用真实 IP，覆盖 TCP 和 UDP | `false` |
| 节点 `target-ip-family` | 限制该节点实际连接的目标地址族；链式代理逐跳检查 | `any` |
| `dns.answer-ip-family-policy` | 按客户端原始查询域名约束 DNS 答案中的地址族 | 不配置，不增加过滤 |

## 业务连接

`target-ip-family` 接受 `any`、`ipv4`、`ipv6`。非 `any` 值要求开启 `proxy-force-ip`；DIRECT、拒绝和 DNS 等非代理节点不接受该限制。EasyTier 当前仅支持 IPv4 目标，因此拒绝 `ipv6`。代理订阅节点沿用同一解析和校验规则，也可通过 provider 的 `override.target-ip-family` 设置。

地址族限制针对该节点实际连接的目标，不是用节点服务器自身的 IP 地址族推测远端出口能力。原有 `ip-version` 字段继续保留；新字段给出目标的明确允许范围。客户端原本提供的真实目标 IP 不会因为域名映射而被悄悄换成另一个地址；明确的 hosts 或嗅探目标覆盖仍生效。Fake-IP 必须还原域名并取得真实 IP，不能直接写入代理协议。

链式代理逐跳检查，不对整条链的限制求交。例如最终服务节点 A 通过承载节点 B 建链，B 限制 IPv4、A 入口具有 IPv4、A 出口支持 IPv6 时，A 仍可访问 IPv6 业务目标：B 检查 A 的入口，A 检查最终业务。下一跳域名使用独立 bootstrap 解析，不套用最终业务域名的客户端 DNS 输出策略。

普通节点沿用本地 DNS 配置；WireGuard、EasyTier、Tailscale、ZeroTier、OpenVPN 等具有协议私有解析能力的节点继续使用其原有解析范围。代理服务器、下一跳和初始化资源的引导解析保持独立，避免用业务 DNS 递归建立自己。TLS 数据中的业务域名及节点服务器的 TLS 身份不因协议目标改为 IP 而改写。

TCP 可以在本次期限内尝试解析得到的其他允许地址；一旦返回连接，就不因后续首包写入失败自动换地址重放业务数据。UDP 后续目标继续使用当前请求上下文解析，并将真实目标反馈给 NAT 映射；不缓存或重放业务数据报。

只有不允许的地址族、没有允许的真实地址、解析失败或目标连接失败时，连接明确报错。超时、取消、临时网络错误不被记作“远端不支持某个地址族”，也不会切换到禁止的地址族。

## 客户端 DNS 答案

`answer-ip-family-policy` 只接受精确域名和 `+.后缀`，大小写不敏感，可带末尾点。精确规则优先于后缀，后缀按最长匹配；`any` 可用于取消更宽后缀的限制。决策绑定原始 QNAME，不因 hosts 别名或 CNAME 中途改变。

这是该域名的客户端输出策略，同一域名之后走 DIRECT 的连接也会受到答案过滤影响。DNS 查询时不能保证客户端随后一定使用某个代理节点，因此只应为确实需要限制的域名配置；`proxy-force-ip` 对 DIRECT 拨号保持原行为，不代表 DNS 输出策略自动豁免 DIRECT。

策略过滤不允许的 A / AAAA，包括附加区，并整理 HTTPS / SVCB 的 `ipv4hint`、`ipv6hint` 与 `mandatory` 参数。被屏蔽的 Fake-IP 地址族不会先分配假地址。只修改返回客户端的副本，解析缓存中的原始答案保持完整；本地制造的无地址结果使用零 TTL，避免把临时配置决定变成长期否定缓存。

需要改写但受 DNSSEC、TSIG 等保护的答案返回 `REFUSED`；支持时附带 EDE 原因，不把已修改的答案伪装成仍通过认证的原始回答。未经修改的受保护答案沿用原行为。初始化及节点服务器 bootstrap 查询不套用客户端输出策略。

`ipv6` 策略与全局或 DNS 的 IPv6 禁用、`force-AAAA-SOA` 等既有限制冲突时，配置加载会报错；`ipv4` 与强制屏蔽 A 的配置同样冲突。

## 与 DNS 模式的关系

保留 `enhanced-mode: normal`。代理目标编码、客户端答案过滤与 Fake-IP 映射是不同功能；开启 `proxy-force-ip` 不要求改成 Fake-IP，也不自动改写现有模式。

本版没有按 DNS 查询所用的出口自动推断未来业务连接的节点，也没有把一次探测失败当成远端缺少 IPv4 / IPv6。已知某节点只支持一个地址族时，显式设置节点限制；需要客户端也只获得对应地址族时，再为实际经过该出口的域名配置输出策略。两者不会相互隐式生成配置。
