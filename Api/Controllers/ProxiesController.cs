using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProxyCraft.Api.Contracts;
using ProxyCraft.Services;

namespace ProxyCraft.Api.Controllers;

[ApiController, Route("api/v1/proxies"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ProxiesController(
    ProxyService proxies,
    ConnectionEventService events) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("List configured proxies")]
    [EndpointDescription("Search is optional and matches domain or destination, ignoring case. Results are ordered by domain and include an in-memory count of current named forwarding sessions. Health is a cached Minecraft TCP check. Responses also include configured/actual RCON forwarding ports and listener errors.")]
    public Task<IReadOnlyList<ProxyResponse>> List([FromQuery] string? search, CancellationToken cancellationToken) =>
        proxies.ListAsync(search, cancellationToken);

    [HttpGet("{id:int}/events")]
    [EndpointSummary("List player connection attempts")]
    [EndpointDescription("Returns the latest 20 saved login attempts for this proxy, newest first. Player names are client-supplied, not verified identities or confirmed joins. Server-list pings are excluded. Deleting the proxy deletes its events.")]
    [ProducesResponseType<IReadOnlyList<ConnectionEventResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ConnectionEventResponse>>> ListEvents(int id, CancellationToken cancellationToken)
    {
        IReadOnlyList<ConnectionEventResponse>? entries = await events.ListAsync(id, cancellationToken);
        return entries is null ? NotFound() : Ok(entries);
    }

    [HttpGet("{id:int}/connections")]
    [EndpointSummary("List live player connections")]
    [EndpointDescription("Returns open forwarded TCP sessions for this proxy, newest first. Sessions are held only in memory and removed when they close or the route resets. Client-supplied names do not confirm Minecraft authentication or a completed login. Status pings and failed backend connections are excluded; a silent network loss remains visible until TCP detects it.")]
    [ProducesResponseType<IReadOnlyList<LiveConnectionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<LiveConnectionResponse>>> ListConnections(int id, CancellationToken cancellationToken)
    {
        IReadOnlyList<LiveConnectionResponse>? connections = await proxies.ListConnectionsAsync(id, cancellationToken);
        return connections is null ? NotFound() : Ok(connections);
    }

    [HttpDelete("{id:int}")]
    [EndpointSummary("Delete a proxy")]
    [EndpointDescription("Removes the saved route, its health checks, and its existing connections. Other routes stay connected. The domain and destination become available immediately.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken) =>
        await proxies.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HttpPut("{id:int}")]
    [EndpointSummary("Edit a proxy")]
    [EndpointDescription("Saves and replaces only this route. Its current players disconnect and can reconnect using the new settings; other routes stay connected. Invalid changes leave the running route untouched. Optional RCON fields preserve existing values when omitted; a new public RCON port is reserved before saving. RCON-only changes preserve Minecraft sessions.")]
    [ProducesResponseType<ProxyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProxyResponse>> Update(int id, CreateProxyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            ProxyResponse? proxy = await proxies.UpdateAsync(id, request, cancellationToken);
            return proxy is null ? NotFound() : Ok(proxy);
        }
        catch (ProxyConflictException exception)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "A proxy for this domain or public RCON port already exists.");
        }
    }

    [HttpPost]
    [EndpointSummary("Create a proxy")]
    [EndpointDescription("The route becomes available immediately. Both the domain and the destination host/port pair must be unique. The same host with a different port is allowed. Optional RCON forwarding defaults off with public/backend port 25575; enabled public ports must be unique and bind successfully before saving.")]
    [ProducesResponseType<ProxyResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProxyResponse>> Create(CreateProxyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            ProxyResponse proxy = await proxies.CreateAsync(request, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, proxy);
        }
        catch (ProxyConflictException exception)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "A proxy for this domain or public RCON port already exists.");
        }
    }
}
