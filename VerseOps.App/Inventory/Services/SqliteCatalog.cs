using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using VerseOps.App.Inventory.Models;

namespace VerseOps.App.Inventory.Services;

/// <summary>
/// Local SQLite catalog for inventory data. Lives at
/// %LOCALAPPDATA%\VerseOps\inventory.db. All writes are wrapped in a single
/// transaction per refresh so the UI never sees partial updates.
/// </summary>
public sealed class SqliteCatalog
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public SqliteCatalog()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VerseOps");
        Directory.CreateDirectory(dir);
        DatabasePath = Path.Combine(dir, "inventory.db");
        _connectionString = $"Data Source={DatabasePath}";
    }

    public void EnsureCreated()
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "Inventory", "Sql", "schema.sql");
        var ddl = File.ReadAllText(schemaPath);

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        cmd.ExecuteNonQuery();

        // Idempotent column migrations for DBs created by earlier app versions.
        // SQLite has no IF NOT EXISTS for ADD COLUMN, so we attempt and swallow
        // the "duplicate column name" error if the column is already there.
        TryAddColumn(conn, "gov_environment", "security_group_id", "TEXT");
        TryAddColumn(conn, "gov_environment", "is_managed_environment", "INTEGER NOT NULL DEFAULT 0");
    }

    /// <summary>
    /// Idempotent <c>ALTER TABLE ... ADD COLUMN</c>. Used to grow tables
    /// across app versions without a full DB rebuild.
    /// </summary>
    private static void TryAddColumn(SqliteConnection conn, string table, string column, string typeDecl)
    {
        try
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeDecl};";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Column already exists — earlier run added it. Safe to ignore.
        }
    }

    /// <summary>
    /// Replace all environments + capacity + (optionally) tenant capacity and assets
    /// in one transaction. Pass <c>assets = null</c> to leave the asset table untouched
    /// (e.g. when only refreshing capacity).
    /// </summary>
    public void ReplaceAll(
        IReadOnlyList<EnvironmentRow> envs,
        IReadOnlyList<CapacityEntry> capacities,
        IReadOnlyList<TenantCapacityEntry>? tenantCapacities = null,
        IReadOnlyList<AssetRow>? assets = null)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();

        ExecNonQuery(conn, tx, "DELETE FROM gov_capacity");
        ExecNonQuery(conn, tx, "DELETE FROM gov_tenant_capacity");
        if (assets is not null)
            ExecNonQuery(conn, tx, "DELETE FROM gov_asset");
        ExecNonQuery(conn, tx, "DELETE FROM gov_environment");

        InsertEnvironments(conn, tx, envs);
        InsertCapacity(conn, tx, capacities);
        if (tenantCapacities is { Count: > 0 })
            InsertTenantCapacity(conn, tx, tenantCapacities);
        if (assets is { Count: > 0 })
            InsertAssets(conn, tx, assets);

        tx.Commit();
    }

    // ------------------------------------------------------------------
    // Per-phase scoped writers. Used by the parallel/incremental refresh
    // path so each phase can land in SQLite the moment its data is ready,
    // without waiting for the slowest phase (Inventory API) to finish.
    // Each is its own transaction.
    // ------------------------------------------------------------------

    /// <summary>
    /// Replace just the environments table. Capacity / tenant capacity /
    /// asset rows are left untouched.
    /// </summary>
    public void ReplaceEnvironments(IReadOnlyList<EnvironmentRow> envs)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();
        ExecNonQuery(conn, tx, "DELETE FROM gov_environment");
        InsertEnvironments(conn, tx, envs);
        tx.Commit();
    }

    /// <summary>Replace just the per-env capacity rows.</summary>
    public void ReplaceCapacity(IReadOnlyList<CapacityEntry> capacities)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();
        ExecNonQuery(conn, tx, "DELETE FROM gov_capacity");
        InsertCapacity(conn, tx, capacities);
        tx.Commit();
    }

    /// <summary>Replace just the tenant-wide capacity rollup rows.</summary>
    public void ReplaceTenantCapacity(IReadOnlyList<TenantCapacityEntry> tenantCapacities)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();
        ExecNonQuery(conn, tx, "DELETE FROM gov_tenant_capacity");
        InsertTenantCapacity(conn, tx, tenantCapacities);
        tx.Commit();
    }

    /// <summary>Replace just the asset rows.</summary>
    public void ReplaceAssets(IReadOnlyList<AssetRow> assets)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();
        ExecNonQuery(conn, tx, "DELETE FROM gov_asset");
        InsertAssets(conn, tx, assets);
        tx.Commit();
    }

    // ------------------------------------------------------------------
    // Insert helpers — extracted from ReplaceAll so the per-phase writers
    // above and the legacy ReplaceAll can share the same parameter binding.
    // ------------------------------------------------------------------

    private static void InsertEnvironments(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<EnvironmentRow> envs)
    {
        using var insertEnv = conn.CreateCommand();
        insertEnv.Transaction = tx;
        insertEnv.CommandText = @"
            INSERT INTO gov_environment
                (env_id, display_name, sku, region, provisioning_state, version,
                 instance_url, is_default, created_utc, last_synced_utc, raw_json,
                 security_group_id, is_managed_environment)
            VALUES (@env_id, @display_name, @sku, @region, @provisioning_state, @version,
                    @instance_url, @is_default, @created_utc, @last_synced_utc, @raw_json,
                    @security_group_id, @is_managed_environment);";
        var pEnv = insertEnv.Parameters;
        pEnv.Add("@env_id", SqliteType.Text);
        pEnv.Add("@display_name", SqliteType.Text);
        pEnv.Add("@sku", SqliteType.Text);
        pEnv.Add("@region", SqliteType.Text);
        pEnv.Add("@provisioning_state", SqliteType.Text);
        pEnv.Add("@version", SqliteType.Text);
        pEnv.Add("@instance_url", SqliteType.Text);
        pEnv.Add("@is_default", SqliteType.Integer);
        pEnv.Add("@created_utc", SqliteType.Text);
        pEnv.Add("@last_synced_utc", SqliteType.Text);
        pEnv.Add("@raw_json", SqliteType.Text);
        pEnv.Add("@security_group_id", SqliteType.Text);
        pEnv.Add("@is_managed_environment", SqliteType.Integer);

        foreach (var e in envs)
        {
            pEnv["@env_id"].Value = e.EnvId;
            pEnv["@display_name"].Value = (object?)e.DisplayName ?? DBNull.Value;
            pEnv["@sku"].Value = (object?)e.Sku ?? DBNull.Value;
            pEnv["@region"].Value = (object?)e.Region ?? DBNull.Value;
            pEnv["@provisioning_state"].Value = (object?)e.ProvisioningState ?? DBNull.Value;
            pEnv["@version"].Value = (object?)e.Version ?? DBNull.Value;
            pEnv["@instance_url"].Value = (object?)e.InstanceUrl ?? DBNull.Value;
            pEnv["@is_default"].Value = e.IsDefault ? 1 : 0;
            pEnv["@created_utc"].Value = e.CreatedUtc.HasValue
                ? e.CreatedUtc.Value.ToString("o", CultureInfo.InvariantCulture)
                : (object)DBNull.Value;
            pEnv["@last_synced_utc"].Value = e.LastSyncedUtc.ToString("o", CultureInfo.InvariantCulture);
            pEnv["@raw_json"].Value = DBNull.Value;
            pEnv["@security_group_id"].Value = (object?)e.SecurityGroupId ?? DBNull.Value;
            pEnv["@is_managed_environment"].Value = e.IsManagedEnvironment ? 1 : 0;
            insertEnv.ExecuteNonQuery();
        }
    }

    private static void InsertCapacity(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<CapacityEntry> capacities)
    {
        using var insertCap = conn.CreateCommand();
        insertCap.Transaction = tx;
        insertCap.CommandText = @"
            INSERT INTO gov_capacity
                (env_id, capacity_type, actual, rated, total, last_synced_utc)
            VALUES (@env_id, @capacity_type, @actual, @rated, @total, @last_synced_utc);";
        var pCap = insertCap.Parameters;
        pCap.Add("@env_id", SqliteType.Text);
        pCap.Add("@capacity_type", SqliteType.Text);
        pCap.Add("@actual", SqliteType.Real);
        pCap.Add("@rated", SqliteType.Real);
        pCap.Add("@total", SqliteType.Real);
        pCap.Add("@last_synced_utc", SqliteType.Text);

        foreach (var c in capacities)
        {
            pCap["@env_id"].Value = c.EnvId;
            pCap["@capacity_type"].Value = c.CapacityType;
            pCap["@actual"].Value = (object?)c.Actual ?? DBNull.Value;
            pCap["@rated"].Value = (object?)c.Rated ?? DBNull.Value;
            pCap["@total"].Value = (object?)c.Total ?? DBNull.Value;
            pCap["@last_synced_utc"].Value = c.LastSyncedUtc.ToString("o", CultureInfo.InvariantCulture);
            insertCap.ExecuteNonQuery();
        }
    }

    private static void InsertTenantCapacity(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<TenantCapacityEntry> tenantCapacities)
    {
        if (tenantCapacities.Count == 0) return;
        using var insertTenant = conn.CreateCommand();
        insertTenant.Transaction = tx;
        insertTenant.CommandText = @"
            INSERT INTO gov_tenant_capacity
                (capacity_type, units, max_capacity, total_capacity, consumed, status, last_synced_utc)
            VALUES (@capacity_type, @units, @max_capacity, @total_capacity, @consumed, @status, @last_synced_utc);";
        var p = insertTenant.Parameters;
        p.Add("@capacity_type", SqliteType.Text);
        p.Add("@units", SqliteType.Text);
        p.Add("@max_capacity", SqliteType.Real);
        p.Add("@total_capacity", SqliteType.Real);
        p.Add("@consumed", SqliteType.Real);
        p.Add("@status", SqliteType.Text);
        p.Add("@last_synced_utc", SqliteType.Text);

        foreach (var t in tenantCapacities)
        {
            p["@capacity_type"].Value = t.CapacityType;
            p["@units"].Value = (object?)t.Units ?? DBNull.Value;
            p["@max_capacity"].Value = (object?)t.MaxCapacity ?? DBNull.Value;
            p["@total_capacity"].Value = (object?)t.TotalCapacity ?? DBNull.Value;
            p["@consumed"].Value = (object?)t.Consumed ?? DBNull.Value;
            p["@status"].Value = (object?)t.Status ?? DBNull.Value;
            p["@last_synced_utc"].Value = t.LastSyncedUtc.ToString("o", CultureInfo.InvariantCulture);
            insertTenant.ExecuteNonQuery();
        }
    }

    private static void InsertAssets(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<AssetRow> assets)
    {
        if (assets.Count == 0) return;
        using var insertAsset = conn.CreateCommand();
        insertAsset.Transaction = tx;
        insertAsset.CommandText = @"
            INSERT OR REPLACE INTO gov_asset
                (asset_type, asset_id, env_id, display_name, owner_id, created_by,
                 region, created_utc, modified_utc, is_quarantined, last_synced_utc)
            VALUES (@asset_type, @asset_id, @env_id, @display_name, @owner_id, @created_by,
                    @region, @created_utc, @modified_utc, @is_quarantined, @last_synced_utc);";
        var p = insertAsset.Parameters;
        p.Add("@asset_type", SqliteType.Text);
        p.Add("@asset_id", SqliteType.Text);
        p.Add("@env_id", SqliteType.Text);
        p.Add("@display_name", SqliteType.Text);
        p.Add("@owner_id", SqliteType.Text);
        p.Add("@created_by", SqliteType.Text);
        p.Add("@region", SqliteType.Text);
        p.Add("@created_utc", SqliteType.Text);
        p.Add("@modified_utc", SqliteType.Text);
        p.Add("@is_quarantined", SqliteType.Integer);
        p.Add("@last_synced_utc", SqliteType.Text);

        foreach (var a in assets)
        {
            p["@asset_type"].Value = a.AssetType;
            p["@asset_id"].Value = a.AssetId;
            p["@env_id"].Value = (object?)a.EnvId ?? DBNull.Value;
            p["@display_name"].Value = (object?)a.DisplayName ?? DBNull.Value;
            p["@owner_id"].Value = (object?)a.OwnerId ?? DBNull.Value;
            p["@created_by"].Value = (object?)a.CreatedBy ?? DBNull.Value;
            p["@region"].Value = (object?)a.Region ?? DBNull.Value;
            p["@created_utc"].Value = a.CreatedUtc.HasValue
                ? a.CreatedUtc.Value.ToString("o", CultureInfo.InvariantCulture)
                : (object)DBNull.Value;
            p["@modified_utc"].Value = a.ModifiedUtc.HasValue
                ? a.ModifiedUtc.Value.ToString("o", CultureInfo.InvariantCulture)
                : (object)DBNull.Value;
            p["@is_quarantined"].Value = a.IsQuarantined.HasValue
                ? (a.IsQuarantined.Value ? 1 : 0)
                : (object)DBNull.Value;
            p["@last_synced_utc"].Value = a.LastSyncedUtc.ToString("o", CultureInfo.InvariantCulture);
            insertAsset.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Read joined env + capacity for the inventory grid.
    /// </summary>
    public IReadOnlyList<EnvironmentRow> ReadAllEnvironments()
    {
        var byId = new Dictionary<string, EnvironmentRow>(StringComparer.OrdinalIgnoreCase);

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT env_id, display_name, sku, region, provisioning_state, version,
                       instance_url, is_default, created_utc, last_synced_utc,
                       security_group_id, is_managed_environment
                FROM   gov_environment
                ORDER  BY display_name COLLATE NOCASE;";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var row = new EnvironmentRow
                {
                    EnvId = rdr.GetString(0),
                    DisplayName = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                    Sku = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    Region = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                    ProvisioningState = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    Version = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    InstanceUrl = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                    IsDefault = !rdr.IsDBNull(7) && rdr.GetInt64(7) != 0,
                    CreatedUtc = rdr.IsDBNull(8)
                        ? null
                        : DateTime.Parse(rdr.GetString(8), CultureInfo.InvariantCulture,
                                         DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                    LastSyncedUtc = DateTime.Parse(rdr.GetString(9), CultureInfo.InvariantCulture,
                                                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                    SecurityGroupId = rdr.IsDBNull(10) ? null : rdr.GetString(10),
                    IsManagedEnvironment = !rdr.IsDBNull(11) && rdr.GetInt64(11) != 0
                };
                byId[row.EnvId] = row;
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT env_id, capacity_type, actual, rated
                FROM   gov_capacity
                WHERE  capacity_type IN ('Database','File','Log','FinOpsDatabase','FinOpsFile');";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                if (!byId.TryGetValue(rdr.GetString(0), out var row)) continue;
                // BAP reports per-env capacity in MB (same unit PPAC uses for
                // tenant capacity). Convert to GB for the dashboard columns.
                double? actualGb = rdr.IsDBNull(2) ? (double?)null : rdr.GetDouble(2) / 1024.0;
                double? ratedGb  = rdr.IsDBNull(3) ? (double?)null : rdr.GetDouble(3) / 1024.0;
                switch (rdr.GetString(1))
                {
                    case "Database":       row.DatabaseGb       = actualGb; row.DatabaseLimitGb       = ratedGb; break;
                    case "File":           row.FileGb           = actualGb; row.FileLimitGb           = ratedGb; break;
                    case "Log":            row.LogGb            = actualGb; row.LogLimitGb            = ratedGb; break;
                    case "FinOpsDatabase": row.FinOpsDatabaseGb = actualGb; row.FinOpsDatabaseLimitGb = ratedGb; break;
                    case "FinOpsFile":     row.FinOpsFileGb     = actualGb; row.FinOpsFileLimitGb     = ratedGb; break;
                }
            }
        }

        return byId.Values.ToList();
    }

    /// <summary>Read all tenant capacity rows (Database/File/Log/...) currently persisted.</summary>
    public IReadOnlyList<TenantCapacityEntry> ReadAllTenantCapacity()
    {
        var list = new List<TenantCapacityEntry>();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT capacity_type, units, max_capacity, total_capacity, consumed, status, last_synced_utc
            FROM   gov_tenant_capacity;";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new TenantCapacityEntry
            {
                CapacityType   = rdr.GetString(0),
                Units          = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                MaxCapacity    = rdr.IsDBNull(2) ? (double?)null : rdr.GetDouble(2),
                TotalCapacity  = rdr.IsDBNull(3) ? (double?)null : rdr.GetDouble(3),
                Consumed       = rdr.IsDBNull(4) ? (double?)null : rdr.GetDouble(4),
                Status         = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                LastSyncedUtc  = DateTime.Parse(rdr.GetString(6), CultureInfo.InvariantCulture,
                                                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            });
        }
        return list;
    }

    /// <summary>
    /// Read every cached asset (apps + flows + agents) tenant-wide. Caller
    /// groups by <see cref="AssetRow.EnvId"/> for the per-env expander UI.
    /// </summary>
    public IReadOnlyList<AssetRow> ReadAllAssets()
    {
        var list = new List<AssetRow>();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT asset_type, asset_id, env_id, display_name, owner_id, created_by,
                   region, created_utc, modified_utc, is_quarantined, last_synced_utc
            FROM   gov_asset;";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new AssetRow
            {
                AssetType     = rdr.GetString(0),
                AssetId       = rdr.GetString(1),
                EnvId         = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                DisplayName   = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                OwnerId       = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                CreatedBy     = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                Region        = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                CreatedUtc    = rdr.IsDBNull(7) ? (DateTime?)null
                                : DateTime.Parse(rdr.GetString(7), CultureInfo.InvariantCulture,
                                                 DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                ModifiedUtc   = rdr.IsDBNull(8) ? (DateTime?)null
                                : DateTime.Parse(rdr.GetString(8), CultureInfo.InvariantCulture,
                                                 DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                IsQuarantined = rdr.IsDBNull(9) ? (bool?)null : rdr.GetInt64(9) != 0,
                LastSyncedUtc = DateTime.Parse(rdr.GetString(10), CultureInfo.InvariantCulture,
                                               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            });
        }
        return list;
    }

    public DateTime? LastRefreshedUtc()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(last_synced_utc) FROM gov_environment;";
        var v = cmd.ExecuteScalar() as string;
        return string.IsNullOrEmpty(v)
            ? null
            : DateTime.Parse(v, CultureInfo.InvariantCulture,
                             DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    private static void ExecNonQuery(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
