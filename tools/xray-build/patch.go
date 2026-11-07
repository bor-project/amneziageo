package xrayruntime

import (
	"bytes"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
)

type PatchedFile struct {
	Path         string `json:"path"`
	BeforeSHA256 string `json:"beforeSha256"`
	AfterSHA256  string `json:"afterSha256"`
}

var patchPaths = []string{
	"app/proxyman/inbound/worker.go",
	"common/udppressure/reporter.go",
	"common/udppressure/reporter_test.go",
	"transport/internet/udp/discard_reporter_test.go",
	"transport/internet/udp/hub.go",
	"transport/pipe/discard_reporter_test.go",
	"transport/pipe/impl.go",
	"transport/pipe/pipe.go",
}

func addedPath(name string) bool {
	return name == "common/udppressure/reporter.go" || name == "common/udppressure/reporter_test.go" ||
		name == "transport/internet/udp/discard_reporter_test.go" || name == "transport/pipe/discard_reporter_test.go"
}

func manifestFiles(values map[string]string) ([]PatchedFile, error) {
	if values["patchFileCount"] != strconv.Itoa(len(patchPaths)) {
		return nil, fmt.Errorf("Xray patched file count differs")
	}
	files := make([]PatchedFile, 0, len(patchPaths))
	checksum := regexp.MustCompile(`^[0-9a-f]{64}$`)
	for index, name := range patchPaths {
		prefix := "patch." + strconv.Itoa(index) + "."
		file := PatchedFile{values[prefix+"path"], values[prefix+"beforeSha256"], values[prefix+"afterSha256"]}
		if file.Path != name || !checksum.MatchString(file.AfterSHA256) ||
			(addedPath(name) && file.BeforeSHA256 != "absent") || (!addedPath(name) && !checksum.MatchString(file.BeforeSHA256)) {
			return nil, fmt.Errorf("Xray patched file identity differs: %s", name)
		}
		files = append(files, file)
	}
	return files, nil
}

type patchHunk struct {
	oldStart, oldCount, newStart, newCount int
	lines                                  []string
}

func parsePatch(data []byte, files []PatchedFile) (map[string][]patchHunk, error) {
	lines := strings.SplitAfter(string(normalized(data)), "\n")
	if lines[len(lines)-1] == "" {
		lines = lines[:len(lines)-1]
	}
	expected := make(map[string]PatchedFile, len(files))
	for _, file := range files {
		if expected[file.Path].Path != "" {
			return nil, fmt.Errorf("duplicate Xray patched path")
		}
		expected[file.Path] = file
	}
	sections := make(map[string][]patchHunk, len(files))
	header := regexp.MustCompile(`^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@\n$`)
	index := 0
	for index < len(lines) {
		if index+2 >= len(lines) || !strings.HasPrefix(lines[index], "--- ") || !strings.HasPrefix(lines[index+1], "+++ b/") {
			return nil, fmt.Errorf("Xray patch file header differs")
		}
		name := strings.TrimSuffix(strings.TrimPrefix(lines[index+1], "+++ b/"), "\n")
		file, exists := expected[name]
		if !exists || sections[name] != nil {
			return nil, fmt.Errorf("unexpected or repeated Xray patched path")
		}
		before := "--- a/" + name + "\n"
		if file.BeforeSHA256 == "absent" {
			before = "--- /dev/null\n"
		}
		if lines[index] != before || lines[index+1] != "+++ b/"+name+"\n" {
			return nil, fmt.Errorf("Xray patch source path differs")
		}
		index += 2
		var hunks []patchHunk
		for index < len(lines) && !strings.HasPrefix(lines[index], "--- ") {
			parts := header.FindStringSubmatch(lines[index])
			if parts == nil {
				return nil, fmt.Errorf("Xray patch hunk header differs")
			}
			numbers := [4]int{}
			for at := 0; at < len(numbers); at++ {
				value := parts[at+1]
				if value == "" && (at == 1 || at == 3) {
					value = "1"
				}
				number, err := strconv.Atoi(value)
				if err != nil || number < 0 {
					return nil, fmt.Errorf("invalid Xray patch hunk range")
				}
				numbers[at] = number
			}
			hunk := patchHunk{oldStart: numbers[0], oldCount: numbers[1], newStart: numbers[2], newCount: numbers[3]}
			if (hunk.oldStart == 0 && hunk.oldCount != 0) || (hunk.newStart == 0 && hunk.newCount != 0) || hunk.oldCount+hunk.newCount == 0 {
				return nil, fmt.Errorf("empty Xray patch hunk range")
			}
			index++
			oldLines, newLines := 0, 0
			for oldLines < hunk.oldCount || newLines < hunk.newCount {
				if index == len(lines) || len(lines[index]) < 2 || !strings.HasSuffix(lines[index], "\n") {
					return nil, fmt.Errorf("truncated Xray patch hunk")
				}
				line := lines[index]
				switch line[0] {
				case ' ':
					oldLines++
					newLines++
				case '-':
					oldLines++
				case '+':
					newLines++
				default:
					return nil, fmt.Errorf("invalid Xray patch hunk line")
				}
				if oldLines > hunk.oldCount || newLines > hunk.newCount {
					return nil, fmt.Errorf("Xray patch hunk count differs")
				}
				hunk.lines = append(hunk.lines, line)
				index++
			}
			hunks = append(hunks, hunk)
		}
		if len(hunks) == 0 {
			return nil, fmt.Errorf("Xray patch file has no hunks")
		}
		sections[name] = hunks
	}
	if len(sections) != len(files) {
		return nil, fmt.Errorf("Xray patch file set differs")
	}
	return sections, nil
}

func applyHunks(original []byte, hunks []patchHunk, file PatchedFile) ([]byte, error) {
	if file.BeforeSHA256 == "absent" {
		if len(original) != 0 {
			return nil, fmt.Errorf("new Xray file has previous content")
		}
	} else if digest(original) != file.BeforeSHA256 {
		return nil, fmt.Errorf("pinned Xray source checksum differs: %s", file.Path)
	}
	lines := strings.SplitAfter(string(original), "\n")
	if lines[len(lines)-1] == "" {
		lines = lines[:len(lines)-1]
	}
	result := make([]string, 0, len(lines))
	cursor := 0
	for _, hunk := range hunks {
		oldIndex, newIndex := hunk.oldStart-1, hunk.newStart-1
		if hunk.oldCount == 0 {
			oldIndex = hunk.oldStart
		}
		if hunk.newCount == 0 {
			newIndex = hunk.newStart
		}
		if oldIndex < cursor || oldIndex > len(lines) || hunk.oldCount > len(lines)-oldIndex || newIndex != len(result)+oldIndex-cursor {
			return nil, fmt.Errorf("Xray patch location differs: %s", file.Path)
		}
		result = append(result, lines[cursor:oldIndex]...)
		cursor = oldIndex
		for _, line := range hunk.lines {
			if line[0] != '+' {
				if cursor >= len(lines) || lines[cursor] != line[1:] {
					return nil, fmt.Errorf("Xray patch context differs: %s", file.Path)
				}
				cursor++
			}
			if line[0] != '-' {
				result = append(result, line[1:])
			}
		}
	}
	result = append(result, lines[cursor:]...)
	changed := []byte(strings.Join(result, ""))
	if digest(changed) != file.AfterSHA256 {
		return nil, fmt.Errorf("patched Xray source checksum differs: %s", file.Path)
	}
	return changed, nil
}

func patchPath(root, name string) (string, error) {
	if !filepath.IsLocal(filepath.FromSlash(name)) || strings.Contains(name, "\\") || filepath.ToSlash(filepath.Clean(filepath.FromSlash(name))) != name {
		return "", fmt.Errorf("Xray patched path differs")
	}
	target := filepath.Join(root, filepath.FromSlash(name))
	for current := target; ; current = filepath.Dir(current) {
		info, err := os.Lstat(current)
		if err == nil && info.Mode()&os.ModeSymlink != 0 {
			return "", fmt.Errorf("Xray patched path contains a symlink")
		}
		if err != nil && !os.IsNotExist(err) {
			return "", err
		}
		if current == root {
			break
		}
		if filepath.Dir(current) == current {
			return "", fmt.Errorf("Xray patched path escapes source")
		}
	}
	return target, nil
}

func applyPatchTree(root string, files []PatchedFile, patch []byte) error {
	root, err := filepath.Abs(root)
	if err != nil {
		return err
	}
	sections, err := parsePatch(patch, files)
	if err != nil {
		return err
	}
	changed := make([][]byte, len(files))
	paths := make([]string, len(files))
	for index, file := range files {
		name, err := patchPath(root, file.Path)
		if err != nil {
			return err
		}
		paths[index] = name
		info, err := os.Lstat(name)
		if file.BeforeSHA256 == "absent" {
			if !os.IsNotExist(err) {
				return fmt.Errorf("new Xray source already exists: %s", file.Path)
			}
		} else if err != nil || !info.Mode().IsRegular() {
			return fmt.Errorf("pinned Xray source file missing: %s", file.Path)
		}
		var original []byte
		if file.BeforeSHA256 != "absent" {
			original, err = os.ReadFile(name)
			if err != nil {
				return err
			}
		}
		changed[index], err = applyHunks(original, sections[file.Path], file)
		if err != nil {
			return err
		}
	}
	for index, target := range paths {
		if err := os.MkdirAll(filepath.Dir(target), 0755); err != nil {
			return err
		}
		if err := os.WriteFile(target, changed[index], 0644); err != nil {
			return err
		}
	}
	return nil
}

func ApplyPatchTree(root string) error {
	manifest, err := Pinned()
	if err != nil {
		return err
	}
	patch, err := inputs.ReadFile("udp-worker-queue.patch")
	if err != nil {
		return err
	}
	if !bytes.Equal(normalized(patch), patch) {
		patch = normalized(patch)
	}
	return applyPatchTree(root, manifest.Files, patch)
}
