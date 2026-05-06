using System.Reflection;

var asm = typeof(Microsoft.PowerPlatform.Management.ServiceClient).Assembly;
Type[] types;
try { types = asm.GetTypes(); }
catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

void Dump(string fullName)
{
    var t = types.FirstOrDefault(x => x.FullName == fullName);
    if (t == null) { Console.WriteLine($"-- not found: {fullName}"); return; }
    Console.WriteLine($"==== {t.FullName} ====");
    foreach (var p in t.GetProperties())
    {
        var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        Console.WriteLine($"  {pt.Name,-50} {p.Name}");
    }
}

// 1) Look at the GET environments query parameters for an "Expand" / "Capacity" flag.
var qp = types.FirstOrDefault(t => t.FullName?.EndsWith("EnvironmentsRequestBuilderGetQueryParameters") == true);
if (qp != null)
{
    Console.WriteLine($"==== {qp.FullName} ====");
    foreach (var p in qp.GetProperties())
        Console.WriteLine($"  {p.PropertyType.Name,-30} {p.Name}");
}

// 2) Inspect Environment / EnvironmentResponse / EnvironmentObject for a Capacity field.
foreach (var n in new[] {
    "Microsoft.PowerPlatform.Management.Models.EnvironmentResponse",
    "Microsoft.PowerPlatform.Management.Models.EnvironmentObject",
    "Microsoft.PowerPlatform.Management.Models.EnvironmentList"})
    Dump(n);

// 3) Anything labelled "EnvironmentCapacity" or with both Environment+Capacity.
Console.WriteLine();
Console.WriteLine("---- types containing 'EnvironmentCapacity' or 'EnvironmentStorage' ----");
foreach (var t in types.Where(t => t.IsPublic && (t.Name.Contains("EnvironmentCapacity") || t.Name.Contains("EnvironmentStorage") || t.Name.Contains("EnvironmentConsumption"))))
    Console.WriteLine($"  {t.FullName}");




