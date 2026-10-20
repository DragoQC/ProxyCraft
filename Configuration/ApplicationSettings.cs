namespace ProxyCraft.Configuration;

public sealed record ApplicationSettings
{
    public const string DefaultPublicDashboardMessage = "Welcome to ProxyCraft";

    public bool AuthenticationEnabled { get; init; }

    public bool PublicDashboardEnabled { get; init; }

    public bool ShowPublicDomains { get; init; }

    public string PublicDashboardMessage { get; init; } = DefaultPublicDashboardMessage;

    public int ListenPort { get; init; } = 25565;
}
