package route

import (
	"strings"
	"testing"

	"github.com/metacubex/http"
	"github.com/metacubex/http/httptest"
	"github.com/metacubex/mihomo/hub/executor"
	"github.com/metacubex/mihomo/tunnel"
)

func TestDNSUpstreamRuleRoutingPatchRequiresReloadBeforeMutation(t *testing.T) {
	previousMode := tunnel.Mode()
	previousFlag := executor.GetGeneral().DNSUpstreamRuleRouting
	t.Cleanup(func() { tunnel.SetMode(previousMode) })
	tunnel.SetMode(tunnel.Rule)
	for _, body := range []string{
		`{"dns-upstream-rule-routing":true,"mode":"global"}`,
		`{"dns-upstream-rule-routing":false,"mode":"direct"}`,
	} {
		response := httptest.NewRecorder()
		patchConfigs(response, httptest.NewRequest(http.MethodPatch, "/configs", strings.NewReader(body)))
		if response.Code != http.StatusBadRequest || !strings.Contains(response.Body.String(), "full configuration reload") {
			t.Fatalf("PATCH accepted a partial resolver topology change: %d %s", response.Code, response.Body.String())
		}
		if executor.GetGeneral().DNSUpstreamRuleRouting != previousFlag || tunnel.Mode() != tunnel.Rule {
			t.Fatal("rejected PATCH partially changed the applied configuration")
		}
	}
}
