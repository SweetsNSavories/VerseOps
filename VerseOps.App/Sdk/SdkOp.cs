using System.Reflection;

namespace VerseOps.App.Sdk;

/// <summary>
/// One invocable SDK operation discovered by reflecting Microsoft.PowerPlatform.Management.
/// Path = sequence of property/indexer steps from ServiceClient down to the RequestBuilder
/// that owns the verb method (GetAsync/PostAsync/PatchAsync/PutAsync/DeleteAsync).
/// </summary>
public sealed record SdkOp(
    IReadOnlyList<SdkStep> Path,        // navigation from ServiceClient
    string HttpMethod,                  // GET / POST / PUT / PATCH / DELETE
    MethodInfo Method,                  // the *Async method
    Type BuilderType,                   // declaring RequestBuilder type
    Type? BodyType,                     // first non-CT/non-config parameter, if any
    string DisplayName,                 // e.g. "GET  Environments"
    string SignatureText                // e.g. "Task<EnvironmentResponseCollection> GetAsync(...)"
)
{
    /// <summary>Reconstructs the dotted path used in the docs, e.g. ServiceClient.Environmentmanagement.Environments[id].</summary>
    public string PathText
    {
        get
        {
            var sb = new System.Text.StringBuilder("ServiceClient");
            foreach (var s in Path)
            {
                sb.Append('.').Append(s.PropertyName);
                if (s.IsIndexer) sb.Append('[').Append(s.FriendlyParamName).Append(']');
            }
            return sb.ToString();
        }
    }

    /// <summary>True if any step is an indexer (i.e. a {token} the user must supply).</summary>
    public bool HasIndexer => Path.Any(s => s.IsIndexer);

    /// <summary>Distinct indexer parameter slot keys along the path (in order).</summary>
    public IEnumerable<string> IndexerSlots => Path.Where(s => s.IsIndexer).Select(s => s.SlotKey);
}

/// <summary>One step of navigation from ServiceClient to a target RequestBuilder.</summary>
public sealed record SdkStep(
    string PropertyName,
    bool IsIndexer,
    string? IndexParamName,            // raw Kiota name (often "position")
    Type DeclaringType,
    Type ResultType,
    string? ParentCollectionName = null // when IsIndexer, the collection name this indexer lives on (e.g. "Environments")
)
{
    /// <summary>Human-friendly indexer label, derived from the parent collection (Environments → environmentId).</summary>
    public string FriendlyParamName
    {
        get
        {
            if (!IsIndexer) return string.Empty;
            var n = ParentCollectionName;
            if (string.IsNullOrEmpty(n)) return IndexParamName ?? "id";
            // Singularize a few common collection suffixes -> singular + Id.
            string singular = n;
            if (singular.EndsWith("ies", StringComparison.Ordinal)) singular = singular[..^3] + "y";
            else if (singular.EndsWith("s", StringComparison.Ordinal) && !singular.EndsWith("ss", StringComparison.Ordinal)) singular = singular[..^1];
            return char.ToLowerInvariant(singular[0]) + singular[1..] + "Id";
        }
    }

    /// <summary>Stable, unique key for the form input: collection name + index in path. Avoids duplicate-row collisions.</summary>
    public string SlotKey => IsIndexer ? (ParentCollectionName ?? IndexParamName ?? "id") : PropertyName;
}
