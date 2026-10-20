using Microsoft.AspNetCore.Mvc;
using ProxyCraft.Api.Contracts;
using ProxyCraft.Services;

namespace ProxyCraft.Api.Controllers;

[ApiController, Route("api/v1/settings/api-key"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ApiKeyController(ApiKeyService keys) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("Read API key status")]
    [EndpointDescription("Returns whether a key is configured and its display prefix. Never returns the secret or its hash.")]
    public Task<ApiKeySettingsResponse> Get(CancellationToken cancellationToken) => keys.GetSettingsAsync(cancellationToken);

    [HttpPost]
    [EndpointSummary("Generate or replace the API key")]
    [EndpointDescription("Returns the raw key once. Only its hash and prefix are stored. Generating another key replaces the single saved key and immediately invalidates the old one.")]
    public Task<GeneratedApiKeyResponse> Generate(CancellationToken cancellationToken) => keys.GenerateAsync(cancellationToken);
}
