namespace ProxyCraft.Api.Contracts;

public sealed record ApiKeySettingsResponse(
    bool IsConfigured,
    string? KeyPrefix);

public sealed record GeneratedApiKeyResponse(
    string ApiKey,
    string KeyPrefix);
