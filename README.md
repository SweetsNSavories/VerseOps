# VerseOps

> **Power Platform inventory dashboard for tenant administrators — WPF / .NET 10 desktop app.**

VerseOps is a single-window Windows desktop tool that pulls a live, read-mostly inventory of
your Power Platform tenant — environments, capacity, solutions, apps, flows, agents, users,
licenses, security groups — into one place. It calls the official
[`Microsoft.PowerPlatform.Management`](https://www.nuget.org/packages/Microsoft.PowerPlatform.Management)
SDK, the BAP capacity API, and Microsoft Graph; everything is local-first (cached in SQLite at
`%LOCALAPPDATA%\VerseOps\verseops.db`).

> **VerseOps is not a Microsoft product.** It is an independent open-source
> project, developed and maintained by **Praveen Thonda** in a personal
> capacity under the [MIT License](LICENSE). It is **not** affiliated with,
> endorsed by, sponsored by, or a product of Microsoft Corporation.
> Power Platform, Dataverse, Dynamics 365, Microsoft Entra, and related names
> are trademarks of Microsoft Corporation, used here solely to describe the
> systems VerseOps interoperates with. See [ATTRIBUTIONS.md](ATTRIBUTIONS.md)
> for full notices.

---

## What it does

| Pane | Source | Cached |
|------|--------|--------|
| Environment list (name, region, type, SKU, security group, default flag) | PPAC `/environments` | yes |
| Capacity (DB / File / Log GB, FinOps DB / FinOps File) | BAP `/capacity` | yes |
| Solutions, apps, flows, agents per environment | PPAC + Dataverse Web API | yes |
| Users + licenses + roles per environment | Dataverse Web API + Microsoft Graph | yes |
| Security group display name resolution | Microsoft Graph `/groups` | yes |

All identity flows are **delegated** (the signed-in user). No client-secret /
service-principal flows. The app uses MSAL with the Azure CLI public client ID (or a
custom client ID if you set one) and ships **no secrets**.

---

## Quick start

### Run from a release

Two archives are published per release:

| Archive | Size | .NET runtime required | When to choose |
|---------|------|-----------------------|----------------|
| `VerseOps.App-vX.Y.Z-win-x64.zip` | small (~7 MB) | yes — install [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) | you already manage .NET runtimes |
| `VerseOps.App-vX.Y.Z-win-x64-selfcontained.zip` | large (~65 MB) | **no** — bundled | "download and run", no admin install needed |
| `VerseOps.App-vX.Y.Z-win-x64-unsigned.msix` | ~65 MB | **no** — bundled | per-user install, Start-menu entry, clean uninstall via Settings → Apps |

> **MSIX is currently an unsigned developer preview.** Windows will show an
> "untrusted publisher" warning on install. A code-signing certificate from
> SignPath.io Foundation (free for OSS) is in progress; once issued, signed
> MSIX packages will install without warnings.

1. Download the appropriate artifact from [Releases](../../releases).
2. Extract a `.zip` and run `VerseOps.App.exe`, or double-click the `.msix`
   (Settings → Privacy & security → For developers → Sideload apps).
3. Sign in with a tenant identity that holds **Power Platform Administrator**
   or **Dynamics 365 Administrator** in Microsoft Entra ID.

Each release also ships a [SLSA build-provenance attestation](https://slsa.dev/spec/v1.0/)
verifiable with the GitHub CLI:

```powershell
gh attestation verify VerseOps.App-vX.Y.Z-win-x64-selfcontained.zip `
  --repo SweetsNSavories/VerseOps
```

### Build from source

```powershell
git clone https://github.com/SweetsNSavories/VerseOps.git
cd VerseOps
dotnet build VerseOps.sln -c Release
.\VerseOps.App\bin\Release\net10.0-windows\VerseOps.App.exe
```

> **Sign your own build before distributing internally.** See [SIGNING.md](SIGNING.md)
> for `signtool` / Azure Trusted Signing instructions.

### Required permissions

| Surface | Minimum role / scope | Why |
|---------|----------------------|-----|
| Power Platform Admin Center (PPAC) | **Power Platform Administrator** *or* **Dynamics 365 Administrator** in Entra | List / read environments, solutions |
| BAP capacity | Same as above | Tenant + per-env capacity figures |
| Per-environment Dataverse Web API | **System Administrator** in that environment | Users, roles, asset enumeration |
| Microsoft Graph | `User.Read`, `Group.Read.All` (delegated) | Resolve security-group display names + license SKUs |

The tool will silently skip panes it cannot read instead of failing the whole refresh.

---

## Configure (bring-your-own app registration)

VerseOps signs in with the well-known **Azure CLI public client**
(`04b07795-8ddb-461a-bbee-02f9e1bf7b46`) by default — no Entra setup needed. For tenants
that block unverified multi-tenant clients, want a tenant-issued audit trail, or want
least-privilege scopes, register your own app and tell VerseOps to use it. Three settings,
**highest priority first**:

| # | Source | When to use |
|---|---|---|
| 1 | Environment variables `VERSEOPS_TENANT_ID`, `VERSEOPS_PUBLIC_CLIENT_ID`, `VERSEOPS_APP_CLIENT_ID` | CI / per-process overrides |
| 2 | `%LOCALAPPDATA%\VerseOps\appsettings.json` | Per-user (written by the **Save defaults** button in the API Explorer) |
| 3 | `appsettings.local.json` next to `VerseOps.App.exe` | Sysadmin pre-config (Intune / share) — `.gitignore`d |

Field names: `tenantId`, `publicClientId`, `appOnlyClientId`. **Secrets are never
written to disk** — the App-only client secret lives in memory for the session only.

See [docs/byo-app-registration.md](docs/byo-app-registration.md) for the Entra portal
walk-through (required API permissions: Power Platform API `user_impersonation`, Dynamics
CRM `user_impersonation`, Graph `User.Read` + `Group.Read.All`). A starter file is at
[appsettings.sample.json](appsettings.sample.json).

For a one-page summary of what the app touches, stores, and never does, read
[docs/threat-model.md](docs/threat-model.md).

---

## Architecture

```
VerseOps.sln
└── VerseOps.App/                ← single WPF executable
    ├── App.xaml(.cs)            ← theme, AUMID, crash-dump pipeline
    ├── MainWindow.xaml(.cs)     ← FluentWindow shell + taskbar icon push
    ├── Auth/AuthService.cs      ← MSAL delegated auth (no secrets)
    ├── Inventory/
    │   ├── InventoryView.xaml   ← main dashboard
    │   ├── InventoryViewModel.cs← MVVM coordinator
    │   ├── Models/              ← EnvironmentRow, AssetRow, ...
    │   ├── Services/            ← PPAC, BAP, Dataverse, Graph clients
    │   └── Sql/schema.sql       ← SQLite cache schema
    └── Themes/                  ← Fluent v2 token brushes (light)
```

There is **no server, no telemetry, and no outbound traffic** beyond the Microsoft endpoints
documented in [docs/network-endpoints.md](docs/network-endpoints.md).

---

## Security & supply chain

* All NuGet packages are version-pinned (no floating `*`).
* `NuGetAudit` is enabled in [`Directory.Build.props`](Directory.Build.props) and fails the
  build on any known CVE.
* Builds are deterministic (`Deterministic=true`, `ContinuousIntegrationBuild=true` in CI).
* Source Link + embedded PDBs make the binary debuggable against the published commit hash.
* Authorization headers are redacted in diagnostic logs (first 8 chars only).
* No secrets are written to disk. MSAL token cache lives in the OS-protected store.
* Crash dumps in `%LOCALAPPDATA%\VerseOps\startup-error.log` contain only the .NET exception
  chain — never tokens or PII.

See [SECURITY.md](SECURITY.md) for the responsible-disclosure policy.

For a generated software bill of materials (SBOM, CycloneDX) and third-party license
attribution, see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the
`sbom.cdx.json` artifact attached to each release.

---

## Privacy

VerseOps reads from your tenant. It does **not** transmit any data to the author or any
third party. The only outbound endpoints are Microsoft's own service URLs
([docs/network-endpoints.md](docs/network-endpoints.md)). All data is cached locally in
`%LOCALAPPDATA%\VerseOps\verseops.db`. To wipe everything: close the app and delete that
folder.

---

## Contributing

PRs welcome. Please:

* Run `dotnet build VerseOps.sln -c Release` clean (warnings are errors).
* Don't include tenant IDs, env IDs, user IDs, or tokens in commits, screenshots, or issue
  reports. The `.gitignore` excludes `*.log` and `appsettings.local.json`; respect that.
* Open an issue first for non-trivial changes.

Security issues — see [SECURITY.md](SECURITY.md), don't open a public issue.

---

## License

MIT — see [LICENSE](LICENSE). Microsoft brand SVGs in
`VerseOps.App/Assets/MicrosoftIcons/` are trademarks of Microsoft Corporation and are
**not** covered by the MIT license; see [ATTRIBUTIONS.md](ATTRIBUTIONS.md).
