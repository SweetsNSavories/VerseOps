using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VerseOps.Authentication;
using VerseOps.Configuration;
using VerseOps.Exceptions;
using VerseOps.Models;
using VerseOps.Services;

// ---------------------------------------------------------------------------
// VerseOps sample: provisions one Power Platform environment using app-only
// auth, then prints the result. Configure via appsettings.json, user-secrets
// (`dotnet user-secrets set "PowerPlatform:ClientSecret" "..."`), or env vars
// (PowerPlatform__ClientSecret=...).
// ---------------------------------------------------------------------------

using var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((ctx, cfg) =>
    {
        cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
           .AddUserSecrets<Program>(optional: true)
           .AddEnvironmentVariables();
    })
    .ConfigureServices((ctx, services) =>
    {
        services.AddOptions<PowerPlatformOptions>()
            .Bind(ctx.Configuration.GetSection("PowerPlatform"));

        services.AddHttpClient(nameof(EnvironmentProvisioningService));
        services.AddSingleton<IPowerPlatformTokenProvider, AppOnlyTokenProvider>();
        services.AddSingleton<IEnvironmentProvisioningService>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<PowerPlatformOptions>>();
            var logger = sp.GetRequiredService<ILogger<EnvironmentProvisioningService>>();
            var tokenProvider = sp.GetRequiredService<IPowerPlatformTokenProvider>();
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(EnvironmentProvisioningService));
            return new EnvironmentProvisioningService(tokenProvider, opts, logger, http);
        });
    })
    .Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var config = host.Services.GetRequiredService<IConfiguration>();
var ppOptions = host.Services.GetRequiredService<IOptions<PowerPlatformOptions>>().Value;

if (string.IsNullOrWhiteSpace(ppOptions.TenantId) || ppOptions.TenantId.StartsWith("<"))
{
    logger.LogError("PowerPlatform:TenantId is not configured. Edit appsettings.json or set env var PowerPlatform__TenantId.");
    return 1;
}
if (string.IsNullOrWhiteSpace(ppOptions.ClientId) || ppOptions.ClientId.StartsWith("<"))
{
    logger.LogError("PowerPlatform:ClientId is not configured.");
    return 1;
}
if (string.IsNullOrWhiteSpace(ppOptions.ClientSecret) || ppOptions.ClientSecret.StartsWith("<"))
{
    logger.LogError("PowerPlatform:ClientSecret is not configured. Use `dotnet user-secrets set \"PowerPlatform:ClientSecret\" \"...\"` or env var PowerPlatform__ClientSecret.");
    return 1;
}

var envSection = config.GetSection("Environment");
var request = new CreateEnvironmentRequest
{
    DisplayName = envSection["DisplayName"] ?? $"VerseOps Demo {DateTime.UtcNow:yyyyMMddHHmm}",
    EnvironmentType = Enum.TryParse<EnvironmentType>(envSection["EnvironmentType"], ignoreCase: true, out var t)
        ? t : EnvironmentType.Sandbox,
    Region = envSection["Region"] ?? "unitedstates",
    ProvisionDataverse = bool.TryParse(envSection["ProvisionDataverse"], out var dv) && dv,
    CurrencyCode = envSection["CurrencyCode"] ?? "USD",
    LanguageCode = int.TryParse(envSection["LanguageCode"], out var lcid) ? lcid : 1033,
    PrincipalOwnerId = string.IsNullOrWhiteSpace(envSection["PrincipalOwnerId"]) ? null : envSection["PrincipalOwnerId"]
};

var service = host.Services.GetRequiredService<IEnvironmentProvisioningService>();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    logger.LogInformation("Starting environment provisioning for '{DisplayName}'...", request.DisplayName);
    var result = await service.CreateEnvironmentAsync(request, cts.Token);

    logger.LogInformation(
        "SUCCESS. EnvironmentId={EnvironmentId} Region={Region} DataverseUrl={DataverseUrl} CorrelationId={CorrelationId} OperationId={OperationId}",
        result.EnvironmentId, result.Region, result.DataverseUrl ?? "(none)", result.CorrelationId, result.OperationId);
    return 0;
}
catch (EnvironmentProvisioningException ex)
{
    logger.LogError(ex,
        "Provisioning failed. CorrelationId={CorrelationId} OperationId={OperationId} Status={Status}",
        ex.CorrelationId, ex.OperationId, ex.OperationStatus);
    return 2;
}
catch (OperationCanceledException)
{
    logger.LogWarning("Provisioning cancelled by user.");
    return 3;
}
