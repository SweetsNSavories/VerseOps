# VerseOps threat model

One-page summary of what the app is, what it touches, and what protections are in place.
For verification of the supply chain (signing, deterministic build, SBOM) see
[../SIGNING.md](../SIGNING.md) and [../SECURITY.md](../SECURITY.md).

## What VerseOps is

A single Windows desktop executable (`VerseOps.App.exe`, .NET 10 WPF). It runs **as the
signed-in user** and acts on **Microsoft service APIs only**. There is no backend, no
hosted service, no telemetry endpoint.

## What it talks to

| Endpoint | Purpose | Auth |
|---|---|---|
| `login.microsoftonline.com` | MSAL token acquisition | OAuth 2.0 auth-code + PKCE (public client) or client credentials (app-only mode, optional) |
| `api.powerplatform.com` | Power Platform admin API (environments, governance, etc.) | Delegated bearer |
| `api.bap.microsoft.com` | Capacity, lifecycle ops fallback, SP registration | Delegated bearer |
| `*.dynamics.com` / `*.crm*.dynamics.com` | Per-environment Dataverse Web API (users, roles, solutions, apps, flows, agents) | Delegated bearer |
| `graph.microsoft.com` | Resolve security-group display names + license SKUs | Delegated bearer |

Full list in [network-endpoints.md](network-endpoints.md).

## What it stores locally

| Path | Contents | Lifetime |
|---|---|---|
| `%LOCALAPPDATA%\VerseOps\verseops.db` | SQLite inventory cache (environment list, capacity, asset rows). Tenant-scoped; never aggregated across tenants. | Until you delete it. |
| `%LOCALAPPDATA%\VerseOps\appsettings.json` | **Optional.** Tenant id + public client id + app-only client id. Created only when you click **Save defaults**. | Until you delete it. |
| `%LOCALAPPDATA%\VerseOps\startup-error.log` | Crash dump — .NET exception chain. Does not contain tokens or full request bodies. | Overwritten on next crash. |
| MSAL token cache (Windows OS-protected store) | Bearer tokens + refresh tokens for the signed-in account. | Managed by MSAL; survives restarts; respects token lifetime + refresh policy. |

To wipe everything: close the app and `Remove-Item -Recurse "$env:LOCALAPPDATA\VerseOps"`.

## What it never stores

* No client secrets. The App-only mode field in the API Explorer holds the secret
  **in memory only** for the duration of the process.
* No request bodies, response bodies, or per-call logs to disk.
* No usage telemetry, no remote logging, no auto-update beacon.

## What it never does

* No outbound network calls beyond the Microsoft endpoints above.
* No writes to your tenant by default. The inventory load is read-only. Mutations
  (lifecycle ops, SP registration) are explicit, button-driven, and prompt for
  confirmation before the call.
* No elevation. Does not request admin rights. Does not modify registry.

## Trust boundaries

| Boundary | What crosses | Mitigation |
|---|---|---|
| **User → app** | Your Entra credentials (via system browser, not the app's UI) | MSAL public-client auth-code + PKCE. The app never sees your password. |
| **App → Microsoft APIs** | Bearer tokens, request paths | TLS, bearer redacted to first 8 chars in any diagnostic log. |
| **App → disk** | SQLite cache, optional settings JSON | Plain files under `%LOCALAPPDATA%`. No encryption — treat the path as `Confidential` if your tenant inventory is sensitive (full-disk encryption recommended). |
| **App → another app** | None | No IPC surface (no named pipe, no listener, no URL handler). |

## Customer-built copies (recommended deployment)

Because VerseOps is published as source, the safest deployment for a regulated tenant is:

1. Fork the repo at a verified commit.
2. Audit `Auth/`, `Inventory/Services/`, and `Explorer/` (the only files that make
   network calls).
3. Build from source with **your** code-signing certificate.
4. Distribute the signed `.exe` via your usual channel (Intune, file share, etc.).

Your signature, your tenant, your audit trail. The published Microsoft-employee builds in
GitHub Releases are convenience artifacts only — they carry the author's signature, not
yours.

## Out of scope

* Hardening against a malicious user with physical access to a logged-in machine.
* Hardening against a compromised MSAL token cache (use Windows Hello / Hello for Business
  for cache protection).
* Defending against malicious tenant administrators — if you sign in with broad admin
  rights, the app sees what you see.
