package cachefile

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"sync/atomic"
	"time"

	"github.com/metacubex/mihomo/component/desktop"
	"github.com/metacubex/mihomo/component/profile"
	C "github.com/metacubex/mihomo/constant"
	"github.com/metacubex/mihomo/log"

	"github.com/metacubex/bbolt"
)

var (
	initOnce     sync.Once
	fileMode     os.FileMode = 0o666
	defaultCache atomic.Pointer[CacheFile]

	bucketSelected         = []byte("selected")
	bucketFakeip           = []byte("fakeip")
	bucketFakeip6          = []byte("fakeip6")
	bucketETag             = []byte("etag")
	bucketSubscriptionInfo = []byte("subscriptioninfo")
	bucketStorage          = []byte("storage")
)

// CacheFile store and update the cache file
type CacheFile struct {
	DB *bbolt.DB
}

func (c *CacheFile) SetSelected(group, selected string) {
	if !profile.StoreSelected.Load() {
		return
	} else if c.DB == nil {
		return
	}

	err := c.DB.Batch(func(t *bbolt.Tx) error {
		bucket, err := t.CreateBucketIfNotExists(bucketSelected)
		if err != nil {
			return err
		}
		return bucket.Put([]byte(group), []byte(selected))
	})
	if err != nil {
		log.Warnln("[CacheFile] write cache to %s failed: %s", c.DB.Path(), err.Error())
		return
	}
}

func (c *CacheFile) SelectedMap() map[string]string {
	if !profile.StoreSelected.Load() {
		return nil
	} else if c.DB == nil {
		return nil
	}

	mapping := map[string]string{}
	c.DB.View(func(t *bbolt.Tx) error {
		bucket := t.Bucket(bucketSelected)
		if bucket == nil {
			return nil
		}

		c := bucket.Cursor()
		for k, v := c.First(); k != nil; k, v = c.Next() {
			mapping[string(k)] = string(v)
		}
		return nil
	})
	return mapping
}

// ReadSelectedSnapshot reads existing selections without initializing or repairing
// the cache. The caller applies its own configuration's store-selected policy.
// An unavailable/locked database returns an error; absence is an empty snapshot.
func ReadSelectedSnapshot(ctx context.Context) (map[string]string, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	path := C.Path.Cache()
	if err := desktop.CheckPath(path); err != nil {
		return nil, err
	}
	// Reopening our own writer would contend with its lifetime file lock. The
	// atomic publication also permits snapshots while Cache is first initializing.
	if cached := defaultCache.Load(); cached != nil && cached.DB != nil &&
		filepath.Clean(cached.DB.Path()) == filepath.Clean(path) {
		return readSelectedSnapshot(ctx, cached.DB)
	}
	info, err := os.Stat(path)
	if os.IsNotExist(err) {
		return nil, ctx.Err()
	}
	if err != nil {
		return nil, err
	}
	if !info.Mode().IsRegular() {
		return nil, fmt.Errorf("selection cache is not a regular database")
	}
	if info.Size() == 0 {
		return nil, bbolt.ErrInvalid
	}
	// Never wait for another core's writer: startup can use the group's default.
	// bbolt uses 50ms lock-retry intervals, so this permits at most the first try.
	lockTimeout := 50 * time.Millisecond
	if deadline, ok := ctx.Deadline(); ok {
		remaining := time.Until(deadline)
		if remaining <= 0 {
			return nil, context.DeadlineExceeded
		}
		if remaining < lockTimeout {
			lockTimeout = remaining
		}
	}
	db, err := bbolt.Open(path, fileMode, &bbolt.Options{
		ReadOnly: true, Timeout: lockTimeout, NoStatistics: true,
	})
	if err != nil {
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	defer db.Close()
	return readSelectedSnapshot(ctx, db)
}

func readSelectedSnapshot(ctx context.Context, db *bbolt.DB) (map[string]string, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	mapping := make(map[string]string)
	err := db.View(func(tx *bbolt.Tx) error {
		bucket := tx.Bucket(bucketSelected)
		if bucket == nil {
			return ctx.Err()
		}
		cursor := bucket.Cursor()
		for key, value := cursor.First(); key != nil; key, value = cursor.Next() {
			if err := ctx.Err(); err != nil {
				return err
			}
			mapping[string(key)] = string(value)
		}
		return ctx.Err()
	})
	if err != nil {
		return nil, err
	}
	return mapping, nil
}

func (c *CacheFile) Close() error {
	return c.DB.Close()
}

func initCache() {
	if err := desktop.CheckPath(C.Path.Cache()); err != nil {
		log.Warnln("[CacheFile] refusing unsafe managed cache: %s", err)
		defaultCache.Store(&CacheFile{})
		return
	}
	options := bbolt.Options{Timeout: time.Second, NoStatistics: true}
	db, err := bbolt.Open(C.Path.Cache(), fileMode, &options)
	switch err {
	case bbolt.ErrInvalid, bbolt.ErrChecksum, bbolt.ErrVersionMismatch:
		if err = desktop.CheckPath(C.Path.Cache()); err != nil {
			break
		}
		if err = os.Remove(C.Path.Cache()); err != nil {
			log.Warnln("[CacheFile] remove invalid cache file error: %s", err.Error())
			break
		}
		log.Infoln("[CacheFile] remove invalid cache file and create new one")
		db, err = bbolt.Open(C.Path.Cache(), fileMode, &options)
	}
	if err != nil {
		log.Warnln("[CacheFile] can't open cache file: %s", err.Error())
	}

	defaultCache.Store(&CacheFile{
		DB: db,
	})
}

// Cache return singleton of CacheFile
func Cache() *CacheFile {
	initOnce.Do(initCache)

	return defaultCache.Load()
}
