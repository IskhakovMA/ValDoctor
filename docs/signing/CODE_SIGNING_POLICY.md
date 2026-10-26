# Code signing policy

> **DRAFT — not in effect.** ValDoctor releases are currently **not signed**. This page becomes valid only after
> SignPath Foundation accepts the project. Items marked **TODO (maintainer)** must be completed by the maintainer
> before publishing this page; they are intentionally left open instead of guessed.

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## What is signed

- `ValDoctor.exe` — the Windows x64 self-contained single-file application published on
  [GitHub Releases](https://github.com/IskhakovMA/riotfix/releases).
- Nothing else: ValDoctor has no installer, no drivers and no services.

Every signed file is built by GitHub Actions on GitHub-hosted Windows runners from this public repository
(`.github/workflows/release.yml` → `.github/workflows/build.yml`), only for a version tag `v*`, and only after
the build, all automated tests, the GUI smoke test and the elevated-helper checks have passed.
The signed file carries ProductName `ValDoctor` and the ProductVersion of the release tag.

## Team roles

| Role | Who | Responsibility |
| --- | --- | --- |
| Author / committer | **TODO (maintainer):** confirm, e.g. `@IskhakovMA` | May change the source code and build scripts |
| Reviewer | **TODO (maintainer)** | Reviews every change from contributors who are not committers |
| Approver | **TODO (maintainer)** | Manually approves every signing request in SignPath |

The repository currently has two collaborators: `IskhakovMA` and `FIXAzz`. **TODO (maintainer):** decide which
of them hold which role (or remove access that is not needed) before applying.

All team members use multi-factor authentication for GitHub and SignPath. **TODO (maintainer):** enable and verify
2FA for every account listed above — this is a SignPath requirement and is not verified by this document.

## Signing process

1. A maintainer pushes a version tag `vX.Y.Z` on a commit of `main` that passed CI and CodeQL.
2. The Release workflow builds, tests and smoke-tests `ValDoctor.exe` and uploads it as a workflow artifact.
3. The workflow submits the artifact to SignPath (origin verified by the SignPath GitHub integration).
4. An approver reviews the request in SignPath and approves it manually. Unapproved requests are never signed.
5. Only the signed file and its SHA-256 checksum are attached to the GitHub Release.

## Privacy

See [PRIVACY.md](PRIVACY.md). ValDoctor has no telemetry and no own servers.

## Reporting misuse

Report suspected misuse of a signed ValDoctor binary or of the certificate via
[GitHub Security Advisories](https://github.com/IskhakovMA/riotfix/security/advisories/new).
