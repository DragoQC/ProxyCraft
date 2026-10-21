using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using ProxyCraft.Configuration;
using ProxyCraft.Services;

namespace ProxyCraft.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = services.CreateScope();
        SettingsStore settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        await settings.InitializeAsync(cancellationToken);

        ProxyCraftDbContext database = scope.ServiceProvider.GetRequiredService<ProxyCraftDbContext>();
        bool created = await database.Database.EnsureCreatedAsync(cancellationToken);

        // Explicitly approved optional user fields. Add only missing columns on older installations.
        await database.Database.OpenConnectionAsync(cancellationToken);
        HashSet<string> userColumns = new(StringComparer.OrdinalIgnoreCase);
        await using (DbCommand command = database.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('users')";
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                userColumns.Add(reader.GetString(1));
            }
        }

        foreach (string column in new string[] { "TwoFactorSecret", "RecoveryCodeHashes", "LastTotpStep" })
        {
            if (userColumns.Contains(column))
            {
                continue;
            }

            string statement = column switch
            {
                "TwoFactorSecret" => "ALTER TABLE \"users\" ADD COLUMN \"TwoFactorSecret\" TEXT NULL",
                "RecoveryCodeHashes" => "ALTER TABLE \"users\" ADD COLUMN \"RecoveryCodeHashes\" TEXT NULL",
                _ => "ALTER TABLE \"users\" ADD COLUMN \"LastTotpStep\" INTEGER NULL"
            };
            await database.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }

        // Explicitly approved optional RCON forwarding fields; older routes stay disabled.
        HashSet<string> proxyColumns = new(StringComparer.OrdinalIgnoreCase);
        await using (DbCommand command = database.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('proxies')";
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                proxyColumns.Add(reader.GetString(1));
            }
        }
        foreach (string column in new string[] { "RconEnabled", "RconListenPort", "RconDestinationPort" })
        {
            if (proxyColumns.Contains(column))
            {
                continue;
            }
            string statement = column switch
            {
                "RconEnabled" => "ALTER TABLE \"proxies\" ADD COLUMN \"RconEnabled\" INTEGER NOT NULL DEFAULT 0",
                "RconListenPort" => "ALTER TABLE \"proxies\" ADD COLUMN \"RconListenPort\" INTEGER NOT NULL DEFAULT 25575",
                _ => "ALTER TABLE \"proxies\" ADD COLUMN \"RconDestinationPort\" INTEGER NOT NULL DEFAULT 25575"
            };
            await database.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }
        await database.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_proxies_RconListenPort"
                ON "proxies" ("RconListenPort") WHERE "RconEnabled" = 1;
            """, cancellationToken);

        await database.Database.CloseConnectionAsync();

        // The user authorized these new tables. EnsureCreated does not add tables to an existing database.
        // Keep additions narrow and idempotent; existing users, proxies, and schemas are untouched.
        await database.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "api_keys" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_api_keys" PRIMARY KEY,
                "KeyHash" TEXT NOT NULL,
                "KeyPrefix" TEXT NOT NULL,
                "created_at" TEXT NOT NULL,
                "modified_at" TEXT NULL,
                "is_active" INTEGER NOT NULL,
                CONSTRAINT "CK_api_keys_singleton" CHECK ("Id" = 1)
            );
            """, cancellationToken);

        await database.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "connection_events" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_connection_events" PRIMARY KEY AUTOINCREMENT,
                "ProxyId" INTEGER NOT NULL,
                "PlayerName" TEXT NOT NULL,
                "Domain" TEXT NOT NULL,
                "created_at" TEXT NOT NULL,
                "modified_at" TEXT NULL,
                "is_active" INTEGER NOT NULL,
                CONSTRAINT "FK_connection_events_proxies_ProxyId"
                    FOREIGN KEY ("ProxyId") REFERENCES "proxies" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_connection_events_ProxyId_Id" ON "connection_events" ("ProxyId", "Id");
            """, cancellationToken);

        if (created && settings.Current.AuthenticationEnabled)
        {
            await settings.UpdateAsync(
                (ApplicationSettings current) => Task.FromResult(current with { AuthenticationEnabled = false }),
                cancellationToken);
        }
    }
}
