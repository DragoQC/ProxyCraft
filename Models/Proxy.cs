namespace ProxyCraft.Models;

public sealed class Proxy : BaseModel
{
    public int Id { get; set; }

    public string Domain { get; set; } = string.Empty;

    public string DestinationHost { get; set; } = string.Empty;

    public int DestinationPort { get; set; } = 25565;

    public bool RconEnabled { get; set; }

    public int RconListenPort { get; set; } = 25575;

    public int RconDestinationPort { get; set; } = 25575;
}
