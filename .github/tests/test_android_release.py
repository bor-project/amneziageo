import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest
import zipfile


path = Path(__file__).resolve().parents[1] / "scripts/android_release.py"
spec = importlib.util.spec_from_file_location("android_release", path)
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class AndroidReleaseTests(unittest.TestCase):
    version = "1.11.3.7801"

    def apk(self, directory, arch, abis=None):
        path = directory / release.package_name(self.version, arch)
        with zipfile.ZipFile(path, "w") as apk:
            apk.writestr("AndroidManifest.xml", b"manifest")
            apk.writestr("classes.dex", b"dex")
            for abi in abis or [release.ABIS[arch]]:
                apk.writestr(f"lib/{abi}/libengine.so", b"engine")
        return path

    def test_public_manifest_keeps_device_architecture_hashes_and_sizes(self):
        with tempfile.TemporaryDirectory() as home:
            directory = Path(home)
            paths = [self.apk(directory, arch) for arch in release.ABIS]
            checked = []
            entries = release.installers(directory, self.version, lambda path, arch, version, tools: checked.append((path, arch, version)))
            self.assertEqual(["arm64", "arm"], [entry["arch"] for entry in entries])
            self.assertEqual(paths, [one[0] for one in checked])
            for entry, path in zip(entries, paths):
                self.assertEqual("android", entry["platform"])
                self.assertEqual("apk", entry["variant"])
                self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), entry["sha256"])
                self.assertEqual(path.stat().st_size, entry["size"])

    def test_missing_packages_and_accidental_qa_packages_stop_publication(self):
        for extra in [None, "android.apk", "android-x64.apk", "unrelated.apk"]:
            with self.subTest(extra=extra), tempfile.TemporaryDirectory() as home:
                directory = Path(home)
                self.apk(directory, "arm64")
                if extra:
                    self.apk(directory, "arm")
                    (directory / extra).write_bytes(b"unexpected")
                with self.assertRaises(release.Refusal):
                    release.installers(directory, self.version, lambda *args: None)

    def test_mislabeled_universal_and_other_native_architectures_are_rejected(self):
        for abis in [["arm64-v8a", "armeabi-v7a"], ["x86_64"], ["armeabi-v7a"]]:
            with self.subTest(abis=abis), tempfile.TemporaryDirectory() as home:
                with self.assertRaises(release.Refusal):
                    release.inspect_apk(self.apk(Path(home), "arm64", abis), "arm64")

    def test_declared_single_architecture_is_accepted(self):
        with tempfile.TemporaryDirectory() as home:
            for arch in release.ABIS:
                release.inspect_apk(self.apk(Path(home), arch), arch)

    def test_only_the_existing_release_signer_is_accepted(self):
        good = f"Verified using v2 scheme (APK Signature Scheme v2): true\nSigner #1 certificate SHA-256 digest: {release.CERTIFICATE}\n"
        release.inspect_signer(good)
        for output in ["", good.replace(release.CERTIFICATE, "a" * 64), good.replace("true", "false"), good + f"Signer #2 certificate SHA-256 digest: {release.CERTIFICATE}\n"]:
            with self.subTest(output=output), self.assertRaises(release.Refusal):
                release.inspect_signer(output)

    def test_build_tools_37_format_preserves_pin_v2_and_single_signer(self):
        good = f"Verified using v2 scheme (APK Signature Scheme v2): true\nV3.0 Signer: certificate SHA-256 digest: {release.CERTIFICATE}\n"
        release.inspect_signer(good)
        outputs = [good.replace(release.CERTIFICATE, "a" * 64), good.replace("true", "false"),
            good + good, good + f"Signer #2 certificate SHA-256 digest: {release.CERTIFICATE}\n",
            good.replace("V3.0 Signer:", "Source Stamp Signer"), good.replace("V3.0 Signer:", "Unknown Signer:")]
        for output in outputs:
            with self.subTest(output=output), self.assertRaises(release.Refusal):
                release.inspect_signer(output)

    def test_package_identity_version_and_both_launchers_are_required(self):
        good = f"package: name='org.amneziageo.android' versionCode='{release.version_code(self.version)}' versionName='{self.version}'\nlaunchable-activity: name='main'\nleanback-launchable-activity: name='main'\n"
        release.inspect_manifest(good, self.version)
        for output in [good.replace("org.amneziageo.android", "other"), good.replace("111037801", "111037800"), good.replace(self.version, "1.11.3.7800"), good.replace("leanback-launchable-activity:", "missing:")]:
            with self.subTest(output=output), self.assertRaises(release.Refusal):
                release.inspect_manifest(output, self.version)


if __name__ == "__main__":
    unittest.main()
