using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProxyCraft.Api.Contracts;
using ProxyCraft.Services;

namespace ProxyCraft.Api.Controllers;

[ApiController, AllowAnonymous, Route("api/v1/public")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PublicController(PublicDashboardService dashboard) : ControllerBase
{
    [HttpGet("dashboard")]
    [EndpointSummary("Read the optional public welcome dashboard")]
    [EndpointDescription("Returns only public domain names when both public dashboard and domain visibility are enabled. Never exposes destinations, players, metrics, or management access. Returns 404 when the public dashboard is disabled.")]
    [ProducesResponseType<PublicDashboardResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicDashboardResponse>> Get(CancellationToken cancellationToken)
    {
        PublicDashboardResponse? response = await dashboard.GetAsync(cancellationToken);
        return response is null ? NotFound() : Ok(response);
    }
}
