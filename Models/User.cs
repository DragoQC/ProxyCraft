namespace ProxyCraft.Models;

public sealed class User : BaseModel
{
    public int Id { get; set; }

    public string UsernameOrEmail { get; set; } = string.Empty;

    public string NormalizedUsernameOrEmail { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    // Protected with ASP.NET Core Data Protection; never store the authenticator secret in plaintext.
    public string? TwoFactorSecret { get; set; }

    public string? RecoveryCodeHashes { get; set; }

    public long? LastTotpStep { get; set; }
}
