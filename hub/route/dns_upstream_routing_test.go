package route

import (
	"encoding/json"
	"strings"
	"testing"
)

func TestConfigSchemaOmitsRemovedDNSUpstreamRoutingFlag(t *testing.T) {
	var schema configSchema
	if err := json.Unmarshal([]byte(`{"dns-upstream-rule-routing":true}`), &schema); err != nil {
		t.Fatal(err)
	}
	wire, err := json.Marshal(schema)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(wire), "dns-upstream-rule-routing") {
		t.Fatalf("removed flag remains in the update schema: %s", wire)
	}
}
