namespace ProxyCraft.Configuration;

public sealed class ProxyOptions
{
    public const string SectionName = "Proxy";

    public int ListenPort { get; set; } = 25565;
}
