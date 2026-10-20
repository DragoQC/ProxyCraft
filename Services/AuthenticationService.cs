using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProxyCraft.Api.Contracts;
using ProxyCraft.Configuration;
using ProxyCraft.Data;
using ProxyCraft.Models;

namespace ProxyCraft.Services;

public sealed class AuthenticationService(
    ProxyCraftDbContext database,
    SettingsStore settings,
    IPasswordHasher<User> passwordHasher)
{
    public async Task<bool> EnableAsync(CredentialsRequest request, CancellationToken cancellationToken = default)
    {
        bool enabled = false;

        await settings.UpdateAsync(async (ApplicationSettings current) =>
        {
            if (current.AuthenticationEnabled)
            {
                return current;
            }

            User? user = await database.Users.FindAsync([1], cancellationToken);

            if (user is null)
            {
                user = new User { Id = 1 };
                database.Users.Add(user);
            }

            user.UsernameOrEmail = request.UsernameOrEmail.Trim();
            user.NormalizedUsernameOrEmail = user.UsernameOrEmail.ToUpperInvariant();
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.TwoFactorSecret = null;
            user.RecoveryCodeHashes = null;
            user.LastTotpStep = null;
            user.IsActive = true;
            await database.SaveChangesAsync(cancellationToken);
            enabled = true;
            return current with
            {
                AuthenticationEnabled = true
            };
        }, cancellationToken);

        return enabled;
    }

    public Task<ApplicationSettings> DisableAsync(CancellationToken cancellationToken = default) =>
        settings.UpdateAsync((ApplicationSettings current) => Task.FromResult(current with { AuthenticationEnabled = false }), cancellationToken);

    public async Task<User?> AuthenticateAsync(CredentialsRequest request, CancellationToken cancellationToken = default)
    {
        if (!settings.Current.AuthenticationEnabled)
        {
            return null;
        }

        string identifier = request.UsernameOrEmail.Trim().ToUpperInvariant();
        User? user = await database.Users.SingleOrDefaultAsync(
            (User candidate) => candidate.NormalizedUsernameOrEmail == identifier && candidate.IsActive, cancellationToken);

        if (user is null)
        {
            return null;
        }

        PasswordVerificationResult result = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);

        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
            await database.SaveChangesAsync(cancellationToken);
        }

        return settings.Current.AuthenticationEnabled ? user : null;
    }

    public Task<bool> IsSessionValidAsync(int userId, string securityStamp, CancellationToken cancellationToken = default) =>
        database.Users.AnyAsync((User user) => user.Id == userId && user.SecurityStamp == securityStamp && user.IsActive, cancellationToken);
}
