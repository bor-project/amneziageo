package xrayruntime

import (
	"bytes"
	"context"
	"encoding/binary"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
)

func fixtureReceipt(t *testing.T, directory, system, architecture string) map[string]string {
	t.Helper()
	if err := os.MkdirAll(directory, 0755); err != nil {
		t.Fatal(err)
	}
	receipt, err := expectedReceipt(system, architecture)
	if err != nil {
		t.Fatal(err)
	}
	image := make([]byte, 20)
	if system == "windows" {
		image = make([]byte, 70)
		copy(image, "MZ")
		binary.LittleEndian.PutUint32(image[60:], 64)
		copy(image[64:], "PE\x00\x00")
		machine := uint16(0x8664)
		if architecture == "arm64" {
			machine = 0xaa64
		}
		binary.LittleEndian.PutUint16(image[68:], machine)
	} else {
		copy(image, []byte{0x7f, 'E', 'L', 'F', 2, 1})
		machine := uint16(62)
		if architecture == "arm64" {
			machine = 183
		}
		binary.LittleEndian.PutUint16(image[18:], machine)
	}
	files := map[string][]byte{binaryName(system): image, "LICENSE": []byte("owned-license-fixture"), "README.md": []byte("owned-source-fixture")}
	for _, name := range []string{"SOURCE.md", "manifest.txt", "udp-worker-queue.patch"} {
		data, err := inputs.ReadFile(name)
		if err != nil {
			t.Fatal(err)
		}
		files[name] = normalized(data)
	}
	for name, data := range files {
		if err := os.WriteFile(filepath.Join(directory, name), data, 0644); err != nil {
			t.Fatal(err)
		}
		receipt["file."+name+".sha256"] = digest(data)
	}
	writeFixtureReceipt(t, directory, receipt)
	return receipt
}

func writeFixtureReceipt(t *testing.T, directory string, receipt map[string]string) {
	t.Helper()
	keys := []string{}
	for key := range receipt {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	var text strings.Builder
	for _, key := range keys {
		text.WriteString(key + "=" + receipt[key] + "\n")
	}
	if err := os.WriteFile(filepath.Join(directory, "runtime.receipt"), []byte(text.String()), 0644); err != nil {
		t.Fatal(err)
	}
}

func TestCachedTargetsRemainSeparateWithoutCompiler(t *testing.T) {
	cache := t.TempDir()
	id, err := Identity()
	if err != nil {
		t.Fatal(err)
	}
	fingerprint, err := Fingerprint()
	if err != nil {
		t.Fatal(err)
	}
	t.Logf("sharedInputFingerprint=%s", fingerprint)
	for _, system := range []string{"windows", "linux"} {
		for _, architecture := range []string{"amd64", "arm64"} {
			directory := filepath.Join(cache, id, system, label(architecture))
			fixtureReceipt(t, directory, system, architecture)
			got, err := Build(context.Background(), cache, system, architecture, "missing-owned-fixture-compiler")
			if err != nil || got != directory {
				t.Fatalf("cache target mismatch: %s %v", got, err)
			}
		}
	}
}

func TestReceiptRejectsStaleTamperedOrMalformedInputs(t *testing.T) {
	for _, mode := range []string{"binary-tamper", "missing-receipt", "wrong-target", "old-inputs", "duplicate-key", "unknown-key", "missing-key", "uppercase-sha", "changed-notice", "old-schema", "old-runtime-id", "changed-patch"} {
		t.Run(mode, func(t *testing.T) {
			directory := t.TempDir()
			receipt := fixtureReceipt(t, directory, "linux", "amd64")
			switch mode {
			case "binary-tamper":
				os.WriteFile(filepath.Join(directory, "xray"), []byte("official-or-other-binary"), 0644)
			case "missing-receipt":
				os.Remove(filepath.Join(directory, "runtime.receipt"))
			case "wrong-target":
				receipt["target.os"] = "windows"
				writeFixtureReceipt(t, directory, receipt)
			case "old-inputs":
				receipt["inputs.sha256"] = strings.Repeat("0", 64)
				writeFixtureReceipt(t, directory, receipt)
			case "duplicate-key":
				file, err := os.OpenFile(filepath.Join(directory, "runtime.receipt"), os.O_APPEND|os.O_WRONLY, 0)
				if err != nil {
					t.Fatal(err)
				}
				file.WriteString("target.os=linux\n")
				file.Close()
			case "unknown-key":
				receipt["extra"] = "unsupported"
				writeFixtureReceipt(t, directory, receipt)
			case "missing-key":
				delete(receipt, "worker.after.sha256")
				writeFixtureReceipt(t, directory, receipt)
			case "uppercase-sha":
				receipt["file.xray.sha256"] = strings.ToUpper(receipt["file.xray.sha256"])
				writeFixtureReceipt(t, directory, receipt)
			case "changed-notice":
				os.WriteFile(filepath.Join(directory, "SOURCE.md"), []byte("other notice"), 0644)
			case "old-schema":
				receipt["schema"] = "amneziageo.xray-runtime.v1"
				writeFixtureReceipt(t, directory, receipt)
			case "old-runtime-id":
				receipt["runtime.id"] = "26.10.31-ag716.1"
				writeFixtureReceipt(t, directory, receipt)
			case "changed-patch":
				os.WriteFile(filepath.Join(directory, "udp-worker-queue.patch"), []byte("other patch"), 0644)
			}
			if err := Verify(directory, "linux", "amd64"); err == nil {
				t.Fatal("invalid receipt/runtime accepted")
			}
		})
	}
}

func TestMetadataParserRejectsAmbiguousRows(t *testing.T) {
	for _, text := range []string{"a=1\na=2\n", "a=1\n\nb=2\n", "a=\n", "=value\n", "a=1\rb=2", " a=1\n", "a=1\x00\n"} {
		if _, err := readValues([]byte(text)); err == nil {
			t.Fatalf("ambiguous rows accepted: %q", text)
		}
	}
	values, err := readValues([]byte("url=https://example.invalid/?a=b\r\n"))
	if err != nil || values["url"] != "https://example.invalid/?a=b" {
		t.Fatal("first equals or CRLF parsing changed")
	}
}

func TestProductPatchSetMetadata(t *testing.T) {
	manifest, err := Pinned()
	if err != nil {
		t.Fatal(err)
	}
	patch, err := inputs.ReadFile("udp-worker-queue.patch")
	if err != nil {
		t.Fatal(err)
	}
	if manifest.Schema != 2 || manifest.Revision != "ag716.2" || manifest.QueueBytes != 262144 || len(manifest.Files) != 8 || bytes.Contains(patch, []byte("udpdiag")) {
		t.Fatal("product patch identity differs")
	}
}

func TestCompilerEnvironmentIgnoresLegacyRoot(t *testing.T) {
	t.Setenv("GOROOT", "owned-legacy-toolchain-fixture")
	for _, row := range buildEnvironment(t.TempDir(), "", "linux", "amd64") {
		if strings.HasPrefix(row, "GOROOT=") {
			t.Fatal("legacy GOROOT leaked into pinned compiler")
		}
	}
}

func TestCompilerEnvironmentPinsArchitectureAndCrypto(t *testing.T) {
	t.Setenv("GOAMD64", "v4")
	t.Setenv("GOARM64", "v8.5")
	t.Setenv("GOFIPS140", "latest")
	for _, architecture := range []string{"amd64", "arm64"} {
		values := map[string]string{}
		for _, row := range buildEnvironment(t.TempDir(), "", "linux", architecture) {
			parts := strings.SplitN(row, "=", 2)
			values[parts[0]] = parts[1]
		}
		if values["GOAMD64"] != "v1" || values["GOARM64"] != "v8.0" || values["GOFIPS140"] != "off" {
			t.Fatalf("inherited compiler variant: GOAMD64=%q GOARM64=%q GOFIPS140=%q", values["GOAMD64"], values["GOARM64"], values["GOFIPS140"])
		}
	}
}
