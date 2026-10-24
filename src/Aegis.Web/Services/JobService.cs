using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Aegis.Web.Services;

/// <summary>Owns the in-memory job table, runs downloads in the background and removes expired files.</summary>
public sealed class JobService
{
    private const int MaxTrackedJobs = 25;

    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly IDownloader _downloader;
    private readonly AegisOptions _options;
    private readonly ILogger<JobService> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _ttl;

    public JobService(IDownloader downloader, IOptions<AegisOptions> options, ILogger<JobService> logger)
    {
        _downloader = downloader;
        _options = options.Value;
        _logger = logger;
        _slots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentJobs));
        _ttl = TimeSpan.FromMinutes(Math.Max(1, _options.FileTtlMinutes));
        Ttl = _ttl;

        // Anything left over from a previous run is garbage by definition.
        try
        {
            if (Directory.Exists(_options.ResolvedWorkDir)) Directory.Delete(_options.ResolvedWorkDir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean work dir at startup");
        }
        Directory.CreateDirectory(_options.ResolvedWorkDir);
    }

    public TimeSpan Ttl { get; }

    private readonly object _statsLock = new();
    private readonly LinkedList<JobRecord> _recent = new();
    private int _total, _succeeded, _failed;
    private long _bytesServed;

    /// <summary>Adds to the "bytes served" counter (called when a file is sent to a client).</summary>
    public void AddBytesServed(long bytes) => Interlocked.Add(ref _bytesServed, bytes);

    public DownloadStats GetStats()
    {
        lock (_statsLock)
        {
            var active = _jobs.Values.Count(j => j.State is JobState.Queued or JobState.Running);
            return new DownloadStats(_total, _succeeded, _failed, active, Interlocked.Read(ref _bytesServed), _recent.ToList());
        }
    }

    private void Record(Job job)
    {
        lock (_statsLock)
        {
            if (job.State == JobState.Done) _succeeded++; else _failed++;
            _recent.AddFirst(new JobRecord(job.Id, SafeHost(job.Url), job.State.ToString().ToLowerInvariant(), job.CreatedAt,
                job.FinishedAt is { } f ? Math.Round((f - job.CreatedAt).TotalSeconds, 1) : null, job.FileSize, job.State == JobState.Failed ? job.Error : null));
            while (_recent.Count > 10) _recent.RemoveLast();
        }
    }

    /// <summary>Only the host is kept for display: full URLs can contain tokens.</summary>
    public static string SafeHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "?";

    public Job? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>Creates a job and starts it in the background. Returns null when too many jobs are tracked.</summary>
    public Job? Start(string url)
    {
        if (_jobs.Count >= MaxTrackedJobs) return null;

        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var job = new Job { Id = id, Url = url, Directory = Path.Combine(_options.ResolvedWorkDir, id) };
        _jobs[id] = job;
        lock (_statsLock) _total++;
        _ = Task.Run(() => RunAsync(job));
        return job;
    }

    private async Task RunAsync(Job job)
    {
        try { await RunCoreAsync(job); }
        finally { Record(job); }
    }

    private async Task RunCoreAsync(Job job)
    {
        await _slots.WaitAsync();
        try
        {
            job.SetRunning();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(1, _options.JobTimeoutMinutes)));
            var path = await _downloader.DownloadAsync(job.Url, job.Directory, (p, m) => job.SetProgress(p, m), cts.Token);
            job.SetDone(path);
            _logger.LogInformation("Job {Id} finished ({Bytes} bytes)", job.Id, job.FileSize);
        }
        catch (OperationCanceledException)
        {
            job.SetFailed("The download took too long and was cancelled.");
            DeleteDirectory(job.Directory);
        }
        catch (DownloadException ex)
        {
            job.SetFailed(ex.Message);
            DeleteDirectory(job.Directory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {Id} crashed", job.Id);
            job.SetFailed("Unexpected server error.");
            DeleteDirectory(job.Directory);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>Deletes a job and its files immediately.</summary>
    public bool Remove(string id)
    {
        if (!_jobs.TryRemove(id, out var job)) return false;
        DeleteDirectory(job.Directory);
        return true;
    }

    /// <summary>Removes finished jobs older than the TTL (and their temp files). Returns how many were removed.</summary>
    public int Sweep(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var (id, job) in _jobs)
        {
            if (job.FinishedAt is { } finished && now - finished > _ttl && job.OneDrive != UploadState.Uploading)
            {
                if (_jobs.TryRemove(id, out _))
                {
                    DeleteDirectory(job.Directory);
                    removed++;
                }
            }
        }
        return removed;
    }

    private void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }
}

/// <summary>Periodically triggers <see cref="JobService.Sweep"/>.</summary>
public sealed class CleanupService(JobService jobs, ILogger<CleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var n = jobs.Sweep(DateTimeOffset.UtcNow);
            if (n > 0) logger.LogInformation("Cleaned up {Count} expired job(s)", n);
        }
    }
}

public sealed record JobRecord(string Id, string Host, string State, DateTimeOffset StartedAt, double? DurationSeconds, long? FileSize, string? Error);

public sealed record DownloadStats(int Total, int Succeeded, int Failed, int Active, long BytesServed, IReadOnlyList<JobRecord> Recent);
