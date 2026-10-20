namespace ProxyCraft.Api.Documentation;

public sealed record ApiFieldReference(
    string Name,
    string Type,
    string Details);

public sealed record ApiResponseReference(
    int StatusCode,
    string Description,
    bool IsCollection,
    IReadOnlyList<ApiFieldReference> Fields,
    string? Example);

public sealed record ApiEndpointReference(
    string Method,
    string Path,
    string Summary,
    string? Notes,
    IReadOnlyList<ApiFieldReference> Parameters,
    IReadOnlyList<ApiFieldReference> RequestFields,
    string? RequestExample,
    IReadOnlyList<ApiResponseReference> Responses);
