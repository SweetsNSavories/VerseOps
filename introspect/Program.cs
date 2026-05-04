using System.Reflection;

var asm = AppDomain.CurrentDomain.GetAssemblies()
    .Concat(System.IO.Directory.GetFiles(AppContext.BaseDirectory, "Microsoft.PowerPlatform.Management.dll")
        .Select(p => Assembly.LoadFrom(p)))
    .First(a => a.GetName().Name == "Microsoft.PowerPlatform.Management");

void Dump(Type t)
{
    Console.WriteLine($"== {t.FullName}  (base={t.BaseType?.Name}) ==");
    foreach (var c in t.GetConstructors())
        Console.WriteLine("  ctor(" + string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")) + ")");
    foreach (var p in t.GetProperties().OrderBy(p => p.Name))
        Console.WriteLine($"  prop: {p.PropertyType.Name} {p.Name}");
    foreach (var m in t.GetMethods()
        .Where(m => !m.IsSpecialName && m.DeclaringType == t)
        .OrderBy(m => m.Name))
    {
        Console.WriteLine($"  meth: {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
    }
    Console.WriteLine();
}

Dump(asm.GetType("Microsoft.PowerPlatform.Management.ServiceClient")!);
Dump(asm.GetType("Microsoft.PowerPlatform.Management.ServiceClientBase")!);
Dump(asm.GetType("Microsoft.PowerPlatform.Management.ServiceClientFactory")!);

Dump(asm.GetType("Microsoft.PowerPlatform.Management.Environmentmanagement.EnvironmentmanagementRequestBuilder")!);
Dump(asm.GetType("Microsoft.PowerPlatform.Management.Environmentmanagement.Environments.EnvironmentsRequestBuilder")!);
Dump(asm.GetType("Microsoft.PowerPlatform.Management.Licensing.LicensingRequestBuilder")!);
Dump(asm.GetType("Microsoft.PowerPlatform.Management.Licensing.TenantCapacity.TenantCapacityRequestBuilder")!);
Dump(asm.GetType("Microsoft.PowerPlatform.Management.Governance.GovernanceRequestBuilder")!);
Dump(asm.GetType("Microsoft.PowerPlatform.Management.Governance.RuleBasedPolicies.RuleBasedPoliciesRequestBuilder")!);
Dump(asm.GetType("Microsoft.PowerPlatform.Management.Powerapps.PowerappsRequestBuilder")!);
Dump(asm.GetType("Microsoft.PowerPlatform.Management.Analytics.AnalyticsRequestBuilder")!);
Dump(asm.GetType("Microsoft.PowerPlatform.Management.Analytics.AdvisorRecommendations.AdvisorRecommendationsRequestBuilder")!);
Dump(asm.GetType("Microsoft.PowerPlatform.Management.Appmanagement.AppmanagementRequestBuilder")!);
Dump(asm.GetType("Microsoft.PowerPlatform.Management.Appmanagement.ApplicationPackages.ApplicationPackagesRequestBuilder")!);
Dump(asm.GetType("Microsoft.PowerPlatform.Management.Powerpages.PowerpagesRequestBuilder")!);
Dump(asm.GetType("Microsoft.PowerPlatform.Management.Usermanagement.UsermanagementRequestBuilder")!);

var pva = asm.GetType("Microsoft.PowerPlatform.Management.Powervirtualagents.PowervirtualagentsRequestBuilder");
if (pva != null) Dump(pva);

Dump(asm.GetType("Microsoft.PowerPlatform.Management.ApiVersionHandler")!);
