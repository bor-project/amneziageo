package main

import (
	"crypto/sha256"
	_ "embed"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"go/format"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	"amneziageo.local/xrayruntime"
)

const version = "v1.260327.1-0.20261031132019-a4a3dff5b20c"

//go:embed native_pool.go.txt
var nativePool []byte

func main() {
	output := flag.String("output", "", "overlay directory")
	fingerprint := flag.Bool("fingerprint", false, "source fingerprint")
	flag.Parse()
	if *fingerprint {
		value, err := sourceFingerprint()
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		fmt.Println(value)
		return
	}
	if err := generate(*output); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func generate(output string) error {
	if output == "" {
		return fmt.Errorf("output directory is required")
	}
	metadata, err := exec.Command("go", "list", "-m", "-json", "github.com/xtls/xray-core").Output()
	if err != nil {
		return fmt.Errorf("resolve pinned Xray: %w", err)
	}
	var module struct{ Version, Dir string }
	if err := json.Unmarshal(metadata, &module); err != nil {
		return err
	}
	if module.Version != version || module.Dir == "" {
		return fmt.Errorf("unexpected Xray module version")
	}
	output, err = validateGeneratedOutput(output, module.Dir)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(output, 0700); err != nil {
		return err
	}
	localModule := filepath.Join(output, "xray")
	if err := cleanupGeneratedAdditions(output, module.Dir); err != nil {
		return err
	}
	if err := filepath.WalkDir(module.Dir, func(path string, entry fs.DirEntry, walkErr error) error {
		if walkErr != nil {
			return walkErr
		}
		relative, err := filepath.Rel(module.Dir, path)
		if err != nil {
			return err
		}
		target, err := generatedPath(output, filepath.ToSlash(filepath.Join("xray", relative)))
		if err != nil {
			return err
		}
		if entry.IsDir() {
			return os.MkdirAll(target, 0700)
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		return os.WriteFile(target, data, 0600)
	}); err != nil {
		return err
	}
	for _, name := range []string{"go.mod", "go.sum"} {
		data, err := os.ReadFile(name)
		if err != nil {
			return err
		}
		if err := os.WriteFile(filepath.Join(output, "native."+name), data, 0600); err != nil {
			return err
		}
	}
	moduleFile := filepath.Join(output, "native.go.mod")
	awg, err := filepath.Abs("../amneziawg-go")
	if err != nil {
		return err
	}
	if data, err := exec.Command("go", "mod", "edit", "-modfile", moduleFile, "-replace", "github.com/amnezia-vpn/amneziawg-go/v3="+awg).CombinedOutput(); err != nil {
		return fmt.Errorf("prepare AWG module: %w: %s", err, data)
	}
	if data, err := exec.Command("go", "mod", "edit", "-modfile", moduleFile, "-replace", "github.com/xtls/xray-core="+localModule).CombinedOutput(); err != nil {
		return fmt.Errorf("prepare native module: %w: %s", err, data)
	}
	shared, err := filepath.Abs("../../tools/xray-build")
	if err != nil {
		return err
	}
	if data, err := exec.Command("go", "mod", "edit", "-modfile", moduleFile, "-replace", "amneziageo.local/xrayruntime="+shared).CombinedOutput(); err != nil {
		return fmt.Errorf("prepare shared Xray patch module: %w: %s", err, data)
	}
	if err := xrayruntime.ApplyPatchTree(localModule); err != nil {
		return err
	}
	replacements := map[string]string{}
	patch := func(name, checksum string, transform func(string) (string, error)) error {
		original := filepath.Join(module.Dir, "transport", "internet", "splithttp", name)
		data, err := os.ReadFile(original)
		if err != nil {
			return err
		}
		digest := sha256.Sum256(data)
		if hex.EncodeToString(digest[:]) != checksum {
			return fmt.Errorf("unexpected Xray source: %s", name)
		}
		changed, err := transform(string(data))
		if err != nil {
			return err
		}
		formatted, err := format.Source([]byte(changed))
		if err != nil {
			return err
		}
		target := filepath.Join(output, name)
		if err := os.WriteFile(target, formatted, 0600); err != nil {
			return err
		}
		replacements[filepath.Join(localModule, "transport", "internet", "splithttp", name)] = target
		return nil
	}
	replace := func(text, old, next string, count int) (string, error) {
		if strings.Count(text, old) != count {
			return "", fmt.Errorf("unexpected Xray patch context")
		}
		return strings.ReplaceAll(text, old, next), nil
	}
	if err := patch("dialer.go", "ed4ec0d02b7f4bed517f1326c534186e7648af81fe0c974f601a1e933666d0cd", func(text string) (string, error) {
		for _, change := range [][2]string{
			{"type dialerConf struct {", "type dialerConf struct {\n owner *NativePoolOwner"},
			{"key := dialerConf{dest, streamSettings}", "key := dialerConf{nativeOwnerFrom(ctx), dest, streamSettings}"},
			{"return createHTTPClient(dest, streamSettings)", "return nativeHTTPClient(ctx, dest, streamSettings)"},
			{"if globalDialerMap == nil {", "if owner := nativeOwnerFrom(ctx); owner != nil && owner.closed() { return nativeClosedClient{}, nil }; if globalDialerMap == nil {"},
		} {
			var err error
			text, err = replace(text, change[0], change[1], 1)
			if err != nil {
				return "", err
			}
		}
		return text, nil
	}); err != nil {
		return err
	}
	if err := patch("client.go", "b528c116a72e77cb51e4fb1978a07d0e92d6b0587e0bafb3011d4891a63499dc", func(text string) (string, error) {
		text, err := replace(text, "context.WithoutCancel(ctx)", "nativeRequestContext(ctx)", 3)
		if err != nil {
			return "", err
		}
		text, err = replace(text, "c.client.Do(req)", "nativeDo(c.client, req)", 2)
		if err != nil {
			return "", err
		}
		text, err = replace(text, "type DefaultDialerClient struct {", "type DefaultDialerClient struct {\n nativeOwner *NativePoolOwner", 1)
		if err != nil {
			return "", err
		}
		return replace(text, "func (c *DefaultDialerClient) Close() error {", "func (c *DefaultDialerClient) Close() error {\n c.client.CloseIdleConnections(); nativeForgetClient(c)", 1)
	}); err != nil {
		return err
	}
	pool, err := format.Source(nativePool)
	if err != nil {
		return err
	}
	target := filepath.Join(output, "ag_native_pool.go")
	if err := os.WriteFile(target, pool, 0600); err != nil {
		return err
	}
	replacements[filepath.Join(localModule, "transport", "internet", "splithttp", "ag_native_pool.go")] = target
	data, err := json.MarshalIndent(struct{ Replace map[string]string }{replacements}, "", "  ")
	if err != nil {
		return err
	}
	path := filepath.Join(output, "overlay.json")
	if err := os.WriteFile(path, data, 0600); err != nil {
		return err
	}
	fmt.Println(path)
	return nil
}
