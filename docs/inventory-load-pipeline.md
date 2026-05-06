# Inventory Dashboard — Load Pipeline

What actually happens, end-to-end, when you start the app and click **Refresh**.
Every call below is wrapped in MSAL token acquisition for the listed scope and
flows through [`HttpDiagnosticsHandler`](../VerseOps.App/Inventory/Services/HttpDiagnosticsHandler.cs)
(append-only line log + full body dump on any non-2xx) at
`%LOCALAPPDATA%\VerseOps\http-trace.log`.

---

## Phase 0 — App start (no network)

[`MainWindow`](../VerseOps.App/MainWindow.xaml.cs) constructs `InventoryViewModel`
and calls `ReloadFromCatalog()` which reads the local SQLite file at
`%LOCALAPPDATA%\VerseOps\inventory.db`
([`SqliteCatalog`](../VerseOps.App/Inventory/Services/SqliteCatalog.cs)) and snaps
the grid to whatever the previous run cached.

**Zero HTTP. The window is interactive immediately.**

Tables read:
- `gov_environment` → env grid rows
- `gov_capacity` → per-env DB / File / Log GB
- `gov_tenant_capacity` → hero KPI tiles
- `gov_asset` → tenant-wide apps / flows / agents

---

## Phase 1 — Refresh
**Entry point:** `PpacInventoryService.RefreshAsync` in
[`PpacInventoryService.cs`](../VerseOps.App/Inventory/Services/PpacInventoryService.cs).

Four independent calls, executed sequentially. Total wire traffic on a tenant of
any size: **3 hosts, 4 audiences, ~N+3 HTTP calls** (where N = pages of assets).

### 1. PPAC env list — source of truth for environments
| | |
|---|---|
| Method | `GET` |
| Endpoint | `https://api.powerplatform.com/environmentmanagement/environments?api-version=2022-03-01-preview` |
| Scope | `https://api.powerplatform.com/.default` |
| Called via | `Microsoft.PowerPlatform.Management` SDK → `ServiceClient.Environmentmanagement.Environments.GetAsync()` (Kiota) |
| Persisted to | `gov_environment` |

Per-env fields harvested: `Id`, `DisplayName`, `Type` (Sandbox/Production/Default/Trial/Developer),
`AzureRegion`, `State`, `Version`, `Url` (Dataverse instance URL), `CreatedDateTime`,
`ProtectionLevel` (Managed Env yes/no), and `securityGroupId` from the
`AdditionalData` bag.

### 2. BAP per-env capacity — the **only** legacy call in the pipeline
| | |
|---|---|
| Method | `GET` |
| Endpoint | `https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2020-10-01&$expand=properties.capacity` |
| Scope | `https://service.powerapps.com/.default` |
| Called via | Raw `HttpClient` in [`BapCapacityClient`](../VerseOps.App/Inventory/Services/BapCapacityClient.cs) |
| Persisted to | `gov_capacity` |

PPAC has no Database / File / Log GB surface today
(`Licensing.Environments[id].Allocations` only returns AI / AppPass / PowerAutomate
currency units, not storage). BAP returns every env **with** its capacity block in
one shot.

> **Trap:** the `$expand` value MUST be `properties.capacity` (dot, not slash).
> The slash form is silently accepted and the capacity block is dropped from the
> response.

### 3. PPAC tenant-wide capacity totals
| | |
|---|---|
| Method | `GET` |
| Endpoint | `https://api.powerplatform.com/licensing/tenantCapacity` |
| Scope | `https://api.powerplatform.com/.default` (token reuse from #1) |
| Called via | `ServiceClient.Licensing.TenantCapacity.GetAsync()` |
| Persisted to | `gov_tenant_capacity` |

Tenant-wide rollup rows (one per `CapacityType`: Database / File / Log /
FinOpsDatabase / ApiCallCount / …). PPAC reports storage in MB; the UI converts
to GB. Powers the hero KPI tiles.

### 4. Power Platform Inventory API — every app / flow / agent in one query
| | |
|---|---|
| Method | `POST` |
| Endpoint | `https://api.powerplatform.com/resourcequery/resources/query?api-version=2024-10-01` |
| Scope | `https://api.powerplatform.com/.default` (token reuse) |
| Called via | Raw `HttpClient` in [`InventoryApiClient`](../VerseOps.App/Inventory/Services/InventoryApiClient.cs) |
| Persisted to | `gov_asset` |

Body is a Kusto-style filter:
```text
where type in~ (
  'microsoft.powerapps/canvasapps',
  'microsoft.powerapps/modeldrivenapps',
  'microsoft.powerapps/codeapps',
  'microsoft.powerautomate/cloudflows',
  'microsoft.powerautomate/agentflows',
  'microsoft.copilotstudio/agents'
)
```
Paged with `SkipToken` until `resultTruncated == 0`, page size 1000.
Per-page retry with exponential backoff (Inventory API is fronted by Azure
Resource Graph and throttles on burst paging). Page-1 raw response is dumped to
`%LOCALAPPDATA%\VerseOps\inventory-api-page1.json` for forensic inspection.

**Why this matters:** replaces what would otherwise be ~6 per-env round trips
(BAP `/apps`, BAP `/flows`, Dataverse `/solutions`, …) × N envs. On a 715-env
tenant that's ~4,000 round trips collapsed into ~N pages of 1,000.

---

## Phase 2 — On demand (lazy, only when you interact)

### Per-env Dataverse drill-down (when you expand an env row)
[`DataverseEnvClient.LoadAllAsync`](../VerseOps.App/Inventory/Services/DataverseEnvClient.cs)
fires three requests **in parallel** against `{instanceUrl}/api/data/v9.2/`.

- **Scope:** per-env `{instanceUrl}/.default` (different token per env)
- **Headers:** `OData-MaxVersion: 4.0`, `OData-Version: 4.0`,
  `Prefer: odata.include-annotations="OData.Community.Display.V1.FormattedValue"`
- **Failure mode:** any one sub-load can fail (e.g. `mspp_website` table missing
  on a non-Pages env) without blocking the others. Failed sub-loads return empty
  lists.

| # | Calls | Purpose |
|---|---|---|
| 1 | `GET solutions?$select=…&$expand=publisherid&$filter=isvisible eq true&$top=500` <br> `GET solutioncomponents?$filter=Microsoft.Dynamics.CRM.In(componenttype, [29,61,80,300])&$top=5000` | Buckets the env's Inventory-API assets into solutions. Component types: **29**=Workflow, **61**=ModelDrivenApp, **80**=CanvasApp, **300**=Bot/Agent. Anything not claimed lands in a synthetic "(unmatched / Default Solution)" group. |
| 2 | `GET mspp_websites?$select=…&$top=200` (fallback `GET adx_websites?…`) | Power Pages sites — modern `mspp_*` table, legacy `adx_*` fallback for old portals. 404 → empty list. |
| 3 | `GET systemusers?$select=…&$expand=systemuserroles_association($select=name)` | Active Dataverse users + security roles. |

### Membership probe (lightweight)
| | |
|---|---|
| Method | `GET` |
| Endpoint | `{instanceUrl}/api/data/v9.2/WhoAmI` |
| Used by | "Only my environments" toggle for envs **without** a security group |
| Status mapping | `200` → member, `401`/`403`/`404` → not, anything else → unknown |

### Microsoft Graph (license SKUs + group membership)
[`GraphLicenseClient`](../VerseOps.App/Inventory/Services/GraphLicenseClient.cs).
Scope: `https://graph.microsoft.com/.default`.

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `https://graph.microsoft.com/v1.0/subscribedSkus?$select=skuId,skuPartNumber` | Tenant SKU catalog → friendly names like `ENTERPRISEPACK`, `POWER_BI_PRO`. |
| `GET` | `https://graph.microsoft.com/v1.0/users?$select=id,userPrincipalName,displayName,assignedLicenses&$top=999` (paged via `@odata.nextLink`) | Per-user license assignment + builds a userId→label map for resolving asset/flow owner GUIDs. |
| `GET` | `https://graph.microsoft.com/v1.0/servicePrincipals?$select=id,displayName,appId&$top=999` | Best-effort, so SP-owned apps/flows resolve to a name instead of a GUID. 403 → silently skip. |
| `POST` | `https://graph.microsoft.com/v1.0/me/checkMemberGroups` (chunks of ≤20 ids per Graph's cap) | Strict "Only my environments" mode — returns the subset of env security-group ids the signed-in user is in. |

---

## Auth layer that wires it all
[`AuthService`](../VerseOps.App/Auth/AuthService.cs) — MSAL with **system-browser**
interactive flow (WAM broker deliberately off) for User mode, or client-credentials
for App-only. **One sign-in, multi-audience**: every service asks
`_auth.GetTokenAsync(scope)` and MSAL returns the right token from the cache for
that audience.

### Audiences in play during a single load

| # | Audience | Used by |
|---|---|---|
| 1 | `https://api.powerplatform.com/.default` | PPAC env list, tenant capacity, Inventory API |
| 2 | `https://service.powerapps.com/.default` | BAP per-env capacity |
| 3 | `https://{org}.crm.dynamics.com/.default` | Per-env Dataverse drill-down (different scope per env) |
| 4 | `https://graph.microsoft.com/.default` | Licenses + group membership |

A wrong audience returns `S2S17001` or "Unauthorized — token signature invalid"
even though the token decodes cleanly. The **Decode bearer** button in the API
Explorer tab dumps `aud` / `idtyp` / `scp` / `roles` for the last token used.

---

## Required permissions (recap)

For the dashboard to fully populate, the signed-in identity needs:

| Tier | Role / scope | Why |
|---|---|---|
| Tenant | **Power Platform Administrator** (or D365 Admin) | PPAC env list, tenant capacity, BAP `?$expand=capacity` |
| Tenant (SP only) | One-time `PUT /adminApplications/{clientId}` on BAP — done by the **Register SP** button | Without this, every admin route returns 403 even with the directory role |
| Graph | `User.Read.All` + `Directory.Read.All` | License SKU lookup + group membership |
| Per env | **System Administrator** (or System Customizer) on each env you want to drill into | Solutions / Pages / users tabs of the env detail expander |

Without per-env Dataverse rights you still get the env in the grid (PPAC sees
it) but the row-detail expander sub-lists come back empty with a `401` in the
trace log — that's the canonical "you're a tenant admin but not an env admin"
signal.
