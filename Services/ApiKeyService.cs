using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProxyCraft.Api.Contracts;
using ProxyCraft.Data;
using ProxyCraft.Models;

namespace ProxyCraft.Services;

public sealed class ApiKeyService(ProxyCraftDbContext database)
{
    public async Task<ApiKeySettingsResponse> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        ApiKey? key = await database.ApiKeys.AsNoTracking().SingleOrDefaultAsync((ApiKey candidate) => candidate.Id == 1 && candidate.IsActive, cancellationToken);
        return new ApiKeySettingsResponse(key is not null, key?.KeyPrefix);
    }

    public async Task<GeneratedApiKeyResponse> GenerateAsync(CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        ApiKey? key = await database.ApiKeys.FindAsync([1], cancellationToken);
        if (key is null)
        {
            key = new ApiKey();
            database.ApiKeys.Add(key);
        }
        string secret = "pc_" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        key.KeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        key.KeyPrefix = secret[..11];
        key.IsActive = true;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new GeneratedApiKeyResponse(secret, key.KeyPrefix);
    }

    public async Task<bool> ValidateAsync(string secret, CancellationToken cancellationToken = default)
    {
        if (secret.Length != 46 || !secret.StartsWith("pc_", StringComparison.Ordinal))
        {
            return false;
        }

        ApiKey? key = await database.ApiKeys.AsNoTracking().SingleOrDefaultAsync((ApiKey candidate) => candidate.Id == 1 && candidate.IsActive, cancellationToken);
        if (key is null)
        {
            return false;
        }

        byte[] suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        byte[] storedHash = Convert.FromHexString(key.KeyHash);
        return CryptographicOperations.FixedTimeEquals(suppliedHash, storedHash);
    }
}
