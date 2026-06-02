using System.Text.Json;
using VerseOps.App.Sdk;
using Xunit;
using Xunit.Abstractions;

namespace VerseOps.SdkTests;

/// <summary>
/// Diagnostic-only: dumps the fixture WarmupLog plus the raw response bodies
/// for the two list endpoints that feed the Operations / EnvironmentGroupOperations
/// indexer slots. Used to discover the actual JSON shape so ExtractFirstId can
/// be taught the right key names. Always passes (no asserts beyond Skip).
/// </summary>
[Collection(SdkAuthCollection.Name)]
public sealed class WarmupDiagnosticsTests
{
    private readonly SdkAuthFixture _fx;
    private readonly ITestOutputHelper _out;

    public WarmupDiagnosticsTests(SdkAuthFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    [SkippableFact]
    public async Task Dump_Warmup_Log_And_Operation_List_Shapes()
    {
        Skip.IfNot(_fx.SignedIn, _fx.SignInError ?? "sign-in failed");

        _out.WriteLine("=== Warmup log ===");
        foreach (var line in _fx.WarmupLog)
        {
            _out.WriteLine(line);
        }

        _out.WriteLine("");
        _out.WriteLine("=== Cached seeds ===");
        foreach (var key in new[] { "Environments", "EnvironmentGroups", "Operations", "EnvironmentGroupOperations" })
        {
            _out.WriteLine($"{key} = {(_fx.TryGetIndexerSeed(key, out var id) ? id : "<MISSING>")}");
        }

        _out.WriteLine("");
        _out.WriteLine("=== Catalog paths containing 'EnvironmentGroupOperations' ===");
        var matchingOps = SdkCatalog.Operations
            .Where(o => o.PathText.Contains("EnvironmentGroupOperations", StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.PathText)
            .ThenBy(o => o.HttpMethod)
            .ToList();
        if (matchingOps.Count == 0)
        {
            _out.WriteLine("(none)");
        }
        else
        {
            foreach (var op in matchingOps)
            {
                _out.WriteLine($"  {op.HttpMethod,-6} {op.PathText}  (body={(op.BodyType?.Name ?? "<none>")})");
            }
        }

        // Re-fetch the two list endpoints that fed the operations seeds and
        // dump the raw JSON so we can see what keys items actually use.
        var executor = new SdkExecutor(_fx.Auth);

        await DumpList(executor,
            label: "EnvironmentGroupOperations (top-level)",
            pathText: "ServiceClient.Environmentmanagement.EnvironmentGroupOperations",
            indexerValues: new Dictionary<string, string>()).ConfigureAwait(false);

        if (_fx.TryGetIndexerSeed("Environments", out var envId))
        {
            await DumpList(executor,
                label: $"Environments[{envId}].Operations",
                pathText: "ServiceClient.Environmentmanagement.Environments.Item[environmentId].Operations",
                indexerValues: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Environments"] = envId
                }).ConfigureAwait(false);
        }
    }

    private async Task DumpList(
        SdkExecutor executor,
        string label,
        string pathText,
        IReadOnlyDictionary<string, string> indexerValues)
    {
        _out.WriteLine("");
        _out.WriteLine($"=== {label} ===");
        _out.WriteLine($"PathText: {pathText}");

        var op = SdkCatalog.Operations.FirstOrDefault(o =>
            o.HttpMethod == "GET" && o.PathText == pathText && o.BodyType == null);
        if (op is null)
        {
            _out.WriteLine("(no matching op in SdkCatalog.Operations)");
            return;
        }

        var result = await executor.ExecuteAsync(op, indexerValues, jsonBody: null, CancellationToken.None)
            .ConfigureAwait(false);

        _out.WriteLine($"Success: {result.Success} StatusText: {result.StatusText}");
        var body = result.Body ?? string.Empty;
        if (body.Length > 4000) body = body.Substring(0, 4000) + "  ...[truncated]";
        _out.WriteLine("--- Body ---");
        _out.WriteLine(body);

        // If body parses, also print the top-level shape (keys + first-item keys).
        try
        {
            using var doc = JsonDocument.Parse(result.Body ?? "{}");
            _out.WriteLine("--- Shape ---");
            _out.WriteLine($"Root kind: {doc.RootElement.ValueKind}");
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    _out.WriteLine($"  .{prop.Name} : {prop.Value.ValueKind}" +
                        (prop.Value.ValueKind == JsonValueKind.Array ? $" (len={prop.Value.GetArrayLength()})" : ""));
                }

                // Try common item-array key names.
                foreach (var arrName in new[] { "Value", "value", "Items", "items" })
                {
                    if (doc.RootElement.TryGetProperty(arrName, out var arr) &&
                        arr.ValueKind == JsonValueKind.Array &&
                        arr.GetArrayLength() > 0)
                    {
                        _out.WriteLine($"  First item under .{arrName}:");
                        var first = arr[0];
                        if (first.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in first.EnumerateObject())
                            {
                                var sample = prop.Value.ValueKind == JsonValueKind.String
                                    ? $" = \"{prop.Value.GetString()}\""
                                    : $" ({prop.Value.ValueKind})";
                                _out.WriteLine($"    .{prop.Name}{sample}");
                            }
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _out.WriteLine($"(body did not parse as JSON: {ex.Message})");
        }
    }
}
