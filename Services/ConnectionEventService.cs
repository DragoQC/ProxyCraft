using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProxyCraft.Api.Contracts;
using ProxyCraft.Data;
using ProxyCraft.Models;

namespace ProxyCraft.Services;

public sealed class ConnectionEventService(ProxyCraftDbContext database)
{
    public async Task RecordAsync(int proxyId, string domain, string playerName, CancellationToken cancellationToken)
    {
        // An immediate SQLite write transaction keeps concurrent inserts/pruning and proxy deletion atomic.
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        if (!await database.Proxies.AnyAsync((Proxy proxy) => proxy.Id == proxyId, cancellationToken))
        {
            return;
        }

        database.ConnectionEvents.Add(new ConnectionEvent { ProxyId = proxyId, Domain = domain, PlayerName = playerName });
        await database.SaveChangesAsync(cancellationToken);
        List<int> expired = await database.ConnectionEvents.Where((ConnectionEvent entry) => entry.ProxyId == proxyId)
            .OrderByDescending((ConnectionEvent entry) => entry.Id).Skip(20)
            .Select((ConnectionEvent entry) => entry.Id).ToListAsync(cancellationToken);
        if (expired.Count > 0)
        {
            await database.ConnectionEvents.Where((ConnectionEvent entry) => expired.Contains(entry.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConnectionEventResponse>?> ListAsync(int proxyId, CancellationToken cancellationToken)
    {
        if (!await database.Proxies.AnyAsync((Proxy proxy) => proxy.Id == proxyId, cancellationToken))
        {
            return null;
        }

        List<ConnectionEvent> entries = await database.ConnectionEvents.AsNoTracking()
            .Where((ConnectionEvent entry) => entry.ProxyId == proxyId)
            .OrderByDescending((ConnectionEvent entry) => entry.Id).Take(20).ToListAsync(cancellationToken);
        return entries.Select((ConnectionEvent entry) => new ConnectionEventResponse(entry.Id, entry.PlayerName, entry.Domain,
            new DateTimeOffset(DateTime.SpecifyKind(entry.CreatedAt, DateTimeKind.Utc)))).ToList();
    }
}
