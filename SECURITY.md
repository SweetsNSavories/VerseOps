# Security policy

## Reporting a vulnerability

If you discover a security issue in VerseOps, **please do not open a public GitHub issue**.

Instead, email the maintainer at the address listed on the GitHub profile of
[@SweetsNSavories](https://github.com/SweetsNSavories), with the subject line:

> `[VerseOps Security] <one-line summary>`

Include:

* Affected version (commit SHA or release tag).
* Reproduction steps, ideally a minimal proof-of-concept.
* Impact assessment (what an attacker can do).
* Whether you have already disclosed this to anyone else.

You will receive an acknowledgement within **5 business days** and a triage decision
within **10 business days**. Coordinated disclosure timelines are negotiable; default is
**90 days** from triage to public disclosure, accelerated if the issue is being actively
exploited.

## Scope

In scope:

* The `VerseOps.App` WPF executable and any code under `VerseOps.App/`.
* The build / sign / release pipeline (`Directory.Build.props`, `.github/workflows/*`).
* Documented behaviour in [README.md](README.md), [SIGNING.md](SIGNING.md), and
  [docs/network-endpoints.md](docs/network-endpoints.md).

Out of scope:

* Vulnerabilities in upstream Microsoft services (`api.powerplatform.com`,
  `*.crm.dynamics.com`, `graph.microsoft.com`, etc.) — report those to
  [Microsoft Security Response Center (MSRC)](https://msrc.microsoft.com/).
* Vulnerabilities in NuGet dependencies — these are tracked via Dependabot and `NuGetAudit`;
  open a normal issue if Dependabot has not picked one up.
* Findings that require the attacker to already control the user's machine, MSAL token
  cache, or `%LOCALAPPDATA%\VerseOps\` directory.

## Security model

VerseOps is a **single-user desktop tool** that calls Microsoft's public APIs with the
signed-in user's delegated tokens. Its threat model assumes:

| Asset | Trusted | Why |
|---|---|---|
| The signed-in Windows user | yes | Owns the process and `%LOCALAPPDATA%` |
| MSAL token cache | yes | OS-protected via `Microsoft.Identity.Client` defaults |
| The local SQLite cache (`verseops.db`) | yes | Read/write by the same Windows user |
| Microsoft service endpoints | yes | Authenticated via TLS + bearer tokens |
| The network between the user and Microsoft | partially trusted | TLS + cert pinning by the OS |
| The publisher's code-signing certificate | yes | Validated by Authenticode at install |

VerseOps does **not** act as a server, expose a listening socket, or accept input from
remote callers. There is no web UI, no IPC surface, and no auto-update mechanism.

## What we do to keep you safe

* All NuGet packages are version-pinned (no floating `*`).
* `NuGetAudit` (low+ severity) fails the build on any CVE.
* `TreatWarningsAsErrors=true` keeps stale APIs from sneaking in.
* `Deterministic=true` + `ContinuousIntegrationBuild` produce reproducible binaries.
* Dependabot watches NuGet, GitHub Actions, and the Dockerfile (none yet).
* CodeQL scans every PR and the default branch.
* Released binaries are signed; see [SIGNING.md](SIGNING.md).
* Authorization headers in diagnostics are truncated to the first 8 characters.
