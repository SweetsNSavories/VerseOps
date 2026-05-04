# VerseOps

> **Experimental WPF tool for exploring the Power Platform admin control plane (BAP & PPAC) side-by-side.**
> Built as a research / engineering aid for Power Platform admins and operations engineers
> who need to understand which REST surfaces actually work for which identities — *today*,
> not according to the docs.

---

## ⚠️ Important context

Microsoft is in the middle of migrating the Power Platform admin control plane from the
**legacy BAP surface** (`api.bap.microsoft.com`, `api.powerapps.com`, `api.flow.microsoft.com`)
to the **new PPAC surface** (`api.powerplatform.com`).

| Surface | Status | Public docs | Used by |
|---|---|---|---|
| **BAP / PowerApps / Flow admin APIs** | Officially **deprecated**, undocumented publicly | none — only via reverse-engineering [`Microsoft.PowerApps.Administration.PowerShell`](https://www.powershellgallery.com/packages/Microsoft.PowerApps.Administration.PowerShell) | PAC CLI, PowerShell admin module, third-party tools |
| **PPAC (`api.powerplatform.com`)** | **Public preview** — fully documented, no support SLA | [Microsoft Learn](https://learn.microsoft.com/en-us/rest/api/power-platform/) + [`Microsoft.PowerPlatform.Management`](https://www.nuget.org/packages/Microsoft.PowerPlatform.Management) NuGet | new PAC CLI flows, official SDK |

This project lets you toggle between the two surfaces in the UI and try **every documented
operation** with a single click, so you can see for yourself what works against your tenant.

> **VerseOps is not affiliated with Microsoft.** It is an independent experiment for
> advanced practitioners. Routes, parameters, and behaviour will change as PPAC moves toward GA.

---

## What's in the box

```
VerseOps.sln
├── VerseOps.App/        ← WPF tool (the thing you run)
├── VerseOps/            ← class library: token providers, EnvironmentProvisioningService
├── VerseOps.SdkRunner/  ← console: reflects over Microsoft.PowerPlatform.Management SDK
│                          and probes every method against your tenant
├── BapDiag/             ← console: hits every well-known BAP route and reports status
├── SpDiag/              ← console: same as BapDiag but sweeps token claims for an SP
└── VerseOps.Sample/     ← minimal sample of the library (read appsettings.json)
```

### The WPF app

* **Surface toggle** — radio buttons switch the tree between **BAP (deprecated)** and **PPAC (preview)** routes.
* **Auth modes** — Interactive (system browser) for User identity, or App-only (client-credentials) for SP.
* **Register SP** button — one-click `PUT /adminApplications/{clientId}` to grant a new SP admin scope.
* **Sweep PPAC** — runs every PPAC operation across the first N environments and prints a coverage matrix.
* **Send / Decode** — pick any operation, edit URL/body inline, fire it, and inspect the JWT used.

### The diagnostic runners

| Tool | Purpose | Output |
|---|---|---|
| `SpDiag` | Verifies the four token audiences for an SP and probes a starter set of routes | `{audience} {status}` per row |
| `BapDiag` | Hits ~45 BAP endpoints (env list, capacity, DLP, flows, tenant settings, etc.) | per-call status, ms, snippet |
| `VerseOps.SdkRunner` | Reflects over the PPAC SDK and calls every accessible client method | `sdk-sweep.log` coverage table |

---

## Identity & permission requirements

This is the part Microsoft's docs gloss over. The same SP / user can return **403** on one
route and **200** on the next, depending on **which Entra role and which environment role**
they hold.

### Tenant-level (required to see anything beyond your own envs)

Add the calling identity (user **or** service principal) to **one** of:

| Role | Where | Why |
|---|---|---|
| **Power Platform Administrator** | Entra → Roles and administrators | Required for `/scopes/admin/*` BAP routes and most PPAC governance routes |
| **Dynamics 365 Administrator** | Entra → Roles and administrators | Equivalent for D365 envs |
| **Global Administrator** | Entra → Roles and administrators | Superset; works but principle-of-least-privilege says don't |

For an **app-only service principal** there is one more required step that breaks most people:

```http
PUT https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/adminApplications/{clientId}?api-version=2020-10-01
Authorization: Bearer <token for api.bap.microsoft.com/.default>
Content-Type: application/json

{}
```

The WPF app's **Register SP** button does this for you. Without this PUT, even an SP holding
the *Power Platform Administrator* directory role will get `403 / Forbidden — service
principal is not registered as an admin application` on most admin-scope routes.

### Per-environment role (required for some routes)

The following routes additionally require the calling identity to be a **member of the
environment's Dataverse security role** in *that* env, not just a tenant-level admin:

| Route family | Required env role |
|---|---|
| `…/powerpages/environments/{id}/websites` (Power Pages) | **Power Pages System Admin** OR **Website Owner** in that env |
| `…/usermanagement/environments/{id}/users` (Dataverse users) | **System Administrator** in that env |
| `…/powervirtualagents/environments/{id}/bots` (Copilot Studio) | **Copilot Maker** OR **System Administrator** in that env |
| Per-env Dataverse Web API (`{org}.crm.dynamics.com/api/data`) | Any role with table-level read |

If a per-env route returns **401** with a generic "user not authenticated" message but your
token decodes cleanly with the right audience, you almost certainly need a per-env role.
Add the SP / user as a Dataverse application user with the **System Administrator** role
inside that environment (Power Platform Admin Center → Environment → Settings → Users +
permissions → Application users / Users).

### Token audience cheat-sheet

| Surface | `Authorization: Bearer` audience |
|---|---|
| BAP env / settings / DLP / capacity | `https://service.powerapps.com/.default` |
| BAP `/adminApplications` PUT | `https://api.bap.microsoft.com/.default` |
| PowerApps admin (`api.powerapps.com`) | `https://service.powerapps.com/.default` |
| Flow admin (`api.flow.microsoft.com`) | `https://service.flow.microsoft.com/.default` |
| **PPAC (`api.powerplatform.com`)** | `https://api.powerplatform.com/.default` |
| Dataverse (per-env Web API) | `https://{org}.crm.dynamics.com/.default` |

A wrong audience returns `S2S17001` or `Unauthorized — token signature invalid` even though
the token is valid for a different host. Use **Decode current token (local)** in the app to
audit `aud`, `idtyp`, `scp`, `roles`.

---

## Quick start

```powershell
git clone https://github.com/<your-account>/VerseOps.git
cd VerseOps
dotnet build VerseOps.sln

# Run the WPF app
dotnet run --project VerseOps.App\VerseOps.App.csproj

# Run the BAP route sweep against a service principal
dotnet run --project BapDiag\BapDiag.csproj -- <tenantId> <clientId> <secret>

# Run the BAP route sweep with your own user identity (device-code)
dotnet run --project BapDiag\BapDiag.csproj -- --device <tenantId>

# Run the PPAC SDK reflection sweep
dotnet run --project VerseOps.SdkRunner\VerseOps.SdkRunner.csproj -- <tenantId> <clientId> <secret>
```

### Configuring credentials

Never commit secrets. Two safe options:

1. **Pass on the command line** (sweepers, one-off runs).
2. **`appsettings.local.json`** in the same folder as `appsettings.json` (already
   ignored by `.gitignore`):

   ```json
   {
     "PowerPlatform": {
       "TenantId":     "<your-tenant-guid>",
       "ClientId":     "<your-app-registration-id>",
       "ClientSecret": "<your-secret-or-cert-thumbprint>"
     }
   }
   ```

If you ever paste a secret into chat with me or anywhere else: **rotate it immediately**
in Entra → App registrations → Certificates & secrets.

---

## What we learned (so far)

After running the SP and user sweeps against a real tenant with 716 environments:

| | PPAC (211 SDK ops) | BAP (~45 documented routes) |
|---|---|---|
| **Working** | 35 | 29 |
| **% useful today** | 16% | 64% |
| **Per-env apps / connections / flows v2** | empty / 401 | works |
| **DLP** | needs BAP fallback | works directly |
| **Environment groups, billing policies, websites** | works (PPAC-only) | n/a |

**Conclusion**: in mid-2026 the practical control plane is still ~70% BAP, ~30% PPAC, with
PPAC taking over for new capabilities (env groups, PAYG billing, websites, governance v2).
The WPF tool catalogues both so you can verify against *your* tenant.

When `api.powerplatform.com` GA'd and the BAP routes hard-fail, this project is ready to
flip to PPAC-only by changing the default surface — every route is already wired into the tree.

---

## Contributing

Issues and PRs welcome, especially:

* New verified routes (please paste status code, audience used, and identity type).
* Per-env role requirements you discover the hard way.
* PPAC routes that flipped from `RouteNotFound` to working — Microsoft is shipping these continuously.

Please **never** include tenant ids, environment ids, or app ids in bug reports.
The `.gitignore` already excludes `*.log`, `bap-sweep*.log`, `sdk-sweep*.log` and similar.

---

## Disclaimer

* The author is a Microsoft full-time employee. **This project is personal work,
  unaffiliated with Microsoft, and does not represent Microsoft's official guidance.**
* BAP routes here are reverse-engineered from publicly distributed PowerShell modules.
  They are documented because Microsoft's own tools depend on them, but they may be
  removed or changed without notice.
* PPAC is in **public preview**. Behaviour, response shapes, and route paths can change.
* No warranty. Use at your own risk against non-production tenants first.

## License

MIT — see [LICENSE](LICENSE).
