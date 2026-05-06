# PPAC SDK CRUD surface — risk buckets

Source: `sdk-crud-catalog.txt` (run `--catalog-crud` to regenerate).
Totals: **63 methods, 59 builders** — Post 46 / Put 6 / Patch 3 / Delete 8.

The buckets below decide which methods can be probed safely and which need an
explicit allow-list + sandbox env.

---

## Bucket A — Effectively read-only POSTs (safe to try anywhere)

These return data; the verb is POST only because the request needs a body or a
query that can't fit in a URL. They don't mutate tenant state.

| Builder | Notes |
|---|---|
| `Resourcequery.Resources.Query.QueryRequestBuilder.PostAsync` | runs a Kusto-style query, returns `ResourceQueryResponse` |
| `Governance.CrossTenantConnectionReports.PostAsync` | returns `CrossTenantConnectionReport` |
| `Licensing.TemporaryCurrencyEntitlement[id].Count.PostAsync` | returns count |
| `Powerpages.Environments[envId].Websites[siteId].Scan.Quick.Execute.PostAsync` | returns scan results, no state change |
| `Analytics.Actions[actionName].PostAsync` | per-action body — depends on action; treat case-by-case |

## Bucket B — Idempotent toggles/refreshers (low risk)

Side effect exists but is reversible / re-runnable.

| Builder | Notes |
|---|---|
| `Licensing.BillingPolicies[id].RefreshProvisioningStatus.PostAsync` | re-reads provisioning state |
| `Copilotstudio…BotQuarantine.SetAsQuarantined / SetAsUnquarantined.PostAsync` | flips a flag, easy to revert |
| `Powerpages…Websites[id].Start / Stop / Restart.PostAsync` | start/stop the portal — disruptive but reversible |
| `Powerpages…Websites[id].EnableWaf / DisableWaf.PostAsync` | toggles WAF |

## Bucket C — Create/Update/Delete on small, scoped resources (sandbox env required)

Need a target env we own and don't care about.

| Builder | Notes |
|---|---|
| `Environmentmanagement.EnvironmentGroups.PostAsync` | create env group (cheap, deletable) |
| `Environmentmanagement.EnvironmentGroups[id].WithGroupItem.PutAsync / DeleteAsync` | modify/delete env group |
| `Environmentmanagement.EnvironmentGroups[id].AddEnvironment[envId] / RemoveEnvironment[envId].PostAsync` | reversible env↔group assignment |
| `Authorization.RoleAssignments.PostAsync` and `RoleAssignments[id].DeleteAsync` | grant/revoke a role assignment |
| `Governance.RuleBasedPolicies.PostAsync`, `…[id].PutAsync`, `…Assignments.PostAsync` | DLP-style policies — keep on a sandbox group |
| `Licensing.IsvContracts.PostAsync`, `…[id].PutAsync / DeleteAsync` | currently 403 for this user anyway |
| `Licensing.BillingPolicies.PostAsync`, `…[id].PutAsync / DeleteAsync` | needs Azure subscription wiring |
| `Licensing.BillingPolicies[id].Environments.Add / Remove.PostAsync` | env↔policy attach/detach |
| `Licensing.Environments[envId].Allocations.PatchAsync` | rewrites license allocation |
| `Powerpages.Environments[envId].Websites.PostAsync` | provisions a Power Pages site (~minutes) |
| `Powerpages.Environments[envId].Websites[id]…UpdatePortalSecurityGroup / UpdateSiteVisibility / SetPortalDataModelVersion / SetPortalBootstrapV5Enabled.PostAsync/PatchAsync` | site-config tweaks |
| `Powerpages.Environments[envId].Websites[id]…Ipaddressrules / CreateWafRules / DeleteWafCustomRules.PostAsync/PutAsync` | WAF rule edits |
| `Powerpages.Environments[envId].Websites[id].WebsitesItem.DeleteAsync` | delete a Power Pages site |
| `Appmanagement.Environments[envId].ApplicationPackages[uniqueName].Install.PostAsync` | install an app package |
| `Copilotstudio.Environments[envId].Bots[botId].Api.BotAdminOperations.DeleteAsync` | bot admin op |
| `Usermanagement.Environments[envId].User.ApplyAdminRole.PostAsync` | grant admin role on the env |
| `Environmentmanagement.Environments[envId].Settings.PostAsync / PatchAsync` | env settings — often blocks running apps |

## Bucket D — High blast radius (env-level lifecycle) — explicit per-call allow-list

Each one is a tenant-impacting operation. Never run in a sweep.

| Builder | Notes |
|---|---|
| `Environmentmanagement.Environments[envId].EnvironmentItem.DeleteAsync` | delete env (recoverable for ~7d, then permanent) |
| `Environmentmanagement.Environments[envId].Recover.PostAsync` | undo soft-delete |
| `Environmentmanagement.Environments[envId].Disable / Enable.PostAsync` | toggles env state |
| `Environmentmanagement.Environments[envId].Copy.PostAsync` | copies an env onto another env |
| `Environmentmanagement.Environments[envId].Restore.PostAsync` | restores from backup, overwrites target |
| `Environmentmanagement.Environments[envId].Backups.PostAsync` | trigger backup (we already saw OperationAlreadyInProgress on SeaCass) |
| `Environmentmanagement.Environments[envId].Backups[backupId].DeleteAsync` | delete backup |
| `Environmentmanagement.Environments[envId].DisasterRecoveryDrill.PostAsync` | DR drill — multi-region, real |
| `Environmentmanagement.Environments[envId].EnableDisasterRecovery / DisableDisasterRecovery.PostAsync` | DR config |
| `Environmentmanagement.Environments[envId].ForceFailover.PostAsync` | actually fails over the env to its DR pair |
| `Environmentmanagement.Environments[envId].ModifySku.PostAsync` | changes env SKU — billing change |
| `Environmentmanagement.Environments[envId].Governancesetting.Disablemanaged / Enablemanaged.PostAsync` | toggle Managed Env |

Note: most Bucket D builders **expose `ValidateOnly` / `ValidateProperties` QPs**.
That gives us a clean dry-run path: set `ValidateOnly = true` and the server
returns the would-be result without applying it. The probe should take that
route for Bucket D when it ever exercises them.

---

## What's NOT in the SDK that you might expect

There's no `Environmentmanagement.Environments.PostAsync` (create env). Env
provisioning is served by **BAP** (`api.bap.microsoft.com`), not PPAC, and is
already covered by the `BapFallback.cs` we have under `VerseOps.SdkRunner`.

---

## Proposed next-step plan

1. **Bucket A + B sweep** (additive to the existing GET probe): same reflective
   walker, same per-call timeout, but also invoke `Post` on each Bucket A/B
   builder reachable from the harvested id pool. Outputs `crud-results.json`.
   No new bodies needed (most of these are body-less).
2. **Bucket D dry-run**: enumerate Bucket D builders, build a minimum body via
   reflection, set `ValidateOnly = true` via the existing QP setter, fire. Server
   returns validation outcome; nothing mutates. Outputs `crud-validate.json`.
3. **Bucket C** is deferred until you point at a sandbox env id and explicitly
   allow-list which builders to exercise. Real bodies, real side effects.
