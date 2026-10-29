namespace DiskHaven.Api;

/// <summary>
/// Live machine numbers for the small corner gauge: CPU, memory, temperature and disk read/write speed.
/// Read every 2 seconds from /proc and /sys (the container sees the host's counters); the API only returns the
/// last sample, so a request never waits for a measurement. Missing sources (no sensor, not Linux) give null.
/// </summary>
public sealed class SystemStats : BackgroundService
{
    public sealed record Sample(DateTimeOffset At, double? Cpu, long? MemUsed, long? MemTotal, double? Temperature,
        long? ReadPerSec, long? WritePerSec, double[] CpuHistory);

    private volatile Sample _last = new(DateTimeOffset.UtcNow, null, null, null, null, null, null, []);
    private readonly Queue<double> _history = new();
    public Sample Last => _last;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        (long idle, long total)? prevCpu = null;
        (long read, long write, DateTimeOffset at)? prevDisk = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                double? cpu = null;
                if (ReadCpu() is { } c)
                {
                    if (prevCpu is { } p && c.total > p.total)
                        cpu = Math.Round(100.0 * (1 - (double)(c.idle - p.idle) / (c.total - p.total)), 1);
                    prevCpu = c;
                }
                var (used, total) = ReadMemory();
                long? rps = null, wps = null;
                var now = DateTimeOffset.UtcNow;
                if (ReadDisks() is { } d)
                {
                    if (prevDisk is { } pd && now > pd.at)
                    {
                        var s = (now - pd.at).TotalSeconds;
                        rps = (long)Math.Max(0, (d.read - pd.read) / s);
                        wps = (long)Math.Max(0, (d.write - pd.write) / s);
                    }
                    prevDisk = (d.read, d.write, now);
                }
                if (cpu is { } v)
                {
                    _history.Enqueue(v);
                    while (_history.Count > 60) _history.Dequeue();
                }
                _last = new Sample(now, cpu, used, total, ReadTemperature(), rps, wps, _history.ToArray());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private static (long idle, long total)? ReadCpu()
    {
        if (!File.Exists("/proc/stat")) return null;
        var line = File.ReadLines("/proc/stat").First();   // cpu  user nice system idle iowait irq softirq steal
        var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(long.Parse).ToArray();
        return (v[3] + v[4], v.Sum());
    }

    private static (long? used, long? total) ReadMemory()
    {
        if (!File.Exists("/proc/meminfo")) return (null, null);
        long? total = null, avail = null;
        foreach (var l in File.ReadLines("/proc/meminfo"))
        {
            if (l.StartsWith("MemTotal:")) total = Kb(l);
            else if (l.StartsWith("MemAvailable:")) avail = Kb(l);
            if (total is not null && avail is not null) break;
        }
        return (total - avail, total);
        static long Kb(string l) => long.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;
    }

    /// <summary>Sum over whole physical disks (sdX, nvmeXnY, vdX; not partitions, loop or dm devices), in bytes.</summary>
    private static (long read, long write)? ReadDisks()
    {
        if (!File.Exists("/proc/diskstats")) return null;
        long r = 0, w = 0;
        foreach (var l in File.ReadLines("/proc/diskstats"))
        {
            var f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 10) continue;
            var name = f[2];
            var whole = (name.StartsWith("sd") || name.StartsWith("vd") || name.StartsWith("hd")) && !char.IsDigit(name[^1])
                        || name.StartsWith("nvme") && !name.Contains('p');
            if (!whole) continue;
            r += long.Parse(f[5]) * 512;   // sectors read
            w += long.Parse(f[9]) * 512;   // sectors written
        }
        return (r, w);
    }

    /// <summary>CPU package temperature: AMD k10temp (Tctl), Intel coretemp (Package), otherwise the first thermal zone.</summary>
    private static double? ReadTemperature()
    {
        const string hw = "/sys/class/hwmon";
        if (Directory.Exists(hw))
            foreach (var dir in Directory.EnumerateDirectories(hw))
            {
                var name = File.Exists(Path.Combine(dir, "name")) ? File.ReadAllText(Path.Combine(dir, "name")).Trim() : "";
                if (name is not ("k10temp" or "coretemp" or "zenpower")) continue;
                var input = Path.Combine(dir, "temp1_input");
                if (File.Exists(input)) return Math.Round(long.Parse(File.ReadAllText(input).Trim()) / 1000.0, 1);
            }
        const string zone = "/sys/class/thermal/thermal_zone0/temp";
        return File.Exists(zone) ? Math.Round(long.Parse(File.ReadAllText(zone).Trim()) / 1000.0, 1) : null;
    }
}
