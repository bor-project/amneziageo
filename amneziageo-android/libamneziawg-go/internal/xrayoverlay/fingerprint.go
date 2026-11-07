package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

func sourceFingerprint() (string, error) {
	root, err := filepath.Abs("..")
	if err != nil {
		return "", err
	}
	files := make(map[string]string)
	for _, directory := range []string{"libamneziawg-go", "amneziawg-go"} {
		err := filepath.WalkDir(filepath.Join(root, directory), func(path string, entry fs.DirEntry, walkErr error) error {
			if walkErr != nil {
				return walkErr
			}
			if entry.IsDir() {
				if entry.Name() == ".git" || entry.Name() == ".local" {
					return filepath.SkipDir
				}
				return nil
			}
			extension := strings.ToLower(filepath.Ext(path))
			if extension != ".go" && extension != ".s" && extension != ".c" && extension != ".h" && entry.Name() != "go.mod" && entry.Name() != "go.sum" && entry.Name() != "native_pool.go.txt" {
				return nil
			}
			relative, err := filepath.Rel(root, path)
			if err != nil {
				return err
			}
			files[filepath.ToSlash(relative)] = path
			return nil
		})
		if err != nil {
			return "", err
		}
	}
	shared := filepath.Join(root, "..", "tools", "xray-build")
	if err := filepath.WalkDir(shared, func(path string, entry fs.DirEntry, walkErr error) error {
		if walkErr != nil {
			return walkErr
		}
		if entry.IsDir() {
			return nil
		}
		name := entry.Name()
		if filepath.Ext(path) != ".go" && name != "go.mod" && name != "go.sum" && name != "manifest.txt" && name != "udp-worker-queue.patch" && name != "SOURCE.md" {
			return nil
		}
		relative, err := filepath.Rel(shared, path)
		if err != nil {
			return err
		}
		files["shared/xray-build/"+filepath.ToSlash(relative)] = path
		return nil
	}); err != nil {
		return "", err
	}
	for _, name := range []string{"tools/build-engine-android.ps1", "tools/build-engine-android.sh"} {
		files[name] = filepath.Join(root, filepath.FromSlash(name))
	}
	names := make([]string, 0, len(files))
	for name := range files {
		names = append(names, name)
	}
	sort.Strings(names)
	hash := sha256.New()
	hash.Write([]byte("amneziageo.android-native.v2\n"))
	for _, name := range names {
		data, err := os.ReadFile(files[name])
		if err != nil {
			return "", err
		}
		digest := sha256.Sum256(bytes.ReplaceAll(data, []byte("\r\n"), []byte("\n")))
		fmt.Fprintf(hash, "%s\x00%x\n", name, digest)
	}
	return hex.EncodeToString(hash.Sum(nil)), nil
}
