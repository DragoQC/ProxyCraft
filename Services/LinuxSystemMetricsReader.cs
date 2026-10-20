using System.Diagnostics;
using System.Globalization;
using ProxyCraft.Api.Contracts;

namespace ProxyCraft.Services;

// procDirectory is injectable for isolated fixtures; production always reads /proc.
internal sealed class LinuxSystemMetricsReader(string procDirectory = "/proc")
{
    private sealed record CgroupMount(
        string Root,
        string Current,
        string Controller,
        bool Unified,
        bool Namespaced);
    private sealed record CpuSample(
        string Source,
        double Cores,
        double Used,
        double Total);
    private CpuSample? _previousCpu;

    public async Task<SystemMetricsResponse> ReadAsync(CancellationToken cancellationToken = default)
    {
        string? stat = await ReadFileAsync(Path.Combine(procDirectory, "stat"), cancellationToken);
        string? meminfo = await ReadFileAsync(Path.Combine(procDirectory, "meminfo"), cancellationToken);
        List<CgroupMount> mounts = await ReadMountsAsync(cancellationToken);
        CpuSample? cpu = await ReadCpuAsync(stat, mounts, cancellationToken);
        double? usage = null;
        if (cpu is not null && _previousCpu is not null && cpu.Source == _previousCpu.Source && cpu.Cores == _previousCpu.Cores)
        {
            double elapsed = cpu.Total - _previousCpu.Total;
            double used = cpu.Used - _previousCpu.Used;
            if (elapsed > 0 && used >= 0)
            {
                usage = Math.Clamp(100 * used / elapsed, 0, 100);
            }
        }
        _previousCpu = cpu;

        Dictionary<string, long> memory = ReadCounters(meminfo);
        long? totalMemory = memory.TryGetValue("MemTotal", out long totalKb) && totalKb > 0 ? totalKb * 1024 : null;
        double? memoryUsage = totalMemory is > 0 && memory.TryGetValue("MemAvailable", out long availableKb)
            ? Math.Clamp(100d * (totalMemory.Value - availableKb * 1024) / totalMemory.Value, 0, 100) : null;
        CgroupMount? memoryMount = FindMount(mounts, "memory");
        if (memoryMount is not null)
        {
            string? selected = null;
            long? limit = null;
            foreach (string directory in Ancestors(memoryMount))
            {
                long? candidate = ParseLong(await ReadFileAsync(Path.Combine(directory,
                    memoryMount.Unified ? "memory.max" : "memory.limit_in_bytes"), cancellationToken));
                // v1 uses a huge integer as its unlimited sentinel; ignore limits above guest RAM.
                if (candidate is > 0 && (memoryMount.Unified || candidate < (1L << 60))
                    && (totalMemory is null || candidate <= totalMemory) && (limit is null || candidate < limit))
                {
                    limit = candidate;
                    selected = directory;
                }
            }
            if (selected is null && IsContainer(memoryMount))
            {
                selected = memoryMount.Root;
            }

            if (selected is not null)
            {
                totalMemory = limit ?? totalMemory;
                long? current = ParseLong(await ReadFileAsync(Path.Combine(selected,
                    memoryMount.Unified ? "memory.current" : "memory.usage_in_bytes"), cancellationToken));
                // Never combine host-wide usage with a container's capacity if its counters are unreadable.
                memoryUsage = current is >= 0 && totalMemory is > 0
                    ? Math.Clamp(100d * current.Value / totalMemory.Value, 0, 100) : null;
            }
        }
        return new SystemMetricsResponse(cpu?.Cores, usage, totalMemory, memoryUsage, null, null);
    }

    private async Task<CpuSample?> ReadCpuAsync(string? stat, List<CgroupMount> mounts, CancellationToken cancellationToken)
    {
        string[] lines = (stat ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        double cores = lines.Count((string line) => line.Length > 3 && line.StartsWith("cpu", StringComparison.Ordinal) && char.IsAsciiDigit(line[3]));
        if (cores == 0)
        {
            return null;
        }

        string? selected = null;
        CgroupMount? cpuMount = FindMount(mounts, "cpu");
        if (cpuMount is not null)
        {
            foreach (string directory in Ancestors(cpuMount))
            {
                double? quota;
                if (cpuMount.Unified)
                {
                    string[] values = (await ReadFileAsync(Path.Combine(directory, "cpu.max"), cancellationToken) ?? string.Empty)
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    long? budget = values.Length == 2 ? ParseLong(values[0]) : null;
                    long? period = values.Length == 2 ? ParseLong(values[1]) : null;
                    quota = budget is > 0 && period is > 0 ? (double)budget.Value / period.Value : null;
                }
                else
                {
                    long? budget = ParseLong(await ReadFileAsync(Path.Combine(directory, "cpu.cfs_quota_us"), cancellationToken));
                    long? period = ParseLong(await ReadFileAsync(Path.Combine(directory, "cpu.cfs_period_us"), cancellationToken));
                    quota = budget is > 0 && period is > 0 ? (double)budget.Value / period.Value : null;
                }
                if (quota is > 0 && (quota < cores || quota == cores && selected is null))
                {
                    cores = Math.Min(cores, quota.Value);
                    selected = directory;
                }
                // A memory limit can identify the container boundary even without a CPU quota.
                if (selected is null && cpuMount.Unified
                    && ParseLong(await ReadFileAsync(Path.Combine(directory, "memory.max"), cancellationToken)) is > 0)
                {
                    selected = directory;
                }
            }
        }
        CgroupMount? cpusetMount = FindMount(mounts, "cpuset");
        if (cpusetMount is not null)
        {
            foreach (string directory in Ancestors(cpusetMount))
            {
                int count = CountCpus(await ReadFileAsync(Path.Combine(directory,
                    cpusetMount.Unified ? "cpuset.cpus.effective" : "cpuset.cpus"), cancellationToken));
                if (count > 0 && count < cores)
                {
                    cores = count;
                    if (cpuMount is not null)
                    {
                        selected = MapDirectory(directory, cpusetMount, cpuMount);
                    }
                }
            }
        }
        if (cpuMount is not null)
        {
            if (selected is null && IsContainer(cpuMount))
            {
                selected = cpuMount.Root;
            }

            if (selected is not null)
            {
                CgroupMount? accounting = FindMount(mounts, "cpuacct");
                if (accounting is null)
                {
                    return null;
                }

                string directory = MapDirectory(selected, cpuMount, accounting);
                double? seconds;
                if (accounting.Unified)
                {
                    Dictionary<string, long> counters = ReadCounters(await ReadFileAsync(Path.Combine(directory, "cpu.stat"), cancellationToken));
                    seconds = counters.TryGetValue("usage_usec", out long microseconds) ? microseconds / 1_000_000d : null;
                }
                else
                {
                    long? nanoseconds = ParseLong(await ReadFileAsync(Path.Combine(directory, "cpuacct.usage"), cancellationToken));
                    seconds = nanoseconds / 1_000_000_000d;
                }
                return seconds is >= 0 ? new CpuSample(directory, cores, seconds.Value, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency * cores) : null;
            }
        }

        string[] fields = (lines.FirstOrDefault((string line) => line.StartsWith("cpu ", StringComparison.Ordinal)) ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5)
        {
            return null;
        }

        double total = 0;
        // guest/guest_nice are already included in user/nice: sum only the first eight counters.
        for (int index = 1; index < Math.Min(fields.Length, 9); index++)
        {
            long? counter = ParseLong(fields[index]);
            if (counter is null || counter < 0)
            {
                return null;
            }

            total += counter.Value;
        }
        long? idle = ParseLong(fields[4]);
        long? ioWait = fields.Length > 5 ? ParseLong(fields[5]) : 0;
        return idle is not null && ioWait is not null ? new CpuSample("proc", cores, total - idle.Value - ioWait.Value, total) : null;
    }

    private async Task<List<CgroupMount>> ReadMountsAsync(CancellationToken cancellationToken)
    {
        string[] memberships = (await ReadFileAsync(Path.Combine(procDirectory, "self/cgroup"), cancellationToken) ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] mounts = (await ReadFileAsync(Path.Combine(procDirectory, "self/mountinfo"), cancellationToken) ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        List<CgroupMount> result = [];
        foreach (string mount in mounts)
        {
            string[] parts = mount.Split(" - ", 2, StringSplitOptions.None);
            if (parts.Length != 2)
            {
                continue;
            }

            string[] left = parts[0].Split(' ');
            string[] right = parts[1].Split(' ');
            if (left.Length < 6 || right.Length < 3 || right[0] is not ("cgroup" or "cgroup2"))
            {
                continue;
            }

            bool unified = right[0] == "cgroup2";
            foreach (string membership in memberships)
            {
                string[] fields = membership.Split(':', 3);
                if (fields.Length != 3 || unified != (fields[0] == "0" && fields[1].Length == 0))
                {
                    continue;
                }

                string[] controllers = unified ? [string.Empty] : fields[1].Split(',').Intersect(right[2].Split(',')).ToArray();
                string root = Unescape(left[3]);
                string mountPoint = Unescape(left[4]).TrimEnd('/');
                string relative = root != "/" && fields[2].StartsWith(root + "/", StringComparison.Ordinal)
                    ? fields[2][root.Length..] : fields[2] == root ? string.Empty : fields[2];
                string current = Path.GetFullPath(Path.Combine(mountPoint, relative.TrimStart('/')));
                if (current != mountPoint && !current.StartsWith(mountPoint + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (string controller in controllers)
                {
                    result.Add(new CgroupMount(mountPoint, current, controller, unified, root != "/"));
                }
            }
        }
        return result;
    }

    private static CgroupMount? FindMount(List<CgroupMount> mounts, string controller) =>
        mounts.FirstOrDefault((CgroupMount mount) => !mount.Unified && mount.Controller == controller)
        ?? mounts.FirstOrDefault((CgroupMount mount) => mount.Unified);

    private static IEnumerable<string> Ancestors(CgroupMount mount)
    {
        List<string> directories = [];
        string? directory = mount.Current;
        while (directory is not null && (directory == mount.Root || directory.StartsWith(mount.Root + "/", StringComparison.Ordinal)))
        {
            directories.Add(directory);
            if (directory == mount.Root)
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }
        directories.Reverse();
        return directories;
    }

    private static string MapDirectory(string directory, CgroupMount from, CgroupMount to) =>
        Path.GetFullPath(Path.Combine(to.Root, Path.GetRelativePath(from.Root, directory)));

    private static bool IsContainer(CgroupMount mount) => mount.Namespaced || File.Exists("/.dockerenv")
        || File.Exists("/run/.containerenv") || File.Exists("/run/systemd/container") || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("container"));

    private static int CountCpus(string? value)
    {
        int count = 0;
        foreach (string range in (value ?? string.Empty).Trim().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] ends = range.Split('-');
            if (!int.TryParse(ends[0], out int first) || first < 0)
            {
                return 0;
            }

            if (ends.Length == 1)
            {
                count++;
            }
            else if (ends.Length == 2 && int.TryParse(ends[1], out int last) && last >= first)
            {
                count += last - first + 1;
            }
            else
            {
                return 0;
            }
        }
        return count;
    }

    private static Dictionary<string, long> ReadCounters(string? content)
    {
        Dictionary<string, long> result = new(StringComparer.Ordinal);
        foreach (string line in (content ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 1 && ParseLong(fields[1]) is long value)
            {
                result[fields[0].TrimEnd(':')] = value;
            }
        }
        return result;
    }

    private static long? ParseLong(string? value) => long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : null;

    private static string Unescape(string value) => value.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");

    private static async Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
