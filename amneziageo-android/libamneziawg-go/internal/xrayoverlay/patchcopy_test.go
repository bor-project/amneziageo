package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"testing"

	"amneziageo.local/xrayruntime"
)

func addedFixture(t *testing.T, root string) (string, string, []xrayruntime.PatchedFile) {
	t.Helper()
	output, official := filepath.Join(root, "output"), filepath.Join(root, "official")
	if err := os.MkdirAll(output, 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(official, 0700); err != nil {
		t.Fatal(err)
	}
	content := []byte("package fixture\n")
	hash := sha256.Sum256(content)
	files := []xrayruntime.PatchedFile{{Path: "common/udppressure/reporter.go", BeforeSHA256: "absent", AfterSHA256: hex.EncodeToString(hash[:])},
		{Path: "common/udppressure/reporter_test.go", BeforeSHA256: "absent", AfterSHA256: hex.EncodeToString(hash[:])}}
	for _, file := range files {
		path := filepath.Join(output, "xray", filepath.FromSlash(file.Path))
		if err := os.MkdirAll(filepath.Dir(path), 0700); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, content, 0600); err != nil {
			t.Fatal(err)
		}
	}
	return output, official, files
}

func TestGenerateRepeatsPinnedTreeWithoutChangingOfficialModule(t *testing.T) {
	official := os.Getenv("AG716_OFFICIAL_MODULE_DIR")
	if official == "" {
		t.Skip("pinned public full source fixture not provided")
	}
	manifest, err := xrayruntime.Pinned()
	if err != nil {
		t.Fatal(err)
	}
	fixture := t.TempDir()
	module := filepath.Join(fixture, "amneziageo-android", "libamneziawg-go")
	if err := os.MkdirAll(module, 0700); err != nil {
		t.Fatal(err)
	}
	text := fmt.Sprintf("module owned.fixture/overlay\ngo 1.27.3\nrequire github.com/xtls/xray-core %s\nreplace github.com/xtls/xray-core => %s\n", version, strconv.Quote(filepath.ToSlash(official)))
	if err := os.WriteFile(filepath.Join(module, "go.mod"), []byte(text), 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(module, "go.sum"), nil, 0600); err != nil {
		t.Fatal(err)
	}
	output := filepath.Join(fixture, "output")
	if keep := os.Getenv("AG716_PERSIST_OVERLAY"); keep != "" {
		stage, err := filepath.Abs(os.Getenv("AG716_TEST_STAGE"))
		if err != nil || !under(stage, keep) {
			t.Fatal("persistent fixture output is outside its owned stage")
		}
		output = keep
	}
	t.Chdir(module)
	for cycle := 0; cycle < 2; cycle++ {
		if err := generate(output); err != nil {
			t.Fatal(err)
		}
		for _, file := range manifest.Files {
			data, err := os.ReadFile(filepath.Join(output, "xray", filepath.FromSlash(file.Path)))
			if err != nil {
				t.Fatal(err)
			}
			value := sha256.Sum256(data)
			if hex.EncodeToString(value[:]) != file.AfterSHA256 {
				t.Fatalf("generated source differs: %s", file.Path)
			}
			original := filepath.Join(official, filepath.FromSlash(file.Path))
			if file.BeforeSHA256 == "absent" {
				if _, err := os.Stat(original); !os.IsNotExist(err) {
					t.Fatal("official module gained generated source")
				}
			} else {
				data, err := os.ReadFile(original)
				if err != nil {
					t.Fatal(err)
				}
				value := sha256.Sum256(data)
				if hex.EncodeToString(value[:]) != file.BeforeSHA256 {
					t.Fatal("official module changed")
				}
			}
		}
		var overlay struct{ Replace map[string]string }
		data, err := os.ReadFile(filepath.Join(output, "overlay.json"))
		if err != nil || json.Unmarshal(data, &overlay) != nil || len(overlay.Replace) != 3 {
			t.Fatal("native pool overlay differs")
		}
	}
}

func TestRepeatedGenerationRemovesOnlyCurrentAdditions(t *testing.T) {
	output, official, files := addedFixture(t, t.TempDir())
	for cycle := 0; cycle < 2; cycle++ {
		if err := cleanupPatchAdditions(output, official, files); err != nil {
			t.Fatal(err)
		}
		for _, file := range files {
			path := filepath.Join(output, "xray", filepath.FromSlash(file.Path))
			if _, err := os.Stat(path); !os.IsNotExist(err) {
				t.Fatal("generated addition remains")
			}
			if err := os.WriteFile(path, []byte("package fixture\n"), 0600); err != nil {
				t.Fatal(err)
			}
		}
	}
}

func TestWrongAdditionPreservesEveryFile(t *testing.T) {
	output, official, files := addedFixture(t, t.TempDir())
	changed := filepath.Join(output, "xray", filepath.FromSlash(files[1].Path))
	if err := os.WriteFile(changed, []byte("foreign"), 0600); err != nil {
		t.Fatal(err)
	}
	if err := cleanupPatchAdditions(output, official, files); err == nil {
		t.Fatal("foreign addition accepted")
	}
	for _, file := range files {
		if _, err := os.Stat(filepath.Join(output, "xray", filepath.FromSlash(file.Path))); err != nil {
			t.Fatal("a file was removed before validation completed")
		}
	}
}

func TestOfficialModuleOverlapPreservesBytes(t *testing.T) {
	output, _, files := addedFixture(t, t.TempDir())
	for _, official := range []string{output, filepath.Join(output, "xray")} {
		if err := cleanupPatchAdditions(output, official, files); err == nil {
			t.Fatal("official module overlap accepted")
		}
	}
	if _, err := os.Stat(filepath.Join(output, "xray", filepath.FromSlash(files[0].Path))); err != nil {
		t.Fatal(err)
	}
}

func TestGeneratedSymlinkEscapePreservesExternalBytes(t *testing.T) {
	root := t.TempDir()
	output, official, files := addedFixture(t, root)
	directory := filepath.Join(output, "xray", "common", "udppressure")
	for _, file := range files {
		if err := os.Remove(filepath.Join(output, "xray", filepath.FromSlash(file.Path))); err != nil {
			t.Fatal(err)
		}
	}
	if err := os.Remove(directory); err != nil {
		t.Fatal(err)
	}
	external := filepath.Join(root, "external")
	if err := os.MkdirAll(external, 0700); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(external, "reporter.go")
	if err := os.WriteFile(path, []byte("package fixture\n"), 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(external, directory); err != nil {
		t.Skipf("symlink unavailable: %v", err)
	}
	if err := cleanupPatchAdditions(output, official, files); err == nil {
		t.Fatal("external symlink accepted")
	}
	if data, err := os.ReadFile(path); err != nil || string(data) != "package fixture\n" {
		t.Fatal("external bytes changed")
	}
}

func TestOutputParentSymlinkRejectsBeforeCreatingFiles(t *testing.T) {
	root := t.TempDir()
	external, official := filepath.Join(root, "external"), filepath.Join(root, "official")
	if err := os.MkdirAll(external, 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(official, 0700); err != nil {
		t.Fatal(err)
	}
	alias := filepath.Join(root, "alias")
	if err := os.Symlink(external, alias); err != nil {
		t.Skipf("symlink unavailable: %v", err)
	}
	if _, err := validateGeneratedOutput(filepath.Join(alias, "new-output"), official); err == nil {
		t.Fatal("output reparse escape accepted")
	}
	if _, err := os.Stat(filepath.Join(external, "new-output")); !os.IsNotExist(err) {
		t.Fatal("external output created")
	}
}

func TestGenerateRejectsReparseOutsideAddedFileSet(t *testing.T) {
	official := os.Getenv("AG716_OFFICIAL_MODULE_DIR")
	if official == "" {
		t.Skip("pinned public full source fixture not provided")
	}
	fixture := t.TempDir()
	module := filepath.Join(fixture, "amneziageo-android", "libamneziawg-go")
	if err := os.MkdirAll(module, 0700); err != nil {
		t.Fatal(err)
	}
	text := fmt.Sprintf("module owned.fixture/overlay\ngo 1.27.3\nrequire github.com/xtls/xray-core %s\nreplace github.com/xtls/xray-core => %s\n", version, strconv.Quote(filepath.ToSlash(official)))
	if err := os.WriteFile(filepath.Join(module, "go.mod"), []byte(text), 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(module, "go.sum"), nil, 0600); err != nil {
		t.Fatal(err)
	}
	output, external := filepath.Join(fixture, "output"), filepath.Join(fixture, "external")
	if err := os.MkdirAll(filepath.Join(output, "xray"), 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(external, 0700); err != nil {
		t.Fatal(err)
	}
	sentinel := filepath.Join(external, "proxy.go")
	if err := os.WriteFile(sentinel, []byte("owner-sentinel"), 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(external, filepath.Join(output, "xray", "proxy")); err != nil {
		t.Skipf("symlink unavailable: %v", err)
	}
	t.Chdir(module)
	if err := generate(output); err == nil {
		t.Fatal("reparse outside the added file set accepted")
	}
	if data, err := os.ReadFile(sentinel); err != nil || string(data) != "owner-sentinel" {
		t.Fatal("external sentinel changed")
	}
	if entries, err := os.ReadDir(external); err != nil || len(entries) != 1 {
		t.Fatal("external directory changed")
	}
}
