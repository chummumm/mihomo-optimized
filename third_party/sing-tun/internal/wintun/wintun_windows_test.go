package wintun

import (
	"errors"
	"runtime"
	"strings"
	"syscall"
	"testing"

	"golang.org/x/sys/windows"
)

func TestDesktopWintunCloseUsesOneNativeHandleAndVoidReturn(t *testing.T) {
	// Exercise the actual Windows call boundary without loading Wintun or
	// creating an adapter. The old SyscallN nargs slot would pass 1, not handle.
	var received []uintptr
	callback := windows.NewCallback(func(handle uintptr) uintptr {
		received = append(received, handle)
		return 0 // A VOID API's return register must not become a Go error.
	})
	original := procWintunCloseAdapter
	procWintunCloseAdapter = &lazyProc{Name: "test close callback", addr: callback}
	t.Cleanup(func() { procWintunCloseAdapter = original })
	const handle = uintptr(0x12345678)
	adapter := &Adapter{handle: handle}
	runtime.SetFinalizer(adapter, closeAdapter)
	if err := adapter.Close(); err != nil {
		t.Fatalf("VOID Close returned a register/LastError value: %v", err)
	}
	closeAdapter(&Adapter{handle: handle + 1})
	if len(received) != 2 || received[0] != handle || received[1] != handle+1 {
		t.Fatalf("explicit close/finalizer received wrong native arguments: %x", received)
	}
}

func TestDesktopWintunCreatePreservesHRESULTAndWin32Cause(t *testing.T) {
	for _, test := range []struct {
		name string
		raw  syscall.Errno
		want syscall.Errno
	}{
		{"existing owner", 0x800700b7, windows.ERROR_ALREADY_EXISTS},
		{"access denied", 0x80070005, windows.ERROR_ACCESS_DENIED},
		{"not ready", 0x80070015, windows.ERROR_NOT_READY},
		{"plain Win32", windows.ERROR_SHARING_VIOLATION, windows.ERROR_SHARING_VIOLATION},
		{"non-Win32 HRESULT", 0x80004005, 0x80004005},
	} {
		t.Run(test.name, func(t *testing.T) {
			err := adapterCreationError(test.raw)
			if !errors.Is(err, test.raw) || !errors.Is(err, test.want) {
				t.Fatalf("native error lost raw code or Win32 meaning: %v, raw=0x%x want=%d", err, uint32(test.raw), test.want)
			}
			if test.raw != test.want && !strings.Contains(err.Error(), "HRESULT 0x") {
				t.Fatalf("native error lost its original numeric code: %v", err)
			}
		})
	}
}
