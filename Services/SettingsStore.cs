using System.Text.Json;
using Microsoft.Extensions.Options;
using ProxyCraft.Api.Contracts;
using ProxyCraft.Configuration;

namespace ProxyCraft.Services;

public sealed class SettingsStore(
    DataPaths paths,
    IOptions<ProxyOptions> defaults) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private ApplicationSettings _current = new() { ListenPort = defaults.Value.ListenPort };

    public ApplicationSettings Current => Volatile.Read(ref _current);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.DirectoryPath);

        if (File.Exists(paths.SettingsPath))
        {
            await using FileStream stream = File.OpenRead(paths.SettingsPath);
            ApplicationSettings settings = await JsonSerializer.DeserializeAsync<ApplicationSettings>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("The settings file is empty.");

            if (settings.ListenPort is < 1 or > 65535)
            {
                throw new InvalidDataException("The configured listener port must be between 1 and 65535.");
            }

            Volatile.Write(ref _current, settings);
        }
        else
        {
            await UpdateAsync((ApplicationSettings settings) => Task.FromResult(settings), cancellationToken);
        }
    }

    public Task<ApplicationSettings> UpdatePublicDashboardAsync(UpdateAppSettingsRequest request, CancellationToken cancellationToken = default)
    {
        if (request.PublicDashboardMessage is not null && string.IsNullOrWhiteSpace(request.PublicDashboardMessage))
        {
            throw new ArgumentException("Enter a welcome message.");
        }

        return UpdateAsync((ApplicationSettings current) => Task.FromResult(current with
        {
            PublicDashboardEnabled = request.PublicDashboardEnabled,
            ShowPublicDomains = request.PublicDashboardEnabled && request.ShowPublicDomains,
            PublicDashboardMessage = request.PublicDashboardMessage?.Trim() ?? current.PublicDashboardMessage
        }), cancellationToken);
    }

    public async Task<ApplicationSettings> UpdateAsync(
        Func<ApplicationSettings, Task<ApplicationSettings>> update,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        string temporaryPath = paths.SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            ApplicationSettings updated = await update(Current);
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(updated, JsonOptions), cancellationToken);
            File.Move(temporaryPath, paths.SettingsPath, overwrite: true);
            Volatile.Write(ref _current, updated);
            return updated;
        }
        finally
        {
            File.Delete(temporaryPath);
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();
}
