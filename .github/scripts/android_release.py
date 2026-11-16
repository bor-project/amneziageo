import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import zipfile


ABIS = {"arm64": "arm64-v8a", "arm": "armeabi-v7a"}
CERTIFICATE = "8f75b2d8ab2698b3f0d36543f5cd1625b109a92452a247c6901cdae75f83f276"


class Refusal(ValueError):
    pass


def version_code(version):
    if not re.fullmatch(r"(?:[0-9]|1[0-9]|20)\.[0-9]{1,2}\.[0-9]{1,2}\.[0-9]{1,4}", version):
        raise Refusal("invalid Android version")
    major, minor, patch, build = map(int, version.split("."))
    return max(1, major * 100000000 + minor * 1000000 + patch * 10000 + build)


def package_name(version, arch):
    return f"AmneziaGeo-{version}-android-{arch}.apk"


def inspect_apk(path, arch):
    with zipfile.ZipFile(path) as apk:
        names = set(apk.namelist())
        if not {"AndroidManifest.xml", "classes.dex"} <= names:
            raise Refusal(f"{path.name}: not an Android application")
        found = {name.split("/")[1] for name in names if name.startswith("lib/") and len(name.split("/")) > 2}
        if found != {ABIS[arch]}:
            raise Refusal(f"{path.name}: expected only {ABIS[arch]}, found {sorted(found)}")


def inspect_signer(output, expected=CERTIFICATE):
    digests = re.findall(r"^(?:Signer #\d+|V3\.0 Signer:) certificate SHA-256 digest: ([0-9a-fA-F]{64})$", output, re.MULTILINE)
    if [digest.lower() for digest in digests] != [expected]:
        observed = "; ".join(line.strip() for line in output.splitlines()
            if "certificate" in line.lower() and "sha-256" in line.lower()) or "no certificate SHA-256 line"
        raise Refusal(f"APK signer differs from the published release certificate: expected {expected}; observed {observed}")
    if "Verified using v2 scheme (APK Signature Scheme v2): true" not in output:
        raise Refusal("APK Signature Scheme v2 did not verify")


def inspect_manifest(output, version):
    package = re.search(r"^package: name='([^']+)' versionCode='([^']+)' versionName='([^']+)'", output, re.MULTILINE)
    if not package or package.groups() != ("org.amneziageo.android", str(version_code(version)), version):
        raise Refusal("APK identity or version differs from the release")
    if not re.search(r"^launchable-activity:", output, re.MULTILINE) or not re.search(r"^leanback-launchable-activity:", output, re.MULTILINE):
        raise Refusal("APK does not declare both phone/tablet and TV launchers")


def build_tools(directory=None):
    if directory:
        return Path(directory)
    root = Path(os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT") or "") / "build-tools"
    versions = [path for path in root.glob("*") if re.fullmatch(r"\d+\.\d+\.\d+", path.name)]
    if not versions:
        raise Refusal("Android SDK build-tools were not found")
    return max(versions, key=lambda path: tuple(map(int, path.name.split("."))))


def verify(path, arch, version, directory=None):
    inspect_apk(path, arch)
    tools = build_tools(directory)
    signer = tools / ("apksigner.bat" if os.name == "nt" else "apksigner")
    aapt = tools / ("aapt2.exe" if os.name == "nt" else "aapt2")
    signed = subprocess.run([str(signer), "verify", "--verbose", "--print-certs", str(path)], capture_output=True, text=True, check=True)
    inspect_signer(signed.stdout)
    manifest = subprocess.run([str(aapt), "dump", "badging", str(path)], capture_output=True, text=True, check=True)
    inspect_manifest(manifest.stdout, version)


def installers(directory, version, verifier=verify, tools=None):
    version_code(version)
    expected = {package_name(version, arch) for arch in ABIS}
    found = {path.name for path in directory.glob("*.apk")}
    if found != expected:
        raise Refusal(f"public Android APK set differs: expected {sorted(expected)}, found {sorted(found)}")
    entries = []
    for arch in ABIS:
        path = directory / package_name(version, arch)
        verifier(path, arch, version, tools)
        with path.open("rb") as source:
            digest = hashlib.sha256()
            for block in iter(lambda: source.read(1024 * 1024), b""):
                digest.update(block)
        entries.append({"name": path.name, "platform": "android", "arch": arch, "variant": "apk", "sha256": digest.hexdigest(), "size": path.stat().st_size})
    return entries


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=["installers", "verify"])
    parser.add_argument("--version", required=True)
    parser.add_argument("--directory", type=Path)
    parser.add_argument("--file", type=Path)
    parser.add_argument("--arch", choices=ABIS)
    parser.add_argument("--build-tools")
    args = parser.parse_args()
    try:
        if args.command == "installers":
            for entry in installers(args.directory, args.version, tools=args.build_tools):
                print(json.dumps(entry))
        else:
            verify(args.file, args.arch, args.version, args.build_tools)
    except (Refusal, OSError, zipfile.BadZipFile, subprocess.CalledProcessError) as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
