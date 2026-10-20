using Microsoft.AspNetCore.Mvc;
using ProxyCraft.Api.Contracts;
using ProxyCraft.Services;

namespace ProxyCraft.Api.Controllers;

[ApiController, Route("api/v1/system"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SystemController(SystemMetricsService metrics) : ControllerBase
{
    [HttpGet("metrics")]
    [EndpointSummary("Read system resource usage")]
    [EndpointDescription("CPU and RAM describe the Linux guest/container, respecting visible cgroup limits. CPU usage is sampled over time; memory in a container includes charged cache. Disk describes the filesystem containing app data. Samples refresh every 2 seconds. Missing, unsupported, or not-yet-sampled readings are null.")]
    public SystemMetricsResponse GetMetrics() => metrics.Current;
}
