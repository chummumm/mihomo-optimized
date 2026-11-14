package net_test

import (
	"bytes"
	"errors"
	"io"
	"net"
	"testing"
	"time"

	N "github.com/metacubex/mihomo/common/net"

	"github.com/metacubex/sing/common/buf"
	"github.com/metacubex/sing/common/bufio"
	M "github.com/metacubex/sing/common/metadata"
	SN "github.com/metacubex/sing/common/network"
	"github.com/metacubex/sing/common/uot"
)

// Match outbound.conn's interface boundary: the underlying stream may have
// other capabilities, but only ExtendedConn is exposed to UOT.
type uotExtendedOnlyConn struct {
	SN.ExtendedConn
}

func TestUOTConnReturnsPayloadLength(t *testing.T) {
	for _, tc := range []struct {
		name        string
		destination string
		isConnect   bool
	}{
		{name: "IPv4", destination: "94.140.14.140:53"},
		{name: "IPv6", destination: "[2001:db8::53]:53"},
		{name: "domain", destination: "resolver.example:53"},
		{name: "connected", destination: "94.140.14.140:53", isConnect: true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			stream, peer := net.Pipe()
			defer stream.Close()
			defer peer.Close()
			deadline := time.Now().Add(3 * time.Second)
			_ = stream.SetDeadline(deadline)
			_ = peer.SetDeadline(deadline)
			destination := M.ParseSocksaddr(tc.destination)
			request := uot.Request{Destination: destination, IsConnect: tc.isConnect}
			conn := N.NewUOTConn(&uotExtendedOnlyConn{bufio.NewExtendedConn(stream)}, request)
			reader := uot.NewConn(peer, request)

			// Reuse the same UOT stream, including a valid empty datagram.
			for _, payload := range [][]byte{bytes.Repeat([]byte{0x42}, 35), []byte("second query"), nil} {
				type writeResult struct {
					n   int
					err error
				}
				written := make(chan writeResult, 1)
				go func() {
					n, err := conn.WriteTo(payload, destination)
					written <- writeResult{n, err}
				}()
				response := buf.With(make([]byte, len(payload)+1))
				source, err := reader.ReadPacket(response)
				if err != nil || !bytes.Equal(response.Bytes(), payload) {
					t.Fatalf("UOT frame did not decode: payload=%x, err=%v", response.Bytes(), err)
				}
				if source.String() != destination.String() {
					t.Fatalf("UOT destination changed: got %s, want %s", source, destination)
				}
				result := <-written
				if result.err != nil || result.n != len(payload) {
					t.Fatalf("payload write returned (%d, %v), want (%d, nil)", result.n, result.err, len(payload))
				}
			}
		})
	}
}

type uotRecordingConn struct {
	net.Conn
	frames [][]byte
	write  func([]byte) (int, error)
}

func (c *uotRecordingConn) Write(p []byte) (int, error) {
	c.frames = append(c.frames, append([]byte(nil), p...))
	if c.write != nil {
		return c.write(p)
	}
	return len(p), nil
}

func TestUOTConnRejectsIncompleteFrame(t *testing.T) {
	writeErr := errors.New("underlying stream write failed")
	payload := bytes.Repeat([]byte{0x42}, 35)
	destination := M.ParseSocksaddr("94.140.14.140:53")
	// Legacy IPv4 UOT adds a seven-byte destination and a two-byte length.
	frameLength := len(payload) + 9
	for _, tc := range []struct {
		name    string
		n       int
		err     error
		wantErr error
	}{
		{name: "no bytes", n: 0, wantErr: io.ErrShortWrite},
		{name: "partial header", n: 8, wantErr: io.ErrShortWrite},
		{name: "header only", n: 9, wantErr: io.ErrShortWrite},
		{name: "count equals payload", n: len(payload), wantErr: io.ErrShortWrite},
		{name: "count exceeds payload", n: len(payload) + 5, wantErr: io.ErrShortWrite},
		{name: "last byte missing", n: frameLength - 1, wantErr: io.ErrShortWrite},
		{name: "invalid excess count", n: frameLength + 1, wantErr: io.ErrShortWrite},
		{name: "partial write error", n: len(payload), err: writeErr, wantErr: writeErr},
		{name: "full count with error", n: frameLength, err: writeErr, wantErr: writeErr},
	} {
		t.Run(tc.name, func(t *testing.T) {
			stream := &uotRecordingConn{write: func([]byte) (int, error) { return tc.n, tc.err }}
			conn := N.NewUOTConn(stream, uot.Request{Destination: destination})
			n, err := conn.Write(payload)
			if n != 0 || !errors.Is(err, tc.wantErr) {
				t.Fatalf("incomplete UOT frame returned (%d, %v), want (0, %v)", n, err, tc.wantErr)
			}
			if len(stream.frames) != 1 || len(stream.frames[0]) != frameLength {
				t.Fatalf("expected one complete frame write, got %v", stream.frames)
			}
		})
	}
}

type uotVectorisedRecordingConn struct {
	*uotRecordingConn
	vectorisedFrames [][]byte
}

func (c *uotVectorisedRecordingConn) WriteVectorised(buffers []*buf.Buffer) error {
	defer buf.ReleaseMulti(buffers)
	frame := make([]byte, buf.LenMulti(buffers))
	buf.CopyMulti(frame, buffers)
	c.vectorisedFrames = append(c.vectorisedFrames, frame)
	return nil
}

func TestUOTConnPreservesVectorisedWriter(t *testing.T) {
	stream := &uotVectorisedRecordingConn{uotRecordingConn: &uotRecordingConn{}}
	destination := M.ParseSocksaddr("94.140.14.140:53")
	conn := N.NewUOTConn(stream, uot.Request{Destination: destination})
	payload := bytes.Repeat([]byte{0x42}, 35)
	n, err := conn.Write(payload)
	if err != nil || n != len(payload) {
		t.Fatalf("vectorised payload write returned (%d, %v)", n, err)
	}
	if conn.Upstream() != stream || len(stream.frames) != 0 || len(stream.vectorisedFrames) != 1 {
		t.Fatal("existing vectorised writer was replaced or bypassed")
	}
}

func TestUOTConnWritePacketChecksFrameAndReleasesBuffers(t *testing.T) {
	destination := M.ParseSocksaddr("94.140.14.140:53")
	payload := bytes.Repeat([]byte{0x42}, 35)
	for _, short := range []bool{false, true} {
		name := "complete"
		if short {
			name = "short"
		}
		t.Run(name, func(t *testing.T) {
			stream := &uotRecordingConn{write: func(frame []byte) (int, error) {
				if short {
					return len(frame) - 1, nil
				}
				return len(frame), nil
			}}
			conn := N.NewUOTConn(stream, uot.Request{Destination: destination})
			buffer := buf.NewSize(len(payload))
			defer buffer.Release()
			_, _ = buffer.Write(payload)
			err := conn.WritePacket(buffer, destination)
			if (short && !errors.Is(err, io.ErrShortWrite)) || (!short && err != nil) {
				t.Fatalf("WritePacket returned %v, short=%v", err, short)
			}
			if buffer.Len() != 0 {
				t.Fatal("UOT vectorised writer did not release its owned payload buffer")
			}
			if len(stream.frames) != 1 || len(stream.frames[0]) != len(payload)+9 || !bytes.Equal(stream.frames[0][9:], payload) {
				t.Fatal("WritePacket did not send one complete frame")
			}
		})
	}
}
