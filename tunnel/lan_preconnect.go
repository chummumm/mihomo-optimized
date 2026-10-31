package tunnel

import (
	"net/netip"
	"sort"

	"github.com/metacubex/mihomo/component/lanpreconnect"
	"github.com/metacubex/mihomo/component/resolver"
	C "github.com/metacubex/mihomo/constant"
)

// RefreshLANPreconnect is called only after a complete configuration has been
// installed. A prefix starts the selected runtime; it is never reduced to a
// representative host and claimed to prove readiness for that whole prefix.
func RefreshLANPreconnect() {
	configMux.RLock()
	intents := staticLANIntents(proxies, rules, mode.Load() == Rule)
	configMux.RUnlock()
	lanpreconnect.Default.Replace(intents)
}

func ResumeLANPreconnect() { lanpreconnect.NetworkChanged() }

func privateLANPrefix(prefix netip.Prefix) bool {
	prefix = normalizeLANPrefix(prefix)
	if !prefix.IsValid() {
		return false
	}
	if resolver.IsFakeIP(prefix.Masked().Addr().Unmap()) {
		return false
	}
	for _, private := range []netip.Prefix{
		netip.MustParsePrefix("10.0.0.0/8"), netip.MustParsePrefix("172.16.0.0/12"),
		netip.MustParsePrefix("192.168.0.0/16"), netip.MustParsePrefix("fc00::/7"),
	} {
		if prefix.Bits() >= private.Bits() && private.Contains(prefix.Masked().Addr()) {
			return true
		}
	}
	return false
}

func normalizeLANPrefix(prefix netip.Prefix) netip.Prefix {
	if prefix.IsValid() && prefix.Addr().Is4In6() {
		if prefix.Bits() < 96 {
			return netip.Prefix{}
		}
		prefix = netip.PrefixFrom(prefix.Addr().Unmap(), prefix.Bits()-96)
	}
	return prefix.Masked()
}

func staticLANIntents(proxies map[string]C.Proxy, rules []C.Rule, ruleMode bool) []lanpreconnect.Intent {
	var intents []lanpreconnect.Intent
	if !ruleMode {
		return nil
	}
	var previous []netip.Prefix
	for _, rule := range rules {
		if wrapped, ok := rule.(C.RuleWrapper); ok && wrapped.IsDisabled() {
			continue
		}
		if rule.RuleType() == C.MATCH {
			// A selected WireGuard may itself declare concrete private routes;
			// MATCH or a default route alone is never a LAN intent.
			if preparer := stableLANOutbound(proxies[rule.Adapter()]); preparer != nil {
				if source, ok := preparer.(interface{ LANPreconnectPrefixes() []netip.Prefix }); ok {
					for _, prefix := range source.LANPreconnectPrefixes() {
						if privateLANPrefix(prefix) && !coveredLANPrefix(prefix, previous) {
							intents = append(intents, lanpreconnect.Intent{Outbound: preparer})
							break
						}
					}
				}
			}
			break
		}
		switch rule.RuleType() {
		case C.IPCIDR:
			prefix, err := netip.ParsePrefix(rule.Payload())
			if err != nil {
				return intents
			}
			prefix = normalizeLANPrefix(prefix)
			if privateLANPrefix(prefix) && !coveredLANPrefix(prefix, previous) {
				if preparer := stableLANOutbound(proxies[rule.Adapter()]); preparer != nil {
					target := netip.Addr{}
					if prefix.IsSingleIP() {
						target = prefix.Addr().Unmap()
					}
					intents = append(intents, lanpreconnect.Intent{Outbound: preparer, Target: target})
				}
			}
			previous = append(previous, prefix)
		case C.GEOSITE:
			// These do not shadow literal-IP traffic to an explicit LAN rule.
		case C.Domain, C.DomainSuffix, C.DomainKeyword:
			if rule.Payload() == "" {
				return intents
			}
		default:
			// Provider/logic/source/process rules cannot be proved disjoint
			// without fabricating a request. Leave their choice to real traffic.
			return intents
		}
	}
	return intents
}

func coveredLANPrefix(prefix netip.Prefix, earlier []netip.Prefix) bool {
	prefix = normalizeLANPrefix(prefix)
	if !prefix.IsValid() {
		return false
	}
	// Union coverage matters: two prior /25 rules also shadow a later /24.
	type interval struct{ start, end netip.Addr }
	var ranges []interval
	for _, previous := range earlier {
		previous = normalizeLANPrefix(previous)
		if previous.Overlaps(prefix) {
			ranges = append(ranges, interval{previous.Masked().Addr(), prefixLastAddr(previous)})
		}
	}
	sort.Slice(ranges, func(i, j int) bool { return ranges[i].start.Less(ranges[j].start) })
	cursor, end := prefix.Masked().Addr(), prefixLastAddr(prefix)
	for _, r := range ranges {
		if cursor.Less(r.start) {
			return false
		}
		if !r.end.Less(end) {
			return true
		}
		if !r.end.Less(cursor) {
			cursor = r.end.Next()
		}
	}
	return false
}

func prefixLastAddr(prefix netip.Prefix) netip.Addr {
	address := prefix.Masked().Addr()
	bytes := address.As16()
	bits := prefix.Bits()
	if address.Is4() {
		bits += 96
	}
	for bit := bits; bit < 128; bit++ {
		bytes[bit/8] |= 1 << (7 - uint(bit%8))
	}
	return netip.AddrFrom16(bytes).Unmap()
}

func stableLANOutbound(proxy C.Proxy) lanpreconnect.Preparer {
	for depth := 0; proxy != nil && depth < 16; depth++ {
		a := proxy.Adapter()
		for i := 0; i < 16; i++ {
			wrapper, ok := a.(interface{ UnderlyingProxyAdapter() C.ProxyAdapter })
			if !ok {
				break
			}
			next := wrapper.UnderlyingProxyAdapter()
			if next == nil || next == a {
				return nil
			}
			a = next
		}
		if a.Type() == C.Selector {
			selector, ok := a.(interface {
				Now() string
				Proxies() []C.Proxy
			})
			if !ok {
				return nil
			}
			name := selector.Now()
			proxy = nil
			for _, candidate := range selector.Proxies() {
				if candidate.Name() == name {
					proxy = candidate
					break
				}
			}
			continue
		}
		switch a.Type() {
		case C.Fallback, C.URLTest, C.LoadBalance, C.Relay:
			return nil
		}
		preparer, _ := a.(lanpreconnect.Preparer)
		return preparer
	}
	return nil
}
