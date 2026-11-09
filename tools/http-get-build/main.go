package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"flag"
	"fmt"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
)

func digest(data []byte) string { sum := sha256.Sum256(data); return hex.EncodeToString(sum[:]) }
func main() {
	cache := flag.String("cache", "", "runtime cache")
	system := flag.String("os", "", "target OS")
	architecture := flag.String("arch", "", "target architecture")
	compiler := flag.String("go", "go", "Go executable")
	flag.Parse()
	if err := build(*cache, *system, *architecture, *compiler); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func build(cache, system, architecture, compiler string) error {
	if cache == "" || (system != "windows" && system != "linux") || (architecture != "amd64" && architecture != "arm64") {
		return fmt.Errorf("invalid HTTP GET runtime target")
	}
	root, err := filepath.Abs("../..")
	if err != nil {
		return err
	}
	source := filepath.Join(root, "amneziageo-android", "libamneziawg-go")
	files := map[string]string{}
	for _, folder := range []string{"cmd/http-get-proxy", "xhttpnative", "transportnative", "internal/udppressure", "internal/httpgetauth"} {
		err := filepath.WalkDir(filepath.Join(source, filepath.FromSlash(folder)), func(path string, entry fs.DirEntry, err error) error {
			if err != nil {
				return err
			}
			if entry.IsDir() || filepath.Ext(path) != ".go" {
				return nil
			}
			name, err := filepath.Rel(source, path)
			if err != nil {
				return err
			}
			files[filepath.ToSlash(name)] = path
			return nil
		})
		if err != nil {
			return err
		}
	}
	for _, name := range []string{"go.mod", "go.sum"} {
		files[name] = filepath.Join(source, name)
	}

	licenses, err := os.ReadDir(filepath.Join(root, "tools", "http-get-build", "licenses"))
	if err != nil {
		return err
	}
	for _, entry := range licenses {
		if !entry.IsDir() {
			files["licenses/"+entry.Name()] = filepath.Join(root, "tools", "http-get-build", "licenses", entry.Name())
		}
	}

	names := make([]string, 0, len(files))
	for name := range files {
		names = append(names, name)
	}
	sort.Strings(names)
	var fingerprint strings.Builder
	fingerprint.WriteString("amneziageo.http-get-runtime.v1\n")
	for _, name := range names {
		data, err := os.ReadFile(files[name])
		if err != nil {
			return err
		}
		fmt.Fprintf(&fingerprint, "%s\x00%s\n", name, digest(bytes.ReplaceAll(data, []byte("\r\n"), []byte("\n"))))
	}
	env := make([]string, 0)
	for _, value := range os.Environ() {
		name, _, _ := strings.Cut(value, "=")
		switch strings.ToUpper(name) {
		case "GOROOT", "GOOS", "GOARCH", "GOARM", "GOAMD64", "GOARM64", "GOFLAGS", "GOEXPERIMENT", "GOWORK", "GOENV", "GOTOOLCHAIN", "GOFIPS140", "CGO_ENABLED":
			continue
		}
		env = append(env, value)
	}
	env = append(env, "GOTOOLCHAIN=local", "GOWORK=off", "GOENV=off", "GOFIPS140=off", "CGO_ENABLED=0", "GOOS="+system, "GOARCH="+architecture, "GOAMD64=v1", "GOARM64=v8.0")
	command := exec.Command(compiler, "env", "GOVERSION")
	command.Env = env
	version, err := command.Output()
	if err != nil || strings.TrimSpace(string(version)) != "go1.27.3" {
		return fmt.Errorf("HTTP GET build requires Go1.27.3")
	}
	directory, err := filepath.Abs(filepath.Join(cache, "v1", system, architecture))
	if err != nil {
		return err
	}
	if err := os.MkdirAll(directory, 0755); err != nil {
		return err
	}
	executable := "http-get-proxy"
	if system == "windows" {
		executable += ".exe"
	}
	stage, err := os.MkdirTemp(directory, ".build-")
	if err != nil {
		return err
	}
	defer os.RemoveAll(stage)
	output := filepath.Join(stage, executable)
	command = exec.Command(compiler, "build", "-trimpath", "-buildvcs=false", "-mod=readonly", "-ldflags=-w -s", "-o", output, "./cmd/http-get-proxy")
	command.Dir = source
	command.Env = env
	command.Stdout = os.Stdout
	command.Stderr = os.Stderr
	if err := command.Run(); err != nil {
		return err
	}
	binary, err := os.ReadFile(output)
	if err != nil {
		return err
	}
	receipt := fmt.Sprintf("schema=amneziageo.http-get-runtime.v1\ngo.version=go1.27.3\ntarget.os=%s\ntarget.arch=%s\ninputs.sha256=%s\nbinary.sha256=%s\n", system, architecture, digest([]byte(fingerprint.String())), digest(binary))
	if err := os.WriteFile(filepath.Join(directory, executable), binary, 0755); err != nil {
		return err
	}
	if err := os.WriteFile(filepath.Join(directory, "runtime.receipt"), []byte(receipt), 0644); err != nil {
		return err
	}
	fmt.Println(directory)
	return nil
}
