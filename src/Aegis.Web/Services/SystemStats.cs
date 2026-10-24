using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Aegis.Web.Services;

public sealed record MemoryInfo(long TotalBytes, long UsedBytes, long AvailableBytes, long SwapTotalBytes, long SwapUsedBytes);

public sealed record DiskInfo(string Mount, long TotalBytes, long UsedBytes, long FreeBytes);

public sealed record CpuSample(long Total, long Idle);

/// <summary>Collects VM facts from /proc, df and the installed tools. Linux specific; on other OSes fields are simply empty.</summary>
public sealed class SystemStatsService(Microsoft.Extensions.Options.IOptions<AegisOptions> options, JobService jobs)
{
    private static readonly DateTimeOffset AppStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private readonly object _cpuLock = new();
    private CpuSample? _lastCpu = ParseCpuStat(Read("/proc/stat"));
    private Dictionary<string, string>? _versions;
    private DateTimeOffset _versionsAt;

    // ---- pure parsers (unit tested) ----

    public static MemoryInfo ParseMeminfo(string text)
    {
        var kb = new Dictionary<string, long>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2) continue;
            var num = parts[1].Trim().Split(' ')[0];
            if (long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) kb[parts[0].Trim()] = v * 1024;
        }
        long Get(string k) => kb.TryGetValue(k, out var v) ? v : 0;
        var total = Get("MemTotal");
        var available = kb.ContainsKey("MemAvailable") ? Get("MemAvailable") : Get("MemFree") + Get("Buffers") + Get("Cached");
        return new MemoryInfo(total, Math.Max(0, total - available), available, Get("SwapTotal"), Math.Max(0, Get("SwapTotal") - Get("SwapFree")));
    }

    /// <summary>Parses the aggregate "cpu" line of /proc/stat into total and idle jiffies.</summary>
    public static CpuSample? ParseCpuStat(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null) return null;
        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(x => long.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
        if (f.Length < 4) return null;
        var idle = f[3] + (f.Length > 4 ? f[4] : 0); // idle + iowait
        return new CpuSample(f.Take(8).Sum(), idle);
    }

    public static double? CpuPercent(CpuSample? prev, CpuSample? now)
    {
        if (prev is null || now is null) return null;
        var dTotal = now.Total - prev.Total;
        if (dTotal <= 0) return null;
        return Math.Round(100.0 * (dTotal - (now.Idle - prev.Idle)) / dTotal, 1);
    }

    public static double[] ParseLoadAvg(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3)
            .Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();

    public static double? ParseUptimeSeconds(string text) =>
        double.TryParse(text.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static string? ParseOsPrettyName(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal));
        return line?["PRETTY_NAME=".Length..].Trim().Trim('"');
    }

    /// <summary>Parses `df -B1 --output=target,size,used,avail PATH` output (header + one row).</summary>
    public static DiskInfo? ParseDf(string text)
    {
        var row = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault();
        if (row is null) return null;
        var p = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 4) return null;
        return long.TryParse(p[^3], out var size) && long.TryParse(p[^2], out var used) && long.TryParse(p[^1], out var avail)
            ? new DiskInfo(string.Join(' ', p[..^3]), size, used, avail) : null;
    }

    // ---- collection ----

    public object Collect()
    {
        var cpuNow = ParseCpuStat(Read("/proc/stat"));
        double? cpuPct;
        lock (_cpuLock)
        {
            cpuPct = CpuPercent(_lastCpu, cpuNow);
            _lastCpu = cpuNow;
        }

        var load = ParseLoadAvg(Read("/proc/loadavg"));
        var now = DateTimeOffset.UtcNow;
        var disk = ParseDf(RunTool("df", ["-B1", "--output=target,size,used,avail", options.Value.ResolvedWorkDir], 3000) ?? "")
                   ?? ParseDf(RunTool("df", ["-B1", "--output=target,size,used,avail", "/"], 3000) ?? "");

        return new
        {
            hostname = Environment.MachineName,
            os = ParseOsPrettyName(Read("/etc/os-release")) ?? RuntimeInformation.OSDescription,
            kernel = Read("/proc/sys/kernel/osrelease").Trim() is { Length: > 0 } k ? k : RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            uptimeSeconds = ParseUptimeSeconds(Read("/proc/uptime")),
            cpuCount = Environment.ProcessorCount,
            cpuPercent = cpuPct,
            loadAverage = load,
            memory = ParseMeminfo(Read("/proc/meminfo")),
            disk,
            versions = Versions(),
            app = new
            {
                startedAt = AppStart,
                uptimeSeconds = Math.Round((now - AppStart).TotalSeconds),
                workingSetBytes = Environment.WorkingSet,
            },
            downloads = jobs.GetStats(),
            serverTime = now,
        };
    }

    private Dictionary<string, string> Versions()
    {
        if (_versions is not null && DateTimeOffset.UtcNow - _versionsAt < TimeSpan.FromMinutes(10)) return _versions;
        var o = options.Value;
        var ffmpeg = RunTool(string.IsNullOrWhiteSpace(o.FfmpegPath) ? "ffmpeg" : o.FfmpegPath, ["-version"], 3000)?.Split('\n')[0];
        _versions = new Dictionary<string, string>
        {
            [".NET"] = RuntimeInformation.FrameworkDescription,
            ["yt-dlp"] = RunTool(o.YtDlpPath, ["--version"], 5000)?.Trim() ?? "not found",
            ["ffmpeg"] = ffmpeg is null ? "not found" : ffmpeg.Replace("ffmpeg version ", "").Split(' ')[0],
        };
        _versionsAt = DateTimeOffset.UtcNow;
        return _versions;
    }

    private static string Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; } catch { return ""; }
    }

    private static string? RunTool(string file, string[] args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch { return null; }
    }
}
