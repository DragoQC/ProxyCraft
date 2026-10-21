using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ProxyCraft.Models;

namespace ProxyCraft.Data;

public sealed class ProxyCraftDbContext(DbContextOptions<ProxyCraftDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Proxy> Proxies => Set<Proxy>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<ConnectionEvent> ConnectionEvents => Set<ConnectionEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().ToTable("users");
        modelBuilder.Entity<User>().HasIndex((User user) => user.NormalizedUsernameOrEmail).IsUnique();
        modelBuilder.Entity<User>().Property((User user) => user.UsernameOrEmail).HasMaxLength(254);
        modelBuilder.Entity<User>().Property((User user) => user.NormalizedUsernameOrEmail).HasMaxLength(254);

        modelBuilder.Entity<Proxy>().ToTable("proxies");
        modelBuilder.Entity<Proxy>().HasIndex((Proxy proxy) => proxy.Domain).IsUnique();
        modelBuilder.Entity<Proxy>().HasIndex((Proxy proxy) => proxy.RconListenPort)
            .IsUnique().HasFilter("\"RconEnabled\" = 1");
        modelBuilder.Entity<Proxy>().Property((Proxy proxy) => proxy.RconEnabled).HasDefaultValue(false);
        modelBuilder.Entity<Proxy>().Property((Proxy proxy) => proxy.RconListenPort).HasDefaultValue(25575);
        modelBuilder.Entity<Proxy>().Property((Proxy proxy) => proxy.RconDestinationPort).HasDefaultValue(25575);
        modelBuilder.Entity<Proxy>().Property((Proxy proxy) => proxy.Domain).HasMaxLength(253);
        modelBuilder.Entity<Proxy>().Property((Proxy proxy) => proxy.DestinationHost).HasMaxLength(253);

        modelBuilder.Entity<ConnectionEvent>().ToTable("connection_events");
        modelBuilder.Entity<ConnectionEvent>().HasOne<Proxy>().WithMany()
            .HasForeignKey((ConnectionEvent entry) => entry.ProxyId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ConnectionEvent>().HasIndex(nameof(ConnectionEvent.ProxyId), nameof(ConnectionEvent.Id));
        modelBuilder.Entity<ConnectionEvent>().Property((ConnectionEvent entry) => entry.PlayerName).HasMaxLength(16);
        modelBuilder.Entity<ConnectionEvent>().Property((ConnectionEvent entry) => entry.Domain).HasMaxLength(253);
        modelBuilder.Entity<ConnectionEvent>().Property((ConnectionEvent entry) => entry.CreatedAt).HasColumnName("created_at");
        modelBuilder.Entity<ConnectionEvent>().Property((ConnectionEvent entry) => entry.ModifiedAt).HasColumnName("modified_at");
        modelBuilder.Entity<ConnectionEvent>().Property((ConnectionEvent entry) => entry.IsActive).HasColumnName("is_active");

        modelBuilder.Entity<ApiKey>().ToTable("api_keys", (Microsoft.EntityFrameworkCore.Metadata.Builders.TableBuilder<ApiKey> table) =>
            table.HasCheckConstraint("CK_api_keys_singleton", "\"Id\" = 1"));
        modelBuilder.Entity<ApiKey>().Property((ApiKey key) => key.Id).ValueGeneratedNever();
        modelBuilder.Entity<ApiKey>().Property((ApiKey key) => key.KeyHash).HasMaxLength(64);
        modelBuilder.Entity<ApiKey>().Property((ApiKey key) => key.KeyPrefix).HasMaxLength(12);
        modelBuilder.Entity<ApiKey>().Property((ApiKey key) => key.CreatedAt).HasColumnName("created_at");
        modelBuilder.Entity<ApiKey>().Property((ApiKey key) => key.ModifiedAt).HasColumnName("modified_at");
        modelBuilder.Entity<ApiKey>().Property((ApiKey key) => key.IsActive).HasColumnName("is_active");

        modelBuilder.Entity<User>().Property((User user) => user.CreatedAt).HasColumnName("created_at");
        modelBuilder.Entity<User>().Property((User user) => user.ModifiedAt).HasColumnName("modified_at");
        modelBuilder.Entity<User>().Property((User user) => user.IsActive).HasColumnName("is_active");
        modelBuilder.Entity<Proxy>().Property((Proxy proxy) => proxy.CreatedAt).HasColumnName("created_at");
        modelBuilder.Entity<Proxy>().Property((Proxy proxy) => proxy.ModifiedAt).HasColumnName("modified_at");
        modelBuilder.Entity<Proxy>().Property((Proxy proxy) => proxy.IsActive).HasColumnName("is_active");
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;

        foreach (EntityEntry<BaseModel> entry in ChangeTracker.Entries<BaseModel>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.ModifiedAt = now;
                entry.Property((BaseModel model) => model.CreatedAt).IsModified = false;
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}
