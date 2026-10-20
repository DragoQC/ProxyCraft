namespace ProxyCraft.Models;

public sealed class ConnectionEvent : BaseModel
{
    public int Id { get; set; }

    public int ProxyId { get; set; }

    public string PlayerName { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;
}
