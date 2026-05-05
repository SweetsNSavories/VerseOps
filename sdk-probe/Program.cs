using System;
using System.Reflection;
using Microsoft.PowerPlatform.Management;

var sc = typeof(ServiceClient);
Console.WriteLine($"ServiceClient: {sc.FullName}");
var props = sc.GetProperties(BindingFlags.Public | BindingFlags.Instance);
Console.WriteLine($"Property count: {props.Length}");
foreach (var p in props)
    Console.WriteLine($"  {p.Name} -> {p.PropertyType.FullName}  (idxParams={p.GetIndexParameters().Length})");

Console.WriteLine();
Console.WriteLine("Verb methods on first child builder:");
var first = props.FirstOrDefault(p => p.PropertyType.Name.EndsWith("RequestBuilder"));
if (first != null)
{
    foreach (var m in first.PropertyType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  {m.Name}");
}
