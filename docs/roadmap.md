# VerseOps Roadmap

Concrete deltas between what we ship today and where we want to be, with
effort estimates, source-of-truth APIs, and required identity scopes.

---

## Legend

- **Effort** — calendar time for one engineer with the codebase loaded.
  - **S** = ≤ 2 hours, no new infra.
  - **M** = ½ – 2 days, one new HTTP client method, possibly a new SQLite column.
  - **L** = 1–2 sprints, new service, new auth scope, new persistence.
  - **XL** = real project, new domain model, design doc warranted.
- **Lazy?** — yes if it should fire on row expand / tile click only; no if it should run during `RefreshAsync`.
- **Cost** — extra HTTP calls per refresh or per row expand.

---

## Phase 1 — cheap wins (target: 1 day total)

### 1.1 ALM badge on every app/flow row
- **Effort:** S
- **Lazy?** N (data already on the row)
- **Cost:** 0 extra HTTP
- **Source:** existing `AssetRow.SolutionName` + new `IsManagedSolution` bit on the per-env solution load
- **Display:** XAML badge — gray "Unmanaged" if `SolutionName == null`, brand "Solution" if set, brand-on-dark "Managed" if the source solution's `ismanaged == true`.
- **Files:** `Inventory/Models/AssetRow.cs` (add `AlmKind` enum), `Inventory/Services/DataverseEnvClient.cs` (read `ismanaged` from the solutions select), `Inventory/InventoryView.xaml` (add column to both Apps + Flows grids, both nested + flat layouts).

### 1.2 Status badge for model-driven apps
- **Effort:** S
- **Lazy?** Y (per-env)
- **Cost:** +1 GET per env on first row expand
- **Source:** `GET {origin}/api/data/v9.2/appmodules?$select=appmoduleid,uniquename,name,statecode,statuscode`
- **statecode:** 0 = Active, 1 = Inactive
- **Join:** `appmoduleid` ↔ `AssetRow.AssetId` for `assetType == "modeldrivenapps"`
- **Display:** green "Active", gray "Inactive" badge.
- **Files:** `DataverseEnvClient.cs` (new `LoadAppModuleStatesAsync(envId)`), `Inventory/Models/AssetRow.cs` (add `Status` INPC string), `Inventory/InventoryView.xaml` (add Status column).

### 1.3 Status column for cloud flows
- **Effort:** S
- **Lazy?** Y (per-env)
- **Cost:** +1 paged GET per env on first row expand
- **Source:** `GET {origin}/api/data/v9.2/workflows?$select=workflowid,name,statecode,statuscode,modifiedon&$filter=category eq 5`
- **statuscode mapping:**
  - 1 = Draft (gray)
  - 2 = Activated (green)
  - 3 = Suspended (amber)
  - 4 = Failed (red)
- **Join:** `workflowid` ↔ `AssetRow.AssetId` for `assetType in ("cloudflows","agentflows","m365agentflows")`
- **Files:** `DataverseEnvClient.cs` (new `LoadFlowStatesAsync(envId)`), reuse `AssetRow.Status` from 1.2, `Inventory/InventoryView.xaml` (Status column on both Flows grids).
- **Limitation:** `category eq 5` covers cloud flows. Agent flows + M365 agent flows live in different category codes — verify against a real env before shipping.

---

## Phase 2 — canvas app metadata (target: 1 sprint)

### 2.1 UI badge for canvas apps (Tablet / Phone)
- **Effort:** M
- **Lazy?** Y (per-env)
- **Cost:** +1 GET per env on first row expand
- **Source:** `GET https://api.powerapps.com/providers/Microsoft.PowerApps/apps?api-version=2016-11-01&$filter=environment eq '{envId}'`
  - Read `properties.appOpenProtocol` or `properties.passportEnabled` and the layout from `properties.webPackages` to derive Tablet vs Phone.
- **Audience:** `https://service.powerapps.com/.default` (already wired)
- **Join:** `name` (app GUID) ↔ `AssetRow.AssetId` for `assetType == "canvasapps"`
- **Files:** new `PowerAppsClient.cs`, extend `AssetRow` with `UiFormFactor` (`"Tablet"`, `"Phone"`, `"Responsive"`).

### 2.2 Status for canvas apps
- **Effort:** S (piggy-backs on 2.1's call)
- **Source:** same response — `properties.lifeCycleId` ("Draft" / "Published")
- **Cost:** 0 extra (folded into 2.1)

### 2.3 Versioning history
- **Effort:** M
- **Source:** `GET .../apps/{name}/versions?api-version=2016-11-01`
- **UI:** column showing version count + tooltip with last 5 versions.
- **Cost:** +1 per app you click into (lazy on Inspect, not on row expand).

---

## Phase 3 — premium connector detection (target: 1 sprint)

### 3.1 Premium ✓ on canvas apps + flows
- **Effort:** L
- **Lazy?** Y (per-env, after 2.1 already loaded the per-env app catalog)
- **Cost:** +1 GET per app for connection-references (or batched via per-env `connections` query)
- **Source:**
  - Per-app connectors: `GET .../apps/{name}/connections?api-version=2016-11-01` returns the `apiName` of every connector the app uses.
  - Premium catalog: maintain a static list (versioned in repo) keyed by connector `apiName`. The Microsoft-published list of premium connectors is at <https://learn.microsoft.com/en-us/connectors/connector-reference/connector-reference-premium-connectors> — scrape on build.
- **Display:** ✓ if any connector in the app's references is in the premium set.

### 3.2 Premium connector inventory drawer
- **Effort:** M (after 3.1)
- **Drawer:** "Premium connectors used in this tenant" — group by connector, show count of apps + flows using each.

---

## Phase 4 — DLP compliance (real project)

### 4.1 Per-env DLP policy view
- **Effort:** L
- **Source:** `GET https://api.bap.microsoft.com/providers/PowerPlatform.Governance/v2/policies?api-version=2018-01-01` + `GET .../v2/policies/{policyId}` for the connector lists.
- **Display:** new "DLP Policies" sub-grid in the env expander.

### 4.2 DLP compliance badge per app/flow
- **Effort:** XL
- **Why XL:** for each app, you must:
  1. Resolve which DLP policy applies to that env (env group → policies, with environment exclusions).
  2. Inspect every connector the app/flow uses (Phase 3.1).
  3. Classify each connector against the policy's Business / Non-business / Blocked groups.
  4. Compute Compliant / Non-compliant per app.
- **Display:** `COMPLIANT` (green) / `NON-COMPLIANT` (red) badge per row, drillable to "which connectors broke the rule".

---

## Phase 5 — deeper PPAC parity

### 5.1 Environment groups
- **Effort:** M
- **Source:** PPAC `EnvironmentGroups.GetAsync()`
- **Display:** new column on env grid + filter; group-level details panel.

### 5.2 Billing policies (PAYG)
- **Effort:** M
- **Source:** PPAC `BillingPolicies.GetAsync()`
- **Display:** drawer showing PAYG policy + which envs are attached.

### 5.3 Governance enforcement events
- **Effort:** L
- **Source:** PPAC `Governance.SharingPolicies.GetAsync()` + `EnvironmentRoutingPolicies` + (where available) audit events.

### 5.4 Per-env connection inventory
- **Effort:** M
- **Source:** BAP `/scopes/admin/environments/{id}/connections?api-version=2020-10-01`
- **Display:** new sub-grid in the env expander.

---

## Phase 6 — usage / runs / sessions

### 6.1 Per-env Dataverse flow runs
- **Effort:** M
- **Source:** `GET {origin}/api/data/v9.2/workflowbinaries` + `processsessions?$filter=...&$top=200`
- **Display:** Usage tab per env, last 30 days runs.

### 6.2 Copilot Studio bot sessions
- **Effort:** M
- **Source:** `https://powerva.microsoft.com/api/botmanagement/.../sessions` (audience: `https://api.powerva.microsoft.com/.default`).
- **Display:** Usage tab — bot session count per env per day.

### 6.3 Tenant-wide usage rollup tile
- **Effort:** S after 6.1 + 6.2
- **Display:** new hero tile.

---

## Phase 7 — multi-tenant + collaboration

### 7.1 Multi-tenant switcher
- **Effort:** L
- **Persistence:** per-tenant SQLite files in `%LOCALAPPDATA%\VerseOps\tenants\{tenantId}.db`.
- **UI:** tenant dropdown in the title bar; cached per tenant.

### 7.2 Export to CSV / Excel
- **Effort:** S per export point.
- **Targets:** env grid, asset grids, license drawer, FinOps overage list.

### 7.3 PowerShell module wrapping the same SQLite cache
- **Effort:** L
- **Why:** automation. CI / cron jobs can re-use the cache without spinning up the WPF app.

---

## Phase 8 — admin actions (gated, audited)

We currently ship exactly one mutation: **Revoke admin on a Dataverse user**.
Each new mutation needs:

1. A confirm dialog with the exact mutation text.
2. An entry in `gov_audit` (new table) capturing user / env / target / action / outcome.
3. A "rollback" hint where applicable.

Candidates (in priority order):

- **Disable a flow** (`PATCH workflows({id}) {"statecode":0,"statuscode":1}`)
- **Quarantine a canvas app** (BAP `/apps/{id}/quarantine`)
- **Add a Dataverse application user** (`POST systemusers` with app id + business unit + role)
- **Apply a security role to a user** (`POST systemuserroles_association/$ref`)
- **Bulk-enable Managed Environments** (BAP `/scopes/admin/environments/{id}/governanceConfiguration`)
- **Set / change an env's security group** (BAP env settings PATCH)

---

## Phase 9 — non-Inventory areas of the tool

VerseOps started life as a route explorer (the API Explorer tab). We deliberately
collapsed that into the Inventory dashboard for the first 1.0, but the route
explorer surface still has value:

- **Route coverage matrix** — re-run `Sweep PPAC` against your tenant, get a CSV of which routes work for which identity types.
- **JWT decoder** — already present, deserves more visibility.
- **Surface toggle (BAP / PPAC)** — the original raison d'être; should resurface as a settings tab or developer mode.

---

## Cross-cutting non-features

These aren't user-facing but they're on the list:

- **Per-tenant SQLite migration** — `gov_*` tables get version-stamped, app upgrades migrate forward, never break a refresh.
- **Background refresh** — opt-in scheduled refresh (every N minutes) so the cache is warm when you open the app.
- **HTTP trace log rotation** — currently append-only, no cap.
- **Token cache encryption** — MSAL already encrypts on Windows; document the location and the rotation story.
- **Telemetry opt-in** — anonymous "which routes returned 401 vs 403" telemetry would be hugely valuable for the community. Strictly opt-in.

---

## Stuff we will *not* build

- A web UI. The whole point is local-first and zero render latency.
- A multi-cloud / multi-vendor abstraction. Power Platform is hard enough.
- A fully read-write management surface. PPAC and PAC CLI exist; we are an inventory + diagnostics tool with surgical mutations.
