package config

import "github.com/metacubex/mihomo/component/desktop"

// ApplyDesktopPolicy is reapplied to every candidate, including direct
// executor reloads, and again after startup CLI options. The on-disk YAML is
// never rewritten to store supervisor credentials or transient endpoints.
func ApplyDesktopPolicy(cfg *Config) {
	if !desktop.Enabled() {
		return
	}
	if cfg.Controller == nil {
		cfg.Controller = &Controller{}
	}
	address, secret := desktop.Controller()
	*cfg.Controller = Controller{
		ExternalController: address,
		Secret:             secret,
		Cors:               Cors{AllowOrigins: desktop.AllowedOrigins()},
	}
	if cfg.General != nil {
		cfg.General.CoreAutoUpdate = false
		cfg.General.ExternalUIAutoUpdate = false
	}
}

func applyDesktopRawPolicy(cfg *RawConfig) {
	if !desktop.Enabled() {
		return
	}
	// Do this before parseController can validate or use the untrusted YAML's
	// extra endpoints. The supervisor's policy is independent of that document.
	cfg.ExternalController, cfg.Secret = desktop.Controller()
	cfg.ExternalControllerTLS = ""
	cfg.ExternalControllerUnix = ""
	cfg.ExternalControllerPipe = ""
	cfg.ExternalControllerRoutingMark = 0
	cfg.ExternalDohServer = ""
	cfg.ExternalControllerCors = RawCors{AllowOrigins: desktop.AllowedOrigins()}
	cfg.ExternalUI, cfg.ExternalUIURL, cfg.ExternalUIName = "", "", ""
	cfg.CoreAutoUpdate, cfg.ExternalUIAutoUpdate = false, false
}
