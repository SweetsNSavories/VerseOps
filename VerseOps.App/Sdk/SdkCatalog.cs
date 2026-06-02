using System.Reflection;
using Microsoft.PowerPlatform.Management;

namespace VerseOps.App.Sdk;

/// <summary>
/// Reflects Microsoft.PowerPlatform.Management.ServiceClient and produces the full
/// list of invocable SDK operations (GetAsync/PostAsync/PutAsync/PatchAsync/DeleteAsync)
/// reachable by walking RequestBuilder properties and indexers.
/// </summary>
public static class SdkCatalog
{
    private const int MaxDepth = 6;
    private static readonly string[] VerbSuffixes =
    {
        "GetAsync", "PostAsync", "PutAsync", "PatchAsync", "DeleteAsync"
    };
    private static readonly Dictionary<string, string> VerbToHttp = new(StringComparer.Ordinal)
    {
        ["GetAsync"] = "GET",
        ["PostAsync"] = "POST",
        ["PutAsync"] = "PUT",
        ["PatchAsync"] = "PATCH",
        ["DeleteAsync"] = "DELETE"
    };

    private static IReadOnlyList<SdkOp>? _cache;
    private static readonly object _gate = new();

    /// <summary>All invocable operations, lazily reflected once.</summary>
    public static IReadOnlyList<SdkOp> Operations
    {
        get
        {
            if (_cache != null) return _cache;
            lock (_gate)
            {
                if (_cache != null) return _cache;
                _cache = Reflect();
                return _cache;
            }
        }
    }

    private static IReadOnlyList<SdkOp> Reflect()
    {
        var list = new List<SdkOp>();
        var visited = new HashSet<Type>();
        Walk(typeof(ServiceClient), new List<SdkStep>(), 0, visited, list);
        return list
            .OrderBy(o => string.Join('.', o.Path.Select(s => s.PropertyName)))
            .ThenBy(o => o.HttpMethod)
            .ToList();
    }

    private static void Walk(Type builderType, List<SdkStep> path, int depth, HashSet<Type> visited, List<SdkOp> sink)
    {
        if (depth > MaxDepth) return;
        // Cycle guard: if any *prior* step (not the just-pushed one) was the same type, stop.
        for (int i = 0; i < path.Count - 1; i++)
            if (path[i].ResultType == builderType) return;

        // Emit verb methods on this builder.
        foreach (var m in builderType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (!VerbToHttp.TryGetValue(m.Name, out var http)) continue;
            // Pick the simplest overload (the one without a request-config delegate, if both exist).
            // Fine to also emit configured overloads but they double the tree noise.
            var ps = m.GetParameters();
            // Body type heuristic: first reference-typed parameter that isn't CancellationToken/Action<...>.
            Type? bodyType = null;
            foreach (var p in ps)
            {
                if (p.ParameterType == typeof(CancellationToken)) continue;
                if (p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(Action<>)) continue;
                bodyType = p.ParameterType;
                break;
            }

            var displayName = $"{http}  {ShortName(builderType)}";
            var sig = BuildSignature(m);
            sink.Add(new SdkOp(
                Path: path.ToArray(),
                HttpMethod: http,
                Method: m,
                BuilderType: builderType,
                BodyType: bodyType,
                DisplayName: displayName,
                SignatureText: sig));
        }

        // Recurse into property-typed RequestBuilder children.
        foreach (var prop in builderType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!IsBuilder(prop.PropertyType)) continue;
            if (prop.GetIndexParameters().Length != 0) continue;
            if (prop.PropertyType == builderType) continue;

            path.Add(new SdkStep(prop.Name, IsIndexer: false, IndexParamName: null,
                DeclaringType: builderType, ResultType: prop.PropertyType));
            Walk(prop.PropertyType, path, depth + 1, visited, sink);
            path.RemoveAt(path.Count - 1);
        }

        // Recurse into single-string indexers (e.g. Environments[envId]).
        foreach (var idx in builderType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var ip = idx.GetIndexParameters();
            if (ip.Length != 1) continue;
            if (ip[0].ParameterType != typeof(string)) continue;
            if (!IsBuilder(idx.PropertyType)) continue;

            // If this indexer sits on an "Item" wrapper builder (Kiota convention),
            // attribute the friendly param name to the *enclosing* collection step.
            string? parentCollection = null;
            if (string.Equals(idx.Name, "Item", StringComparison.Ordinal) && path.Count > 0 && !path[^1].IsIndexer)
                parentCollection = path[^1].PropertyName;

            path.Add(new SdkStep(idx.Name, IsIndexer: true, IndexParamName: ip[0].Name ?? "id",
                DeclaringType: builderType, ResultType: idx.PropertyType,
                ParentCollectionName: parentCollection));
            Walk(idx.PropertyType, path, depth + 1, visited, sink);
            path.RemoveAt(path.Count - 1);
        }
    }

    private static bool IsBuilder(Type t)
        => t.Name.EndsWith("RequestBuilder", StringComparison.Ordinal)
           && t.Namespace?.StartsWith("Microsoft.PowerPlatform.Management", StringComparison.Ordinal) == true;

    private static string ShortName(Type t)
    {
        // Strip "RequestBuilder" suffix and ".WithXyzItem" prefix-style naming.
        var n = t.Name;
        if (n.EndsWith("RequestBuilder", StringComparison.Ordinal))
            n = n.Substring(0, n.Length - "RequestBuilder".Length);
        return n;
    }

    private static string BuildSignature(MethodInfo m)
    {
        var ret = FriendlyType(m.ReturnType);
        var ps = string.Join(", ",
            m.GetParameters().Select(p => $"{FriendlyType(p.ParameterType)} {p.Name}{(p.HasDefaultValue ? " = default" : "")}"));
        return $"{ret} {m.Name}({ps})";
    }

    private static string FriendlyType(Type t)
    {
        if (t == typeof(void)) return "void";
        if (t == typeof(CancellationToken)) return "CancellationToken";
        if (t.IsGenericType)
        {
            var gen = t.GetGenericTypeDefinition().Name;
            var tick = gen.IndexOf('`');
            if (tick >= 0) gen = gen.Substring(0, tick);
            var inner = string.Join(", ", t.GetGenericArguments().Select(FriendlyType));
            return $"{gen}<{inner}>";
        }
        return t.Name;
    }
}
