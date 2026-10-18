# Code signing policy

> **Status, October 2026:** signing is not in place yet. The code signing certificate is being issued;
> until then Windows releases are unsigned and SmartScreen warns about an unknown publisher. The policy
> below describes how signing will work once the certificate is issued.

Windows binaries and installers of AmneziaGeo are code signed through SignPath.io. Signing happens inside
the release pipeline; no signing key ever exists on a developer machine. Releases up to 1.5.x are unsigned.

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).

## What is signed

Everything the Windows part of [`.github/workflows/release.yml`](.github/workflows/release.yml) produces:

- the installer bundle `AmneziaGeo-<version>-win-<arch>[-fdd].exe` and its Burn engine;
- the MSI the bundle carries;
- the AmneziaGeo binaries inside that MSI - the agent service, the tray and the UI.

Third-party components shipped inside the installer keep the signature their own vendor gave them.

Linux packages and the Android APK are outside this policy: the APK carries its own upload key, the deb
packages are unsigned.

## Roles

| Role | Who |
|---|---|
| Author - may change the source | bor, repository owner |
| Reviewer - reviews outside contributions | bor |
| Approver - may authorize a signing request | bor |

The GitHub account and the SignPath account are both protected with multi-factor authentication.

## What may be signed

A signing request is accepted only when all of this holds:

- it comes from `.github/workflows/release.yml` in this repository;
- the workflow runs on a `refs/tags/v*` tag;
- the artifact was built by that same workflow run, on GitHub-hosted runners.

Origin verification on the SignPath side enforces these conditions; nothing else can submit an artifact.

## Reporting a problem

A signed build that behaves unexpectedly, or a signature that cannot be explained: open an issue at
https://github.com/bor-project/amneziageo/issues.
