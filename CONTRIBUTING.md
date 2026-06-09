# Contributing to VerseOps

Thanks for taking the time to look! VerseOps is two things in one repo:

1. **`VerseOps.App/`** — the standalone WPF / .NET 10 inventory dashboard.
2. **`VerseOps.XrmToolBox/`** — the XrmToolBox plugin (`net48`) that exposes the same PPAC/BAP operation catalog inside the XrmToolBox host.

Both rest on a shared catalog library:

- **`VerseOps.Api.Core/`** (`netstandard2.0`) — the operation catalog (`ApiCatalog.cs`, `PpacGeneratedCatalog.cs`), the `ApiExecutor`, and the shared MSAL token cache layout. Anything you add here surfaces in **both** the WPF app and the XrmToolBox plugin automatically.

---

## Quick start

```powershell
git clone https://github.com/SweetsNSavories/VerseOps.git
cd VerseOps
dotnet build VerseOps.sln -c Release
```

The solution targets `.NET 10` for the WPF app and `net48` for the plugin, so you need:

- **.NET 10 SDK** (or newer)
- The **net48 reference assemblies** (installed automatically by `dotnet build` via the `Microsoft.NETFramework.ReferenceAssemblies` package — no Visual Studio install needed)
- **Windows** — both projects target Windows

Run the WPF app:

```powershell
.\VerseOps.App\bin\Release\net10.0-windows\VerseOps.App.exe
```

Pack the XrmToolBox plugin for local testing:

```powershell
dotnet pack VerseOps.XrmToolBox\VerseOps.XrmToolBox.csproj -c Release
# Output: VerseOps.XrmToolBox\bin\Release\VerseOps.XrmToolBox.<version>.nupkg
```

To sideload it into your local XrmToolBox:

```powershell
$plugins = "$env:APPDATA\MscrmTools\XrmToolBox\Plugins\VerseOps"
Remove-Item $plugins -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $plugins | Out-Null
Expand-Archive .\VerseOps.XrmToolBox\bin\Release\VerseOps.XrmToolBox.*.nupkg -DestinationPath $env:TEMP\verseops-xtb -Force
Copy-Item "$env:TEMP\verseops-xtb\lib\net48\*" $plugins
# Restart XrmToolBox
```

---

## Project structure

```
VerseOps.sln
├── VerseOps.Api.Core/          ← shared operation catalog (netstandard2.0)
│   ├── ApiCatalog.cs           ← hand-curated BAP operations
│   ├── PpacGeneratedCatalog.cs ← generated PPAC operations
│   └── ApiExecutor.cs          ← MSAL token acquisition + HTTP execution
├── VerseOps.App/               ← WPF dashboard (net10.0-windows)
├── VerseOps.XrmToolBox/        ← XrmToolBox plugin (net48)
│   ├── VerseOpsPluginFactory.cs ← MEF entry point + IGitHubPlugin / IHelpPlugin
│   ├── VerseOpsPluginControl.cs ← INoConnectionRequired control
│   ├── Auth/PluginAuthService.cs ← MSAL signin (browser + device-code)
│   └── README.md               ← plugin-store readme
├── VerseOps.SdkTests/          ← integration tests against live tenants
├── VerseOps.UiTests/           ← FlaUI smoke tests for the WPF app
├── docs/                       ← architecture / threat model / blog
└── tools/                      ← packaging / brand asset / capture scripts
```

---

## Where to add what

| Change | File(s) |
|---|---|
| New BAP operation | `VerseOps.Api.Core/ApiCatalog.cs` (add an `ApiOperation`) |
| Corrected PPAC operation | `VerseOps.Api.Core/PpacGeneratedCatalog.cs` |
| New plugin UI panel | `VerseOps.XrmToolBox/VerseOpsPluginControl.*.cs` |
| New WPF view | `VerseOps.App/Inventory/<View>.xaml(.cs)` |
| New endpoint (DNS / firewall doc) | `docs/network-endpoints.md` |

If you're correcting a route that returns `404`, please paste the raw response body in the PR description — it almost always tells you whether the issue is `/scopes/admin/` prefixing, HTTP verb, or `api-version` mismatch.

---

## Tests

Two flavours:

- **`VerseOps.SdkTests/`** — live integration tests against your tenant. Read-by-default; mutating tests gated by `VERSEOPS_INVOKE_MUTATIONS=1` and `VERSEOPS_MUTATION_ALLOW=<substring>`. Use `VERSEOPS_AUTH_NONINTERACTIVE=1` for CI / silent runs (requires a prior signed-in MSAL cache).
- **`VerseOps.UiTests/`** — FlaUI tests that drive the WPF app.

Run all:

```powershell
dotnet test VerseOps.sln -c Release
```

---

## Pull requests

1. **One concern per PR.** Catalog corrections, plugin UX, and infra (CI / packaging) go in separate PRs.
2. **Clean build required.** The repo treats warnings as errors and runs `NuGetAudit` — both must pass.
3. **No secrets, no real tenant data.** Strip `tenantId`, env GUIDs, user UPNs, and SG GUIDs from logs, screenshots, and tests. The `.gitignore` already excludes `*.log` and `appsettings.local.json` — respect that.
4. **Bump the right version.** XrmToolBox plugin changes to tile metadata or operation catalog need a `<Version>` bump in `VerseOps.XrmToolBox.csproj`, otherwise XTB caches the old `manifest.json` and your change won't render.
5. **Update release notes.** `PackageReleaseNotes` in the plugin csproj is what the plugin store displays.

---

## Reporting issues

- **Security issues:** see [SECURITY.md](SECURITY.md). Do not file public issues for vulnerabilities.
- **Bugs and feature requests:** use the GitHub issue templates under [`.github/ISSUE_TEMPLATE/`](.github/ISSUE_TEMPLATE/).
- **API 404 reports:** include the operation name, the URL the plugin executed (visible in the request panel), and the response body / correlation id.

---

## Releasing the XrmToolBox plugin

(For maintainers.) The flow:

1. Bump `<Version>` and `PackageReleaseNotes` in `VerseOps.XrmToolBox/VerseOps.XrmToolBox.csproj`.
2. Commit + push.
3. Tag: `git tag -a xrmtoolbox-vX.Y.Z -m "..."` and `git push origin xrmtoolbox-vX.Y.Z`.
4. Approve the GitHub Actions `release` environment pending deployment. The workflow `publish-xrmtoolbox-plugin.yml` trusted-publishes to nuget.org as `VerseOps.XrmToolBox`.
5. The XrmToolBox plugin store scans nuget.org daily for packages tagged `XrmToolBox` and auto-lists the new version.

---

Thanks again — issues and PRs are the fastest way to make this more useful for the next tenant admin who needs to introspect PPAC/BAP.
