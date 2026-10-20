namespace ProxyCraft.Models;

public abstract class BaseModel
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ModifiedAt { get; set; }

    public bool IsActive { get; set; } = true;
}
