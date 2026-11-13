package tun

import (
	"errors"
	"os"
	"strings"

	"golang.org/x/sys/windows"
)

var migrateLegacyWindowsAdapter = func(name string) error {
	return migrateWindowsProductAdapter(name, readWindowsInterfaceIdentities, queryWindowsAdapter, createWindowsProductAdapter)
}

// Older standalone optimized cores used the product GUID. After a creator
// crashes, a different-GUID Create can merely rename that old adapter instead
// of removing it. Claim the verified old instance through native Create, then
// close only the creator handle we obtained before proceeding with generic New.
// Neither a down link nor the absence of a session proves creator ownership.
func migrateWindowsProductAdapter(name string,
	interfaces func() ([]windowsInterfaceIdentity, error),
	query func(string, windows.GUID) (windowsAdapterIdentity, error),
	claim func(string, windows.GUID) (windowsAdapter, error),
) error {
	inspection := windowsAdapterInspection{ExpectedGUID: *generateExclusiveGUIDByDeviceName(name)}
	fail := func(reason string, err error) error {
		return inspection.failure("migrate", reason, err)
	}
	rows, err := interfaces()
	if err != nil {
		return fail("interface_query_failed", err)
	}
	for _, row := range rows {
		if row.GUID == inspection.ExpectedGUID {
			inspection.Interfaces = append(inspection.Interfaces, row)
		}
	}
	if len(inspection.Interfaces) == 0 {
		// Generic and foreign interfaces are outside this legacy-product step.
		// Leave their handling to upstream Create/Open/StartSession.
		return nil
	}
	if len(inspection.Interfaces) != 1 {
		return fail("ambiguous_product_guid", errors.New("multiple interfaces have the legacy product GUID"))
	}
	row := inspection.Interfaces[0]
	if row.Alias == "" || !row.matchesProduct(row.Alias, inspection.ExpectedGUID) {
		return fail("identity_mismatch", errors.New("legacy product interface has no valid native identity"))
	}
	aliases := 0
	for _, candidate := range rows {
		if strings.EqualFold(candidate.Alias, row.Alias) {
			aliases++
			if candidate.GUID != inspection.ExpectedGUID {
				inspection.Interfaces = append(inspection.Interfaces, candidate)
			}
		}
	}
	if aliases != 1 {
		return fail("legacy_product_alias_conflict", errors.New("legacy product alias is not unique to its interface"))
	}
	identity, err := query(row.Alias, inspection.ExpectedGUID)
	if err != nil {
		if err != os.ErrNotExist {
			inspection.Native = &identity
			return fail("native_query_failed", err)
		}
		// Preserve the 1.0.36 retiring-row boundary exactly: an explicit absent
		// devnode plus the original alias, exact product GUID, virtual type and
		// nonzero LUID/index. A renamed row without PnP evidence is not enough.
		if !row.matchesProduct(name, inspection.ExpectedGUID) {
			return fail("native_identity_missing", errors.New("renamed legacy product interface has no verifiable native identity"))
		}
	} else {
		inspection.Native = &identity
		// Wintun's alias-conflict rename also updates DEVPKEY_Wintun_Name.
		// Bind that current name to the row; derive the GUID from the original
		// requested name, never from a suffix such as "utun 1".
		if !identity.matchesProduct(row.Alias, inspection.ExpectedGUID) {
			return fail("identity_mismatch", errors.New("legacy native adapter does not match the product identity and current alias"))
		}
	}

	// This read-only check does not atomically reserve an alias against another
	// program. Native Create arbitrates the software-device creator. Do not Open
	// or fall through to a generic Create when a live creator refuses this claim.
	adapter, err := claim(row.Alias, inspection.ExpectedGUID)
	if err != nil {
		return fail("legacy_product_claim_failed", err)
	}
	if err := adapter.Close(); err != nil {
		return fail("legacy_product_close_failed", err)
	}
	return nil
}
