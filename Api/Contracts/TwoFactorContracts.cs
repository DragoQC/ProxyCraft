using System.ComponentModel.DataAnnotations;

namespace ProxyCraft.Api.Contracts;

public sealed record TwoFactorSettingsResponse(
    bool IsEnabled,
    int RecoveryCodesRemaining);

public sealed record TwoFactorSetupResponse(
    string Secret,
    string AuthenticatorUri,
    string SetupToken);

public sealed record TwoFactorEnabledResponse(IReadOnlyList<string> RecoveryCodes);

public sealed class TwoFactorPasswordRequest
{
    [Required, StringLength(128, MinimumLength = 12)]
    public string Password { get; set; } = string.Empty;
}

public sealed class ConfirmTwoFactorRequest
{
    [Required, StringLength(4096)]
    public string SetupToken { get; set; } = string.Empty;

    [Required, RegularExpression("^[0-9]{6}$")]
    public string Code { get; set; } = string.Empty;
}

public sealed class DisableTwoFactorRequest
{
    [Required, StringLength(128, MinimumLength = 12)]
    public string Password { get; set; } = string.Empty;

    [Required, StringLength(32)]
    public string Code { get; set; } = string.Empty;
}

public sealed class LoginRequest : CredentialsRequest
{
    [StringLength(32)]
    public string? TwoFactorCode { get; set; }
}
