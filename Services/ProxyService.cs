using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProxyCraft.Api.Contracts;
using ProxyCraft.Data;
using ProxyCraft.Models;

namespace ProxyCraft.Services;

public sealed class ProxyService(
    ProxyCraftDbContext database,
    MinecraftProxyService runtime,
    RconProxyService rcon,
    DomainEventPublisher notifications)
{
    public async Task<IReadOnlyList<ProxyResponse>> ListAsync(string? search, CancellationToken cancellationToken = default)
    {
        IQueryable<Proxy> query = database.Proxies.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim().ToLowerInvariant();
            query = query.Where((Proxy proxy) => proxy.Domain.Contains(term) || proxy.DestinationHost.Contains(term));
        }

        List<Proxy> proxies = await query.OrderBy((Proxy proxy) => proxy.Domain).ToListAsync(cancellationToken);
        IReadOnlyDictionary<int, int> connectionCounts = runtime.GetLiveConnectionCounts();
        return proxies.Select((Proxy proxy) => ToResponse(proxy, connectionCounts)).ToList();
    }

    public async Task<IReadOnlyList<LiveConnectionResponse>?> ListConnectionsAsync(int id, CancellationToken cancellationToken)
    {
        if (!await database.Proxies.AnyAsync((Proxy proxy) => proxy.Id == id, cancellationToken))
        {
            return null;
        }

        return runtime.GetLiveConnections(id).Select((LiveConnection connection) => new LiveConnectionResponse(
            connection.Id, connection.PlayerName, connection.Domain, connection.ConnectedAt)).ToList();
    }

    public async Task<ProxyResponse> CreateAsync(CreateProxyRequest request, CancellationToken cancellationToken = default)
    {
        Proxy proxy = new();
        ApplySettings(proxy, request);
        // SQLite starts an immediate write transaction: validation and saving cannot race another writer.
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await EnsureDestinationAvailableAsync(proxy, cancellationToken);
        database.Proxies.Add(proxy);
        await rcon.SaveRouteAsync(proxy, async () =>
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);
        // A committed edit must reach the runtime even if the HTTP caller disconnects.
        await runtime.RefreshRouteAsync(proxy.Id);
        await notifications.PublishAsync("DomainAdded", proxy.Id, proxy.Domain);
        return ToResponse(proxy, runtime.GetLiveConnectionCounts());
    }

    public async Task<ProxyResponse?> UpdateAsync(int id, CreateProxyRequest request, CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        Proxy? proxy = await database.Proxies.FindAsync([id], cancellationToken);
        if (proxy is null)
        {
            return null;
        }

        string previousDomain = proxy.Domain;
        string previousHost = proxy.DestinationHost;
        int previousPort = proxy.DestinationPort;
        bool wasActive = proxy.IsActive;
        bool previousRconEnabled = proxy.RconEnabled;
        int previousRconListenPort = proxy.RconListenPort;
        int previousRconDestinationPort = proxy.RconDestinationPort;
        ApplySettings(proxy, request);
        await EnsureDestinationAvailableAsync(proxy, cancellationToken);
        proxy.IsActive = true;
        await rcon.SaveRouteAsync(proxy, async () =>
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);
        bool onlyRconChanged = wasActive && previousDomain == proxy.Domain && previousHost == proxy.DestinationHost
            && previousPort == proxy.DestinationPort && (previousRconEnabled != proxy.RconEnabled
                || previousRconListenPort != proxy.RconListenPort || previousRconDestinationPort != proxy.RconDestinationPort);
        if (!onlyRconChanged)
        {
            await runtime.RefreshRouteAsync(proxy.Id);
        }
        if (previousDomain != proxy.Domain)
        {
            await notifications.PublishAsync("DomainRemoved", id, previousDomain);
            await notifications.PublishAsync("DomainAdded", id, proxy.Domain);
        }
        return ToResponse(proxy, runtime.GetLiveConnectionCounts());
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        Proxy? proxy = await database.Proxies.FindAsync([id], cancellationToken);
        if (proxy is null)
        {
            return false;
        }

        database.Proxies.Remove(proxy);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await rcon.DeleteRouteAsync(id);
        await runtime.RefreshRouteAsync(id);
        await notifications.PublishAsync("DomainRemoved", id, proxy.Domain);
        return true;
    }

    private ProxyResponse ToResponse(Proxy proxy, IReadOnlyDictionary<int, int> connectionCounts)
    {
        RouteHealth health = runtime.GetHealth(proxy);
        RconForwardingStatus rconStatus = rcon.GetStatus(proxy);
        return new ProxyResponse(proxy.Id, proxy.Domain, proxy.DestinationHost, proxy.DestinationPort, proxy.IsActive,
            health.IsReachable, health.LastCheckedAt, connectionCounts.GetValueOrDefault(proxy.Id),
            proxy.RconEnabled, proxy.RconListenPort, proxy.RconDestinationPort, rconStatus.ListeningPort, rconStatus.Error);
    }

    private static void ApplySettings(Proxy proxy, CreateProxyRequest request)
    {
        string domain = request.Domain.Trim().TrimEnd('.').ToLowerInvariant();
        string destinationHost = NormalizeDestinationHost(request.DestinationHost);

        if (!domain.Contains('.') || Uri.CheckHostName(domain) != UriHostNameType.Dns)
        {
            throw new ArgumentException("Enter a valid domain containing a dot, without a protocol or port.");
        }

        if (Uri.CheckHostName(destinationHost) == UriHostNameType.Unknown)
        {
            throw new ArgumentException("Enter a destination hostname or IP address without a protocol or port.");
        }

        proxy.Domain = domain;
        proxy.DestinationHost = destinationHost;
        proxy.DestinationPort = request.DestinationPort;
        proxy.RconEnabled = request.RconEnabled ?? proxy.RconEnabled;
        proxy.RconListenPort = request.RconListenPort ?? proxy.RconListenPort;
        proxy.RconDestinationPort = request.RconDestinationPort ?? proxy.RconDestinationPort;
        if (proxy.RconListenPort is < 1 or > 65535 || proxy.RconDestinationPort is < 1 or > 65535)
        {
            throw new ArgumentException("Enter RCON ports between 1 and 65535.");
        }
    }

    private async Task EnsureDestinationAvailableAsync(Proxy proxy, CancellationToken cancellationToken)
    {
        if (proxy.RconEnabled && await database.Proxies.AnyAsync((Proxy existing) => existing.Id != proxy.Id
            && existing.RconEnabled && existing.RconListenPort == proxy.RconListenPort, cancellationToken))
        {
            throw new ProxyConflictException("Another proxy already uses this public RCON port. Each RCON route needs a different public port.");
        }
        List<string> hosts = await database.Proxies.AsNoTracking()
            .Where((Proxy existing) => existing.Id != proxy.Id && existing.DestinationPort == proxy.DestinationPort)
            .Select((Proxy existing) => existing.DestinationHost).ToListAsync(cancellationToken);

        // Normalize existing values too, so older IPv6 spellings do not bypass validation.
        if (hosts.Any((string host) => NormalizeDestinationHost(host) == proxy.DestinationHost))
        {
            throw new ProxyConflictException("A proxy for this destination host and port already exists.");
        }
    }

    private static string NormalizeDestinationHost(string host)
    {
        string destinationHost = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (destinationHost.StartsWith('[') && destinationHost.EndsWith(']'))
        {
            destinationHost = destinationHost[1..^1];
        }

        if (IPAddress.TryParse(destinationHost, out IPAddress? address))
        {
            return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        }

        return destinationHost;
    }
}
