using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using ProxyCraft.Api.Contracts;

namespace ProxyCraft.Api.Documentation;

public sealed class ApiReferenceService(
    IApiDescriptionGroupCollectionProvider descriptions,
    IModelMetadataProvider metadata)
{
    private static readonly JsonSerializerOptions ExampleOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public IReadOnlyList<ApiEndpointReference> List() => descriptions.ApiDescriptionGroups.Items
        .SelectMany((ApiDescriptionGroup group) => group.Items)
        .Where((ApiDescription endpoint) => endpoint.RelativePath?.StartsWith("api/v1/", StringComparison.Ordinal) == true)
        .OrderBy((ApiDescription endpoint) => endpoint.RelativePath)
        .ThenBy((ApiDescription endpoint) => endpoint.HttpMethod)
        .Select((ApiDescription endpoint) => Describe(endpoint)).ToList();

    private ApiEndpointReference Describe(ApiDescription endpoint)
    {
        ApiParameterDescription? body = endpoint.ParameterDescriptions.FirstOrDefault(
            (ApiParameterDescription parameter) => parameter.Source == BindingSource.Body);
        List<ApiFieldReference> parameters = endpoint.ParameterDescriptions
            .Where((ApiParameterDescription parameter) => parameter.Source == BindingSource.Path || parameter.Source == BindingSource.Query)
            .Select((ApiParameterDescription parameter) => new ApiFieldReference(parameter.Name, TypeName(parameter.Type),
                $"{(parameter.Source == BindingSource.Path ? "Path" : "Query")} parameter. {(parameter.IsRequired ? "Required." : "Optional.")}"))
            .ToList();
        List<ApiResponseReference> responses = endpoint.SupportedResponseTypes.OrderBy((ApiResponseType response) => response.StatusCode)
            .Select((ApiResponseType response) => new ApiResponseReference(response.StatusCode, ReasonPhrases.GetReasonPhrase(response.StatusCode),
                IsCollection(response.Type), Fields(response.Type, false),
                endpoint.RelativePath == "api/v1/authentication/enable" && response.StatusCode == StatusCodes.Status200OK
                    ? JsonSerializer.Serialize(new AppSettingsResponse(true), ExampleOptions) : Example(response.Type))).ToList();
        return new ApiEndpointReference(endpoint.HttpMethod ?? "GET", $"/{endpoint.RelativePath}",
            endpoint.ActionDescriptor.EndpointMetadata.OfType<IEndpointSummaryMetadata>().FirstOrDefault()?.Summary ?? "Management operation",
            endpoint.ActionDescriptor.EndpointMetadata.OfType<IEndpointDescriptionMetadata>().FirstOrDefault()?.Description,
            parameters, Fields(body?.Type, true), Example(body?.Type), responses);
    }

    private IReadOnlyList<ApiFieldReference> Fields(Type? type, bool request)
    {
        if (type is null || type == typeof(void))
        {
            return [];
        }

        Type itemType = CollectionItemType(type) ?? type;
        NullabilityInfoContext nullability = new();
        return metadata.GetMetadataForType(itemType).Properties.Select((ModelMetadata property) =>
        {
            List<string> details = [];
            if (request)
            {
                details.Add(property.ValidatorMetadata.OfType<RequiredAttribute>().Any() ? "Required." : "Optional.");
                foreach (ValidationAttribute validator in property.ValidatorMetadata.OfType<ValidationAttribute>())
                {
                    if (validator is not RequiredAttribute)
                    {
                        details.Add(validator.FormatErrorMessage(property.PropertyName ?? "Value"));
                    }
                }
            }
            string? note = property.PropertyName switch
            {
                nameof(LiveConnectionResponse.Domain) when itemType == typeof(LiveConnectionResponse) => "Requested domain for this open forwarded session.",
                nameof(LiveConnectionResponse.Id) when itemType == typeof(LiveConnectionResponse) => "In-memory connection ID. Separate sessions can have the same player name.",
                nameof(LiveConnectionResponse.ConnectedAt) => "UTC time the proxy opened the forwarding session; not proof of a completed Minecraft login.",
                nameof(ConnectionEventResponse.Domain) when itemType == typeof(ConnectionEventResponse) => "Requested domain at the time of the attempt.",
                nameof(ConnectionEventResponse.PlayerName) => "Client-supplied name; not proof of authentication or a successful join.",
                nameof(ConnectionEventResponse.CreatedAt) => "UTC time the login attempt was recorded.",
                nameof(CreateProxyRequest.Domain) => "Domain containing a dot; no protocol or port. Must be unique.",
                nameof(CreateProxyRequest.DestinationHost) => "Hostname, IPv4, or IPv6. Each host/port pair must be unique.",
                nameof(CreateProxyRequest.DestinationPort) or nameof(UpdateProxySettingsRequest.ListenPort) when request => "Defaults to 25565.",
                nameof(ProxyResponse.IsReachable) => "TCP reachability; null until checked. Refreshed every 10 seconds.",
                nameof(ProxyResponse.LastCheckedAt) => "UTC time of the latest TCP check; null until checked.",
                nameof(UpdateAppSettingsRequest.ShowPublicDomains) => "Only available when publicDashboardEnabled is true; otherwise saved as false.",
                nameof(UpdateAppSettingsRequest.PublicDashboardMessage) => "Plain-text paragraph, 1–500 characters. Defaults to The Minecraft proxy that just works.; omit to preserve the saved message.",
                nameof(UpdateAppSettingsRequest.PublicDashboardTitle) => "Plain-text heading, 1–120 characters. Defaults to Welcome to ProxyCraft; omit to preserve the saved title.",
                nameof(PublicDashboardResponse.Message) => "Configured plain-text paragraph below the title; never interpreted as HTML.",
                nameof(PublicDashboardResponse.Title) => "Configured plain-text welcome heading; never interpreted as HTML.",
                nameof(ProxyResponse.LiveConnectionCount) => "Current named forwarding sessions for this proxy, held in memory. Excludes status pings and failed backend connections.",
                nameof(ProxySettingsResponse.ListenerError) => "Check this even on HTTP 200; a failed bind retains the previous listening port.",
                nameof(GeneratedApiKeyResponse.ApiKey) => "Returned once. Store it safely; regeneration invalidates the old key.",
                nameof(SessionResponse.CsrfToken) => "Send with the browser cookie in X-CSRF-TOKEN; refresh after signing in.",
                _ => null
            };
            if (note is not null)
            {
                details.Add(note);
            }

            string fieldType = TypeName(property.ModelType);
            PropertyInfo? declaration = itemType.GetProperty(property.PropertyName ?? string.Empty);
            if (declaration is not null && nullability.Create(declaration).ReadState == NullabilityState.Nullable
                && !fieldType.EndsWith(" | null", StringComparison.Ordinal))
            {
                fieldType += " | null";
            }

            return new ApiFieldReference(JsonNamingPolicy.CamelCase.ConvertName(property.PropertyName ?? string.Empty), fieldType, string.Join(" ", details));
        }).ToList();
    }

    private static Type? CollectionItemType(Type? type)
    {
        if (type is null || type == typeof(string))
        {
            return null;
        }

        Type? enumerable = type.GetInterfaces().Append(type).FirstOrDefault((Type candidate) =>
            candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GenericTypeArguments[0];
    }

    private static bool IsCollection(Type? type) => CollectionItemType(type) is not null;

    private static string TypeName(Type type)
    {
        Type? underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return $"{TypeName(underlying)} | null";
        }

        if (type == typeof(string))
        {
            return "string";
        }

        Type? itemType = CollectionItemType(type);
        if (itemType is not null)
        {
            return $"array of {TypeName(itemType)}";
        }

        if (type == typeof(bool))
        {
            return "boolean";
        }

        if (type == typeof(int))
        {
            return "integer";
        }

        if (type == typeof(long))
        {
            return "integer";
        }

        if (type == typeof(double))
        {
            return "number";
        }

        if (type == typeof(DateTimeOffset))
        {
            return "date-time string";
        }

        return type.Name;
    }

    // Illustrative values only: this page never reads live settings, routes, credentials, or API keys.
    private static string? Example(Type? type)
    {
        if (type is not null && typeof(IEnumerable<LiveConnectionResponse>).IsAssignableFrom(type))
        {
            return JsonSerializer.Serialize(new LiveConnectionResponse[]
            {
                new(42, "Steve", "play.example.com", new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero))
            }, ExampleOptions);
        }

        if (type is not null && typeof(IEnumerable<ConnectionEventResponse>).IsAssignableFrom(type))
        {
            return JsonSerializer.Serialize(new ConnectionEventResponse[]
            {
                new(1, "Steve", "play.example.com", new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero))
            }, ExampleOptions);
        }

        ProxyResponse proxy = new(1, "play.example.com", "192.168.120.40", 25565, true, true,
            new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), 3);
        if (IsCollection(type))
        {
            return JsonSerializer.Serialize(new ProxyResponse[] { proxy }, ExampleOptions);
        }

        if (type == typeof(ProxyResponse))
        {
            return JsonSerializer.Serialize(proxy, ExampleOptions);
        }

        if (type == typeof(CreateProxyRequest))
        {
            return JsonSerializer.Serialize(new CreateProxyRequest
            {
                Domain = "play.example.com",
                DestinationHost = "192.168.120.40",
                DestinationPort = 25565
            }, ExampleOptions);
        }

        if (type == typeof(CredentialsRequest))
        {
            return JsonSerializer.Serialize(new CredentialsRequest
            {
                UsernameOrEmail = "owner@example.com",
                Password = "example-password-123"
            }, ExampleOptions);
        }

        if (type == typeof(LoginRequest))
        {
            return JsonSerializer.Serialize(new LoginRequest
            {
                UsernameOrEmail = "owner@example.com",
                Password = "example-password-123",
                TwoFactorCode = "123456"
            }, ExampleOptions);
        }

        if (type == typeof(UpdateAppSettingsRequest))
        {
            return JsonSerializer.Serialize(new UpdateAppSettingsRequest
            {
                PublicDashboardEnabled = true,
                PublicDashboardMessage = "The Minecraft proxy that just works.",
                PublicDashboardTitle = "Welcome to ProxyCraft"
            }, ExampleOptions);
        }

        if (type == typeof(PublicDashboardResponse))
        {
            return JsonSerializer.Serialize(new PublicDashboardResponse(["play.example.com"], "The Minecraft proxy that just works.", "Welcome to ProxyCraft"), ExampleOptions);
        }

        if (type == typeof(TwoFactorSettingsResponse))
        {
            return JsonSerializer.Serialize(new TwoFactorSettingsResponse(true, 8), ExampleOptions);
        }

        if (type == typeof(TwoFactorPasswordRequest))
        {
            return JsonSerializer.Serialize(new TwoFactorPasswordRequest { Password = "example-password-123" }, ExampleOptions);
        }

        if (type == typeof(ConfirmTwoFactorRequest))
        {
            return JsonSerializer.Serialize(new ConfirmTwoFactorRequest { SetupToken = "protected-enrollment-token", Code = "123456" }, ExampleOptions);
        }

        if (type == typeof(DisableTwoFactorRequest))
        {
            return JsonSerializer.Serialize(new DisableTwoFactorRequest { Password = "example-password-123", Code = "123456" }, ExampleOptions);
        }

        if (type == typeof(TwoFactorSetupResponse))
        {
            return JsonSerializer.Serialize(new TwoFactorSetupResponse("EXAMPLESECRET", "otpauth://totp/ProxyCraft:owner?secret=EXAMPLESECRET&issuer=ProxyCraft", "protected-enrollment-token"), ExampleOptions);
        }

        if (type == typeof(TwoFactorEnabledResponse))
        {
            return JsonSerializer.Serialize(new TwoFactorEnabledResponse(["ABCDE-12345-ABCDE-12345"]), ExampleOptions);
        }

        if (type == typeof(UpdateProxySettingsRequest))
        {
            return JsonSerializer.Serialize(new UpdateProxySettingsRequest(), ExampleOptions);
        }

        if (type == typeof(AppSettingsResponse))
        {
            return JsonSerializer.Serialize(new AppSettingsResponse(false), ExampleOptions);
        }

        if (type == typeof(ProxySettingsResponse))
        {
            return JsonSerializer.Serialize(new ProxySettingsResponse(25565, 25565, null), ExampleOptions);
        }

        if (type == typeof(SessionResponse))
        {
            return JsonSerializer.Serialize(new SessionResponse(false, false, null, "example-csrf-token"), ExampleOptions);
        }

        if (type == typeof(ApiKeySettingsResponse))
        {
            return JsonSerializer.Serialize(new ApiKeySettingsResponse(true, "pc_example_"), ExampleOptions);
        }

        if (type == typeof(GeneratedApiKeyResponse))
        {
            return JsonSerializer.Serialize(new GeneratedApiKeyResponse("pc_example_key_shown_only_once", "pc_example_"), ExampleOptions);
        }

        if (type == typeof(SystemMetricsResponse))
        {
            return JsonSerializer.Serialize(new SystemMetricsResponse(4, 12.5, 8L * 1024 * 1024 * 1024, 35.2,
            64L * 1024 * 1024 * 1024, 42.1), ExampleOptions);
        }

        return null;
    }
}
