package route

import (
	"context"
	"errors"
	"fmt"
	"net"
	"sync"

	"github.com/metacubex/mihomo/adapter/inbound"
	"github.com/metacubex/mihomo/component/desktop"
	"github.com/metacubex/mihomo/hub/executor"
	"github.com/metacubex/mihomo/log"

	"github.com/metacubex/http"
)

type managedControllerServer struct {
	listener net.Listener
	server   *http.Server
}

var (
	managedControllerMu sync.Mutex
	managedController   *managedControllerServer
)

// A managed controller belongs to the process, not the reloadable proxy
// configuration. Bind synchronously so a failed control channel fails startup
// and reaches the daemon's existing owned-resource cleanup.
func startManagedController(cfg *Config) error {
	managedControllerMu.Lock()
	defer managedControllerMu.Unlock()
	if managedController != nil {
		return nil
	}
	listenConfig := inbound.NewListenConfig()
	listener, err := listenConfig.Listen(context.Background(), "tcp", cfg.Addr)
	if err != nil {
		return fmt.Errorf("desktop controller listen: %w", err)
	}
	server := &http.Server{Handler: router(false, cfg.Secret, "", cfg.Cors)}
	if err := desktop.PublishController(listener.Addr().String(), executor.DesktopCoreInstance()); err != nil {
		listener.Close()
		return fmt.Errorf("desktop controller discovery: %w", err)
	}
	owned := &managedControllerServer{listener: listener, server: server}
	managedController = owned
	go func() {
		err := server.Serve(listener)
		managedControllerMu.Lock()
		if managedController != owned {
			managedControllerMu.Unlock()
			return
		}
		managedController = nil
		withdrawErr := desktop.WithdrawController()
		managedControllerMu.Unlock()
		log.Errorln("Desktop controller stopped unexpectedly: %v", errors.Join(err, withdrawErr))
		// Without its authenticated controller a managed process cannot stay
		// ready. Let the daemon close its TUN and all other owned resources.
		desktop.RequestShutdown()
	}()
	log.Infoln("RESTful API listening at: %s", listener.Addr().String())
	return nil
}

// CloseManagedController does not touch standalone listeners. Clearing the
// owner first prevents the Serve goroutine from treating this close as a crash.
func CloseManagedController() error {
	managedControllerMu.Lock()
	defer managedControllerMu.Unlock()
	owned := managedController
	managedController = nil
	if owned == nil {
		return nil
	}
	withdrawErr := desktop.WithdrawController()
	serverErr := owned.server.Close()
	listenerErr := owned.listener.Close()
	if errors.Is(listenerErr, net.ErrClosed) {
		listenerErr = nil
	}
	return errors.Join(withdrawErr, serverErr, listenerErr)
}
