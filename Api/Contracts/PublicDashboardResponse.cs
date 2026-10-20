namespace ProxyCraft.Api.Contracts;

public sealed record PublicDashboardResponse(
    IReadOnlyList<string> Domains,
    string Message,
    string Title);
