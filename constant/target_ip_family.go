package constant

import (
	"errors"
	"fmt"
	"net/netip"
)

// TargetIPFamily constrains the business destination, independently of the
// address family used to reach a proxy server. The zero value adds no limit.
type TargetIPFamily string

const (
	TargetIPAny TargetIPFamily = ""
	TargetIPv4  TargetIPFamily = "ipv4"
	TargetIPv6  TargetIPFamily = "ipv6"
)

var ErrTargetIPFamily = errors.New("target address family is not allowed")
var ErrNoProxyTarget = errors.New("no compatible proxy target address")

func ParseTargetIPFamily(value string) (TargetIPFamily, error) {
	switch value {
	case "", "any":
		return TargetIPAny, nil
	case "ipv4":
		return TargetIPv4, nil
	case "ipv6":
		return TargetIPv6, nil
	default:
		return TargetIPAny, fmt.Errorf("invalid IP family %q: expected any, ipv4 or ipv6", value)
	}
}

func (family TargetIPFamily) String() string {
	if family == TargetIPAny {
		return "any"
	}
	return string(family)
}

func (family TargetIPFamily) Allows(ip netip.Addr) bool {
	ip = ip.Unmap()
	return ip.IsValid() && (family == TargetIPAny || family == TargetIPv4 && ip.Is4() || family == TargetIPv6 && ip.Is6())
}

func (family TargetIPFamily) Check(ip netip.Addr) error {
	if !family.Allows(ip) {
		return fmt.Errorf("%w: %s (policy %s)", ErrTargetIPFamily, ip, family)
	}
	return nil
}
