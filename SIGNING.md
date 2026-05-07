# Code-signing guide (BYO certificate)

VerseOps does **not** ship a publisher-signed binary in the source repo. If you intend
to redistribute the executable inside your organisation — or are required to pass a
penetration test that flags unsigned executables — you must sign it yourself.

This document covers three workflows in increasing order of trust:

1. [Self-signed certificate (dev / internal lab only)](#1-self-signed-dev-only)
2. [Azure Trusted Signing (recommended for prod)](#2-azure-trusted-signing-recommended)
3. [Standard OV / EV code-signing certificate (DigiCert / Sectigo / SSL.com)](#3-ov--ev-code-signing-certificate)

> ⚠️ **A self-signed signature will fail any pentest checklist that requires a trusted
> root.** Use it for dev only. For real distribution, choose option 2 or 3.

---

## What gets signed

After `dotnet publish`, sign exactly one file:

```
VerseOps.App\bin\Release\net10.0-windows\publish\VerseOps.App.exe
```

The DLLs in that folder are Microsoft-published NuGet packages and do not need re-signing
unless you have a strict policy that requires it (in which case sign them all with the same
cert).

---

## 1. Self-signed (dev only)

```powershell
# 1. Generate a one-year self-signed code-signing cert in the user store.
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject "CN=VerseOps Dev (do-not-distribute)" `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA -KeyLength 3072 `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter (Get-Date).AddYears(1)

# 2. Export to PFX (enter a passphrase when prompted).
$pwd = Read-Host -AsSecureString -Prompt "PFX password"
Export-PfxCertificate -Cert $cert -FilePath .\verseops-dev.pfx -Password $pwd

# 3. Sign the EXE.
& "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe" sign `
    /f .\verseops-dev.pfx `
    /p (ConvertFrom-SecureString -SecureString $pwd -AsPlainText) `
    /fd SHA256 `
    /tr http://timestamp.digicert.com `
    /td SHA256 `
    /d "VerseOps" `
    /du "https://github.com/SweetsNSavories/VerseOps" `
    .\VerseOps.App\bin\Release\net10.0-windows\publish\VerseOps.App.exe

# 4. Verify.
& "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe" verify /pa /v `
    .\VerseOps.App\bin\Release\net10.0-windows\publish\VerseOps.App.exe
```

End users will see "Unknown publisher" / "Untrusted root" warnings unless they import
the dev cert into their Trusted Publishers + Trusted Root stores. **Do not distribute
the PFX file.**

---

## 2. Azure Trusted Signing (recommended)

[Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/overview) is
Microsoft's managed code-signing service. It costs ~$10/month, requires no hardware token,
and produces an Authenticode signature whose chain is rooted in Microsoft's trusted root
program — meaning it passes every pentest checklist that asks for "signed by a trusted CA."

### Prerequisites

* An Azure subscription with permission to create a `Microsoft.CodeSigning/codeSigningAccounts`
  resource.
* A verified identity (organisation or individual) that the certificate will be issued to.
* The `signtool.exe` from a recent Windows SDK (10.0.22621 or newer).
* The [`Azure.CodeSigning.Dlib`](https://www.nuget.org/packages/Azure.CodeSigning.Dlib)
  signing dispatcher.

### One-time setup

1. In the Azure portal, create a **Trusted Signing Account** in a supported region (East US,
   West Europe, or West US 2).
2. Create a **Certificate Profile** of type *Public Trust* against your verified identity.
3. Note the resulting **endpoint**, **account name**, and **certificate profile name**.
4. Grant the signing identity (your Azure AD user, or a service principal used by CI) the
   role **Trusted Signing Certificate Profile Signer** on the account.

### Sign

```powershell
# Install the signing dispatcher next to signtool.
Install-Module -Name Az.CodeSigning -Scope CurrentUser

# metadata.json — describes the Trusted Signing target.
@'
{
  "Endpoint": "https://eus.codesigning.azure.net/",
  "CodeSigningAccountName": "your-account-name",
  "CertificateProfileName": "your-profile-name",
  "ExcludeCredentials": ["ManagedIdentityCredential"]
}
'@ | Out-File metadata.json -Encoding utf8

# Sign.
$dlib = (Get-Module Az.CodeSigning).ModuleBase + "\bin\Azure.CodeSigning.Dlib.dll"
& "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe" sign `
    /v `
    /debug `
    /fd SHA256 `
    /tr http://timestamp.acs.microsoft.com `
    /td SHA256 `
    /dlib $dlib `
    /dmdf metadata.json `
    .\VerseOps.App\bin\Release\net10.0-windows\publish\VerseOps.App.exe
```

In CI (GitHub Actions) use the [`azure/trusted-signing-action`](https://github.com/azure/trusted-signing-action)
action with a workload-identity federation login — never store a service-principal secret
in repository secrets.

---

## 3. OV / EV code-signing certificate

Standard certificate authorities (DigiCert, Sectigo, GlobalSign, SSL.com) issue
**Organisation Validated (OV)** and **Extended Validation (EV)** code-signing certificates.
EV certs come on a hardware token and earn instant SmartScreen reputation.

```powershell
# Assume the cert is in Cert:\CurrentUser\My with the SHA1 thumbprint shown by
# Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Format-List Thumbprint, Subject
$thumbprint = "AB12CD34..."

& "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe" sign `
    /sha1 $thumbprint `
    /fd SHA256 `
    /tr http://timestamp.digicert.com `
    /td SHA256 `
    /d "VerseOps" `
    /du "https://github.com/SweetsNSavories/VerseOps" `
    .\VerseOps.App\bin\Release\net10.0-windows\publish\VerseOps.App.exe
```

For an EV token, the `signtool sign` call will pop a PIN prompt; integrate with your
HSM / token vendor's signing service in CI.

---

## Verifying a signed binary

```powershell
& "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe" verify /pa /all /v `
    .\VerseOps.App.exe
```

The output should show:

* `Hash of file (sha256): ...` matching the build artifact.
* `Signing Certificate Chain` ending in a Microsoft-trusted root (DigiCert, Sectigo,
  GlobalSign, Microsoft, etc.).
* A `Timestamp` block confirming the signature is countersigned and will outlive the
  certificate's expiry.

You can also verify with PowerShell:

```powershell
(Get-AuthenticodeSignature .\VerseOps.App.exe).Status      # → Valid
(Get-AuthenticodeSignature .\VerseOps.App.exe).SignerCertificate.Subject
```

---

## Reproducible builds

`Directory.Build.props` enables `Deterministic=true` and `ContinuousIntegrationBuild`. To
verify your local build matches a published release:

```powershell
git checkout v<release-tag>
dotnet publish VerseOps.App\VerseOps.App.csproj -c Release -r win-x64 --self-contained false
Get-FileHash .\VerseOps.App\bin\Release\net10.0-windows\win-x64\publish\VerseOps.App.exe
```

The unsigned hash should match the one printed in the release notes (signature changes the
hash; verify against the unsigned artifact).
