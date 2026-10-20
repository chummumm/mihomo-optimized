package provider

import (
	"context"
	"errors"
	"net"
	"net/http"
	"net/http/httptest"
	"net/netip"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/metacubex/mihomo/common/utils"
	"github.com/metacubex/mihomo/component/geodata"
	"github.com/metacubex/mihomo/component/geodata/router"
	"github.com/metacubex/mihomo/component/resource"
	C "github.com/metacubex/mihomo/constant"
	P "github.com/metacubex/mihomo/constant/provider"
	"github.com/metacubex/mihomo/rules/common"

	"google.golang.org/protobuf/proto"
)

type bootstrapRuleDialer struct {
	address string
	calls   atomic.Int32
}

func (d *bootstrapRuleDialer) DialContext(ctx context.Context, network, _ string) (net.Conn, error) {
	d.calls.Add(1)
	return (&net.Dialer{}).DialContext(ctx, network, d.address)
}
func (*bootstrapRuleDialer) ListenPacket(context.Context, string, string, netip.AddrPort) (net.PacketConn, error) {
	return nil, errors.New("unexpected UDP")
}

func TestResourceBootstrapClassicalGeoSharesRouteAndDeadline(t *testing.T) {
	for _, expired := range []bool{false, true} {
		t.Run(map[bool]string{false: "success", true: "shared_deadline"}[expired], func(t *testing.T) {
			previousTunnel, previousHome, previousURL := tunnel, C.Path.HomeDir(), geodata.GeoSiteUrl()
			SetTunnel(updateTestTunnel{callback: utils.NewCallback[P.RuleProvider]()})
			C.SetHomeDir(t.TempDir())
			geodata.ClearGeoSiteCache()
			t.Cleanup(func() {
				SetTunnel(previousTunnel)
				C.SetHomeDir(previousHome)
				geodata.SetGeoSiteUrl(previousURL)
				geodata.ClearGeoSiteCache()
			})
			data, err := proto.Marshal(&router.GeoSiteList{Entry: []*router.GeoSite{{CountryCode: "CN", Domain: []*router.Domain{{Type: router.Domain_Full, Value: "geo.example"}}}}})
			if err != nil {
				t.Fatal(err)
			}
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.URL.Path == "/rules" {
					_, _ = w.Write([]byte("payload:\n  - GEOSITE,cn\n"))
					return
				}
				if expired {
					time.Sleep(100 * time.Millisecond)
				}
				_, _ = w.Write(data)
			}))
			defer server.Close()
			dialer := &bootstrapRuleDialer{address: strings.TrimPrefix(server.URL, "http://")}
			timeout := 2 * time.Second
			if expired {
				timeout = 40 * time.Millisecond
			}
			scope := resource.NewBootstrap(context.Background(), timeout, dialer, nil)
			defer scope.Close()
			geodata.SetGeoSiteUrl("http://unresolvable.invalid/geo?token=must-not-appear")
			parse := func(tp, payload, target string, params []string, subRules map[string][]C.Rule) (C.Rule, error) {
				return common.NewGEOSITE(payload, target)
			}
			path := filepath.Join(C.Path.HomeDir(), "rules.yaml")
			provider := NewRuleSetProvider("bootstrap-classical", P.Classical, P.YamlRule, 0, resource.NewHTTPVehicle("http://unresolvable.invalid/rules", path, "", nil, 10*time.Millisecond, 1024), nil, nil, parse).(*RuleSetProvider)
			defer provider.Close()
			err = provider.InitialContext(scope.Context())
			if dialer.calls.Load() != 2 {
				t.Fatalf("provider and nested Geo did not share scoped route: %d %v", dialer.calls.Load(), err)
			}
			if expired {
				if err == nil || !strings.Contains(err.Error(), "[resource-bootstrap]") || strings.Contains(err.Error(), "must-not-appear") {
					t.Fatalf("unsafe/missing Geo failure: %v", err)
				}
				for _, file := range []string{path, C.Path.GeoSite()} {
					if _, err := os.Stat(file); !os.IsNotExist(err) {
						t.Fatalf("expired preparation committed %s: %v", file, err)
					}
				}
				if provider.Count() != 0 {
					t.Fatal("failed classical provider published partial rules")
				}
				return
			}
			if err != nil || provider.Count() != 1 {
				t.Fatalf("classical initialization = %v count=%d", err, provider.Count())
			}
			stored, err := os.ReadFile(C.Path.GeoSite())
			if err != nil || string(stored) != string(data) {
				t.Fatalf("Geo cache = %v", err)
			}
		})
	}
}
