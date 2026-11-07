package xrayruntime

import (
	"archive/tar"
	"bytes"
	"compress/gzip"
	"context"
	"crypto/sha256"
	"embed"
	"encoding/binary"
	"encoding/hex"
	"fmt"
	"io"
	"io/fs"
	"net/http"
	"os"
	"os/exec"
	"path"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"
	"unicode"
)

//go:embed *.go go.* manifest.txt udp-worker-queue.patch SOURCE.md cmd/xray-build/*.go
var inputs embed.FS

type Manifest struct {
	Schema              int           `json:"schema"`
	Version             string        `json:"version"`
	Revision            string        `json:"revision"`
	SourceCommit        string        `json:"sourceCommit"`
	SourceArchiveURL    string        `json:"sourceArchiveUrl"`
	SourceArchiveSHA256 string        `json:"sourceArchiveSha256"`
	GoVersion           string        `json:"goVersion"`
	WorkerPath          string        `json:"workerPath"`
	WorkerBeforeSHA256  string        `json:"workerBeforeSha256"`
	WorkerAfterSHA256   string        `json:"workerAfterSha256"`
	PatchSHA256         string        `json:"patchSha256"`
	QueueBytes          int           `json:"queueBytes"`
	Files               []PatchedFile `json:"files"`
}

func normalized(data []byte) []byte { return bytes.ReplaceAll(data, []byte("\r\n"), []byte("\n")) }
func digest(data []byte) string     { value := sha256.Sum256(data); return hex.EncodeToString(value[:]) }
func fileDigest(name string) (string, error) {
	data, err := os.ReadFile(name)
	if err != nil {
		return "", err
	}
	return digest(data), nil
}

func writeAtomic(name string, data []byte, mode os.FileMode) error {
	file, err := os.CreateTemp(filepath.Dir(name), ".xray-write-")
	if err != nil {
		return err
	}
	temporary := file.Name()
	defer os.Remove(temporary)
	if err := file.Chmod(mode); err != nil {
		file.Close()
		return err
	}
	if _, err := file.Write(data); err != nil {
		file.Close()
		return err
	}
	if err := file.Close(); err != nil {
		return err
	}
	if err := os.Rename(temporary, name); err == nil {
		return nil
	}
	info, err := os.Lstat(name)
	if err != nil || !info.Mode().IsRegular() {
		return fmt.Errorf("Xray cache destination differs")
	}
	if err := os.Remove(name); err != nil {
		return err
	}
	return os.Rename(temporary, name)
}

func Pinned() (Manifest, error) {
	var manifest Manifest
	data, err := inputs.ReadFile("manifest.txt")
	if err != nil {
		return manifest, err
	}
	values, err := readValues(data)
	if err != nil {
		return manifest, err
	}
	for _, name := range []string{"schema", "version", "revision", "sourceCommit", "sourceArchiveUrl", "sourceArchiveSha256", "goVersion", "workerPath", "workerBeforeSha256", "workerAfterSha256", "patchSha256", "queueBytes", "patchFileCount"} {
		if values[name] == "" {
			return manifest, fmt.Errorf("Xray manifest key missing: %s", name)
		}
	}
	if len(values) != 13+3*len(patchPaths) {
		return manifest, fmt.Errorf("Xray manifest key differs")
	}
	manifest.Files, err = manifestFiles(values)
	if err != nil {
		return manifest, err
	}
	manifest.Schema, _ = strconv.Atoi(values["schema"])
	manifest.QueueBytes, _ = strconv.Atoi(values["queueBytes"])
	manifest.Version = values["version"]
	manifest.Revision = values["revision"]
	manifest.SourceCommit = values["sourceCommit"]
	manifest.SourceArchiveURL = values["sourceArchiveUrl"]
	manifest.SourceArchiveSHA256 = values["sourceArchiveSha256"]
	manifest.GoVersion = values["goVersion"]
	manifest.WorkerPath = values["workerPath"]
	manifest.WorkerBeforeSHA256 = values["workerBeforeSha256"]
	manifest.WorkerAfterSHA256 = values["workerAfterSha256"]
	manifest.PatchSHA256 = values["patchSha256"]
	patch, err := inputs.ReadFile("udp-worker-queue.patch")
	if err != nil {
		return manifest, err
	}
	if manifest.Schema != 2 || manifest.Revision != "ag716.2" || manifest.Version != "26.10.31" || manifest.GoVersion != "go1.27.3" || manifest.QueueBytes != 262144 || manifest.WorkerPath != "app/proxyman/inbound/worker.go" || manifest.PatchSHA256 != digest(normalized(patch)) || !regexp.MustCompile(`^[a-zA-Z0-9.-]+$`).MatchString(manifest.Revision) {
		return manifest, fmt.Errorf("Xray source manifest differs")
	}
	if manifest.Files[0].Path != manifest.WorkerPath || manifest.Files[0].BeforeSHA256 != manifest.WorkerBeforeSHA256 || manifest.Files[0].AfterSHA256 != manifest.WorkerAfterSHA256 {
		return manifest, fmt.Errorf("Xray worker aliases differ")
	}
	if _, err := parsePatch(patch, manifest.Files); err != nil {
		return manifest, err
	}
	for _, value := range []string{manifest.SourceArchiveSHA256, manifest.WorkerBeforeSHA256, manifest.WorkerAfterSHA256, manifest.PatchSHA256} {
		if !regexp.MustCompile(`^[0-9a-f]{64}$`).MatchString(value) {
			return manifest, fmt.Errorf("Xray manifest checksum differs")
		}
	}
	if !regexp.MustCompile(`^[0-9a-f]{40}$`).MatchString(manifest.SourceCommit) || manifest.SourceArchiveURL != "https://codeload.github.com/XTLS/Xray-core/tar.gz/"+manifest.SourceCommit {
		return manifest, fmt.Errorf("Xray source identity differs")
	}
	return manifest, nil
}

func readValues(data []byte) (map[string]string, error) {
	values := map[string]string{}
	lines := strings.Split(string(normalized(data)), "\n")
	for index, line := range lines {
		if line == "" && index == len(lines)-1 {
			continue
		}
		parts := strings.SplitN(line, "=", 2)
		if len(parts) != 2 || parts[0] == "" || parts[1] == "" || values[parts[0]] != "" || strings.TrimSpace(parts[0]) != parts[0] || strings.TrimSpace(parts[1]) != parts[1] || strings.ContainsFunc(parts[0]+parts[1], unicode.IsControl) {
			return nil, fmt.Errorf("invalid Xray metadata row")
		}
		values[parts[0]] = parts[1]
	}
	return values, nil
}

func Fingerprint() (string, error) {
	names := []string{}
	if err := fs.WalkDir(inputs, ".", func(name string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if !entry.IsDir() {
			names = append(names, name)
		}
		return nil
	}); err != nil {
		return "", err
	}
	sort.Strings(names)
	var text strings.Builder
	text.WriteString("amneziageo.xray-runtime.v2\n")
	for _, name := range names {
		data, err := inputs.ReadFile(name)
		if err != nil {
			return "", err
		}
		fmt.Fprintf(&text, "%s\x00%s\n", name, digest(normalized(data)))
	}
	return digest([]byte(text.String())), nil
}

func Identity() (string, error) {
	manifest, err := Pinned()
	return manifest.Version + "-" + manifest.Revision, err
}
func label(architecture string) string {
	if architecture == "amd64" {
		return "x64"
	}
	return architecture
}
func binaryName(system string) string {
	if system == "windows" {
		return "xray.exe"
	}
	return "xray"
}

func expectedReceipt(system, architecture string) (map[string]string, error) {
	manifest, err := Pinned()
	if err != nil {
		return nil, err
	}
	if (system != "windows" && system != "linux") || (architecture != "amd64" && architecture != "arm64") {
		return nil, fmt.Errorf("unsupported Xray target")
	}
	fingerprint, err := Fingerprint()
	if err != nil {
		return nil, err
	}
	id, _ := Identity()
	metadata, _ := inputs.ReadFile("manifest.txt")
	notice, _ := inputs.ReadFile("SOURCE.md")
	return map[string]string{"schema": "amneziageo.xray-runtime.v2", "runtime.id": id, "source.commit": manifest.SourceCommit, "source.archive.sha256": manifest.SourceArchiveSHA256, "worker.before.sha256": manifest.WorkerBeforeSHA256, "worker.after.sha256": manifest.WorkerAfterSHA256, "patch.sha256": manifest.PatchSHA256, "go.version": manifest.GoVersion, "target.os": system, "target.arch": architecture, "inputs.sha256": fingerprint, "file.manifest.txt.sha256": digest(normalized(metadata)), "file.udp-worker-queue.patch.sha256": manifest.PatchSHA256, "file.SOURCE.md.sha256": digest(normalized(notice))}, nil
}

func Verify(directory, system, architecture string) error {
	expected, err := expectedReceipt(system, architecture)
	if err != nil {
		return err
	}
	data, err := os.ReadFile(filepath.Join(directory, "runtime.receipt"))
	if err != nil {
		return err
	}
	actual, err := readValues(data)
	if err != nil {
		return err
	}
	if len(actual) != 17 {
		return fmt.Errorf("Xray receipt key differs")
	}
	for key, value := range expected {
		if actual[key] != value {
			return fmt.Errorf("Xray receipt differs: %s", key)
		}
	}
	for _, file := range []string{binaryName(system), "LICENSE", "README.md", "SOURCE.md", "manifest.txt", "udp-worker-queue.patch"} {
		if !regexp.MustCompile(`^[0-9a-f]{64}$`).MatchString(actual["file."+file+".sha256"]) {
			return fmt.Errorf("Xray receipt checksum differs: %s", file)
		}
		value, err := fileDigest(filepath.Join(directory, file))
		if err != nil || value != actual["file."+file+".sha256"] {
			return fmt.Errorf("Xray packaged file differs: %s", file)
		}
	}
	return architectureOK(filepath.Join(directory, binaryName(system)), system, architecture)
}

func architectureOK(name, system, architecture string) error {
	data, err := os.ReadFile(name)
	if err != nil {
		return err
	}
	if system == "windows" {
		if len(data) < 64 || string(data[:2]) != "MZ" {
			return fmt.Errorf("Xray PE header missing")
		}
		offset := int(binary.LittleEndian.Uint32(data[60:64]))
		machine := uint16(0x8664)
		if architecture == "arm64" {
			machine = 0xaa64
		}
		if offset < 0 || offset > len(data)-6 || string(data[offset:offset+4]) != "PE\x00\x00" || binary.LittleEndian.Uint16(data[offset+4:offset+6]) != machine {
			return fmt.Errorf("Xray PE architecture differs")
		}
	} else {
		machine := uint16(62)
		if architecture == "arm64" {
			machine = 183
		}
		if len(data) < 20 || !bytes.Equal(data[:6], []byte{0x7f, 'E', 'L', 'F', 2, 1}) || binary.LittleEndian.Uint16(data[18:20]) != machine {
			return fmt.Errorf("Xray ELF architecture differs")
		}
	}
	return nil
}

func Build(ctx context.Context, cache, system, architecture, goExecutable string) (string, error) {
	manifest, err := Pinned()
	if err != nil {
		return "", err
	}
	receipt, err := expectedReceipt(system, architecture)
	if err != nil {
		return "", err
	}
	id, _ := Identity()
	cache, err = filepath.Abs(cache)
	if err != nil {
		return "", err
	}
	destination := filepath.Join(cache, id, system, label(architecture))
	if err := Verify(destination, system, architecture); err == nil {
		return destination, nil
	}
	compiler, err := exec.LookPath(goExecutable)
	if err != nil {
		return "", fmt.Errorf("Go1.27.3 is required: %w", err)
	}
	version := exec.CommandContext(ctx, compiler, "env", "GOVERSION")
	version.Env = buildEnvironment(cache, "", system, architecture)
	got, err := version.Output()
	if err != nil || strings.TrimSpace(string(got)) != manifest.GoVersion {
		return "", fmt.Errorf("Xray build requires exact %s", manifest.GoVersion)
	}
	if err := os.MkdirAll(cache, 0755); err != nil {
		return "", err
	}
	stage, err := os.MkdirTemp(cache, ".xray-build-")
	if err != nil {
		return "", err
	}
	defer func() {
		relative, err := filepath.Rel(cache, stage)
		if err == nil && !strings.HasPrefix(relative, "..") && filepath.Dir(stage) == cache {
			_ = os.RemoveAll(stage)
		}
	}()
	archive := filepath.Join(cache, manifest.SourceCommit+".tar.gz")
	if got, _ := fileDigest(archive); got != manifest.SourceArchiveSHA256 {
		request, err := http.NewRequestWithContext(ctx, http.MethodGet, manifest.SourceArchiveURL, nil)
		if err != nil {
			return "", err
		}
		response, err := (&http.Client{Timeout: 2 * time.Minute}).Do(request)
		if err != nil {
			return "", err
		}
		defer response.Body.Close()
		if response.StatusCode != http.StatusOK {
			return "", fmt.Errorf("Xray source download status %d", response.StatusCode)
		}
		data, err := io.ReadAll(io.LimitReader(response.Body, 64<<20))
		if err != nil || digest(data) != manifest.SourceArchiveSHA256 {
			return "", fmt.Errorf("Xray source archive checksum differs")
		}
		if err := writeAtomic(archive, data, 0644); err != nil {
			return "", err
		}
	}
	source := filepath.Join(stage, "source")
	if err := unpack(archive, source); err != nil {
		return "", err
	}
	if err := ApplyPatchTree(source); err != nil {
		return "", err
	}
	output := filepath.Join(stage, binaryName(system))
	command := exec.CommandContext(ctx, compiler, "build", "-trimpath", "-buildvcs=false", "-mod=readonly", "-o", output, "./main")
	command.Dir = source
	command.Env = buildEnvironment(cache, stage, system, architecture)
	command.Stdout = os.Stdout
	command.Stderr = os.Stderr
	if err := command.Run(); err != nil {
		return "", err
	}
	if err := architectureOK(output, system, architecture); err != nil {
		return "", err
	}
	if err := os.MkdirAll(destination, 0755); err != nil {
		return "", err
	}
	files := map[string][]byte{}
	files[binaryName(system)], err = os.ReadFile(output)
	if err != nil {
		return "", err
	}
	for _, name := range []string{"LICENSE", "README.md"} {
		data, err := os.ReadFile(filepath.Join(source, name))
		if err != nil {
			return "", err
		}
		files[name] = data
	}
	for _, name := range []string{"SOURCE.md", "manifest.txt", "udp-worker-queue.patch"} {
		data, err := inputs.ReadFile(name)
		if err != nil {
			return "", err
		}
		files[name] = normalized(data)
	}
	for name, data := range files {
		mode := os.FileMode(0644)
		if name == binaryName(system) {
			mode = 0755
		}
		if err := writeAtomic(filepath.Join(destination, name), data, mode); err != nil {
			return "", err
		}
		receipt["file."+name+".sha256"] = digest(data)
	}
	keys := []string{}
	for key := range receipt {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	var text strings.Builder
	for _, key := range keys {
		fmt.Fprintf(&text, "%s=%s\n", key, receipt[key])
	}
	if err := writeAtomic(filepath.Join(destination, "runtime.receipt"), []byte(text.String()), 0644); err != nil {
		return "", err
	}
	if err := Verify(destination, system, architecture); err != nil {
		return "", err
	}
	return destination, nil
}

func buildEnvironment(cache, stage, system, architecture string) []string {
	values := map[string]string{}
	for _, item := range os.Environ() {
		parts := strings.SplitN(item, "=", 2)
		if len(parts) == 2 {
			values[parts[0]] = parts[1]
		}
	}
	delete(values, "GOROOT")
	for key, value := range map[string]string{"GOENV": "off", "GOTOOLCHAIN": "local", "GOWORK": "off", "GOFLAGS": "", "GOEXPERIMENT": "", "GOOS": system, "GOARCH": architecture, "GOAMD64": "v1", "GOARM64": "v8.0", "GOFIPS140": "off", "CGO_ENABLED": "0", "GOCACHE": filepath.Join(cache, "gocache"), "GOMODCACHE": filepath.Join(cache, "modcache"), "GOPATH": filepath.Join(cache, "gopath")} {
		values[key] = value
	}
	if stage != "" {
		values["GOTMPDIR"] = stage
	}
	result := []string{}
	for key, value := range values {
		result = append(result, key+"="+value)
	}
	return result
}

func unpack(archive, destination string) error {
	file, err := os.Open(archive)
	if err != nil {
		return err
	}
	defer file.Close()
	compressed, err := gzip.NewReader(file)
	if err != nil {
		return err
	}
	defer compressed.Close()
	reader := tar.NewReader(compressed)
	for {
		header, err := reader.Next()
		if err == io.EOF {
			return nil
		}
		if err != nil {
			return err
		}
		parts := strings.SplitN(header.Name, "/", 2)
		if len(parts) < 2 || parts[1] == "" {
			continue
		}
		relative := parts[1]
		if !filepath.IsLocal(filepath.FromSlash(relative)) || path.Clean(relative) != strings.TrimSuffix(relative, "/") || strings.HasPrefix(relative, "/") || strings.Contains(relative, "\\") || relative == ".." || strings.HasPrefix(relative, "../") {
			return fmt.Errorf("Xray source archive path differs")
		}
		target := filepath.Join(destination, filepath.FromSlash(relative))
		if header.Typeflag == tar.TypeDir {
			if err := os.MkdirAll(target, 0755); err != nil {
				return err
			}
			continue
		}
		if header.Typeflag != tar.TypeReg {
			return fmt.Errorf("Xray source archive entry unsupported")
		}
		if err := os.MkdirAll(filepath.Dir(target), 0755); err != nil {
			return err
		}
		output, err := os.OpenFile(target, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0644)
		if err != nil {
			return err
		}
		_, copyErr := io.Copy(output, reader)
		closeErr := output.Close()
		if copyErr != nil {
			return copyErr
		}
		if closeErr != nil {
			return closeErr
		}
	}
}
