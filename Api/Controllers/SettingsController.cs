using Microsoft.AspNetCore.Mvc;
using ProxyCraft.Api.Contracts;
using ProxyCraft.Configuration;
using ProxyCraft.Services;

namespace ProxyCraft.Api.Controllers;

[ApiController, Route("api/v1/settings"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SettingsController(
    SettingsStore settings,
    MinecraftProxyService runtime) : ControllerBase
{
    [HttpGet("app")]
    [EndpointSummary("Read app settings")]
    public AppSettingsResponse GetApp() => new(
        settings.Current.AuthenticationEnabled,
        settings.Current.PublicDashboardEnabled,
        settings.Current.PublicDashboardEnabled && settings.Current.ShowPublicDomains,
        settings.Current.PublicDashboardMessage,
        settings.Current.PublicDashboardTitle);

    [HttpPut("app")]
    [EndpointSummary("Change public dashboard visibility")]
    [EndpointDescription("Controls the anonymous welcome page, plain-text title/paragraph, and optional domain-name list. Disabling the page clears domain visibility. Omitted/null text fields are preserved; titles require 1–120 characters with non-whitespace text. Messages allow 0–500 characters; send an empty string to clear the paragraph. Management information remains protected when sign-in is enabled.")]
    [ProducesResponseType<AppSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AppSettingsResponse>> UpdateApp(UpdateAppSettingsRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await settings.UpdatePublicDashboardAsync(request, cancellationToken);
            return GetApp();
        }
        catch (ArgumentException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
    }

    [HttpGet("proxy")]
    [EndpointSummary("Read listener settings")]
    [EndpointDescription("Returns the configured Minecraft port, actual listening port, and any bind error. Dashboard HTTP/HTTPS ports are separate.")]
    public ProxySettingsResponse GetProxy()
    {
        ListenerStatus status = runtime.Status;
        return new ProxySettingsResponse(settings.Current.ListenPort, status.ListeningPort, status.Error);
    }

    [HttpPut("proxy")]
    [EndpointSummary("Change the Minecraft listener port")]
    [EndpointDescription("Saves the requested port and tries to bind it before releasing the old listener. Existing connections survive. Check listenerError even on HTTP 200: a failed bind retains the previous listener.")]
    public async Task<ProxySettingsResponse> UpdateProxy(UpdateProxySettingsRequest request, CancellationToken cancellationToken)
    {
        await settings.UpdateAsync(
            (ApplicationSettings current) => Task.FromResult(current with { ListenPort = request.ListenPort }), cancellationToken);
        await runtime.ConfigureListenerAsync();
        return GetProxy();
    }
}
