# VerseOps

> **Power Platform inventory dashboard for tenant administrators — WPF / .NET 10 desktop app.**

VerseOps is a single-window Windows desktop tool that pulls a live, read-mostly inventory of
your Power Platform tenant — environments, capacity, solutions, apps, flows, agents, users,
licenses, security groups — into one place. It calls the official
[`Microsoft.PowerPlatform.Management`](https://www.nuget.org/packages/Microsoft.PowerPlatform.Management)
SDK, the BAP capacity API, and Microsoft Graph; everything is local-first (cached in SQLite at
`%LOCALAPPDATA%\VerseOps\verseops.db`).

> **VerseOps is not a Microsoft product.** It is an independent open-source utility for
> Power Platform administrators. Trademarks belong to their owners — see
> [ATTRIBUTIONS.md](ATTRIBUTIONS.md).

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

1. Download the latest signed `.zip` from [Releases](../../releases) (signed with the
   publisher's code-signing certificate; see [SIGNING.md](SIGNING.md) for verification).
2. Extract and run `VerseOps.App.exe`.
3. Sign in with a tenant identity that holds **Power Platform Administrator** or
   **Dynamics 365 Administrator** in Microsoft Entra ID.

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
