using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace ProxyCraft.Api.Hubs;

internal sealed record DomainSubscriber(
    string KeyHash,
    Action Abort);

public sealed class DomainHubConnections
{
    private readonly ConcurrentDictionary<string, DomainSubscriber> _connections = new();

    public void Add(HubCallerContext context)
    {
        string? hash = context.User?.FindFirstValue("api_key_hash");
        if (hash is null)
        {
            context.Abort();
            return;
        }

        _connections[context.ConnectionId] = new DomainSubscriber(hash, context.Abort);
    }

    public void Remove(string id) => _connections.TryRemove(id, out DomainSubscriber? removed);

    public IReadOnlyList<string> GetRecipients(string? currentKeyHash)
    {
        List<string> recipients = [];
        foreach (KeyValuePair<string, DomainSubscriber> connection in _connections)
        {
            if (connection.Value.KeyHash == currentKeyHash)
            {
                recipients.Add(connection.Key);
            }
            else if (_connections.TryRemove(connection.Key, out DomainSubscriber? revoked))
            {
                revoked.Abort();
            }
        }

        return recipients;
    }
}
