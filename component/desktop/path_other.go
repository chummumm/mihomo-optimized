//go:build !windows

package desktop

import "os"

func isLink(info os.FileInfo) bool {
	return info.Mode()&os.ModeSymlink != 0
}
