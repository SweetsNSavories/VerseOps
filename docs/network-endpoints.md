# Network endpoints & OAuth scopes

VerseOps makes outbound HTTPS calls to the Microsoft endpoints listed below — and only
to those endpoints. There is no telemetry, no analytics, no auto-update, and no
third-party SaaS dependency. Allow-list these hosts in any environment that restricts
egress.

## Endpoints

| Host | Purpose | Auth | Source file |
|------|---------|------|-------------|
| `login.microsoftonline.com` | MSAL token acquisition (delegated user flow) | OAuth 2.0 / OpenID Connect | `Auth/AuthService.cs` |
| `api.powerplatform.com` | PPAC environment + solution + asset enumeration | Bearer (delegated) | `Inventory/Services/PpacInventoryService.cs` |
| `api.bap.microsoft.com` | BAP capacity API (DB / File / Log GB, FinOps) | Bearer (delegated) | `Inventory/Services/BapCapacityClient.cs` |
| `<org>.crm.dynamics.com` (per env) / `<org>.crm<region>.dynamics.com` | Dataverse Web API — users, roles, asset detail | Bearer (delegated, per-env audience) | `Inventory/Services/DataverseEnvClient.cs` |
| `graph.microsoft.com` | License SKU + security-group display-name resolution | Bearer (delegated) | `Inventory/Services/GraphLicenseClient.cs` |

The exact Dataverse hostnames depend on your tenant's environments and are discovered at
runtime from the PPAC `/environments` response — they are never hard-coded.

## OAuth scopes (delegated)

VerseOps requests the **minimum scopes** required for read access. None are
admin-consent-only beyond the standard "tenant administrator can read tenant data" grants.

| Scope | Used for |
|-------|----------|
| `https://api.powerplatform.com/.default` | PPAC inventory |
| `https://service.powerapps.com/.default` | BAP capacity |
| `https://<org>.crm.dynamics.com/.default` | Per-environment Dataverse Web API |
| `https://graph.microsoft.com/User.Read` | Sign-in identity |
| `https://graph.microsoft.com/Group.Read.All` | Resolve security-group display names |
| `https://graph.microsoft.com/Directory.Read.All` *(optional)* | Bulk `directoryObjects/getByIds` for SG names; falls back to per-id `/groups/{id}` if the user does not hold this scope |

The signed-in user is expected to hold the **Power Platform Administrator** or
**Dynamics 365 Administrator** Entra role at the tenant level, plus
**System Administrator** on each Dataverse environment they want to enumerate.

## Public-client identity

VerseOps signs in as a public client using:

* **Default client ID:** `04b07795-8ddb-461a-bbee-02f9e1bf7b46` (Azure CLI's well-known
  public client). This is identical to the identity used by `az login` and requires no
  app registration on your tenant.
* **Redirect URI:** `http://localhost` (loopback, MSAL handles the bind).
* **Tenant:** `common` (multi-tenant) by default. Override with `--tenant <guid>` if you
  need to lock to a specific tenant.

You may register your own public client app and use its client ID instead — see
`Auth/AuthService.cs`. VerseOps **never uses** a confidential client / client secret /
certificate flow.

## What does NOT leave your machine

* No telemetry to the maintainer or any third party.
* No crash dumps uploaded anywhere — `%LOCALAPPDATA%\VerseOps\startup-error.log` is
  written locally only.
* No auto-update check; releases are pulled by the user manually from GitHub.
* No background sync; the app refreshes only when the user clicks Refresh.

## Local data

| Path | Contents | Sensitive? |
|------|----------|------------|
| `%LOCALAPPDATA%\VerseOps\verseops.db` | Cached PPAC / BAP / Dataverse / Graph responses | Tenant inventory metadata (env IDs, app IDs, user UPNs) |
| `%LOCALAPPDATA%\VerseOps\theme.txt` | UI theme preference | No |
| `%LOCALAPPDATA%\VerseOps\startup-error.log` | .NET exception chains from unhandled errors | Exception messages may contain correlation IDs and HTTP status codes; never tokens (auth headers are redacted to first 8 chars) |
| MSAL token cache (per `Microsoft.Identity.Client` defaults) | Refresh tokens for the signed-in user | Yes — OS-protected via DPAPI / Keychain equivalent |

To wipe everything, close the app and delete `%LOCALAPPDATA%\VerseOps\`.
