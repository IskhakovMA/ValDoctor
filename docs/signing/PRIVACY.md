# ValDoctor privacy policy

> **DRAFT.** Describes ValDoctor 0.3.0 exactly as implemented in this repository. Update it whenever network
> behaviour changes.

**Summary:** ValDoctor has no telemetry, no accounts and no servers of its own. It never reads or sends Riot
account data, passwords, tokens or cookies. It contacts only the systems listed below, and only to check
connectivity or to look for a newer version.

## Network connections

| When | Destination | What is sent | Why |
| --- | --- | --- | --- |
| You press «Проверить подключение» (and again before/after a fix) | System DNS resolver of your computer | Lookups of the 6 Riot host names below | Find the addresses Windows uses |
| Same | The addresses of `auth.riotgames.com`, `account.riotgames.com`, `clientconfig.rpg.riotgames.com`, `entitlements.auth.riotgames.com`, `data.riotgames.com`, `sieve.services.riotcdn.net` (port 443) | TLS handshake for that host name and one anonymous `GET /` with `User-Agent: ValDoctor/<version> (connectivity check)`; no cookies, no credentials | Check DNS → TCP → TLS → HTTPS reachability |
| Only if a Riot service is unreachable | DNS-over-HTTPS: `cloudflare-dns.com` / `1.1.1.1` / `1.0.0.1`, `dns.google` / `8.8.8.8` / `8.8.4.4`, `dns.quad9.net` / `9.9.9.9`, `unfiltered.adguard-dns.com` | DNS queries (A/AAAA) for the affected Riot host name | Find verified alternative addresses |
| When the window opens, at most once per 24 hours | `api.github.com` (`/repos/IskhakovMA/riotfix/releases`) | One anonymous HTTPS GET with `User-Agent: ValDoctor/<version>` | Show a notice if a newer ValDoctor exists |
| Only when you click a link («Скачать», «Исходный код») | `github.com` in your web browser | Normal browser request | Open the release or source page |

As with any internet connection, these services see your IP address. ValDoctor sends no other data and no
identifiers. Nothing is downloaded or executed automatically.

## Data stored on your computer

| Location | Content |
| --- | --- |
| `%LocalAppData%\ValDoctor\logs\valdoctor-YYYYMMDD.log` (last 10 files) | Riot host names, Riot server addresses, error categories, timings. Your user profile path is removed from every line. |
| `%LocalAppData%\ValDoctor\update-state.json` | Time of the last update check, latest version seen, version whose notice you dismissed |
| `C:\Windows\System32\drivers\etc\hosts` and `hosts.valdoctor-*.bak` | Only after you confirm a fix and approve the Windows UAC prompt: ValDoctor's own lines and backups |

ValDoctor reads, but never changes, files left by its previous name RiotFix (`%LocalAppData%\RiotFix`,
`hosts.riotfix-*.bak`). The technical report («Подробности» → «Скопировать отчёт») is copied to the clipboard only
when you click the button; it contains the same kind of data as the logs.

## Your choices

- Connectivity checks run only when you start them.
- The hosts file is changed only after your explicit confirmation and a Windows UAC prompt; «Отменить изменения»
  removes ValDoctor's (and RiotFix's) entries.
- **Gap (not implemented):** the daily update check cannot currently be turned off in the app. SignPath's terms
  ask for an option to disable functions that send data to other systems; see `SIGNPATH_APPLICATION.md`.

## Contact

[GitHub Issues](https://github.com/IskhakovMA/riotfix/issues) or, for security matters,
[Security Advisories](https://github.com/IskhakovMA/riotfix/security/advisories/new).
