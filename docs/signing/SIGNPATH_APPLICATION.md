# SignPath Foundation application — preparation (draft, not submitted)

Sources: [signpath.org/terms](https://signpath.org/terms), [signpath.org/apply](https://signpath.org/apply)
(form fields read on 2026-10-26), [SignPath GitHub integration](https://docs.signpath.io/trusted-build-systems/github).
Nothing here has been submitted. The maintainer submits the form personally.

## 1. Compliance assessment (honest)

| Requirement (terms) | Status for ValDoctor |
| --- | --- |
| OSI license for all components, no commercial dual licensing | ✅ MIT. Dependencies: .NET/WPF runtime (MIT) only; tests use xUnit (Apache-2.0), not shipped |
| No proprietary / closed components | ✅ None |
| No malware / PUP | ✅ No hidden behaviour; source and builds public |
| Built from source in a verifiable, automated way | ✅ GitHub Actions on GitHub-hosted Windows runners; release only from `v*` tags after tests |
| Only own binaries signed | ✅ Only `ValDoctor.exe` |
| Actively maintained | ✅ Regular commits/PRs (repository created 2026-10-26 — very young) |
| **Already released in the form to be signed** | ❌ **Not yet**: no ValDoctor release exists (latest release is RiotFix v0.2.1). Terms: *"The project must already be released in the form that should be signed."* → ValDoctor v0.3.0 must be published **unsigned** first |
| Functionality described on the download page | ✅ README + release notes (Russian). An English summary is advisable |
| **Verifiable reputation (for executables)** | ❌ **Main risk**: form field *Reputation* is mandatory; today 0 stars, 0 forks, ~8 EXE downloads in total |
| Roles: authors, reviewers, approvers; manual approval of every signing request | ⚠️ To define (single maintainer + collaborator `FIXAzz`); see CODE_SIGNING_POLICY.md |
| MFA for all team members (GitHub + SignPath) | ⚠️ Unknown — not visible to the tools used; maintainer must verify |
| "Code signing policy" page linked from home/download page with the exact attribution | ⚠️ Draft ready (`CODE_SIGNING_POLICY.md`); must be published after acceptance |
| Privacy policy if data is sent to other systems; option to disable such functions | ⚠️ Draft ready (`PRIVACY.md`). **Gap:** update check has no off switch. No installer, so "display during installation" does not apply; show the policy on the download page |
| No unannounced system configuration changes (*"must not modify the user's system configuration without proper warnings"*) | ✅ hosts is changed only after a confirmation dialog listing what happens + UAC; backups; full rollback button |
| No tools that *"circumvent security measures of their execution environment"* / exploit vulnerabilities | ⚠️ Interpretation risk, see §3. ValDoctor does not touch Windows security, Defender, UAC, Vanguard or certificates |
| Metadata: product name = project name, product version consistent | ✅ ProductName/FileDescription `ValDoctor`, FileVersion/ProductVersion from the tag (verified in CI for 0.3.0) |
| Publisher shown to users | ℹ️ "SignPath Foundation", not the maintainer |

**Verdict:** technically ValDoctor fits SignPath's model well (public MIT source, CI-only builds, deterministic
metadata). Today an application would most likely be **declined or postponed** because (1) no ValDoctor release
exists yet and (2) the mandatory *Reputation* evidence is very thin. Recommended order: publish ValDoctor 0.3.0
unsigned → collect real usage evidence (downloads, issues, community mentions) → finish roles/MFA/privacy items →
apply.

## 2. English project description (for the form)

**Project name:** ValDoctor (formerly RiotFix)

**Tagline:** Free Windows tool that checks connectivity to Riot Games services and, with the user's consent,
applies a verified hosts-file fix.

**Category:** Utilities / Networking (choose the closest option in the form).

**Description:**
ValDoctor is a small open-source Windows utility for players of VALORANT and League of Legends who cannot reach Riot
Games services. It checks six Riot endpoints the way the Riot Client does — system DNS, TCP 443, TLS with full
certificate validation for the exact host name, and an HTTPS response — and explains the result in plain language.
If a service is unreachable because of a broken or tampered DNS answer, ValDoctor looks up the official addresses of
that host via DNS-over-HTTPS resolvers (Cloudflare, Google, Quad9, AdGuard), verifies each candidate twice end-to-end
with a valid certificate, and offers to pin a verified address in the Windows hosts file. The change is made only
after explicit confirmation and a Windows UAC prompt, is backed up, and can be fully reverted with one button. The
app has no telemetry, no accounts, no background service and no auto-start. It never touches game files, Riot
Vanguard, Windows Defender or system security settings.

**Repository URL:** https://github.com/IskhakovMA/riotfix
**Homepage URL:** https://github.com/IskhakovMA/riotfix
**Download URL:** https://github.com/IskhakovMA/riotfix/releases (must mention SignPath once accepted)
**Privacy Policy URL:** link to the published `PRIVACY.md`
**Build system:** GitHub Actions
**Reputation:** **TODO (maintainer)** — only real, verifiable links (download statistics, issues from real users,
community threads, articles). Do not overstate.

## 3. Why the hosts change is not a security bypass (explanation for reviewers)

- **What it changes:** only ValDoctor's own, clearly marked block in `%SystemRoot%\System32\drivers\etc\hosts`, for a
  fixed allowlist of six Riot Games host names, pointing them to addresses that the official DNS answers for exactly
  those names and that passed full TLS certificate validation for exactly those names. No other entries are written;
  third-party lines are never removed (only commented out with explicit consent, and restored on rollback).
- **Consent:** a confirmation dialog explains the change; Windows then shows a UAC prompt for a short-lived helper
  process. Without both, nothing is written. A re-check runs before the UAC prompt so a stale result is never applied.
- **Safety of the elevated helper:** it accepts only allowlisted host names and public unicast IPv4/IPv6 addresses,
  restricts native DLL loading to System32, resolves the hosts path without user-controlled environment variables,
  writes atomically with backups and refuses to write through reparse points.
- **Reversible:** «Отменить изменения» removes exactly ValDoctor's (and legacy RiotFix's) lines and restores
  commented lines byte-for-byte.
- **What it does not do:** it does not disable or weaken TLS validation, Defender, firewall, UAC, Vanguard or any
  other protection; it adds no VPN, proxy, tunnel, driver or service; it does not hide traffic or impersonate
  servers. It only chooses, among the official addresses of a Riot host, one that actually works and presents a
  valid certificate for that host — the same result a user gets by editing hosts manually, made safer.

**Residual risk:** SignPath may still consider "working around ISP-level DNS problems" a form of circumvention.
This is their decision; the description above is factual and should be used as is, without renaming or hiding
the feature.

## 4. Steps for the maintainer

1. Merge PR #5 after review, tag `v0.3.0`, let the Release workflow publish **unsigned** ValDoctor v0.3.0.
2. Enable 2FA on every GitHub account with write access (`IskhakovMA`, `FIXAzz`) or remove unneeded access.
3. Decide roles (Author, Reviewer, Approver); if you are the only maintainer, say so in the form.
4. Decide on the update-check opt-out gap (small future PR) and publish `PRIVACY.md`.
5. Prepare honest *Reputation* evidence; wait until there is some if needed.
6. Fill the form at https://signpath.org/apply yourself (fields: project name, repository/homepage/download/privacy
   URLs, tagline, category, description, reputation, maintainer type, build system = GitHub Actions, your name and
   e-mail, discovery channel; accept the SignPath Foundation Code of Conduct).
7. After acceptance: publish the Code signing policy page, then integrate signing (see `SIGNPATH_INTEGRATION.md`).
