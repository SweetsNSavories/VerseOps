# Morning demo runbook — VerseOps BYO config

> **One-page script for the demo.** Read top to bottom. Each step is ~30s.

## 0. Pre-flight (do this 5 min before the demo)

1. **Rotate the secret you pasted earlier.** Entra → App registrations → `3b451e9c-c4d7-4c12-8d12-69f996e7fd48` → Certificates & secrets → delete the old one ending `…sa3h` → New client secret → copy the new value to clipboard.
2. Confirm no leftover instance is running:
   ```powershell
   Get-Process VerseOps.App -ErrorAction SilentlyContinue | Stop-Process -Force
   ```
3. (Optional but cleaner) Hide any prior config so you can demo the "first launch" experience:
   ```powershell
   $cfg = "$env:LOCALAPPDATA\VerseOps\appsettings.json"
   if (Test-Path $cfg) { Move-Item $cfg "$cfg.bak" -Force }
   ```
   Restore after demo: `Move-Item "$cfg.bak" $cfg -Force`

## 1. Launch (~40s cold start)

Double-click `VerseOps.App.exe` (or run the EXE in the project's bin folder).

Expect first paint in ~40 seconds on this laptop. Don't panic; that's Defender scanning the freshly built binaries on first run.

## 2. The story: "no Microsoft account, no Entra changes needed"

Talk track: *"VerseOps signs in with the Azure CLI public client by default — no app registration needed, just sign in with your tenant admin."*

Click the **Inventory** tab. Show the empty grid + hero tiles. Click **Refresh** → browser pops → sign in → grid fills.

## 3. The pivot: "for regulated tenants, BYO your own app"

Talk track: *"For a regulated tenant that blocks unverified multi-tenant clients, or anywhere you want a tenant-issued audit trail, register your own app and point VerseOps at it. Three settings, zero rebuild."*

Click the **API Explorer** tab. Expand the **Auth** panel at the top.

Show the three pre-populated fields (these came from `AppSettings.LoadFromDisk()` at startup):
- **Tenant**: `common` (default) — change to `1557f771-4c8e-4dbd-8b80-dd00a88e833e` (your tenant)
- **Public client id**: `04b07795-…` (Azure CLI default)
- **App ClientId** (under App-only radio): `3b451e9c-c4d7-4c12-8d12-69f996e7fd48` (your registered app)

Click **Save defaults**.

Status bar shows: `Saved defaults to C:\Users\…\AppData\Local\VerseOps\appsettings.json`

## 4. Prove no secret is on disk

Open File Explorer to `%LOCALAPPDATA%\VerseOps\` and open `appsettings.json` in Notepad. Show the audience the contents:

```json
{
  "tenantId": "1557f771-4c8e-4dbd-8b80-dd00a88e833e",
  "publicClientId": "04b07795-8ddb-461a-bbee-02f9e1bf7b46",
  "appOnlyClientId": "3b451e9c-c4d7-4c12-8d12-69f996e7fd48"
}
```

Three keys. No `secret`, no `password`, no token. Talk track: *"Even if I'd entered the client secret in the UI, it would not be here. Secrets live in process memory only — they're wiped when the app closes."*

## 5. Show the App-only flow (with the rotated secret)

Switch to **App-only (client credentials)** radio. The App ClientId field is already filled from Save defaults. Paste the **new** rotated secret into the **Secret** field.

Click **Acquire token**. Status shows `Acquired app-only token (audience: api.powerplatform.com/.default)`.

Pick `Environments → List` from the tree → **Send**. Response pane fills with the tenant's environments.

Close the app. Reopen. Open Notepad on `appsettings.json` again — secret is still not there.

## 6. The compliance bullet

Land on this slide / talk point:

| Identifier | Where it lives | Why |
|---|---|---|
| Tenant id | `appsettings.json`, plain text | Not a secret |
| Public client id | `appsettings.json`, plain text | Not a secret |
| App-only client id | `appsettings.json`, plain text | Not a secret |
| App-only client secret | Process memory only, in-session | **Bearer credential — never persisted** |
| MSAL refresh token | Windows DPAPI / WAM token cache | OS-protected, per-user |

## 7. Recovery if the demo goes sideways

| Symptom | Fix |
|---|---|
| Cold start > 1 min | Window will appear. Keep talking. |
| `appsettings.json` corrupted by demo | App falls back to defaults silently — no crash. Re-Save from UI. |
| `Acquire token` fails with `AADSTS7000215` (invalid secret) | The pasted secret is wrong. Rotate again and re-paste. |
| `Acquire token` fails with `AADSTS65001` (consent missing) | Tenant admin needs to grant consent for the API permissions. Use a different tenant for the demo. |
| App won't close cleanly | `Get-Process VerseOps.App | Stop-Process -Force` |

## 8. What you should NOT do during the demo

- **Do not paste the client secret into chat, screenshare, or any markdown file.** Only the UI text box.
- Do not run the API Explorer's **Register SP** button on an unfamiliar tenant — it permanently registers your service principal as a tenant admin management application.
- Do not show `%LOCALAPPDATA%\VerseOps\verseops.db` in a screenshare — it contains cached tenant data with env GUIDs and user UPNs.
