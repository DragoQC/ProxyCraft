using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ProxyCraft.Api.Hubs;

[Authorize(Policy = "DomainEvents")]
public sealed class DomainHub(DomainHubConnections connections) : Hub
{
    public override Task OnConnectedAsync()
    {
        connections.Add(Context);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
