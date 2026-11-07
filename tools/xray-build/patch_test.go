package xrayruntime

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func patchFixture() ([]PatchedFile, []byte, []byte, []byte) {
	before := []byte("a\nb\nc\nd\ne\nf\n")
	after := []byte("a\nB\nc\nd\nE\nf\n")
	added := []byte("package pressure\n")
	files := []PatchedFile{{"old.go", digest(before), digest(after)}, {"common/new.go", "absent", digest(added)}}
	patch := []byte("--- a/old.go\n+++ b/old.go\n@@ -2 +2 @@\n-b\n+B\n@@ -5 +5 @@\n-e\n+E\n--- /dev/null\n+++ b/common/new.go\n@@ -0,0 +1 @@\n+package pressure\n")
	return files, patch, before, after
}

func TestMultiFilePatchAppliesMultipleHunksAndNewSource(t *testing.T) {
	files, patch, before, after := patchFixture()
	root := t.TempDir()
	if err := os.WriteFile(filepath.Join(root, "old.go"), before, 0600); err != nil {
		t.Fatal(err)
	}
	if err := applyPatchTree(root, files, patch); err != nil {
		t.Fatal(err)
	}
	if got, err := os.ReadFile(filepath.Join(root, "old.go")); err != nil || string(got) != string(after) {
		t.Fatal("existing file changed incorrectly")
	}
	if got, err := os.ReadFile(filepath.Join(root, "common", "new.go")); err != nil || string(got) != "package pressure\n" {
		t.Fatal("new file changed incorrectly")
	}
	if err := applyPatchTree(root, files, patch); err == nil {
		t.Fatal("already patched source accepted")
	}
}

func TestAllFilesValidateBeforeFirstWrite(t *testing.T) {
	for _, mode := range []string{"new-already-exists", "wrong-after", "wrong-before", "missing-original"} {
		t.Run(mode, func(t *testing.T) {
			files, patch, before, _ := patchFixture()
			root := t.TempDir()
			path := filepath.Join(root, "old.go")
			if err := os.WriteFile(path, before, 0600); err != nil {
				t.Fatal(err)
			}
			switch mode {
			case "new-already-exists":
				if err := os.MkdirAll(filepath.Join(root, "common"), 0700); err != nil {
					t.Fatal(err)
				}
				if err := os.WriteFile(filepath.Join(root, "common", "new.go"), []byte("foreign"), 0600); err != nil {
					t.Fatal(err)
				}
			case "wrong-after":
				files[1].AfterSHA256 = strings.Repeat("0", 64)
			case "wrong-before":
				files[0].BeforeSHA256 = strings.Repeat("0", 64)
			case "missing-original":
				if err := os.Remove(path); err != nil {
					t.Fatal(err)
				}
			}
			if err := applyPatchTree(root, files, patch); err == nil {
				t.Fatal("invalid tree accepted")
			}
			if mode != "missing-original" {
				if got, err := os.ReadFile(path); err != nil || string(got) != string(before) {
					t.Fatal("an earlier file was written before validation completed")
				}
			}
		})
	}
}

func TestPatchRejectsAmbiguousFilesAndHunks(t *testing.T) {
	_, valid, _, _ := patchFixture()
	for _, mode := range []string{"unknown-path", "duplicate-file", "missing-file", "wrong-before-header", "wrong-new-start", "wrong-count", "overlap", "extra-line", "unterminated-line"} {
		t.Run(mode, func(t *testing.T) {
			files, _, before, _ := patchFixture()
			text := string(valid)
			switch mode {
			case "unknown-path":
				text = strings.ReplaceAll(text, "common/new.go", "common/other.go")
			case "duplicate-file":
				text += "--- /dev/null\n+++ b/common/new.go\n@@ -0,0 +1 @@\n+package pressure\n"
			case "missing-file":
				text = text[:strings.Index(text, "--- /dev/null")]
			case "wrong-before-header":
				text = strings.Replace(text, "--- /dev/null", "--- a/common/new.go", 1)
			case "wrong-new-start":
				text = strings.Replace(text, "@@ -5 +5 @@", "@@ -5 +6 @@", 1)
			case "wrong-count":
				text = strings.Replace(text, "@@ -2 +2 @@", "@@ -2,2 +2,2 @@", 1)
			case "overlap":
				text = strings.Replace(text, "@@ -5 +5 @@", "@@ -1 +1 @@", 1)
			case "extra-line":
				text += "+unknown\n"
			case "unterminated-line":
				text = strings.TrimSuffix(text, "\n")
			}
			root := t.TempDir()
			if err := os.WriteFile(filepath.Join(root, "old.go"), before, 0600); err != nil {
				t.Fatal(err)
			}
			if err := applyPatchTree(root, files, []byte(text)); err == nil {
				t.Fatal("ambiguous patch accepted")
			}
			if got, err := os.ReadFile(filepath.Join(root, "old.go")); err != nil || string(got) != string(before) {
				t.Fatal("source changed on patch rejection")
			}
		})
	}
}

func TestPatchRejectsTraversalAndSymlinks(t *testing.T) {
	root := t.TempDir()
	if _, err := patchPath(root, "../outside.go"); err == nil {
		t.Fatal("traversal accepted")
	}
	if _, err := patchPath(root, "a/../outside.go"); err == nil {
		t.Fatal("unclean path accepted")
	}
	external := t.TempDir()
	if err := os.WriteFile(filepath.Join(external, "file.go"), []byte("foreign"), 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(external, filepath.Join(root, "link")); err != nil {
		t.Skipf("symlink unavailable: %v", err)
	}
	if _, err := patchPath(root, "link/file.go"); err == nil {
		t.Fatal("symlink escape accepted")
	}
	if got, err := os.ReadFile(filepath.Join(external, "file.go")); err != nil || string(got) != "foreign" {
		t.Fatal("external bytes changed")
	}
}

func TestManifestFileSetRejectsUnpinnedIdentities(t *testing.T) {
	valid := map[string]string{"patchFileCount": "8"}
	for index, path := range patchPaths {
		prefix := "patch." + string(rune('0'+index)) + "."
		valid[prefix+"path"] = path
		valid[prefix+"beforeSha256"] = strings.Repeat("a", 64)
		if addedPath(path) {
			valid[prefix+"beforeSha256"] = "absent"
		}
		valid[prefix+"afterSha256"] = strings.Repeat("b", 64)
	}
	if _, err := manifestFiles(valid); err != nil {
		t.Fatal(err)
	}
	for _, mode := range []string{"wrong-count", "wrong-order", "new-not-absent", "existing-absent", "missing-after", "uppercase-after"} {
		t.Run(mode, func(t *testing.T) {
			values := map[string]string{}
			for key, value := range valid {
				values[key] = value
			}
			switch mode {
			case "wrong-count":
				values["patchFileCount"] = "7"
			case "wrong-order":
				values["patch.0.path"] = values["patch.1.path"]
			case "new-not-absent":
				values["patch.1.beforeSha256"] = strings.Repeat("a", 64)
			case "existing-absent":
				values["patch.0.beforeSha256"] = "absent"
			case "missing-after":
				delete(values, "patch.7.afterSha256")
			case "uppercase-after":
				values["patch.7.afterSha256"] = strings.Repeat("B", 64)
			}
			if _, err := manifestFiles(values); err == nil {
				t.Fatal("invalid patched file identity accepted")
			}
		})
	}
}

func TestPinnedPatchMatchesAllEightOfficialFiles(t *testing.T) {
	official := os.Getenv("AG716_OFFICIAL_SOURCE_TREE")
	if official == "" {
		t.Skip("pinned public upstream-before fixture not provided")
	}
	manifest, err := Pinned()
	if err != nil {
		t.Fatal(err)
	}
	if len(manifest.Files) != 8 {
		t.Fatal("pinned patch file set differs")
	}
	root := t.TempDir()
	for _, file := range manifest.Files {
		if file.BeforeSHA256 == "absent" {
			continue
		}
		data, err := os.ReadFile(filepath.Join(official, filepath.FromSlash(file.Path)))
		if err != nil {
			t.Fatal(err)
		}
		target := filepath.Join(root, filepath.FromSlash(file.Path))
		if err := os.MkdirAll(filepath.Dir(target), 0700); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(target, data, 0600); err != nil {
			t.Fatal(err)
		}
	}
	if err := ApplyPatchTree(root); err != nil {
		t.Fatal(err)
	}
	for _, file := range manifest.Files {
		data, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(file.Path)))
		if err != nil || digest(data) != file.AfterSHA256 {
			t.Fatalf("pinned patch differs: %s", file.Path)
		}
	}
	if err := ApplyPatchTree(root); err == nil {
		t.Fatal("already-patched eight-file tree accepted")
	}
}
