using System.Net.Sockets;
using ProxyCraft.Models;

namespace ProxyCraft.Services;

internal sealed record RouteHealth(
    bool? IsReachable,
    DateTimeOffset? LastCheckedAt);

internal sealed class ProxyRoute(Proxy proxy) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private RouteHealth _health = new(null, null);

    public int Id { get; } = proxy.Id;
    public string Domain { get; } = proxy.Domain;
    public string DestinationHost { get; } = proxy.DestinationHost;
    public int DestinationPort { get; } = proxy.DestinationPort;
    public bool IsActive { get; } = proxy.IsActive;
    public RouteHealth Health => Volatile.Read(ref _health);
    public Task Probes { get; private set; } = Task.CompletedTask;

    public CancellationToken Token => _lifetime.Token;

    public void Start() => Probes = ProbeAsync();

    public void Stop() => _lifetime.Cancel();

    private async Task ProbeAsync()
    {
        CancellationToken cancellationToken = _lifetime.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested && IsActive)
            {
                bool reachable = false;
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    using TcpClient client = new();
                    await client.ConnectAsync(DestinationHost, DestinationPort, timeout.Token);
                    reachable = true;
                }
                catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
                Volatile.Write(ref _health, new RouteHealth(reachable, DateTimeOffset.UtcNow));
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping or replacing a route ends its health loop.
        }
    }

    public void Dispose() => _lifetime.Dispose();
}
