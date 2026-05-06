# SQLite Catalog Schema

Local persistence layer for VerseOps. Lives at:

```text
%LOCALAPPDATA%\VerseOps\inventory.db
```

Owned by [`SqliteCatalog`](../VerseOps.App/Inventory/Services/SqliteCatalog.cs).
DDL source of truth: [`Inventory/Sql/schema.sql`](../VerseOps.App/Inventory/Sql/schema.sql)
(copied into the build output and executed on every app start via
`EnsureCreated()` — `CREATE TABLE IF NOT EXISTS` makes it idempotent).

---

## Design notes

- **Hot cache, not a system of record.** Anything in the DB is a snapshot from
  the last successful refresh; nothing here is authoritative — re-running
  `Refresh` rebuilds everything from PPAC + BAP + Inventory API.
- **Single transaction per refresh.** `ReplaceAll(...)` opens one
  `BeginTransaction`, deletes everything in `gov_environment`, `gov_capacity`,
  `gov_tenant_capacity`, and (optionally) `gov_asset`, then re-inserts. The UI
  never observes a half-written DB.
- **Timestamps are ISO-8601 UTC strings** (`DateTime.ToString("o")`). Stored as
  `TEXT`, parsed back with `DateTimeStyles.AdjustToUniversal | AssumeUniversal`.
  No `DATETIME` type — SQLite has none.
- **Booleans are `INTEGER` 0/1.** SQLite has no `BOOL` type.
- **All capacity values are in MB on the wire** (BAP and PPAC both report MB);
  the UI converts to GB at render time. We **do not** transform on insert so
  the DB stays faithful to the source.
- **No `raw_json` is currently written.** The column exists on
  `gov_environment` for forensic dumps but the insert binds `DBNull.Value`. The
  Metadata Inspector window pulls raw JSON from the live SDK call, not the DB.
- **No FK constraints declared.** SQLite would enforce them only with
  `PRAGMA foreign_keys = ON`, which the catalog does not set. Joins are
  performed in code.

---

## Migrations

SQLite has **no `IF NOT EXISTS` for `ALTER TABLE ADD COLUMN`**. The catalog uses
the try-and-swallow pattern:

```csharp
try {
    ALTER TABLE gov_environment ADD COLUMN security_group_id TEXT;
} catch (SqliteException ex) when (ex.Message.Contains("duplicate column name")) {
    // already added on a previous run — ignore
}
```

Currently applied in [`SqliteCatalog.EnsureCreated`](../VerseOps.App/Inventory/Services/SqliteCatalog.cs#L33):

| Table | Column | Type | Added |
|---|---|---|---|
| `gov_environment` | `security_group_id` | `TEXT` | 2026-05 — for "Only my environments" Graph membership filter |
| `gov_environment` | `is_managed_environment` | `INTEGER NOT NULL DEFAULT 0` | 2026-05 — for Managed Env badge in the grid |

Both are also declared in `schema.sql` so a fresh DB gets them up-front; the
runtime ALTER only fires for users who first opened an older build.

---

## Tables

### `gov_environment`
One row per Power Platform environment in the tenant. Source: PPAC
`Environmentmanagement.Environments.GetAsync()`.

| Column | Type | Notes |
|---|---|---|
| `env_id` | `TEXT NOT NULL PRIMARY KEY` | PPAC environment GUID |
| `display_name` | `TEXT` | User-facing name (mutable in PPAC) |
| `sku` | `TEXT` | Sandbox / Production / Default / Trial / Developer / Teams |
| `region` | `TEXT` | `unitedstates`, `europe`, `india`, … |
| `provisioning_state` | `TEXT` | `Ready`, `Provisioning`, `Failed`, `Deleting`, … |
| `version` | `TEXT` | Dataverse build (`9.2.x`) — `NULL` for non-Dataverse envs |
| `instance_url` | `TEXT` | Dataverse origin (`https://{org}.crm.dynamics.com`); `NULL` if no Dataverse |
| `is_default` | `INTEGER NOT NULL DEFAULT 0` | 1 = the tenant Default Environment |
| `created_utc` | `TEXT` | ISO-8601 |
| `last_synced_utc` | `TEXT NOT NULL` | When this row was rewritten by `ReplaceAll` |
| `raw_json` | `TEXT` | Reserved — currently always `NULL` (insert binds `DBNull.Value`) |
| `security_group_id` | `TEXT` | Entra ID security group GUID restricting env access; `NULL` = open to whole tenant |
| `is_managed_environment` | `INTEGER NOT NULL DEFAULT 0` | 1 if PPAC `ProtectionLevel == Standard` |

Indexes:
- `ix_gov_environment_synced` on `(last_synced_utc)` — staleness queries

Bound by: [`EnvironmentRow`](../VerseOps.App/Inventory/Models/EnvironmentRow.cs).

---

### `gov_capacity`
Per-env storage capacity. Source: BAP
`/scopes/admin/environments?$expand=properties.capacity`.
**One row per `(env_id, capacity_type)`.**

| Column | Type | Notes |
|---|---|---|
| `env_id` | `TEXT NOT NULL` | FK (in spirit) to `gov_environment.env_id` |
| `capacity_type` | `TEXT NOT NULL` | `Database` / `File` / `Log` (other types occasionally appear; we render the three known ones) |
| `actual` | `REAL` | Current consumption, **MB** |
| `rated` | `REAL` | Allocated cap from the BAP plan, **MB** |
| `total` | `REAL` | Alias for `rated` (BAP only returns actual+rated; `total` retained for back-compat) |
| `last_synced_utc` | `TEXT NOT NULL` | ISO-8601 |
| **PK** | `(env_id, capacity_type)` | composite |

Indexes:
- `ix_gov_capacity_env` on `(env_id)` — env-row joins

Bound by: [`CapacityEntry`](../VerseOps.App/Inventory/Models/CapacityEntry.cs).
Read by [`ReadAllEnvironments`](../VerseOps.App/Inventory/Services/SqliteCatalog.cs#L240),
which divides `actual` by 1024 to populate `EnvironmentRow.DatabaseGb` /
`FileGb` / `LogGb` for the grid.

---

### `gov_tenant_capacity`
Tenant-wide capacity rollup. Source: PPAC
`Licensing.TenantCapacity.GetAsync()`.
**One row per `capacity_type`.** Powers the hero KPI tiles.

| Column | Type | Notes |
|---|---|---|
| `capacity_type` | `TEXT NOT NULL PRIMARY KEY` | `Database` / `File` / `Log` / `FinOpsDatabase` / `ApiCallCount` / … |
| `units` | `TEXT` | `MB`, `Calls`, etc. — **stored verbatim** from the SDK; no conversion |
| `max_capacity` | `REAL` | SKU ceiling |
| `total_capacity` | `REAL` | Tenant entitlement (purchased) |
| `consumed` | `REAL` | Current usage |
| `status` | `TEXT` | Optional health hint from PPAC |
| `last_synced_utc` | `TEXT NOT NULL` | ISO-8601 |

No additional indexes (the table has < 10 rows on any tenant).

Bound by: [`TenantCapacityEntry`](../VerseOps.App/Inventory/Models/TenantCapacityEntry.cs).

---

### `gov_asset`
Tenant-wide asset catalog (every app, flow, and agent in every env). Source:
Inventory API
`POST https://api.powerplatform.com/resourcequery/resources/query?api-version=2024-10-01`.
**One row per `(asset_type, asset_id)`.**

| Column | Type | Notes |
|---|---|---|
| `asset_type` | `TEXT NOT NULL` | Suffix only — `microsoft.<vendor>/` is stripped on insert. Values: `canvasapps`, `modeldrivenapps`, `codeapps`, `cloudflows`, `agentflows`, `agents` |
| `asset_id` | `TEXT NOT NULL` | Resource GUID. Same value as Dataverse `solutioncomponent.objectid` for canvas/model-driven/agent — that's how the env-detail expander joins assets back to solutions |
| `env_id` | `TEXT` | PPAC env GUID; `NULL` for the rare tenant-scoped asset |
| `display_name` | `TEXT` | |
| `owner_id` | `TEXT` | Entra object id (user or service principal); `GraphLicenseClient.UserLabelsById` resolves it to a name in the UI |
| `created_by` | `TEXT` | Entra object id of original creator |
| `region` | `TEXT` | Lower-case Azure region |
| `created_utc` | `TEXT` | ISO-8601 |
| `modified_utc` | `TEXT` | ISO-8601 |
| `is_quarantined` | `INTEGER` | 0/1/`NULL` |
| `last_synced_utc` | `TEXT NOT NULL` | ISO-8601 |
| **PK** | `(asset_type, asset_id)` | composite |

Indexes:
- `ix_gov_asset_env` on `(env_id)` — per-env grouping for the expander
- `ix_gov_asset_type` on `(asset_type)` — type-sliced KPI counts

Insert mode: `INSERT OR REPLACE` (vs `INSERT` for the other tables) because the
Inventory API rarely returns the same asset twice across pages but we want the
upsert to be silent if it ever does.

Bound by: [`AssetRow`](../VerseOps.App/Inventory/Models/AssetRow.cs).

---

## Read API surface

All on [`SqliteCatalog`](../VerseOps.App/Inventory/Services/SqliteCatalog.cs):

| Method | Returns | Used by |
|---|---|---|
| `EnsureCreated()` | `void` | App startup — creates DB and runs idempotent migrations |
| `ReplaceAll(envs, capacities, tenantCapacities?, assets?)` | `void` | Single-transaction refresh writer |
| `ReadAllEnvironments()` | `IReadOnlyList<EnvironmentRow>` | Grid hydrate at startup; joins `gov_capacity.actual` → `EnvironmentRow.{DatabaseGb,FileGb,LogGb}` |
| `ReadAllTenantCapacity()` | `IReadOnlyList<TenantCapacityEntry>` | Hero tiles + Licenses drawer |
| `ReadAllAssets()` | `IReadOnlyList<AssetRow>` | Asset KPI counts + per-env expander grids |
| `LastRefreshedUtc()` | `DateTime?` | Status bar "Last refreshed: …" indicator |
| `DatabasePath` | `string` | Surfaced in the toolbar "Open data folder" menu |

---

## Things this schema deliberately does NOT store

- **Per-env Dataverse drill-down data** (solutions, Power Pages sites,
  systemusers). Loaded on-demand by
  [`DataverseEnvClient`](../VerseOps.App/Inventory/Services/DataverseEnvClient.cs)
  when an env row is expanded; held in memory on the `EnvironmentRow` only.
- **Microsoft Graph license map.** Cached in-process by
  [`GraphLicenseClient`](../VerseOps.App/Inventory/Services/GraphLicenseClient.cs)
  for the session; not persisted (license state changes too often to be worth
  caching to disk).
- **Security group membership results.** Recomputed each session.
- **HTTP traces.** Written separately to
  `%LOCALAPPDATA%\VerseOps\http-trace.log` by `HttpDiagnosticsHandler`, not the
  catalog DB.
- **Auth tokens.** MSAL keeps its own encrypted cache; we never touch it.
