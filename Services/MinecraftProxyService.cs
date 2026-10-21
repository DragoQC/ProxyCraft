using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProxyCraft.Data;
using ProxyCraft.Models;

namespace ProxyCraft.Services;

public sealed record ListenerStatus(
    int? ListeningPort,
    string? Error);

internal sealed record LiveConnection(
    long Id,
    int ProxyId,
    string PlayerName,
    string Domain,
    DateTimeOffset ConnectedAt);

public sealed class MinecraftProxyService(
    IServiceScopeFactory scopes,
    SettingsStore settings,
    ILogger<MinecraftProxyService> logger) : IHostedService, IDisposable
{
    private readonly Lock _routesLock = new();
    private readonly Dictionary<int, ProxyRoute> _routes = [];
    private readonly Dictionary<string, ProxyRoute> _domains = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly SemaphoreSlim _listenerLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly Dictionary<long, LiveConnection> _liveConnections = [];
    private ListenerStatus _status = new(null, null);
    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCancellation;
    private Task _acceptTask = Task.CompletedTask;
    private long _connectionId;
    private bool _disposed;

    public ListenerStatus Status => Volatile.Read(ref _status);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        ProxyCraftDbContext database = scope.ServiceProvider.GetRequiredService<ProxyCraftDbContext>();
        List<Proxy> proxies = await database.Proxies.AsNoTracking().ToListAsync(cancellationToken);
        foreach (Proxy proxy in proxies)
        {
            await ReplaceRouteAsync(proxy.Id, proxy);
        }

        await ConfigureListenerAsync(cancellationToken);
    }

    // Read committed state again, serialized, so overlapping saves/deletes cannot install an older snapshot.
    public async Task RefreshRouteAsync(int id)
    {
        await _reloadLock.WaitAsync(_shutdown.Token);
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            ProxyCraftDbContext database = scope.ServiceProvider.GetRequiredService<ProxyCraftDbContext>();
            Proxy? proxy = await database.Proxies.AsNoTracking().SingleOrDefaultAsync((Proxy row) => row.Id == id, _shutdown.Token);
            await ReplaceRouteAsync(id, proxy);
        }
        finally { _reloadLock.Release(); }
    }

    private async Task ReplaceRouteAsync(int id, Proxy? proxy)
    {
        ProxyRoute? replacement = proxy is null ? null : new ProxyRoute(proxy);
        ProxyRoute? previous;
        lock (_routesLock)
        {
            _shutdown.Token.ThrowIfCancellationRequested();
            _routes.Remove(id, out previous);
            if (previous is not null && _domains.TryGetValue(previous.Domain, out ProxyRoute? mapped) && ReferenceEquals(mapped, previous))
            {
                _domains.Remove(previous.Domain);
            }

            if (replacement is not null)
            {
                _routes[id] = replacement;
                if (replacement.IsActive)
                {
                    _domains[replacement.Domain] = replacement;
                }

                replacement.Start();
            }
            previous?.Stop();
        }
        if (previous is not null)
        {
            await previous.Probes;
            previous.Dispose();
        }
    }

    internal RouteHealth GetHealth(Proxy proxy)
    {
        lock (_routesLock)
        {
            return _routes.TryGetValue(proxy.Id, out ProxyRoute? route)
                && route.Domain == proxy.Domain && route.DestinationHost == proxy.DestinationHost
                && route.DestinationPort == proxy.DestinationPort
                    ? route.Health : new RouteHealth(null, null);
        }
    }

    internal IReadOnlyList<LiveConnection> GetLiveConnections(int proxyId)
    {
        lock (_routesLock)
        {
            return _liveConnections.Values.Where((LiveConnection connection) => connection.ProxyId == proxyId)
                .OrderByDescending((LiveConnection connection) => connection.Id).ToList();
        }
    }

    internal IReadOnlyDictionary<int, int> GetLiveConnectionCounts()
    {
        lock (_routesLock)
        {
            Dictionary<int, int> counts = [];
            foreach (LiveConnection connection in _liveConnections.Values)
            {
                counts.TryGetValue(connection.ProxyId, out int count);
                counts[connection.ProxyId] = count + 1;
            }
            return counts;
        }
    }

    private void RemoveLiveConnection(long id)
    {
        lock (_routesLock)
        {
            _liveConnections.Remove(id);
        }
    }

    public async Task ConfigureListenerAsync(CancellationToken cancellationToken = default)
    {
        await _listenerLock.WaitAsync(cancellationToken);
        try
        {
            int port = settings.Current.ListenPort;
            if (Status.ListeningPort == port)
            {
                Volatile.Write(ref _status, new ListenerStatus(port, null));
                return;
            }
            TcpListener listener = TcpListener.Create(port);
            try
            {
                listener.Start();
            }
            catch (SocketException exception)
            {
                listener.Stop();
                Volatile.Write(ref _status, new ListenerStatus(Status.ListeningPort, $"Cannot listen on port {port}: {exception.Message}"));
                logger.LogWarning(exception, "Cannot bind Minecraft listener on port {Port}", port);
                return;
            }

            // Bind the replacement first. Existing streams are independent of the accepting socket.
            _acceptCancellation?.Cancel();
            _listener?.Stop();
            await _acceptTask;
            _acceptCancellation?.Dispose();
            _listener = listener;
            _acceptCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            Volatile.Write(ref _status, new ListenerStatus(port, null));
            _acceptTask = AcceptAsync(listener, _acceptCancellation.Token);
            logger.LogInformation("Minecraft Java listener started on TCP port {Port}", port);
        }
        finally { _listenerLock.Release(); }
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
                long id = Interlocked.Increment(ref _connectionId);
                Task connection = ForwardAsync(client, id, _shutdown.Token);
                _connections[id] = connection;
                _ = connection.ContinueWith((Task completed) =>
                {
                    _connections.TryRemove(id, out Task? removed);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Closing the listener ends the pending accept during shutdown or a port change.
        }
        catch (SocketException exception)
        {
            listener.Stop();
            Volatile.Write(ref _status, new ListenerStatus(null, $"Minecraft listener stopped: {exception.Message}"));
            logger.LogError(exception, "Minecraft listener stopped accepting connections");
        }
    }

    private async Task ForwardAsync(TcpClient client, long id, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                NetworkStream incoming = client.GetStream();
                using CancellationTokenSource handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                MinecraftHandshake handshake = await MinecraftHandshake.ReadAsync(incoming, handshakeTimeout.Token);
                ProxyRoute? route;
                CancellationToken routeToken;
                lock (_routesLock)
                {
                    if (!_domains.TryGetValue(handshake.Domain, out route))
                    {
                        return;
                    }

                    routeToken = route.Token;
                }

                using CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, routeToken);
                using TcpClient backend = new()
                {
                    NoDelay = true
                };
                // Closing both sockets on cancellation also unblocks pending writes during a route replacement.
                using CancellationTokenRegistration closeClient = session.Token.Register(() => client.Dispose());
                using CancellationTokenRegistration closeBackend = session.Token.Register(() => backend.Dispose());
                using CancellationTokenRegistration removeLiveConnection = session.Token.Register(() => RemoveLiveConnection(id));
                MinecraftLoginStart? login = null;
                if (handshake.NextState is 2 or 3)
                {
                    using CancellationTokenSource loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    loginTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    login = await MinecraftLoginStart.ReadAsync(incoming, loginTimeout.Token);
                    await SaveConnectionEventAsync(route.Id, handshake.Domain, login.PlayerName, session.Token);
                }
                using CancellationTokenSource connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await backend.ConnectAsync(route.DestinationHost, route.DestinationPort, connectTimeout.Token);
                if (backend.Client.RemoteEndPoint is IPEndPoint endpoint && IsListenerEndpoint(endpoint))
                {
                    throw new IOException("A route cannot forward back into this listener.");
                }

                NetworkStream outgoing = backend.GetStream();
                await outgoing.WriteAsync(handshake.Packet, session.Token);
                if (login is not null)
                {
                    await outgoing.WriteAsync(login.Packet, session.Token);
                }

                if (login is not null)
                {
                    lock (_routesLock)
                    {
                        session.Token.ThrowIfCancellationRequested();
                        if (!_routes.TryGetValue(route.Id, out ProxyRoute? current) || !ReferenceEquals(current, route))
                        {
                            return;
                        }

                        _liveConnections[id] = new LiveConnection(id, route.Id, login.PlayerName, handshake.Domain, DateTimeOffset.UtcNow);
                    }
                }
                Task upload = incoming.CopyToAsync(outgoing, session.Token);
                Task download = outgoing.CopyToAsync(incoming, session.Token);
                await Task.WhenAny(upload, download);
                await session.CancelAsync();
                try
                {
                    await Task.WhenAll(upload, download);
                }
                catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // Ending either direction cancels and closes the remaining forwarding operation.
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException or OperationCanceledException or ObjectDisposedException or DecoderFallbackException)
            {
                logger.LogDebug(exception, "Minecraft connection closed");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Minecraft connection failed");
            }
            finally
            {
                RemoveLiveConnection(id);
            }
        }
    }

    private async Task SaveConnectionEventAsync(int proxyId, string domain, string playerName, CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            ConnectionEventService events = scope.ServiceProvider.GetRequiredService<ConnectionEventService>();
            await events.RecordAsync(proxyId, domain, playerName, cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException)
        {
            logger.LogWarning(exception, "Could not save a player connection event for proxy {ProxyId}", proxyId);
        }
    }

    private bool IsListenerEndpoint(IPEndPoint endpoint)
    {
        if (endpoint.Port != Status.ListeningPort)
        {
            return false;
        }

        IPAddress address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
        return IPAddress.IsLoopback(address) || NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany((NetworkInterface adapter) => adapter.GetIPProperties().UnicastAddresses)
            .Any((UnicastIPAddressInformation local) => local.Address.Equals(address));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync();
        await _listenerLock.WaitAsync(cancellationToken);
        try
        {
            _acceptCancellation?.Cancel();
            _listener?.Stop();
            await _acceptTask;
            Volatile.Write(ref _status, new ListenerStatus(null, null));
        }
        finally { _listenerLock.Release(); }
        ProxyRoute[] routes;
        lock (_routesLock)
        {
            routes = _routes.Values.ToArray();
            _routes.Clear();
            _domains.Clear();
            foreach (ProxyRoute route in routes)
            {
                route.Stop();
            }
        }
        await Task.WhenAll(routes.Select((ProxyRoute route) => route.Probes)).WaitAsync(cancellationToken);
        await Task.WhenAll(_connections.Values).WaitAsync(cancellationToken);
        foreach (ProxyRoute route in routes)
        {
            route.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _listener?.Stop();
        _acceptCancellation?.Dispose();
        _shutdown.Dispose();
        _reloadLock.Dispose();
        _listenerLock.Dispose();
    }
}
