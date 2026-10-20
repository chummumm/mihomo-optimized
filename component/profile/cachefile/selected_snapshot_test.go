package cachefile

import (
	"bytes"
	"context"
	"errors"
	"os"
	"os/exec"
	"reflect"
	"testing"
	"time"

	"github.com/metacubex/bbolt"
	"github.com/metacubex/mihomo/component/desktop"
	"github.com/metacubex/mihomo/component/profile"
	C "github.com/metacubex/mihomo/constant"
)

func selectedSnapshotHome(t *testing.T) string {
	t.Helper()
	previousHome, previousCache := C.Path.HomeDir(), defaultCache.Swap(nil)
	C.SetHomeDir(t.TempDir())
	t.Cleanup(func() {
		defaultCache.Store(previousCache)
		C.SetHomeDir(previousHome)
	})
	return C.Path.Cache()
}

func selectedSnapshotDatabase(t *testing.T, path string) *bbolt.DB {
	t.Helper()
	db, err := bbolt.Open(path, 0o600, &bbolt.Options{NoStatistics: true})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = db.Close() })
	if err := db.Update(func(tx *bbolt.Tx) error {
		bucket, err := tx.CreateBucketIfNotExists(bucketSelected)
		if err != nil {
			return err
		}
		return bucket.Put([]byte("Bootstrap"), []byte("saved-node"))
	}); err != nil {
		t.Fatal(err)
	}
	return db
}

func TestReadSelectedSnapshotDoesNotCreateOrRepair(t *testing.T) {
	path := selectedSnapshotHome(t)
	if selected, err := ReadSelectedSnapshot(context.Background()); err != nil || len(selected) != 0 {
		t.Fatalf("missing cache: %v, %v", selected, err)
	}
	if _, err := os.Stat(path); !os.IsNotExist(err) {
		t.Fatalf("snapshot created a missing database: %v", err)
	}
	for _, content := range [][]byte{nil, []byte("an existing invalid selection cache")} {
		if err := os.WriteFile(path, content, 0o600); err != nil {
			t.Fatal(err)
		}
		if _, err := ReadSelectedSnapshot(context.Background()); err == nil {
			t.Fatal("invalid cache unexpectedly accepted")
		}
		after, err := os.ReadFile(path)
		if err != nil || !bytes.Equal(after, content) {
			t.Fatalf("invalid cache was repaired or removed: %v", err)
		}
	}
}

func TestReadSelectedSnapshotExistingDatabaseIsUnchanged(t *testing.T) {
	path := selectedSnapshotHome(t)
	db := selectedSnapshotDatabase(t, path)
	if err := db.Close(); err != nil {
		t.Fatal(err)
	}
	before, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	selected, err := ReadSelectedSnapshot(context.Background())
	if err != nil || !reflect.DeepEqual(selected, map[string]string{"Bootstrap": "saved-node"}) {
		t.Fatalf("existing selection: %v, %v", selected, err)
	}
	selected["Bootstrap"] = "caller-only-change"
	again, err := ReadSelectedSnapshot(context.Background())
	if err != nil || again["Bootstrap"] != "saved-node" {
		t.Fatalf("snapshot exposed mutable database storage: %v, %v", again, err)
	}
	after, err := os.ReadFile(path)
	if err != nil || !bytes.Equal(before, after) {
		t.Fatalf("snapshot changed database bytes: %v", err)
	}
}

func TestReadSelectedSnapshotReusesWriterAndIgnoresRuntimePolicy(t *testing.T) {
	path := selectedSnapshotHome(t)
	db := selectedSnapshotDatabase(t, path)
	defaultCache.Store(&CacheFile{DB: db})
	previous := profile.StoreSelected.Swap(false)
	t.Cleanup(func() { profile.StoreSelected.Store(previous) })
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()
	selected, err := ReadSelectedSnapshot(ctx)
	if err != nil || selected["Bootstrap"] != "saved-node" {
		t.Fatalf("same-process selection snapshot: %v, %v", selected, err)
	}
	if profile.StoreSelected.Load() {
		t.Fatal("snapshot changed the running configuration's selection policy")
	}
}

func TestReadSelectedSnapshotLockedDatabaseIsBounded(t *testing.T) {
	path := selectedSnapshotHome(t)
	selectedSnapshotDatabase(t, path) // Held writer outside the published singleton.
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Millisecond)
	defer cancel()
	started := time.Now()
	selected, err := ReadSelectedSnapshot(ctx)
	if len(selected) != 0 || (!errors.Is(err, bbolt.ErrTimeout) && !errors.Is(err, context.DeadlineExceeded)) {
		t.Fatalf("locked database should be unavailable: %v, %v", selected, err)
	}
	if elapsed := time.Since(started); elapsed > 250*time.Millisecond {
		t.Fatalf("snapshot waited for the writer: %v", elapsed)
	}
}

func TestReadSelectedSnapshotCancellation(t *testing.T) {
	path := selectedSnapshotHome(t)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := ReadSelectedSnapshot(ctx); !errors.Is(err, context.Canceled) {
		t.Fatalf("cancellation lost: %v", err)
	}
	if _, err := os.Stat(path); !os.IsNotExist(err) {
		t.Fatalf("cancelled snapshot created a database: %v", err)
	}
}

func TestReadSelectedSnapshotManagedPath(t *testing.T) {
	const child = "MIHOMO_TEST_SELECTED_SNAPSHOT_MANAGED"
	if os.Getenv(child) != "1" {
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		command := exec.CommandContext(ctx, os.Args[0], "-test.run=^TestReadSelectedSnapshotManagedPath$")
		command.Env = append(os.Environ(), child+"=1")
		if output, err := command.CombinedOutput(); err != nil {
			t.Fatalf("managed path check: %v\n%s", err, output)
		}
		return
	}
	path := selectedSnapshotHome(t)
	db := selectedSnapshotDatabase(t, path)
	defaultCache.Store(&CacheFile{DB: db})
	if err := desktop.Enable(desktop.Options{
		Home: t.TempDir(), Controller: "127.0.0.1:1",
		Secret: "selected-snapshot-test-secret-32-characters",
	}); err != nil {
		t.Fatal(err)
	}
	if _, err := ReadSelectedSnapshot(context.Background()); err == nil {
		t.Fatal("snapshot bypassed managed path authorization for an already-open database")
	}
}
