package socks5

import (
	"bytes"
	"errors"
	"fmt"
	"io"
	"net"
	"syscall"
	"testing"
	"time"
)

func TestReplyCodeForError(t *testing.T) {
	for _, test := range []struct {
		err  error
		want Error
	}{
		{syscall.ENETUNREACH, ErrNetworkUnreachable},
		{syscall.EHOSTUNREACH, ErrHostUnreachable},
		{syscall.ECONNREFUSED, ErrConnectionRefused},
		{syscall.EPERM, ErrConnectionNotAllowed},
		{ErrTTLExpired, ErrTTLExpired},
		{errors.New("unclassified remote failure"), ErrGeneralFailure},
	} {
		t.Run(test.err.Error(), func(t *testing.T) {
			if got := ReplyCodeForError(fmt.Errorf("dial: %w", test.err)); got != test.want {
				t.Fatalf("got %d, want %d", got, test.want)
			}
		})
	}
}

func TestSOCKS5ClientHandshakeReturnsRefusal(t *testing.T) {
	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()
	_ = server.SetDeadline(time.Now().Add(3 * time.Second))
	_ = client.SetDeadline(time.Now().Add(3 * time.Second))
	done := make(chan error, 1)
	go func() {
		var auth [3]byte
		if _, err := io.ReadFull(server, auth[:]); err != nil {
			done <- err
			return
		}
		if !bytes.Equal(auth[:], []byte{5, 1, 0}) {
			done <- fmt.Errorf("wrong auth request: %x", auth)
			return
		}
		if _, err := server.Write([]byte{5, 0}); err != nil {
			done <- err
			return
		}
		var request [3]byte
		if _, err := io.ReadFull(server, request[:]); err != nil {
			done <- err
			return
		}
		if _, err := ReadAddr(server, make([]byte, MaxAddrLen)); err != nil {
			done <- err
			return
		}
		// A complete, independently encoded server refusal must be consumed.
		_, err := server.Write([]byte{5, 5, 0, 1, 0, 0, 0, 0, 0, 0})
		done <- err
	}()
	bound, err := ClientHandshake(client, ParseAddr("192.0.2.1:443"), CmdConnect, nil)
	if !errors.Is(err, ErrConnectionRefused) || bound != nil {
		t.Fatalf("refusal treated as success: bound=%v err=%v", bound, err)
	}
	if err := <-done; err != nil {
		t.Fatalf("client did not consume the complete failure response: %v", err)
	}
}

type handshakeReplyReader struct {
	*bytes.Reader
}

func (handshakeReplyReader) Write(p []byte) (int, error) { return len(p), nil }

func TestSOCKS5ClientHandshakeValidatesReplyVersion(t *testing.T) {
	reply := []byte{5, 0, 4, 0, 0, 1, 0, 0, 0, 0, 0, 0}
	rw := handshakeReplyReader{bytes.NewReader(reply)}
	if _, err := ClientHandshake(rw, ParseAddr("192.0.2.1:443"), CmdConnect, nil); err == nil {
		t.Fatal("invalid response version was accepted")
	}
}
