using ProxyCraft.Api.Contracts;
using ProxyCraft.Configuration;

namespace ProxyCraft.Services;

public sealed class SystemMetricsService(DataPaths paths) : BackgroundService
{
    private readonly LinuxSystemMetricsReader _linux = new();
    private SystemMetricsResponse _current = new(null, null, null, null, null, null);

    public SystemMetricsResponse Current => Volatile.Read(ref _current);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(2));
        do
        {
            SystemMetricsResponse sample = OperatingSystem.IsLinux()
                ? await _linux.ReadAsync(stoppingToken)
                : new SystemMetricsResponse(Environment.ProcessorCount, null, null, null, null, null);
            try
            {
                // On Unix, DriveInfo accepts an existing directory and queries its actual filesystem.
                string directory = OperatingSystem.IsWindows() ? Path.GetPathRoot(paths.DirectoryPath)! : paths.DirectoryPath;
                DriveInfo drive = new(directory);
                long total = drive.TotalSize;
                if (total > 0)
                {
                    sample = sample with
                    {
                        TotalDiskBytes = total,
                        DiskUsagePercent = Math.Clamp(100d * (total - drive.TotalFreeSpace) / total, 0, 100)
                    };
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Keep the disk reading unavailable when its filesystem cannot be inspected.
            }
            Volatile.Write(ref _current, sample);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
