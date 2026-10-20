using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProxyCraft.Api.Contracts;
using ProxyCraft.Data;
using ProxyCraft.Models;
using ProxyCraft.Security;

namespace ProxyCraft.Services;

internal sealed record TwoFactorEnrollment(
    int UserId,
    string SecurityStamp,
    string Secret,
    DateTimeOffset CreatedAt);

public sealed class TwoFactorService(
    ProxyCraftDbContext database,
    SettingsStore settings,
    IPasswordHasher<User> passwordHasher,
    IDataProtectionProvider protection)
{
    private readonly IDataProtector _secrets = protection.CreateProtector("ProxyCraft.TwoFactor.Secret.v1");
    private readonly IDataProtector _enrollment = protection.CreateProtector("ProxyCraft.TwoFactor.Enrollment.v1");

    public async Task<TwoFactorSettingsResponse> GetAsync(int userId, CancellationToken cancellationToken)
    {
        User user = await database.Users.AsNoTracking().SingleAsync((User row) => row.Id == userId, cancellationToken);
        return new TwoFactorSettingsResponse(user.TwoFactorSecret is not null, ReadRecoveryHashes(user).Count);
    }

    public async Task<TwoFactorSetupResponse?> BeginAsync(int userId, string password, CancellationToken cancellationToken)
    {
        User? user = await database.Users.AsNoTracking().SingleOrDefaultAsync((User row) => row.Id == userId, cancellationToken);
        if (!CanManage(user, password) || user!.TwoFactorSecret is not null)
        {
            return null;
        }

        string secret = TotpCodes.CreateSecret();
        TwoFactorEnrollment enrollment = new(user.Id, user.SecurityStamp, secret, DateTimeOffset.UtcNow);
        string token = _enrollment.Protect(JsonSerializer.Serialize(enrollment));
        string label = Uri.EscapeDataString("ProxyCraft:" + user.UsernameOrEmail);
        string uri = $"otpauth://totp/{label}?secret={secret}&issuer=ProxyCraft&algorithm=SHA1&digits=6&period=30";
        return new TwoFactorSetupResponse(secret, uri, token);
    }

    public async Task<TwoFactorEnabledResponse?> ConfirmAsync(int userId, ConfirmTwoFactorRequest request, CancellationToken cancellationToken)
    {
        TwoFactorEnrollment? enrollment;
        try
        {
            enrollment = JsonSerializer.Deserialize<TwoFactorEnrollment>(_enrollment.Unprotect(request.SetupToken));
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (enrollment is null || enrollment.UserId != userId || enrollment.CreatedAt > now
            || now - enrollment.CreatedAt > TimeSpan.FromMinutes(10))
        {
            return null;
        }

        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        User? user = await database.Users.SingleOrDefaultAsync((User row) => row.Id == userId, cancellationToken);
        if (user is null || !user.IsActive || !settings.Current.AuthenticationEnabled
            || user.SecurityStamp != enrollment.SecurityStamp || user.TwoFactorSecret is not null)
        {
            return null;
        }

        long? step = TotpCodes.Match(enrollment.Secret, request.Code, now, -1);
        if (step is null)
        {
            return null;
        }

        List<string> recoveryCodes = [];
        for (int index = 0; index < 8; index++)
        {
            string raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
            recoveryCodes.Add($"{raw[..5]}-{raw[5..10]}-{raw[10..15]}-{raw[15..]}");
        }

        user.TwoFactorSecret = _secrets.Protect(enrollment.Secret);
        user.RecoveryCodeHashes = JsonSerializer.Serialize(recoveryCodes.Select((string code) => HashRecoveryCode(code)));
        user.LastTotpStep = step;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TwoFactorEnabledResponse(recoveryCodes);
    }

    public async Task<bool> VerifyAsync(int userId, string? code, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        User? user = await database.Users.SingleOrDefaultAsync((User row) => row.Id == userId, cancellationToken);
        if (user is null)
        {
            return false;
        }

        // AuthenticationService may already be tracking this user. Reload under the write lock before consuming a code.
        await database.Entry(user).ReloadAsync(cancellationToken);
        if (!user.IsActive || !settings.Current.AuthenticationEnabled || !ConsumeCode(user, code))
        {
            return false;
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DisableAsync(int userId, DisableTwoFactorRequest request, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        User? user = await database.Users.SingleOrDefaultAsync((User row) => row.Id == userId, cancellationToken);
        if (!CanManage(user, request.Password) || user!.TwoFactorSecret is null || !ConsumeCode(user, request.Code))
        {
            return false;
        }

        user.TwoFactorSecret = null;
        user.RecoveryCodeHashes = null;
        user.LastTotpStep = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private bool CanManage(User? user, string password) => settings.Current.AuthenticationEnabled
        && user is { IsActive: true }
        && passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;

    private bool ConsumeCode(User user, string? supplied)
    {
        if (user.TwoFactorSecret is null)
        {
            return true;
        }

        string code = supplied?.Trim() ?? string.Empty;
        try
        {
            long? step = TotpCodes.Match(_secrets.Unprotect(user.TwoFactorSecret), code, DateTimeOffset.UtcNow, user.LastTotpStep ?? -1);
            if (step is not null)
            {
                user.LastTotpStep = step;
                return true;
            }
        }
        catch (CryptographicException)
        {
            // Recovery codes still work if a deployment loses its Data Protection key ring.
        }

        List<string> hashes = ReadRecoveryHashes(user);
        byte[] candidate = Convert.FromHexString(HashRecoveryCode(code));
        int match = hashes.FindIndex((string hash) => CryptographicOperations.FixedTimeEquals(candidate, Convert.FromHexString(hash)));
        if (match < 0)
        {
            return false;
        }

        hashes.RemoveAt(match);
        user.RecoveryCodeHashes = JsonSerializer.Serialize(hashes);
        return true;
    }

    private static List<string> ReadRecoveryHashes(User user) =>
        user.RecoveryCodeHashes is null ? [] : JsonSerializer.Deserialize<List<string>>(user.RecoveryCodeHashes) ?? [];

    private static string HashRecoveryCode(string code)
    {
        string normalized = code.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
