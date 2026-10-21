using System.ComponentModel.DataAnnotations;

namespace ProxyCraft.Api.Contracts;

public sealed record ProxyResponse(
    int Id,
    string Domain,
    string DestinationHost,
    int DestinationPort,
    bool IsActive,
    bool? IsReachable,
    DateTimeOffset? LastCheckedAt,
    int LiveConnectionCount);

public sealed class CreateProxyRequest
{
    [Required, StringLength(253)]
    public string Domain { get; set; } = string.Empty;

    [Required, StringLength(253)]
    public string DestinationHost { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int DestinationPort { get; set; } = 25565;
}
