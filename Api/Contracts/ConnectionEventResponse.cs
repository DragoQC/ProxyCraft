namespace ProxyCraft.Api.Contracts;

public sealed record ConnectionEventResponse(
    int Id,
    string PlayerName,
    string Domain,
    DateTimeOffset CreatedAt);
