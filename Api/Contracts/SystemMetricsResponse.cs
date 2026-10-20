namespace ProxyCraft.Api.Contracts;

public sealed record SystemMetricsResponse(
    double? TotalCpuCores,
    double? CpuUsagePercent,
    long? TotalMemoryBytes,
    double? MemoryUsagePercent,
    long? TotalDiskBytes,
    double? DiskUsagePercent);
