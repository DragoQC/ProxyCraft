namespace ProxyCraft.Models;

public sealed class ApiKey : BaseModel
{
    public int Id { get; set; } = 1;

    public string KeyHash { get; set; } = string.Empty;

    public string KeyPrefix { get; set; } = string.Empty;
}
