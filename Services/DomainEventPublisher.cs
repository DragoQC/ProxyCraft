using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ProxyCraft.Api.Contracts;
using ProxyCraft.Api.Hubs;
using ProxyCraft.Data;
using ProxyCraft.Models;

namespace ProxyCraft.Services;

public sealed class DomainEventPublisher(
    IServiceScopeFactory scopes,
    DomainHubConnections connections,
    IHubContext<DomainHub> hub,
    ILogger<DomainEventPublisher> logger)
{
    public async Task PublishAsync(string eventName, int proxyId, string domain)
    {
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            ProxyCraftDbContext database = scope.ServiceProvider.GetRequiredService<ProxyCraftDbContext>();
            string? hash = await database.ApiKeys.AsNoTracking()
                .Where((ApiKey key) => key.Id == 1 && key.IsActive)
                .Select((ApiKey key) => key.KeyHash)
                .SingleOrDefaultAsync();

            IReadOnlyList<string> recipients = connections.GetRecipients(hash);
            if (recipients.Count == 0)
            {
                return;
            }

            // A slow or disconnected subscriber cannot undo a saved route.
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            DomainEventResponse notification = new(proxyId, domain, DateTimeOffset.UtcNow);
            await hub.Clients.Clients(recipients).SendAsync(eventName, notification, timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not publish {EventName} for proxy {ProxyId}", eventName, proxyId);
        }
    }
}
