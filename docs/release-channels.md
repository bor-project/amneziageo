# Release channels

| Branch | Channel | Tag example |
| --- | --- | --- |
| `master` | stable | `v1.12.0.0` |
| `beta` | beta | `v1.12.0.1-beta.1` |
| `test` | test | `v1.12.0.2-test.1` |
| `dev` | development | No release |

Builds and publication run only after a `v*` tag push or a manual release workflow run. Ordinary branch pushes do not build or publish packages. The separate CI workflow remains available manually.

A release tag must point to the current head of its channel branch. Every build and manifest uses that exact commit and the numeric version from the tag. A stable version ends in `.0`. Increase the fourth field for another development or test version; increase the third field and reset the fourth to zero for a stable release. Do not reuse a numeric version for another release tag or channel.

For a manual release, select `master`, `beta` or `test` in the workflow and supply the complete tag in the `tag` input. The tag must match the selected channel. An existing tag must point to the selected commit. When the tag does not exist, publication creates it at the validated commit after the builds succeed. An existing published release is not replaced.

Promote reviewed changes between branches explicitly before tagging. Development changes remain in `dev` until promoted. Updating a release branch alone does not publish a release.

Stable releases become GitHub `latest`. Beta and test releases are prereleases and do not replace `latest`. Signing, package architectures and update manifests remain part of the release workflow.
