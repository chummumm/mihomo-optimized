package desktop

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestDesktopPolicyRequiresPrivateBootstrap(t *testing.T) {
	home := t.TempDir()
	valid := Options{Home: home, Controller: "127.0.0.1:19871", Secret: strings.Repeat("a1", 24)}
	if _, err := validateOptions(valid); err != nil {
		t.Fatal(err)
	}
	for _, address := range []string{"0.0.0.0:19871", "192.168.1.1:19871", "localhost:19871", "[::]:19871", "127.0.0.1:0", "127.0.0.1:65536", "127.0.0.1"} {
		t.Run(address, func(t *testing.T) {
			options := valid
			options.Controller = address
			if _, err := validateOptions(options); err == nil {
				t.Fatal("unsafe controller accepted")
			}
		})
	}
	for _, secret := range []string{"", "too-short", strings.Repeat("x", 32) + "\n", strings.Repeat("x", 257)} {
		options := valid
		options.Secret = secret
		if _, err := validateOptions(options); err == nil {
			t.Fatal("invalid bootstrap secret accepted")
		}
	}
	options := valid
	options.Home = "relative-home"
	if _, err := validateOptions(options); err == nil {
		t.Fatal("relative home accepted")
	}
	options = valid
	options.Controller = "[::1]:19871"
	if _, err := validateOptions(options); err != nil {
		t.Fatal(err)
	}
}

func TestDesktopPathConfinement(t *testing.T) {
	home, err := filepath.EvalSymlinks(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	for _, path := range []string{home, filepath.Join(home, "config.yaml"), filepath.Join(home, "rules", "pending.yaml")} {
		if err := checkPath(home, path); err != nil {
			t.Fatalf("normal managed path rejected: %s: %v", path, err)
		}
	}
	for _, path := range []string{"config.yaml", filepath.Join(home, "..", "outside.yaml"), home + "-sibling/file", filepath.Join(home, "config.yaml:stream"), filepath.Join(home, "alias. ")} {
		if err := checkPath(home, path); err == nil {
			t.Fatalf("unsafe path accepted: %s", path)
		}
	}
	regular := filepath.Join(home, "not-a-directory")
	if err := os.WriteFile(regular, []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := checkPath(home, filepath.Join(regular, "child")); err == nil {
		t.Fatal("file parent accepted")
	}
	t.Run("symlinks", func(t *testing.T) {
		outside := t.TempDir()
		link := filepath.Join(home, "linked")
		if err := os.Symlink(outside, link); err != nil {
			t.Skipf("this runner cannot create symbolic links: %v", err)
		}
		if err := checkPath(home, filepath.Join(link, "new-file")); err == nil {
			t.Fatal("symlink parent escaped managed home")
		}
		options := Options{Home: link, Controller: "127.0.0.1:19871", Secret: strings.Repeat("a1", 24)}
		if _, err := validateOptions(options); err == nil {
			t.Fatal("symlink home accepted")
		}
	})
}

func TestDesktopPathRejectsReplacedHomeParent(t *testing.T) {
	root, err := filepath.EvalSymlinks(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	parent := filepath.Join(root, "parent")
	home := filepath.Join(parent, "home")
	if err := os.MkdirAll(home, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := checkPath(home, filepath.Join(home, "provider.yaml")); err != nil {
		t.Fatal(err)
	}
	relocated := filepath.Join(root, "relocated")
	if err := os.Rename(parent, relocated); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(relocated, parent); err != nil {
		t.Skipf("this runner cannot create symbolic links: %v", err)
	}
	if err := checkPath(home, filepath.Join(home, "provider.yaml")); err == nil {
		t.Fatal("replaced home parent redirected a managed write")
	}
}
