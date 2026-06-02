using System.IO;
using System.Text.Json;
using VerseOps.App.Sdk;
using Xunit;
using Xunit.Abstractions;

namespace VerseOps.SdkTests;

/// <summary>
/// End-to-end CRUD round-trip against the live tenant on a throwaway
/// EnvironmentGroup. Sequence: POST (create) → GET (read back) → PUT
/// (rename) → DELETE (cleanup). A try/finally guarantees DELETE runs even
/// if a middle step fails so we don't leak fixture state into the tenant.
///
/// User explicitly authorised creation. This test only ever touches groups
/// it created itself in this run; existing groups owned by the tester are
/// never read, modified, or enumerated for mutation.
/// </summary>
[Collection(SdkAuthCollection.Name)]
public sealed class EnvironmentGroupCrudTests
{
    private readonly SdkAuthFixture _fx;
    private readonly ITestOutputHelper _out;

    public EnvironmentGroupCrudTests(SdkAuthFixture fx, ITestOutputHelper @out)
    {
        _fx  = fx;
        _out = @out;
    }

    [SkippableFact]
    public async Task EnvironmentGroup_Create_Get_Rename_Delete()
    {
        Skip.IfNot(_fx.SignedIn, $"Auth fixture did not sign in: {_fx.SignInError}");

        var executor = new SdkExecutor(_fx.Auth);
        var ct = CancellationToken.None;

        // Locate the four ops in the reflected catalog. PathText matching keeps
        // the test resilient to internal builder-class renames.
        var postOp   = FindOp("ServiceClient.Environmentmanagement.EnvironmentGroups",                "POST", needsBody: true);
        var getOp    = FindOp("ServiceClient.Environmentmanagement.EnvironmentGroups.Item[environmentGroupId]", "GET",    needsBody: false);
        var putOp    = FindOp("ServiceClient.Environmentmanagement.EnvironmentGroups.Item[environmentGroupId]", "PUT",    needsBody: true);
        var deleteOp = FindOp("ServiceClient.Environmentmanagement.EnvironmentGroups.Item[environmentGroupId]", "DELETE", needsBody: false);

        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var originalName = $"verseops-rig-{stamp}";
        var renamedName  = $"verseops-rig-{stamp}-renamed";

        // -------- 1. CREATE --------
        var createBody = JsonSerializer.Serialize(new
        {
            displayName = originalName,
            description = "Throwaway group created by VerseOps.SdkTests EnvironmentGroupCrudTests. Safe to delete.",
        });
        _out.WriteLine($"[CREATE] POST EnvironmentGroups  body={createBody}");
        var create = await executor.ExecuteAsync(postOp, EmptyIdx(), createBody, ct).ConfigureAwait(false);
        _out.WriteLine($"[CREATE] STATUS={create.StatusText}  TIME={create.ElapsedMs}ms");
        _out.WriteLine("[CREATE] BODY:");
        _out.WriteLine(Trunc(create.Body));
        Assert.True(create.Success, $"Create failed: {create.StatusText} — {create.Body}");

        var groupId = ExtractId(create.Body);
        Assert.False(string.IsNullOrEmpty(groupId), $"Could not extract group id from create response.\nBody: {create.Body}");
        _out.WriteLine($"[CREATE] groupId={groupId}");

        var indexer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EnvironmentGroups"] = groupId!
        };

        try
        {
            // -------- 2. GET --------
            _out.WriteLine($"[GET   ] GET EnvironmentGroups[{groupId}]");
            var get = await executor.ExecuteAsync(getOp, indexer, jsonBody: null, ct).ConfigureAwait(false);
            _out.WriteLine($"[GET   ] STATUS={get.StatusText}  TIME={get.ElapsedMs}ms");
            _out.WriteLine("[GET   ] BODY:");
            _out.WriteLine(Trunc(get.Body));
            Assert.True(get.Success, $"Get failed: {get.StatusText}");

            // -------- 3. PUT (rename) --------
            // Round-trip the body the server returned and only swap displayName, so
            // we don't strip any server-side fields the PUT validation requires.
            var putBody = SwapDisplayName(get.Body, renamedName);
            _out.WriteLine($"[PUT   ] PUT EnvironmentGroups[{groupId}]  newName={renamedName}");
            _out.WriteLine($"[PUT   ] body={Trunc(putBody, 600)}");
            var put = await executor.ExecuteAsync(putOp, indexer, putBody, ct).ConfigureAwait(false);
            _out.WriteLine($"[PUT   ] STATUS={put.StatusText}  TIME={put.ElapsedMs}ms");
            _out.WriteLine("[PUT   ] BODY:");
            _out.WriteLine(Trunc(put.Body));
            Assert.True(put.Success, $"Put failed: {put.StatusText} — {put.Body}");

            // -------- 4. GET AGAIN (verify rename) --------
            var getAfter = await executor.ExecuteAsync(getOp, indexer, jsonBody: null, ct).ConfigureAwait(false);
            _out.WriteLine($"[VERIFY] STATUS={getAfter.StatusText}  TIME={getAfter.ElapsedMs}ms");
            _out.WriteLine("[VERIFY] BODY:");
            _out.WriteLine(Trunc(getAfter.Body));
            Assert.True(getAfter.Success, $"Get-after-put failed: {getAfter.StatusText}");
            var observedName = ExtractDisplayName(getAfter.Body);
            Assert.Equal(renamedName, observedName);
        }
        finally
        {
            // -------- 5. DELETE (cleanup) --------
            _out.WriteLine($"[DELETE] DELETE EnvironmentGroups[{groupId}]");
            var del = await executor.ExecuteAsync(deleteOp, indexer, jsonBody: null, ct).ConfigureAwait(false);
            _out.WriteLine($"[DELETE] STATUS={del.StatusText}  TIME={del.ElapsedMs}ms");
            if (!del.Success)
            {
                _out.WriteLine($"[DELETE] WARNING — cleanup failed, group {groupId} may persist. Body:\n{del.Body}");
            }
        }
    }

    private static SdkOp FindOp(string pathText, string verb, bool needsBody)
    {
        var op = SdkCatalog.Operations.FirstOrDefault(o =>
            o.PathText == pathText &&
            o.HttpMethod == verb &&
            ((needsBody && o.BodyType != null) || (!needsBody && o.BodyType == null)));
        if (op is null)
            throw new InvalidOperationException(
                $"SdkCatalog has no {verb} op at {pathText} (needsBody={needsBody}). " +
                "SDK shape may have changed; update the test or PathText.");
        return op;
    }

    private static IReadOnlyDictionary<string, string> EmptyIdx() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static string? ExtractId(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            // Render() in SdkExecutor uses PascalCase, but PPAC server responses
            // come back camelCase. Try both, then fall back to "name" (some PPAC
            // resources use Name as the id slot).
            foreach (var key in new[] { "Id", "id", "Name", "name" })
            {
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
        }
        catch { }
        return null;
    }

    private static string? ExtractDisplayName(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var key in new[] { "DisplayName", "displayName" })
            {
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
        }
        catch { }
        return null;
    }

    private static string SwapDisplayName(string body, string newName)
    {
        // Build a flat JSON object preserving everything we read back, with
        // only DisplayName replaced. If parsing fails just send a minimal body.
        try
        {
            using var doc = JsonDocument.Parse(body);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "DisplayName", StringComparison.OrdinalIgnoreCase))
                        continue;
                    prop.WriteTo(w);
                }
                w.WriteString("displayName", newName);
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return JsonSerializer.Serialize(new { displayName = newName });
        }
    }

    private static string Trunc(string? s, int max = 2000)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        return s.Length <= max ? s : s[..max] + $"\n...({s.Length - max} more chars truncated)";
    }
}
