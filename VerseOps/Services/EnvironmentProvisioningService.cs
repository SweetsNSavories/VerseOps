using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VerseOps.Authentication;
using VerseOps.Configuration;
using VerseOps.Exceptions;
using VerseOps.Models;

namespace VerseOps.Services;

/// <summary>
/// Provisions Power Platform environments via the BAP control-plane APIs using
/// an app-only (client credentials) token. No user, no Dataverse Web API,
/// no Microsoft.PowerPlatform.Dataverse.Client.
///
/// Why this works app-only:
///   The Azure AD application is granted the directory role
///   "Power Platform Administrator" (role assignable to service principals).
///   Tokens issued for the PowerApps Service audience are accepted by
///   api.bap.microsoft.com for tenant-wide environment management. No Power Apps
///   license, no Dataverse application user, and no per-environment role
///   provisioning is required for the *control-plane* create/delete operations.
/// </summary>
public sealed class EnvironmentProvisioningService : IEnvironmentProvisioningService
{
    private const string CorrelationHeader = "x-ms-correlation-request-id";
    private const string ClientRequestHeader = "x-ms-client-request-id";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IPowerPlatformTokenProvider _tokenProvider;
    private readonly PowerPlatformOptions _options;
    private readonly ILogger<EnvironmentProvisioningService> _logger;
    private readonly HttpClient _httpClient;

    public EnvironmentProvisioningService(
        IPowerPlatformTokenProvider tokenProvider,
        IOptions<PowerPlatformOptions> options,
        ILogger<EnvironmentProvisioningService> logger,
        HttpClient? httpClient = null)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri(_options.BapBaseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<EnvironmentProvisioningResult> CreateEnvironmentAsync(
        CreateEnvironmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            throw new ArgumentException("DisplayName is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Region))
            throw new ArgumentException("Region is required.", nameof(request));

        // Microsoft platform constraint: the Developer SKU is per-user and
        // license-gated (Power Apps Developer Plan). The BAP control plane
        // rejects app-only (client-credentials) creation with HTTP 409
        // "DeveloperEnvironmentCreationByUserMissingLicense" because a service
        // principal cannot hold a user license. There is no payload, role, or
        // permission that unlocks this — Developer envs must be self-provisioned
        // by a licensed user via the Maker Portal / PPAC. VerseOps surfaces a
        // clear error rather than letting the call fail late inside the API.
        if (request.EnvironmentType == EnvironmentType.Developer)
        {
            throw new NotSupportedException(
                "Developer SKU environments cannot be created with app-only (service principal) authentication. " +
                "Microsoft requires a user with the Power Apps Developer Plan license to be the creator. " +
                "Use Sandbox + PrincipalOwnerId for a developer-style workspace, or have the user self-provision " +
                "at https://make.powerapps.com.");
        }

        var clientRequestId = Guid.NewGuid().ToString();
        var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        // BAP environment-create endpoint. The "async=true" pattern returns 202 + Location header
        // pointing to a lifecycle operation we then poll.
        var createUri = $"/providers/Microsoft.BusinessAppPlatform/environments" +
                        $"?api-version={_options.ApiVersion}";

        var payload = BuildCreatePayload(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, createUri)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        httpRequest.Headers.TryAddWithoutValidation(ClientRequestHeader, clientRequestId);

        _logger.LogInformation(
            "Submitting environment create. DisplayName={DisplayName} Region={Region} Type={Type} Dataverse={Dataverse} ClientRequestId={ClientRequestId}",
            request.DisplayName, request.Region, request.EnvironmentType, request.ProvisionDataverse, clientRequestId);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var correlationId = GetHeader(response, CorrelationHeader) ?? clientRequestId;

        if (response.StatusCode != HttpStatusCode.Accepted && !response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            _logger.LogError(
                "Environment create failed. Status={Status} CorrelationId={CorrelationId} Body={Body}",
                (int)response.StatusCode, correlationId, body);
            throw new EnvironmentProvisioningException(
                $"Environment create rejected with status {(int)response.StatusCode}.",
                correlationId: correlationId);
        }

        // For 202 Accepted, BAP returns a Location header with the lifecycle operation URL.
        var operationLocation = response.Headers.Location?.ToString()
                                ?? GetHeader(response, "Operation-Location");

        if (string.IsNullOrEmpty(operationLocation))
        {
            // Fast-path: environment came back synchronously (rare). Try to parse the body.
            var sync = await response.Content.ReadFromJsonAsync<EnvironmentResource>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (sync?.Name is null)
            {
                throw new EnvironmentProvisioningException(
                    "BAP returned no Location header and no environment body.",
                    correlationId: correlationId);
            }

            return BuildResult(sync, request, correlationId, operationId: "synchronous");
        }

        _logger.LogInformation(
            "Environment create accepted. CorrelationId={CorrelationId} OperationLocation={OperationLocation}",
            correlationId, operationLocation);

        var (finalEnv, operationId) = await PollLifecycleOperationAsync(
                operationLocation, correlationId, cancellationToken)
            .ConfigureAwait(false);

        return BuildResult(finalEnv, request, correlationId, operationId);
    }

    private async Task<(EnvironmentResource Environment, string OperationId)> PollLifecycleOperationAsync(
        string operationLocation,
        string correlationId,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.ProvisioningTimeout);

        var operationId = ExtractOperationId(operationLocation);
        var attempt = 0;

        while (true)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            attempt++;

            var token = await _tokenProvider.GetAccessTokenAsync(timeoutCts.Token).ConfigureAwait(false);

            using var pollRequest = new HttpRequestMessage(HttpMethod.Get, operationLocation);
            pollRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var pollResponse = await _httpClient
                .SendAsync(pollRequest, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);

            var pollCorrelation = GetHeader(pollResponse, CorrelationHeader) ?? correlationId;

            if (!pollResponse.IsSuccessStatusCode && pollResponse.StatusCode != HttpStatusCode.Accepted)
            {
                var body = await SafeReadBodyAsync(pollResponse, timeoutCts.Token).ConfigureAwait(false);
                throw new EnvironmentProvisioningException(
                    $"Polling failed with status {(int)pollResponse.StatusCode}.",
                    correlationId: pollCorrelation,
                    operationId: operationId);
            }

            var lifecycle = await pollResponse.Content
                .ReadFromJsonAsync<LifecycleOperationResource>(JsonOptions, timeoutCts.Token)
                .ConfigureAwait(false);

            var status = ParseStatus(lifecycle?.Properties?.State?.Id ?? lifecycle?.Properties?.Status);

            _logger.LogInformation(
                "Polling environment provisioning. Attempt={Attempt} OperationId={OperationId} Status={Status} CorrelationId={CorrelationId}",
                attempt, operationId, status, pollCorrelation);

            switch (status)
            {
                case OperationStatus.Succeeded:
                    var env = lifecycle?.Properties?.LinkedEnvironmentMetadata
                              ?? lifecycle?.Properties?.Environment
                              ?? throw new EnvironmentProvisioningException(
                                  "Operation reported Succeeded but no environment payload was returned.",
                                  correlationId: pollCorrelation,
                                  operationId: operationId,
                                  operationStatus: status.ToString());
                    return (env, operationId);

                case OperationStatus.Failed:
                case OperationStatus.Cancelled:
                    var error = lifecycle?.Properties?.Error?.Message ?? "Unknown error";
                    throw new EnvironmentProvisioningException(
                        $"Environment provisioning ended in {status}: {error}",
                        correlationId: pollCorrelation,
                        operationId: operationId,
                        operationStatus: status.ToString());

                case OperationStatus.Running:
                case OperationStatus.NotStarted:
                default:
                    await Task.Delay(_options.PollingInterval, timeoutCts.Token).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static object BuildCreatePayload(CreateEnvironmentRequest request)
    {
        var properties = new Dictionary<string, object?>
        {
            ["displayName"] = request.DisplayName,
            ["environmentSku"] = request.EnvironmentType.ToString()
            // NOTE: do not set "azureRegion" here. The top-level "location"
            // field (Power Platform geo, e.g. "unitedstates") is sufficient;
            // BAP picks a concrete Azure region inside that geo. Setting
            // azureRegion to the geo name yields HTTP 400
            // "EnvironmentCreationNotSupportedInAzureRegion".
        };

        if (!string.IsNullOrWhiteSpace(request.PrincipalOwnerId))
        {
            // BAP property name is "principalCreator" (object with userId/email/type).
            // Required for Developer SKU (an SP cannot own a Developer env);
            // optional for other SKUs to assign user ownership on behalf of the caller.
            properties["principalCreator"] = new
            {
                userId = request.PrincipalOwnerId,
                type = "User"
            };
        }

        if (!string.IsNullOrWhiteSpace(request.SecurityGroupId))
        {
            properties["provisioningModel"] = "User";
            properties["bapClientApi.securityGroupId"] = request.SecurityGroupId;
        }

        if (request.ProvisionDataverse)
        {
            // Including linkedEnvironmentMetadata triggers Dataverse provisioning
            // alongside the environment in a single BAP call.
            properties["linkedEnvironmentMetadata"] = new Dictionary<string, object?>
            {
                ["baseLanguage"] = request.LanguageCode,
                ["domainName"] = request.DomainName,
                ["currency"] = new { code = request.CurrencyCode },
                ["templates"] = Array.Empty<string>()
            };
        }

        return new
        {
            location = request.Region,
            properties
        };
    }

    private EnvironmentProvisioningResult BuildResult(
        EnvironmentResource env,
        CreateEnvironmentRequest request,
        string correlationId,
        string operationId) => new()
    {
        EnvironmentId = env.Name ?? env.Id ?? string.Empty,
        DisplayName = env.Properties?.DisplayName ?? request.DisplayName,
        Region = env.Location ?? request.Region,
        EnvironmentType = request.EnvironmentType,
        DataverseUrl = env.Properties?.LinkedEnvironmentMetadata?.InstanceUrl,
        CorrelationId = correlationId,
        OperationId = operationId
    };

    private static string ExtractOperationId(string operationLocation)
    {
        try
        {
            var uri = new Uri(operationLocation, UriKind.RelativeOrAbsolute);
            var segments = uri.IsAbsoluteUri ? uri.Segments : operationLocation.Split('/');
            return segments[^1].Trim('/');
        }
        catch
        {
            return operationLocation;
        }
    }

    private static OperationStatus ParseStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "succeeded" => OperationStatus.Succeeded,
        "failed" => OperationStatus.Failed,
        "cancelled" or "canceled" => OperationStatus.Cancelled,
        "running" or "inprogress" or "in_progress" => OperationStatus.Running,
        "notstarted" or "not_started" or null or "" => OperationStatus.NotStarted,
        _ => OperationStatus.Running
    };

    private static string? GetHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---------- Internal DTOs that mirror the BAP control-plane wire shape ----------

    private sealed class EnvironmentResource
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Location { get; set; }
        public EnvironmentProperties? Properties { get; set; }
    }

    private sealed class EnvironmentProperties
    {
        public string? DisplayName { get; set; }
        public string? EnvironmentSku { get; set; }
        public LinkedEnvironmentMetadata? LinkedEnvironmentMetadata { get; set; }
    }

    private sealed class LinkedEnvironmentMetadata
    {
        public string? InstanceUrl { get; set; }
        public string? DomainName { get; set; }
    }

    private sealed class LifecycleOperationResource
    {
        public LifecycleOperationProperties? Properties { get; set; }
    }

    private sealed class LifecycleOperationProperties
    {
        public string? Status { get; set; }
        public LifecycleState? State { get; set; }
        public LifecycleError? Error { get; set; }
        public EnvironmentResource? LinkedEnvironmentMetadata { get; set; }
        public EnvironmentResource? Environment { get; set; }
    }

    private sealed class LifecycleState
    {
        public string? Id { get; set; }
    }

    private sealed class LifecycleError
    {
        public string? Code { get; set; }
        public string? Message { get; set; }
    }
}
