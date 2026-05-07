CREATE TABLE IF NOT EXISTS gov_environment (
    env_id              TEXT NOT NULL PRIMARY KEY,
    display_name        TEXT,
    sku                 TEXT,
    region              TEXT,
    provisioning_state  TEXT,
    version             TEXT,
    instance_url        TEXT,
    is_default          INTEGER NOT NULL DEFAULT 0,
    created_utc         TEXT,
    last_synced_utc     TEXT NOT NULL,
    raw_json            TEXT,
    -- Azure AD security group GUID (NULL = env open to whole tenant).
    -- Added 2026-05 for the "Only my environments" Graph membership filter.
    security_group_id     TEXT,
    -- 1 = Power Platform "Managed Environments" enabled (ProtectionLevel=Standard).
    is_managed_environment INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS gov_capacity (
    env_id          TEXT NOT NULL,
    capacity_type   TEXT NOT NULL,
    actual          REAL,
    rated           REAL,
    total           REAL,
    last_synced_utc TEXT NOT NULL,
    PRIMARY KEY (env_id, capacity_type)
);

-- Tenant-wide storage / API capacity from PPAC TenantCapacity endpoint.
-- One row per CapacityType (Database, File, Log, FinOpsDatabase, ApiCallCount, ...).
-- All values are stored in the units the SDK reports them in (Database/File/Log = MB).
CREATE TABLE IF NOT EXISTS gov_tenant_capacity (
    capacity_type   TEXT NOT NULL PRIMARY KEY,
    units           TEXT,
    max_capacity    REAL,
    total_capacity  REAL,
    consumed        REAL,
    status          TEXT,
    last_synced_utc TEXT NOT NULL
);

-- Power Platform tenant-wide asset catalog. Populated in one POST against the
-- Power Platform Inventory API:
--   POST https://api.powerplatform.com/resourcequery/resources/query
--        ?api-version=2024-10-01
-- Returns every canvas app, model-driven app, code app, cloud flow,
-- agent flow, and Copilot Studio agent across every env in the tenant.
-- We strip the "microsoft.<vendor>/" prefix from `type` and store just the
-- suffix in asset_type (canvasapps / cloudflows / agents / etc.).
CREATE TABLE IF NOT EXISTS gov_asset (
    asset_type      TEXT NOT NULL,
    asset_id        TEXT NOT NULL,
    env_id          TEXT,
    display_name    TEXT,
    owner_id        TEXT,
    created_by      TEXT,
    region          TEXT,
    created_utc     TEXT,
    modified_utc    TEXT,
    is_quarantined  INTEGER,
    last_synced_utc TEXT NOT NULL,
    PRIMARY KEY (asset_type, asset_id)
);

CREATE INDEX IF NOT EXISTS ix_gov_environment_synced ON gov_environment(last_synced_utc);
CREATE INDEX IF NOT EXISTS ix_gov_capacity_env       ON gov_capacity(env_id);
CREATE INDEX IF NOT EXISTS ix_gov_asset_env          ON gov_asset(env_id);
CREATE INDEX IF NOT EXISTS ix_gov_asset_type         ON gov_asset(asset_type);

-- Per-environment Dataverse drill-down cache (solutions, Power Pages,
-- users + per-asset enrichments such as Status / IsPremium / DlpStatus /
-- SolutionName / IsManaged). Populated lazily the first time an env row
-- is expanded, hydrated synchronously on every subsequent expand. The
-- per-env "Refresh" button is the only thing that invalidates a row.
-- Payload is a JSON snapshot of EnvDetailsSnapshot (see
-- VerseOps.App.Inventory.Services.EnvDetailsSnapshot) — schema-less by
-- design so we can grow the snapshot without DB migrations.
CREATE TABLE IF NOT EXISTS gov_env_details (
    env_id          TEXT NOT NULL PRIMARY KEY,
    payload_json    TEXT NOT NULL,
    last_synced_utc TEXT NOT NULL
);

