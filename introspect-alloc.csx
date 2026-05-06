using System;
using System.Linq;
using System.Reflection;
var asm = Assembly.LoadFrom(@"VerseOps.App\bin\Debug\net10.0-windows\Microsoft.PowerPlatform.Management.dll");
Type[] types;
try { types = asm.GetTypes(); }
catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
Console.WriteLine($"types loaded: {types.Length}");
foreach (var t in types.Where(t => t.Name.Contains("Allocation") || t.Name.Contains("Currency")))
{
    Console.WriteLine($"==== {t.FullName} ====");
    foreach (var p in t.GetProperties())
        Console.WriteLine($"  {p.PropertyType.Name,-30} {p.Name}");
}
