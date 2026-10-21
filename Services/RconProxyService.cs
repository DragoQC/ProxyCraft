using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using ProxyCraft.Data;
using ProxyCraft.Models;

namespace ProxyCraft.Services;

public sealed record RconForwardingStatus(int? ListeningPort, string? Error);

// RCON stays opaque: passwords and commands are handled by the backend and its client.
public sealed class RconProxyService(IServiceScopeFactory scopes, SettingsStore settings,
    ILogger<RconProxyService> logger) : IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<int, Binding> _bindings = new();
    private readonly ConcurrentDictionary<int, string> _errors = new();
    private readonly ConcurrentDictionary<long, Task> _sessions = new();
    private readonly SemaphoreSlim _updates = new(1, 1);
    private readonly SemaphoreSlim _capacity = new(128, 128);
    private readonly CancellationTokenSource _shutdown = new();
    private long _connectionId;
    private bool _disposed;

    public RconForwardingStatus GetStatus(Proxy proxy)
    {
        if (_bindings.TryGetValue(proxy.Id, out Binding? binding) && binding.IsRunning && proxy.RconEnabled && proxy.IsActive)
        {
            Target target = binding.Target;
            if (binding.Port == proxy.RconListenPort && target.Host == proxy.DestinationHost && target.Port == proxy.RconDestinationPort)
            {
                return new RconForwardingStatus(binding.Port, null);
            }
        }
        return new RconForwardingStatus(null, _errors.GetValueOrDefault(proxy.Id));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        ProxyCraftDbContext database = scope.ServiceProvider.GetRequiredService<ProxyCraftDbContext>();
        List<Proxy> proxies = await database.Proxies.AsNoTracking()
            .Where((Proxy proxy) => proxy.RconEnabled && proxy.IsActive).ToListAsync(cancellationToken);
        foreach (Proxy proxy in proxies)
        {
            try
            {
                await SaveRouteAsync(proxy, () => Task.CompletedTask, cancellationToken);
            }
            catch (ProxyConflictException exception)
            {
                _errors[proxy.Id] = exception.Message;
                logger.LogWarning("RCON forwarding for proxy {ProxyId} could not start: {Error}", proxy.Id, exception.Message);
            }
        }
    }

    // Hold the new socket and serialize runtime changes across the database commit.
    // A failed bind or failed persistence leaves the existing listener/sessions untouched.
    public async Task SaveRouteAsync(Proxy proxy, Func<Task> persist, CancellationToken cancellationToken)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _updates.WaitAsync(lifetime.Token);
        TcpListener? reserved = null;
        try
        {
            bool enabled = proxy.RconEnabled && proxy.IsActive;
            _bindings.TryGetValue(proxy.Id, out Binding? previous);
            if (enabled)
            {
                if (proxy.RconListenPort == settings.Current.ListenPort)
                {
                    throw new ProxyConflictException("The public RCON port must differ from the Minecraft listener port.");
                }
                if (previous is null || !previous.IsRunning || previous.Port != proxy.RconListenPort)
                {
                    reserved = TcpListener.Create(proxy.RconListenPort);
                    try
                    {
                        reserved.Start();
                    }
                    catch (SocketException)
                    {
                        throw new ProxyConflictException($"TCP port {proxy.RconListenPort} is already in use or cannot be opened. Choose another public RCON port.");
                    }
                }
            }

            await persist();
            // Once committed, apply the change even if the HTTP caller has disconnected.
            _errors.TryRemove(proxy.Id, out string? ignored);
            if (enabled && previous is Binding { IsRunning: true } && previous.Port == proxy.RconListenPort)
            {
                previous.ReplaceTarget(proxy);
            }
            else
            {
                if (enabled)
                {
                    Binding replacement = new(proxy, reserved ?? throw new InvalidOperationException("No RCON listener was reserved."), this);
                    reserved = null;
                    _bindings[proxy.Id] = replacement;
                    replacement.Start();
                }
                else
                {
                    _bindings.TryRemove(proxy.Id, out Binding? removed);
                }
                if (previous is not null)
                {
                    await previous.StopAsync();
                }
            }
        }
        finally
        {
            reserved?.Stop();
            _updates.Release();
        }
    }

    public async Task DeleteRouteAsync(int id)
    {
        await _updates.WaitAsync(_shutdown.Token);
        try
        {
            _errors.TryRemove(id, out string? ignored);
            if (_bindings.TryRemove(id, out Binding? previous))
            {
                await previous.StopAsync();
            }
        }
        finally { _updates.Release(); }
    }

    private async Task AcceptAsync(Binding binding, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await binding.Listener.AcceptTcpClientAsync(cancellationToken);
                if (!_capacity.Wait(0))
                {
                    client.Dispose();
                    continue;
                }
                long id = Interlocked.Increment(ref _connectionId);
                Task session = ForwardAsync(client, binding.Target);
                _sessions[id] = session;
                _ = session.ContinueWith((Task completed) => _sessions.TryRemove(id, out Task? removed),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested
            && exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Route removal or shutdown closes this accepting socket.
        }
        catch (SocketException exception)
        {
            binding.MarkStopped();
            _errors[binding.Target.ProxyId] = "The RCON listener stopped. Save the route to retry.";
            binding.Listener.Stop();
            logger.LogWarning(exception, "RCON listener stopped on TCP port {Port}", binding.Port);
        }
    }

    private async Task ForwardAsync(TcpClient client, Target target)
    {
        using (client)
        using (TcpClient backend = new() { NoDelay = true })
        using (CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, target.Token))
        using (CancellationTokenRegistration closeClient = session.Token.Register(() => client.Dispose()))
        using (CancellationTokenRegistration closeBackend = session.Token.Register(() => backend.Dispose()))
        {
            try
            {
                client.NoDelay = true;
                using CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                connect.CancelAfter(TimeSpan.FromSeconds(5));
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(target.Host, connect.Token);
                if (addresses.Any((IPAddress address) => IsLocalListener(address, target.Port)))
                {
                    throw new IOException("RCON forwarding cannot target a local proxy listener.");
                }
                await backend.ConnectAsync(addresses, target.Port, connect.Token);
                session.CancelAfter(TimeSpan.FromMinutes(2));
                Task upload = PumpAsync(client.GetStream(), backend.GetStream(), session);
                Task download = PumpAsync(backend.GetStream(), client.GetStream(), session);
                await Task.WhenAny(upload, download);
                await session.CancelAsync();
                try
                {
                    await Task.WhenAll(upload, download);
                }
                catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // Closing either direction ends this forwarding session.
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                logger.LogDebug(exception, "RCON forwarding connection closed for proxy {ProxyId}", target.ProxyId);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "RCON forwarding connection failed for proxy {ProxyId}", target.ProxyId);
            }
            finally { _capacity.Release(); }
        }
    }

    private static async Task PumpAsync(NetworkStream source, NetworkStream destination, CancellationTokenSource session)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(0, 16384), session.Token)) != 0)
            {
                session.CancelAfter(TimeSpan.FromMinutes(2));
                await destination.WriteAsync(buffer.AsMemory(0, count), session.Token);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private bool IsLocalListener(IPAddress address, int port)
    {
        if (port != settings.Current.ListenPort && !_bindings.Values.Any((Binding binding) => binding.Port == port))
        {
            return false;
        }
        address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return IPAddress.IsLoopback(address) || NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany((NetworkInterface adapter) => adapter.GetIPProperties().UnicastAddresses)
            .Any((UnicastIPAddressInformation local) => local.Address.Equals(address));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync();
        await _updates.WaitAsync(cancellationToken);
        try
        {
            foreach (Binding binding in _bindings.Values)
            {
                await binding.StopAsync();
            }
            _bindings.Clear();
        }
        finally { _updates.Release(); }
        await Task.WhenAll(_sessions.Values).WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _shutdown.Cancel();
        foreach (Binding binding in _bindings.Values)
        {
            binding.Listener.Stop();
        }
        _shutdown.Dispose();
        _updates.Dispose();
        _capacity.Dispose();
    }

    private sealed class Target : IDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        public Target(Proxy proxy)
        {
            ProxyId = proxy.Id;
            Host = proxy.DestinationHost;
            Port = proxy.RconDestinationPort;
            Token = _lifetime.Token;
        }
        public int ProxyId { get; }
        public string Host { get; }
        public int Port { get; }
        public CancellationToken Token { get; }
        public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
    }

    private sealed class Binding(Proxy proxy, TcpListener listener, RconProxyService owner)
    {
        private readonly CancellationTokenSource _acceptStop = CancellationTokenSource.CreateLinkedTokenSource(owner._shutdown.Token);
        private Target _target = new(proxy);
        private Task _acceptTask = Task.CompletedTask;
        private bool _running;
        public int Port { get; } = proxy.RconListenPort;
        public TcpListener Listener { get; } = listener;
        public Target Target => Volatile.Read(ref _target);
        public bool IsRunning => Volatile.Read(ref _running);
        public void Start()
        {
            Volatile.Write(ref _running, true);
            _acceptTask = owner.AcceptAsync(this, _acceptStop.Token);
        }
        public void MarkStopped() => Volatile.Write(ref _running, false);
        public void ReplaceTarget(Proxy updated)
        {
            Target previous = Interlocked.Exchange(ref _target, new Target(updated));
            previous.Dispose();
        }
        public async Task StopAsync()
        {
            MarkStopped();
            await _acceptStop.CancelAsync();
            Listener.Stop();
            Target.Dispose();
            await _acceptTask;
            _acceptStop.Dispose();
        }
    }
}
