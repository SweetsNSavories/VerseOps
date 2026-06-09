# VerseOps API Explorer for XrmToolBox

> **Browse and execute 200+ Power Platform admin (PPAC) and BAP REST operations from inside XrmToolBox.**

[![CI](https://github.com/SweetsNSavories/VerseOps/actions/workflows/ci.yml/badge.svg)](https://github.com/SweetsNSavories/VerseOps/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/VerseOps.XrmToolBox.svg?label=Plugin%20Store)](https://www.nuget.org/packages/VerseOps.XrmToolBox)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/SweetsNSavories/VerseOps/blob/main/LICENSE)

VerseOps API Explorer is an XrmToolBox plugin that turns the full Power Platform Admin (PPAC) and Business Application Platform (BAP) REST surface into a searchable, executable explorer. Browse and execute documented operations for environments, environment groups, DLP policies, billing policies, governance, capacity, lifecycle, and tenant settings — directly from the XrmToolBox host.

![VerseOps API Explorer inside XrmToolBox](https://raw.githubusercontent.com/SweetsNSavories/VerseOps/main/docs/brand/xtb-after-fix.png)

---

## Why this plugin?

XrmToolBox has fantastic Dataverse tooling. What it doesn't have, until now, is a first-class **tenant-level Power Platform admin REST explorer**. VerseOps API Explorer fills that gap:

- **One catalog, two API surfaces** — PPAC (`api.powerplatform.com`) and BAP (`api.bap.microsoft.com`) operations live side-by-side in one tree.
- **Documented routes** — every operation links to the official Microsoft Learn reference for the underlying API.
- **No SDK glue required** — operations execute as raw REST calls, so the response panel shows you exactly what Microsoft returns.
- **Same catalog as the standalone VerseOps WPF app** — the operations are sourced from a shared `VerseOps.Api.Core` library.

---

## Install

### Option A — XrmToolBox Plugin Store (recommended)

1. Open XrmToolBox.
2. Click **Configuration → Tool Library**.
3. Search for `VerseOps`.
4. Click **Install**, then restart XrmToolBox when prompted.

The plugin will appear on the tile grid as **VerseOps API Explorer**.

### Option B — Manual install

1. Download the `.nupkg` from [nuget.org](https://www.nuget.org/packages/VerseOps.XrmToolBox).
2. Extract the `lib/net48/` folder contents to:
   `%appdata%\MscrmTools\XrmToolBox\Plugins\VerseOps\`
3. Restart XrmToolBox.

---

## Use

1. Click the **VerseOps API Explorer** tile. There's **no Dataverse connection prompt** — the plugin uses tenant-level admin APIs, not org-scoped ones.
2. On first launch, click **Sign in** in the splash dialog. The plugin opens the system browser (or falls back to device-code) and asks for delegated Power Platform admin scopes.
3. Use the search box and the operation tree on the left to find an API (e.g. type `environment` → expand → pick `List environments`).
4. Fill any parameters (the form is generated from the operation's URL placeholders), then click **Send**.
5. Inspect the **Response**, **Tree** (parsed JSON), or **Headers** tab. For long-running (HTTP 202) operations, the **Poll op** button re-GETs the `operation-location` for you.

### Required permissions

You need a tenant identity that holds one of:

- **Power Platform Administrator** (Entra role), or
- **Dynamics 365 Administrator** (Entra role), or
- **Global Administrator**.

The plugin makes silent token requests; if interactive consent is needed, your tenant's
"Users can consent to apps from verified publishers" setting will apply.

---

## What it does NOT do

This is important and intentional:

- **No telemetry.** Zero. The plugin makes no outbound calls except (a) Microsoft Entra token endpoints for sign-in and (b) the PPAC/BAP/ARM endpoints you explicitly invoke by clicking **Send**. See [`docs/network-endpoints.md`](https://github.com/SweetsNSavories/VerseOps/blob/main/docs/network-endpoints.md) for the full list. No App Insights, no analytics SDK, no callbacks.
- **No Dataverse `IOrganizationService` calls.** The plugin marks itself `INoConnectionRequired` because every operation is tenant-level and authenticated with the signed-in user's MSAL token. Your XrmToolBox connection list is unaffected.
- **No persisted secrets.** Tokens live in the OS-protected MSAL cache (DPAPI on Windows). The plugin ships no client secret and supports no client-credential / SP flow.
- **No write-by-default surprises.** Every operation displays its HTTP verb and target URL up-front. You explicitly click **Send** to execute.

---

## Operation surface (snapshot)

| Surface | Categories covered |
|---|---|
| **PPAC** (`api.powerplatform.com`) | Environments, Environment Groups, Currencies, Languages, Templates, Locations, Recommendations, Solution Checker, License Plans, ALM (Solutions, Stages, Lifecycle), Governance |
| **BAP** (`api.bap.microsoft.com`) | Environments (admin), Capacity, Backups (restore/list), Tenant Settings, DLP Policies, Billing Policies, Connectors, Roles |
| **Microsoft Graph** | Lightweight role / group lookups used in env detail flows |

The full machine-readable catalog is in [`VerseOps.Api.Core/PpacGeneratedCatalog.cs`](https://github.com/SweetsNSavories/VerseOps/blob/main/VerseOps.Api.Core/PpacGeneratedCatalog.cs) and [`VerseOps.Api.Core/ApiCatalog.cs`](https://github.com/SweetsNSavories/VerseOps/blob/main/VerseOps.Api.Core/ApiCatalog.cs).

---

## Versioning & release notes

See the [release notes](https://github.com/SweetsNSavories/VerseOps/releases) for changes per version. The plugin follows semantic versioning aligned with `xrmtoolbox-vX.Y.Z` tags. The `AssemblyVersion` is bumped on every metadata or tile-asset change so XrmToolBox invalidates its `manifest.json` cache.

---

## Links

- **Source / issues:** [github.com/SweetsNSavories/VerseOps](https://github.com/SweetsNSavories/VerseOps)
- **NuGet package:** [nuget.org/packages/VerseOps.XrmToolBox](https://www.nuget.org/packages/VerseOps.XrmToolBox)
- **Standalone WPF app:** [the main README](https://github.com/SweetsNSavories/VerseOps/blob/main/README.md)
- **Security disclosure:** [SECURITY.md](https://github.com/SweetsNSavories/VerseOps/blob/main/SECURITY.md)
- **Contributing:** [CONTRIBUTING.md](https://github.com/SweetsNSavories/VerseOps/blob/main/CONTRIBUTING.md)

---

## License & trademarks

MIT — see [LICENSE](https://github.com/SweetsNSavories/VerseOps/blob/main/LICENSE).

**VerseOps is not a Microsoft product.** It is an independent open-source project, developed and maintained by **Praveen Thonda** in a personal capacity. It is not affiliated with, endorsed by, sponsored by, or a product of Microsoft Corporation. **XrmToolBox** is the trademark of Tanguy Touzard / MscrmTools; VerseOps is an independent plugin that targets the public XrmToolBox Plugin SDK. Power Platform, Dataverse, Dynamics 365, Microsoft Entra, and related names are trademarks of Microsoft Corporation, used here solely to describe the systems VerseOps interoperates with.
