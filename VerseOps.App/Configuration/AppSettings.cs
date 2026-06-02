using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VerseOps.App.Configuration;

/// <summary>
/// Customer-overridable app configuration. Loaded once at startup and held as
/// a singleton via <see cref="Current"/>. Holds tenant + client-id values so
/// each customer can BYO Entra app registration without rebuilding.
///
/// <para>Load priority (highest wins):</para>
/// <list type="number">
///   <item>Environment variables: <c>VERSEOPS_TENANT_ID</c>,
///         <c>VERSEOPS_PUBLIC_CLIENT_ID</c>, <c>VERSEOPS_APP_CLIENT_ID</c>.</item>
///   <item><c>%LOCALAPPDATA%\VerseOps\appsettings.json</c> — written by the
///         in-app "Save defaults" button; survives reinstalls.</item>
///   <item><c>appsettings.local.json</c> next to the EXE — shipped by ops
///         teams that pre-configure builds before handing them out.</item>
///   <item>Hard-coded defaults from <see cref="AppConstants"/>.</item>
/// </list>
///
/// <para><b>Secrets are NEVER persisted.</b> App-only client secrets entered
/// in the API Explorer live only in memory for the session. Persisting them
/// to disk would defeat MSAL's secret-handling guarantees and create a
/// stealable credential blob on the user's laptop.</para>
/// </summary>
public sealed class AppSettings
{
    private const string EnvTenantId       = "VERSEOPS_TENANT_ID";
    private const string EnvPublicClientId = "VERSEOPS_PUBLIC_CLIENT_ID";
    private const string EnvAppClientId    = "VERSEOPS_APP_CLIENT_ID";
    private const string FileName          = "appsettings.json";
    private const string LocalFileName     = "appsettings.local.json";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The process-wide singleton. Populated by <see cref="LoadFromDisk"/>.</summary>
    public static AppSettings Current { get; private set; } = new();

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = AppConstants.DefaultTenant;

    [JsonPropertyName("publicClientId")]
    public string PublicClientId { get; set; } = AppConstants.AzureCliPublicClientId;

    /// <summary>Optional. App-only / confidential-client id used by the API Explorer's App-only mode.</summary>
    [JsonPropertyName("appOnlyClientId")]
    public string AppOnlyClientId { get; set; } = string.Empty;

    /// <summary>Where <see cref="Save"/> writes. Survives reinstalls.</summary>
    public static string UserSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VerseOps", FileName);

    /// <summary>Optional repo/EXE-adjacent overlay used by ops teams shipping pre-configured builds.</summary>
    public static string ExeSettingsPath => Path.Combine(AppContext.BaseDirectory, LocalFileName);

    /// <summary>
    /// Populate <see cref="Current"/> by merging the four sources. Never throws;
    /// any per-source failure (missing file, bad JSON, unreadable env var) is
    /// swallowed and the next-lower priority source wins for that field.
    /// </summary>
    public static void LoadFromDisk()
    {
        var merged = new AppSettings();

        // Priority 3 (lowest above defaults): EXE-adjacent overlay
        TryMergeFromFile(ExeSettingsPath, merged);

        // Priority 2: user-scope file under %LOCALAPPDATA%
        TryMergeFromFile(UserSettingsPath, merged);

        // Priority 1: env vars override everything
        var envTenant = Environment.GetEnvironmentVariable(EnvTenantId);
        if (!string.IsNullOrWhiteSpace(envTenant)) merged.TenantId = envTenant.Trim();

        var envPublic = Environment.GetEnvironmentVariable(EnvPublicClientId);
        if (!string.IsNullOrWhiteSpace(envPublic)) merged.PublicClientId = envPublic.Trim();

        var envApp = Environment.GetEnvironmentVariable(EnvAppClientId);
        if (!string.IsNullOrWhiteSpace(envApp)) merged.AppOnlyClientId = envApp.Trim();

        Current = merged;
    }

    private static void TryMergeFromFile(string path, AppSettings into)
    {
        try
        {
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return;
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
            if (loaded is null) return;
            if (!string.IsNullOrWhiteSpace(loaded.TenantId))        into.TenantId        = loaded.TenantId.Trim();
            if (!string.IsNullOrWhiteSpace(loaded.PublicClientId))  into.PublicClientId  = loaded.PublicClientId.Trim();
            if (!string.IsNullOrWhiteSpace(loaded.AppOnlyClientId)) into.AppOnlyClientId = loaded.AppOnlyClientId.Trim();
        }
        catch
        {
            // Intentional: a corrupt settings file must never block app launch.
            // The user can re-save from the in-app config UI to repair.
        }
    }

    /// <summary>
    /// Persist the current values to <see cref="UserSettingsPath"/>. Creates
    /// the directory if missing. Throws on IO failure so the caller can show
    /// the user a "Save failed" message — silent save failures are worse than
    /// loud ones for a config UI.
    /// </summary>
    public void Save()
    {
        var dir = Path.GetDirectoryName(UserSettingsPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, JsonOpts);
        File.WriteAllText(UserSettingsPath, json);
    }
}
