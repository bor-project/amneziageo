package main

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"amneziageo.local/xrayruntime"
)

func samePath(left, right string) bool {
	return filepath.Clean(left) == filepath.Clean(right) || strings.EqualFold(filepath.VolumeName(left), filepath.VolumeName(right)) &&
		filepath.VolumeName(left) != "" && strings.EqualFold(filepath.Clean(left), filepath.Clean(right))
}

func under(root, name string) bool {
	relative, err := filepath.Rel(root, name)
	return err == nil && filepath.IsLocal(relative)
}

func validateGeneratedOutput(output, official string) (string, error) {
	output, err := filepath.Abs(output)
	if err != nil {
		return "", err
	}
	for current := output; ; current = filepath.Dir(current) {
		_, err := os.Lstat(current)
		if err == nil {
			resolved, err := filepath.EvalSymlinks(current)
			if err != nil || !samePath(current, resolved) {
				return "", fmt.Errorf("generated Xray output contains a reparse point")
			}
		} else if !os.IsNotExist(err) {
			return "", err
		}
		if filepath.Dir(current) == current {
			break
		}
	}
	official, err = filepath.Abs(official)
	if err != nil {
		return "", err
	}
	resolvedOfficial, err := filepath.EvalSymlinks(official)
	if err != nil {
		return "", err
	}
	if samePath(output, resolvedOfficial) || under(resolvedOfficial, output) || under(output, resolvedOfficial) {
		return "", fmt.Errorf("generated Xray output overlaps the official module")
	}
	return output, nil
}

func generatedPath(root, name string) (string, error) {
	if !filepath.IsLocal(filepath.FromSlash(name)) || strings.Contains(name, "\\") || filepath.ToSlash(filepath.Clean(filepath.FromSlash(name))) != name {
		return "", fmt.Errorf("generated Xray path differs")
	}
	target := filepath.Join(root, filepath.FromSlash(name))
	for existing := target; ; existing = filepath.Dir(existing) {
		_, err := os.Lstat(existing)
		if err == nil {
			resolved, err := filepath.EvalSymlinks(existing)
			if err != nil || !samePath(resolved, existing) {
				return "", fmt.Errorf("generated Xray path contains a reparse point")
			}
		} else if !os.IsNotExist(err) {
			return "", err
		}
		if samePath(existing, root) {
			break
		}
		if filepath.Dir(existing) == existing {
			return "", fmt.Errorf("generated Xray path escapes output")
		}
	}
	return target, nil
}

func cleanupPatchAdditions(output, official string, files []xrayruntime.PatchedFile) error {
	output, err := validateGeneratedOutput(output, official)
	if err != nil {
		return err
	}
	module := filepath.Join(output, "xray")
	removals := []string{}
	for _, file := range files {
		if file.BeforeSHA256 != "absent" {
			continue
		}
		original := filepath.Join(official, filepath.FromSlash(file.Path))
		if _, err := os.Lstat(original); !os.IsNotExist(err) {
			return fmt.Errorf("official Xray module contains an added source path")
		}
		target, err := generatedPath(output, "xray/"+file.Path)
		if err != nil {
			return err
		}
		if !under(module, target) {
			return fmt.Errorf("generated Xray file escapes module copy")
		}
		info, err := os.Lstat(target)
		if os.IsNotExist(err) {
			continue
		}
		if err != nil || !info.Mode().IsRegular() {
			return fmt.Errorf("generated Xray added source type differs")
		}
		data, err := os.ReadFile(target)
		if err != nil {
			return err
		}
		value := sha256.Sum256(data)
		if hex.EncodeToString(value[:]) != file.AfterSHA256 {
			return fmt.Errorf("generated Xray added source differs; use a fresh overlay output")
		}
		removals = append(removals, target)
	}
	for _, target := range removals {
		if err := os.Remove(target); err != nil {
			return err
		}
	}
	return nil
}

func cleanupGeneratedAdditions(output, official string) error {
	manifest, err := xrayruntime.Pinned()
	if err != nil {
		return err
	}
	return cleanupPatchAdditions(output, official, manifest.Files)
}
