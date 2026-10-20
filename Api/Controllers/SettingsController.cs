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
        settings.Current.ShowPublicDomains);

    [HttpPut("app")]
    [EndpointSummary("Change public dashboard visibility")]
    [EndpointDescription("Controls the anonymous welcome page and optional domain-name list. Management endpoints, backend addresses, player history, and resource metrics remain protected when sign-in is enabled.")]
    public async Task<AppSettingsResponse> UpdateApp(UpdateAppSettingsRequest request, CancellationToken cancellationToken)
    {
        await settings.UpdateAsync((ApplicationSettings current) => Task.FromResult(current with
        {
            PublicDashboardEnabled = request.PublicDashboardEnabled,
            ShowPublicDomains = request.ShowPublicDomains
        }), cancellationToken);
        return GetApp();
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
