# VerseOps — building a tenant-wide Power Platform cockpit, from scratch, in WPF

> A field report on building a single-pane-of-glass for Power Platform admins
> that loads in seconds, works offline, and shows the things PPAC won't.

![VerseOps Inventory cockpit — 714 environments, tenant capacity rollups, KPI tiles, per-column funnel filters, all loaded from local SQLite cache in under a second](images/01-overview.png)

*Above: real production tenant — 714 environments, 4.5 TB tenant file storage, 146k assets — rendered from local cache before any network call. Per-column funnel filters (the small icon next to each column header) let you narrow on Name, SKU, Status, Region, Storage, FinOps tier, Instance URL, etc., with no round-trip.*

---

## Why build this when PPAC already exists?

If you administer a Power Platform tenant, you already know the workflow:

1. Open `admin.powerplatform.microsoft.com`.
2. Wait for it to load.
3. Click **Environments** — wait again.
4. Pick an env — wait again.
5. Click **Resources → Apps** to see one env's apps. Wait again.
6. Repeat for every env you care about. There are **716** of them in our reference tenant.

PPAC is a beautiful, modern, **per-environment** UI. It does not pretend to be a
cross-tenant inventory tool. We tried building one as a Dataverse model-driven
app backed by a Function App ingesting into custom tables — the maintenance
surface (custom tables, ribbon buttons, async plugins, Function App
deployments, schema migrations across regions) ate more time than the feature
work itself, and every filter or detail click still round-trips to the server.

We wanted something different: **an opinionated, local-first, latency-zero
admin cockpit** — a native client, no server moving parts, joins data
Microsoft never joins for you, ships in one EXE.

The result is **VerseOps**.

---

## What it is

A WPF (.NET 10) desktop app that:

- **Loads instantly** from a local SQLite catalog on launch — zero network calls before the window is interactive.
- **Refreshes the entire tenant in a handful of HTTP calls** (3 hosts, 4 audiences, ~N+3 round trips for N envs — see [`docs/inventory-load-pipeline.md`](../inventory-load-pipeline.md) for the full sequence).
- **Joins data Microsoft never joins for you** — env grid + storage GB + tenant capacity rollups + Inventory API assets + Graph licenses + per-env Dataverse drill-down, all in one screen, all live-filterable client-side.
- **Lazily fetches** the expensive bits (per-env Dataverse solutions / users / Power Pages, Graph license enrichment) only when you expand a row or click a tile.
- **Trace-logs every HTTP request** to `%LOCALAPPDATA%\VerseOps\http-trace.log` with full request/response bodies on any non-2xx — so you can debug 401s without a Fiddler proxy.

---

## The architecture

```
┌──────────────────────────────────────────────────────────────────┐
│  WPF UI (InventoryView.xaml)                                     │
│  ├─ Hero KPIs: env count, license use, tenant storage            │
│  ├─ Env grid with live token-AND search                          │
│  ├─ Per-env expander: solutions, apps, flows, agents, users      │
│  └─ Drawer: license SKUs, FinOps over-limit, security groups     │
├──────────────────────────────────────────────────────────────────┤
│  InventoryViewModel  (INotifyPropertyChanged everywhere)         │
│  ├─ ObservableCollection<EnvironmentRow>                         │
│  ├─ ObservableCollection<TenantCapacityEntry>                    │
│  ├─ ObservableCollection<AssetRow>      ← tenant-wide cache      │
│  └─ Lazy: GraphLicenseClient, DataverseEnvClient (per env)       │
├──────────────────────────────────────────────────────────────────┤
│  PpacInventoryService.RefreshAsync                               │
│  ├─ 1. PPAC env list      (api.powerplatform.com  — SDK)         │
│  ├─ 2. BAP env+capacity   (api.bap.microsoft.com  — REST)        │
│  ├─ 3. PPAC tenant cap.   (api.powerplatform.com  — SDK)         │
│  └─ 4. PPAC Inventory API (api.powerplatform.com  — REST, paged) │
├──────────────────────────────────────────────────────────────────┤
│  SqliteCatalog — %LOCALAPPDATA%\VerseOps\inventory.db            │
│  (gov_environment, gov_capacity, gov_tenant_capacity, gov_asset) │
└──────────────────────────────────────────────────────────────────┘
        │                         │                         │
        ▼                         ▼                         ▼
   Per-env Dataverse        Microsoft Graph          HttpDiagnosticsHandler
   solutions / users /      subscribedSkus /         http-trace.log
   pages / workflows        users / groups
   (lazy on row expand)     (lazy on first need)
```

### Three design decisions that paid off

**1. SQLite as the cache, not as the source of truth.**
The app mirrors `RefreshAsync` results into SQLite so the next launch is
instant. We never query SQLite for a "current" view of the tenant — that always
goes through `RefreshAsync`. This means a stale launch is fine (you can see what
it looked like yesterday) and a refresh is authoritative (it overwrites). No
sync engine, no conflict resolution, no migration headaches.

**2. One Inventory API call instead of N×6 BAP calls.**
PPAC has a Resource Graph–style endpoint
(`POST /resourcequery/resources/query?api-version=2024-10-01`) that returns
**every app, flow, and agent in the tenant in one paged response**. On a 715-env
tenant that's ~4,000 round trips collapsed into ~N pages of 1,000. This is the
single biggest reason a refresh takes seconds, not minutes.

**3. Lazy per-env Dataverse drill-down with parallel sub-loads.**
When you expand an environment row, [`DataverseEnvClient.LoadAllAsync`](../../VerseOps.App/Inventory/Services/DataverseEnvClient.cs)
fires three GETs *in parallel* against the env's Web API: solutions +
solutioncomponents (for the asset → solution mapping), Power Pages sites
(`mspp_websites` with fallback to legacy `adx_websites`), and systemusers with
roles. Any one can fail — say, an env that doesn't have Pages installed —
without blocking the others. Failed sub-loads return empty lists.

---

## What's in the box today

### Hero KPI tiles (always visible)

| Tile | Source | Data |
|---|---|---|
| **ENVIRONMENTS** | PPAC env list | count + breakdown by Sandbox/Production/Default/Trial/Developer |
| **LICENSED USERS** | Graph `users?$select=assignedLicenses` | count of users with ≥1 SKU (lazy — clicks open per-SKU drawer) |
| **TENANT STORAGE** | PPAC `Licensing.TenantCapacity` + summed BAP capacity | DB / File / Log GB used vs. tenant cap |
| **ASSETS** | Inventory API | total apps + flows + agents across all envs |
| **GOVERNANCE** | DLP policy count | (placeholder count — full DLP eval is roadmap) |

### Environment grid

Live token-AND search across name, SKU, region, version, default flag,
managed-env flag, created date, security group name + id, all storage GB
strings, instance URL, env id. Type a few tokens, see only matching envs.

| Column | Source |
|---|---|
| Display Name + URL | PPAC |
| SKU (Sandbox/Prod/…) | PPAC |
| Region | PPAC |
| Version | PPAC |
| Default ✓ | PPAC |
| Managed Env ✓ | PPAC |
| Created | PPAC |
| DB / File / Log GB | BAP `$expand=properties.capacity` |
| Security Group | Graph (resolved name from `securityGroupId`) |
| Instance URL | PPAC `properties.linkedEnvironmentMetadata.instanceUrl` |

### Per-env expander (lazy on click)

Two layouts, toggled by a radio button:

- **Grouped by solution** — every Dataverse solution shown with its Apps / Flows / Agents nested below it. "(unmatched)" for assets the catalog couldn't trace to a visible solution.
- **Flat** — paged DataGrids per asset type with a Solution column.

Plus standalone sub-grids for **Power Pages sites** and **Dataverse users with roles** (with a one-click **Revoke admin** action that fires `DELETE systemuserroles_association(systemuserid)/$ref` against that env).

### Storage & FinOps awareness

Per-env DB / File / Log GB are coloured by utilization (green / amber / red),
sortable, and totalled in the hero tile. Over-limit envs get a red bar with the
exact overage in GB and a link to the FinOps tier upgrade flow.

### Diagnostics

- **`http-trace.log`** — append-only log of every HTTP request, with full body dump on any non-2xx.
- **`inventory-api-page1.json`** — first page of the Inventory API response saved to disk for forensic inspection.
- **Inspect JSON** action on every asset — opens a modal with the raw record from the Inventory API.

---

## What's missing today

We surface every cross-env signal we've found valuable so far, but the
per-asset metadata layer is still thin. The next sprint adds:

- **App UI** badge (Tablet / Phone) for canvas apps
- **Premium connector** indicator (✓ if any premium connector is referenced)
- **ALM badge** (Solution / Unmanaged) per asset
- **DLP badge** (Compliant / Blocked) per app + flow, joined against the
  tenant DLP policy list
- **Asset Status** (Ready / Suspended / Draft) for canvas + model-driven apps
- **Flow Status** (On / Off / Suspended) for cloud flows

These are all reachable via existing per-env Dataverse calls or BAP
governance APIs we already authenticate against — it's a question of
rendering, not auth or routing.

---

## What's coming next

See [`docs/roadmap.md`](../roadmap.md) for the full breakdown with effort
estimates and source-of-truth APIs. The headline items:

### Phase 1 — cheap wins (~1 day total)
- **ALM badge** on every app/flow row — derived from existing `SolutionName`. "Unmanaged" if null, else read the solution's `ismanaged` bit.
- **Status badge for model-driven apps** — already in per-env Dataverse via `appmodule.statecode`. One extra GET per env on row expand.
- **Status column for cloud flows** — per-env Dataverse `workflows?$filter=category eq 5&$select=workflowid,statecode,statuscode`. Joins to `AssetRow.AssetId` by workflow GUID.

### Phase 2 — canvas app metadata (~1 sprint)
- **UI** (Tablet / Phone) and **Status** for canvas apps via PowerApps API: `https://api.powerapps.com/providers/Microsoft.PowerApps/apps?$filter=environment eq '{envId}'`. One batch call per env, lazy on row expand.

### Phase 3 — premium detection (~1 sprint)
- Inspect connector references on each canvas app, cross-reference against the premium connector catalog.

### Phase 4 — DLP compliance (real project)
- Per-env DLP policy + connector classification + per-app connector inventory. PPAC has its own classifier worth several pages of code.

### Phase 5 — deeper PPAC parity
- Environment groups (PPAC-only).
- Billing policies (PPAC-only PAYG enforcement).
- Governance enforcement events (sharing limits, request-access workflows).

---

## Honest limitations

This is a research / engineering aid, not a Microsoft product:

- **WPF + .NET 10-windows** — Windows desktop only. No web, no Mac.
- **Single tenant per launch** — no multi-tenant overlay yet.
- **Read-mostly** — only one mutation today (`Revoke admin` on Dataverse users). Adding more is intentionally gated until we have a confirm/audit story.
- **No support SLA** — PPAC is in public preview, BAP is officially deprecated. Routes change. We try to keep up.
- **Permissions matter** — see the [README](../../README.md#identity--permission-requirements) for the full role + audience matrix. A tenant admin who isn't an env admin will see envs in the grid but empty drill-down panels (with `401`s in the trace log — the canonical signal).

---

## Why this approach generalises

The pattern — *local SQLite cache + a small number of bulk REST calls + lazy
per-resource enrichment + live client-side filtering* — is the right shape for
**any** Microsoft cloud admin tool that needs to surface thousands of resources
across hundreds of containers. It works for Power Platform envs, it would work
for Dynamics orgs, M365 tenants, Azure subscriptions, GitHub orgs.

The thing PPAC, Azure Portal, and the M365 admin center all get wrong is
treating every page load as a fresh remote query. For an admin doing 200
operations per session against a known set of resources, that's a bad trade.
**Cache, then refresh.** It's a 30-year-old desktop pattern and it's still the
fastest UI you can build.

---

## Try it

```powershell
git clone https://github.com/SweetsNSavories/VerseOps.git
cd VerseOps
dotnet build VerseOps.sln
dotnet run --project VerseOps.App\VerseOps.App.csproj
```

You'll need:
- A Power Platform / Dynamics 365 administrator account (or an SP registered as an admin application — see the README).
- `User.Read.All` + `Directory.Read.All` on Graph for the licence drawer.
- System Administrator on each env you want to drill into.

Issues, PRs, and "why doesn't it show X" complaints all welcome at
[github.com/SweetsNSavories/VerseOps](https://github.com/SweetsNSavories/VerseOps).
