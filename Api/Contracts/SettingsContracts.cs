using System.ComponentModel.DataAnnotations;
using ProxyCraft.Configuration;

namespace ProxyCraft.Api.Contracts;

public sealed record AppSettingsResponse(
    bool AuthenticationEnabled,
    bool PublicDashboardEnabled = false,
    bool ShowPublicDomains = false,
    string PublicDashboardMessage = ApplicationSettings.DefaultPublicDashboardMessage,
    string PublicDashboardTitle = ApplicationSettings.DefaultPublicDashboardTitle);

public sealed class UpdateAppSettingsRequest
{
    public bool PublicDashboardEnabled { get; set; }

    public bool ShowPublicDomains { get; set; }

    [StringLength(500, MinimumLength = 1)]
    public string? PublicDashboardMessage { get; set; }

    [StringLength(120, MinimumLength = 1)]
    public string? PublicDashboardTitle { get; set; }
}

public sealed record ProxySettingsResponse(
    int ListenPort,
    int? ListeningPort,
    string? ListenerError);

public sealed record SessionResponse(
    bool AuthenticationEnabled,
    bool IsAuthenticated,
    string? UsernameOrEmail,
    string CsrfToken);

public sealed class UpdateProxySettingsRequest
{
    [Range(1, 65535)]
    public int ListenPort { get; set; } = 25565;
}
