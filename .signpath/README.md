# Code signing with SignPath

AmneziaGeo is applying for free code signing from the [SignPath Foundation](https://signpath.org/). This
directory holds the artifact configurations the signing pipeline will use. They are drafts: nothing here
has been exercised against a real SignPath project yet, and the open questions at the bottom must be
settled with a `test-signing` run before the first signed release.

Nothing in this directory is read by the build. SignPath stores the configuration on its side; these files
are the reviewable source of truth for what we asked it to do.

## Why three configurations and not one

SignPath cannot open a WiX Burn bundle. Its artifact-configuration schema has composite elements for MSI,
CAB, ZIP and the various package formats, but none for a Burn bundle or bootstrapper, and `<pe-file>` is
not composite, so there is no way to descend from the bundle stub into the engine, the UX container or the
attached MSI. The bundle has to be signed from the inside out, and SignPath only ever sees what we submit
to it. Hence three submissions per matrix variant:

| Configuration | Submitted | Signs |
|---|---|---|
| [`payload.xml`](payload.xml) | before the bundle is linked | our binaries inside the MSI cabinet, the MSI itself, our binaries among the Burn payloads |
| [`burn-engine.xml`](burn-engine.xml) | after `wix burn detach` | the Burn engine |
| [`installer.xml`](installer.xml) | after `wix burn reattach` | the bundle |

Signing order matters, because every repack invalidates the hashes above it: files first, then the MSI,
then the bundle. This is the same order `build-installer.ps1` follows for a local signed build.

`installer.xml` alone would still produce a bundle Windows trusts on launch, but the installed agent
service, tray and UI would sit unsigned on disk. `payload.xml` is what fixes that, and it is the part with
the open questions.

## What the release pipeline still needs

`.github/workflows/release.yml` has a `sign` job, gated on the `SIGNPATH_ORGANIZATION_ID` repository
variable, that predates this analysis. As written it would fail if enabled:

- It passes `artifact-path`, an input the current `signpath/github-action-submit-signing-request` no
  longer has. The action now takes `github-artifact-id` and downloads the artifact from the run itself,
  which is how it verifies the artifact's origin. The separate `download-artifact` step becomes dead.
- The artifact id is produced inside the build matrix, and a matrix job cannot export a distinct output
  per leg. The signing steps therefore have to move into the `build` job, which is where they belong
  anyway: it runs on Windows, and detach/reattach needs the `wix` CLI.
- The top-level `permissions:` block lists only `contents: write`, which zeroes every scope it omits. The
  action needs `actions: read` to fetch the artifact.
- `wait-for-completion` defaults to a 600 second timeout. If `release-signing` requires manual approval,
  the job dies waiting.

That surgery is deliberately not done yet: the inputs must be checked against the action's `action.yml` at
the moment we wire it up, and it cannot be tested without a SignPath account. The job stays gated off, so
releases keep shipping unsigned in the meantime.

## Setup checklist

The slugs must match `release.yml` exactly.

1. Apply to SignPath Foundation. Requirements we already meet: an OSI-approved licence (GPL-3.0), a public
   repository, and a build that runs in a trusted CI. The `amneziawg-windows` submodule is public upstream
   and is not an obstacle.
2. Create the project `amneziageo`.
3. Organization, Trusted build systems: add GitHub.com and link it to the project.
4. Origin verification on the project: repository `https://github.com/bor-project/amneziageo`, restricted
   to `.github/workflows/release.yml` and `refs/tags/v*`.
5. Create the three artifact configurations above, with slugs `payload`, `burn-engine` and `installer`.
6. Create a submitter user, take an API token, and set the repository secret `SIGNPATH_API_TOKEN` and the
   repository variable `SIGNPATH_ORGANIZATION_ID`. The variable also gates the whole job.
7. Run the first submissions through the `test-signing` policy and read the parsed artifact tree in the
   signing-request details. That tree is the only reliable way to confirm the paths below.
8. Decide whether `release-signing` needs manual approval. Three submissions across four variants is
   twelve signing requests per release, and with `wait-for-completion` that is twelve clicks on a timer.

## Open questions, to settle with a test-signing run

- **Paths inside the MSI.** `payload.xml` matches `**/AmneziaGeo*.dll`. Whether that pattern also matches
  files at depth zero, and whether the path SignPath exposes includes the `ProgramFiles64Folder/AmneziaGeo/`
  segments, is not documented. If the pattern yields no match, the request fails (min-matches is 1), and a
  `<pe-file-set>` with both a bare and a `**/` include is the fallback. Note that file names inside the
  cabinet are short-name mangled, so matching must key off the long name.
- **MSI repacking.** SignPath must recompute `MsiFileHash` after replacing bytes in the cabinet, or
  `msiexec /f` will fail on repair. Verify on the signed MSI: install, then repair, then `msival2`.
- **Direct bundle signing.** If WiX 5 turns out to survive a signature appended to the bundle without the
  detach/reattach dance, `burn-engine.xml` and two of the steps disappear. The comment in
  `build-installer.ps1` says otherwise, from a real failure, so we assume detach/reattach is required.
- **Publisher name.** The SignPath Foundation certificate is an OV certificate issued to the Foundation,
  so the publisher Windows shows may read SignPath Foundation rather than AmneziaGeo. Confirm before the
  first signed release. OV also does not grant instant SmartScreen reputation, which still accrues with
  downloads over time.
