# Bring-your-own app registration

VerseOps signs in **as you** using MSAL. By default it uses the well-known **Azure CLI
public client** (`04b07795-8ddb-461a-bbee-02f9e1bf7b46`) so the app works out of the box
without any setup in your tenant. That client is a Microsoft-owned multi-tenant public
client with broad delegated permissions to Power Platform and Graph; it cannot read
anything that you, the signed-in user, do not already have rights to read.

For tenants that block "unverified" multi-tenant apps, want a tenant-issued audit trail
("which app fetched this data?"), or want least-privilege scopes, register your own app
and point VerseOps at it.

## When to register your own app

| You want… | Use Azure CLI default | Register your own |
|---|---|---|
| Try VerseOps quickly | yes | — |
| Tenant blocks Azure CLI sign-in | — | yes |
| Audit trail per app | — | yes |
| Least-privilege scopes (only what VerseOps actually calls) | — | yes |
| Run the **App-only** mode in the API Explorer to call `BAP/adminApplications` | — | yes (you must register an SP) |

The two modes you can configure are independent:

1. **Public client** (`publicClientId`) — used by every "User (interactive)" sign-in:
   inventory load, API Explorer in User mode, the Register-SP one-time call.
2. **App-only client** (`appOnlyClientId`) — used only by the API Explorer's App-only mode
   and the Register-SP button. **The secret is never persisted** — VerseOps holds it in
   memory for the session only and forgets it on exit.

## Register a public client (interactive sign-in)

Portal: **Microsoft Entra admin center → App registrations → New registration**.

| Field | Value |
|---|---|
| Name | `VerseOps – Public client` (or anything you like) |
| Supported account types | **Accounts in this organizational directory only** (single-tenant) — unless you intend to share the registration |
| Redirect URI | **Public client/native** → `http://localhost` |

After Create:

1. **Authentication** → confirm `http://localhost` redirect, and toggle **Allow public
   client flows** to **Yes**. Save.
2. **API permissions** → Add a permission (Delegated, then Grant admin consent):
   - **Microsoft Graph**: `User.Read`, `Group.Read.All`
   - **Power Platform API**: `user_impersonation` *(if not visible, see "Power Platform API
     not in the picker" below)*
   - **Dynamics CRM**: `user_impersonation`
3. Copy the **Application (client) ID** GUID — that is your `publicClientId`.
4. Copy the **Directory (tenant) ID** GUID — that is your `tenantId`.

### Power Platform API not in the picker

Some tenants don't show "Power Platform API" in the permission picker by default. Run this
once with a Global Administrator account to register it in the tenant:

```powershell
Connect-AzureAD
New-AzureADServicePrincipal -AppId 8578e004-a5c6-46e7-913e-12f58912df43
```

(Service principal id `8578e004-a5c6-46e7-913e-12f58912df43` is Microsoft's well-known
Power Platform API resource.) Re-open the permission picker; it will now appear.

## Register an App-only (confidential) client — optional

Only needed if you intend to use the API Explorer's App-only radio or the Register-SP
button against a service principal you own.

1. **App registrations → New registration** → name it `VerseOps – SP`, single tenant, no
   redirect URI.
2. **Certificates & secrets → New client secret** → copy the secret VALUE immediately. You
   will paste it into VerseOps each session; it is never written to disk.
3. **API permissions** → Add a permission (Application, then Grant admin consent):
   - **Power Platform API**: `.default`
   - **Dynamics CRM**: `.default`
4. In VerseOps → API Explorer tab → switch radio to **App-only** → paste **Application
   (client) ID** and **Secret** → click **Register SP**. This sends one PUT to
   `https://api.bap.microsoft.com/.../adminApplications/{clientId}?api-version=2020-10-01`
   to register the SP as a Power Platform tenant admin management application.
5. Optionally paste the App-only client id into the **App ClientId** field and click
   **Save defaults** to persist it (the secret is **not** saved).

## Tell VerseOps which client to use

Three ways, **highest priority first**:

1. **Environment variables** (per-process, never persisted by VerseOps):
   - `VERSEOPS_TENANT_ID`
   - `VERSEOPS_PUBLIC_CLIENT_ID`
   - `VERSEOPS_APP_CLIENT_ID`

2. **User settings file** — `%LOCALAPPDATA%\VerseOps\appsettings.json`. Created/updated by
   the **Save defaults** button in the API Explorer Authentication panel. Format:

   ```json
   {
     "tenantId": "<your tenant GUID>",
     "publicClientId": "<your public client GUID>",
     "appOnlyClientId": "<your SP client GUID, optional>"
   }
   ```

3. **EXE-adjacent file** — `appsettings.local.json` next to `VerseOps.App.exe`. Useful for
   sysadmins distributing a pre-configured copy via Intune / network share. Same format as
   above. `.gitignore` already excludes it.

If none are present, VerseOps falls back to the Azure CLI public client and tenant
`common`.

## Verify

1. Launch VerseOps.
2. API Explorer tab → confirm the **Tenant** and **Public client id** fields are
   pre-filled from your settings.
3. Click **Sign in** (or run any inventory load) and complete the interactive prompt.
4. In **Microsoft Entra → Enterprise applications**, find your app and inspect
   **Sign-in logs** — you should see your sign-in attributed to your app, not Azure CLI.

## Revoke / rotate

* **Public client** — delete or disable the app registration in Entra. VerseOps users in
  your tenant immediately stop being able to sign in via that client id.
* **App-only secret** — delete it under **Certificates & secrets**. Since VerseOps never
  persisted it, there is nothing to scrub on user machines beyond the OS-protected MSAL
  token cache (which expires naturally).
