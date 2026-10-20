package updater

import (
	"context"
	"io"
	"os"
	"time"

	"github.com/metacubex/mihomo/component/desktop"
	mihomoHttp "github.com/metacubex/mihomo/component/http"

	"github.com/metacubex/http"
)

const defaultHttpTimeout = time.Second * 90

func downloadForBytes(url string) ([]byte, error) {
	ctx, cancel := context.WithTimeout(context.Background(), defaultHttpTimeout)
	defer cancel()
	resp, err := mihomoHttp.HttpRequest(ctx, url, http.MethodGet, nil, nil)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()

	return io.ReadAll(resp.Body)
}

func saveFile(bytes []byte, path string) error {
	if err := desktop.CheckPath(path); err != nil {
		return err
	}
	return os.WriteFile(path, bytes, 0o644)
}
