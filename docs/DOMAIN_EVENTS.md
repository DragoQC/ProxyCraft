# Live domain events

ProxyCraft exposes a SignalR hub at **`/api/v1/hubs/domains`**. It uses ASP.NET Core's shared framework; the server needs no new package, browser library, broker, or polling process.

Generate an API key in App Settings. An integration sends that key in `X-API-Key` during negotiation and every transport request. A dashboard cookie alone cannot subscribe, even when normal dashboard sign-in is disabled. Keys are not accepted in URL parameters.

| Notification | When it is sent |
| --- | --- |
| `DomainAdded` | A new route has committed and its runtime route has refreshed. |
| `DomainRemoved` | A route has been deleted and its runtime route removed. |

Renaming a route emits `DomainRemoved` for its previous name, followed by `DomainAdded` for its new name. Editing only the destination does not add or remove a domain. Failed validation and duplicate-domain errors do not publish a notification. UI and API changes share the same `ProxyService` operations.

Both events carry the same JSON object:

```json
{
  "proxyId": 12,
  "domain": "play.example.com",
  "occurredAt": "2026-10-20T12:00:00+00:00"
}
```

`proxyId` identifies the configured proxy; `domain` is the added or removed name; `occurredAt` is UTC. The hub exposes no client-callable mutation methods.

## Connecting another C# application

In the consuming application, use the Microsoft `Microsoft.AspNetCore.SignalR.Client` package appropriate for its runtime. This is a dependency of your integration, not an added dependency of ProxyCraft.

```csharp
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;

string apiKey = Environment.GetEnvironmentVariable("PROXYCRAFT_API_KEY")
    ?? throw new InvalidOperationException("Set PROXYCRAFT_API_KEY.");

await using HubConnection connection = new HubConnectionBuilder()
    .WithUrl("https://your-dashboard/api/v1/hubs/domains", (HttpConnectionOptions options) =>
    {
        options.Headers["X-API-Key"] = apiKey;
    })
    .WithAutomaticReconnect()
    .Build();

connection.On<DomainEvent>("DomainAdded", (DomainEvent notification) =>
{
    Console.WriteLine($"Added: {notification.Domain}");
});

connection.On<DomainEvent>("DomainRemoved", (DomainEvent notification) =>
{
    Console.WriteLine($"Removed: {notification.Domain}");
});

await connection.StartAsync();
Console.ReadLine();

public sealed record DomainEvent(
    int ProxyId,
    string Domain,
    DateTimeOffset OccurredAt);
```

This is a live, best-effort feed. Notifications are not stored or replayed. After initial connection or reconnection, reconcile with `GET /api/v1/proxies`; treat notifications as prompts to refresh the relevant state. Concurrent changes can occur while a snapshot is fetched. An integration should make its own downstream actions idempotent and verify current configuration before acting. Reconnect attempts also need a valid current key.

Before publishing, the server checks subscribers against the currently active key hash and closes revoked subscribers. A failed or slow subscriber cannot roll back a saved route; delivery waits at most five seconds. There is no distributed backplane or durable event queue.

The custom `/settings/api` page includes the hub's path, inputs, outputs, and notification names. `tests/features_test.py` uses a real WebSocket and SignalR JSON handshake to verify additions, renames, deletions, authentication, and key rotation.

Implementation follows the .NET 10 documentation for [SignalR hubs](https://learn.microsoft.com/en-us/aspnet/core/signalr/hubs?view=aspnetcore-10.0) and [authentication and authorization](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-10.0).
