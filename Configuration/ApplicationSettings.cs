namespace ProxyCraft.Configuration;

public sealed record ApplicationSettings
{
    public bool AuthenticationEnabled { get; init; }

    public bool PublicDashboardEnabled { get; init; }

    public bool ShowPublicDomains { get; init; }

    public int ListenPort { get; init; } = 25565;
}
