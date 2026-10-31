package outbound

import (
	"context"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"io"
	"net/netip"
	"os"
	"strconv"
	"strings"
	"testing"
	"time"

	amneziav3 "github.com/metacubex/amneziawg-go/device"
	amnezia "github.com/metacubex/amneziawg-go/device_v1"
	C "github.com/metacubex/mihomo/constant"
	"github.com/metacubex/mihomo/constant/features"
	wgconn "github.com/metacubex/wireguard-go/conn"
	"github.com/metacubex/wireguard-go/device"
	"golang.org/x/crypto/curve25519"
)

func TestLANWireGuardPeerUsesLongestPrefixAndLastEqualPrefix(t *testing.T) {
	w := &WireGuard{option: WireGuardOption{Peers: []WireGuardPeerOption{
		{PublicKey: "default", AllowedIPs: []string{"0.0.0.0/0"}},
		{PublicKey: "broad", AllowedIPs: []string{"192.168.0.0/16", "fd42::/48"}},
		{PublicKey: "specific", AllowedIPs: []string{"192.168.42.0/24"}},
		{PublicKey: "replacement", AllowedIPs: []string{"192.168.42.0/24"}},
	}}}
	for _, tc := range []struct{ target, key string }{
		{"192.168.42.7", "replacement"}, {"::ffff:192.168.42.7", "replacement"}, {"192.168.1.7", "broad"}, {"10.0.0.1", "default"}, {"fd42::7", "broad"}, {"fd43::7", ""},
	} {
		if got := w.wireGuardLANPeer(netip.MustParseAddr(tc.target)); got != tc.key {
			t.Errorf("%s selects %s, want %s", tc.target, got, tc.key)
		}
	}
}

type lanWireGuardDevice interface {
	wireguardGoDevice
	Up() error
	IpcGet() (string, error)
}

// All native exchanges stay on loopback. The fixture proves that preparation
// performs a real handshake before a business dial, then the resulting stack
// transports application data. No key material is written to a test log.
func TestLANWireGuardNativePreparationBeforeBusiness(t *testing.T) {
	if os.Getenv("MIHOMO_LAN_INTEGRATION") != "1" {
		t.Skip("set MIHOMO_LAN_INTEGRATION=1 for loopback native handshakes")
	}
	for _, mode := range []string{ipStackMips, ipStackGVisor} {
		if mode == ipStackGVisor && !features.WithGVisor {
			continue
		}
		for _, version := range []int{0, 1, 3} {
			t.Run(fmt.Sprintf("%s/engine-%d", mode, version), func(t *testing.T) {
				ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
				defer cancel()
				serverPrivate, clientPrivate := make([]byte, 32), make([]byte, 32)
				for i := range serverPrivate {
					serverPrivate[i] = byte(i + 1)
					clientPrivate[i] = byte(i + 33)
				}
				serverPublic, err := curve25519.X25519(serverPrivate, curve25519.Basepoint)
				if err != nil {
					t.Fatal(err)
				}
				clientPublic, err := curve25519.X25519(clientPrivate, curve25519.Basepoint)
				if err != nil {
					t.Fatal(err)
				}
				stack, err := newIPStack(IPStackOption{Mode: mode}, []netip.Prefix{netip.MustParsePrefix("10.90.0.1/24")}, 1408)
				if err != nil {
					t.Fatal(err)
				}
				tun, err := newWireguardDevice(stack)
				if err != nil {
					_ = stack.Close()
					t.Fatal(err)
				}
				logger := &device.Logger{Verbosef: func(string, ...any) {}, Errorf: func(string, ...any) {}}
				var server lanWireGuardDevice
				switch version {
				case 1:
					server = amnezia.NewDevice(tun, wgconn.NewDefaultBind(), logger, 1)
				case 3:
					server = amneziav3.NewDevice(tun, wgconn.NewDefaultBind(), logger, 1)
				default:
					server = device.NewDevice(tun, wgconn.NewDefaultBind(), logger, 1)
				}
				defer server.Close()
				config := "private_key=" + hex.EncodeToString(serverPrivate) + "\nlisten_port=0\npublic_key=" + hex.EncodeToString(clientPublic) + "\nallowed_ip=10.90.0.2/32\n"
				if err = server.IpcSet(config); err != nil {
					t.Fatal("configure native server:", err)
				}
				if err = server.Up(); err != nil {
					t.Fatal(err)
				}
				state, err := server.IpcGet()
				if err != nil {
					t.Fatal("read native server status:", err)
				}
				port := 0
				for _, line := range strings.Split(state, "\n") {
					if value, ok := strings.CutPrefix(line, "listen_port="); ok {
						port, _ = strconv.Atoi(value)
					}
				}
				if port == 0 {
					t.Fatal("native server did not bind a loopback test port")
				}
				option := WireGuardOption{Name: "lan-native-test", Ip: "10.90.0.2", UDP: true, PrivateKey: base64.StdEncoding.EncodeToString(clientPrivate), Workers: 1, IPStack: IPStackOption{Mode: mode}, WireGuardPeerOption: WireGuardPeerOption{Server: "127.0.0.1", Port: port, PublicKey: base64.StdEncoding.EncodeToString(serverPublic)}}
				if version != 0 {
					option.AmneziaWGOption = &AmneziaWGOption{Version: version}
				}
				client, err := NewWireGuard(option)
				if err != nil {
					t.Fatal(err)
				}
				defer client.Close()
				target := netip.MustParseAddr("10.90.0.1")
				if err = client.PrepareLAN(ctx, target); err != nil {
					t.Fatal("native preparation:", err)
				}
				if !wireGuardRecentHandshake(client.device, hex.EncodeToString(serverPublic), time.Now()) {
					t.Fatal("preparation returned without a confirmed native handshake")
				}
				if client.option.PersistentKeepalive != 0 {
					t.Fatal("preparation changed user keepalive policy")
				}
				listener, err := stack.ListenTCP(ctx, "tcp4", netip.AddrPortFrom(target, 7777))
				if err != nil {
					t.Fatal(err)
				}
				defer listener.Close()
				served := make(chan error, 1)
				go func() {
					peer, acceptErr := listener.Accept()
					if acceptErr != nil {
						served <- acceptErr
						return
					}
					defer peer.Close()
					_ = peer.SetDeadline(time.Now().Add(5 * time.Second))
					data := make([]byte, 4)
					_, readErr := io.ReadFull(peer, data)
					if readErr != nil {
						served <- readErr
						return
					}
					if string(data) != "ping" {
						served <- fmt.Errorf("unexpected business payload %q", data)
						return
					}
					_, writeErr := peer.Write([]byte("pong"))
					served <- writeErr
				}()
				dialCtx, stop := context.WithTimeout(ctx, C.DefaultTCPTimeout)
				defer stop()
				connection, err := client.DialContext(dialCtx, &C.Metadata{NetWork: C.TCP, DstIP: target, DstPort: 7777})
				if err != nil {
					t.Fatal("business dial after preparation:", err)
				}
				defer connection.Close()
				_ = connection.SetDeadline(time.Now().Add(5 * time.Second))
				if _, err = connection.Write([]byte("ping")); err != nil {
					t.Fatal(err)
				}
				response := make([]byte, 4)
				if _, err = io.ReadFull(connection, response); err != nil || string(response) != "pong" {
					t.Fatalf("business response=%q err=%v", response, err)
				}
				select {
				case err = <-served:
					if err != nil {
						t.Fatal(err)
					}
				case <-ctx.Done():
					t.Fatal(ctx.Err())
				}
			})
		}
	}
}
