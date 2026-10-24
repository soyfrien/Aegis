using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Aegis.Web.Services;

/// <summary>Owns the in-memory job table, runs downloads in the background and removes expired files.</summary>
public sealed class JobService
{
    private const int MaxTrackedJobs = 25;

    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly IDownloader _downloader;
    private readonly IReencoder _reencoder;
    private readonly AegisOptions _options;
    private readonly ILogger<JobService> _logger;
    private readonly SemaphoreSlim _slots;
    // Only one ffmpeg encode at a time: the VM is tiny. It is separate from the download slots so queued encodes never block downloads.
    private readonly SemaphoreSlim _encodeSlot = new(1, 1);
    private readonly TimeSpan _ttl;

    public JobService(IDownloader downloader, IReencoder reencoder, IOptions<AegisOptions> options, ILogger<JobService> logger)
    {
        _downloader = downloader;
        _reencoder = reencoder;
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
    private int _reencoded, _reencodeFailed;
    private double _reencodeSeconds;

    /// <summary>Adds to the "bytes served" counter (called when a file is sent to a client).</summary>
    public void AddBytesServed(long bytes) => Interlocked.Add(ref _bytesServed, bytes);

    public DownloadStats GetStats()
    {
        lock (_statsLock)
        {
            var active = _jobs.Values.Count(j => j.State is JobState.Queued or JobState.Running);
            return new DownloadStats(_total, _succeeded, _failed, active, Interlocked.Read(ref _bytesServed), _recent.ToList(),
                _reencoded, _reencodeFailed, Math.Round(_reencodeSeconds, 1));
        }
    }

    private void Record(Job job)
    {
        lock (_statsLock)
        {
            if (job.State == JobState.Done) _succeeded++; else _failed++;
            _recent.AddFirst(new JobRecord(job.Id, SafeHost(job.Url), job.State.ToString().ToLowerInvariant(), job.CreatedAt,
                job.FinishedAt is { } f ? Math.Round((f - job.CreatedAt).TotalSeconds, 1) : null, job.FileSize, job.State == JobState.Failed ? job.Error : null, job.ReencodeResult));
            while (_recent.Count > 10) _recent.RemoveLast();
        }
    }

    /// <summary>Only the host is kept for display: full URLs can contain tokens.</summary>
    public static string SafeHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "?";

    public Job? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>Waits until the job is Done or Failed (or the token is cancelled / the timeout passes) and returns it.</summary>
    public async Task<Job> WaitAsync(Job job, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (job.State is JobState.Queued or JobState.Running)
                await Task.Delay(250, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // timed out waiting; caller inspects job.State
        }
        return job;
    }

    public TimeSpan JobTimeout => TimeSpan.FromMinutes(Math.Max(1, _options.JobTimeoutMinutes));

    public TimeSpan ReencodeTimeout => TimeSpan.FromMinutes(Math.Max(1, _options.Reencode.TimeoutMinutes));

    /// <summary>The longest a whole job (download, queueing for the encoder, encode) can legitimately take.</summary>
    public TimeSpan MaxJobDuration => JobTimeout + ReencodeTimeout + ReencodeTimeout;

    /// <summary>Creates a job and starts it in the background. Returns null when too many jobs are tracked.</summary>
    public Job? Start(string url, ReencodeMode reencode = ReencodeMode.Off)
    {
        if (_jobs.Count >= MaxTrackedJobs) return null;

        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var job = new Job { Id = id, Url = url, Directory = Path.Combine(_options.ResolvedWorkDir, id), Reencode = reencode };
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
        string path;
        await _slots.WaitAsync();
        try
        {
            job.SetRunning();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(1, _options.JobTimeoutMinutes)));
            path = await _downloader.DownloadAsync(job.Url, job.Directory, (p, m) => job.SetProgress(p, m), cts.Token);
        }
        catch (OperationCanceledException)
        {
            job.SetFailed("The download took too long and was cancelled.");
            DeleteDirectory(job.Directory);
            return;
        }
        catch (DownloadException ex)
        {
            job.SetFailed(ex.Message, ex.Code);
            DeleteDirectory(job.Directory);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {Id} crashed", job.Id);
            job.SetFailed("Unexpected server error.");
            DeleteDirectory(job.Directory);
            return;
        }
        finally
        {
            _slots.Release();
        }

        // The download slot is free again; re-encoding (if asked for) has its own single slot.
        try
        {
            if (job.Reencode != ReencodeMode.Off) path = await ReencodeAsync(job, path);
            job.SetDone(path);
            _logger.LogInformation("Job {Id} finished ({Bytes} bytes, re-encode: {Result})", job.Id, job.FileSize, job.ReencodeResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {Id} crashed", job.Id);
            job.SetFailed("Unexpected server error.");
            DeleteDirectory(job.Directory);
        }
    }

    /// <summary>Runs the re-encode step. Never throws for encoder problems: on failure or timeout the original file is kept and the job says so.</summary>
    private async Task<string> ReencodeAsync(Job job, string original)
    {
        job.SetProgress(0, "Waiting for another re-encode to finish");
        await _encodeSlot.WaitAsync();
        var started = DateTimeOffset.UtcNow;
        try
        {
            job.SetProgress(0, "Re-encoding");
            using var cts = new CancellationTokenSource(ReencodeTimeout);
            var result = await _reencoder.ProcessAsync(original, job.Reencode, (p, m) => job.SetProgress(p, m), cts.Token);
            var key = result.Outcome switch
            {
                ReencodeOutcome.Skipped => "skipped",
                ReencodeOutcome.Remuxed => "remuxed",
                ReencodeOutcome.AudioConverted => "audio",
                _ => "reencoded",
            };
            job.SetReencodeResult(key, result.Notice);
            lock (_statsLock)
            {
                if (result.Outcome != ReencodeOutcome.Skipped)
                {
                    _reencoded++;
                    _reencodeSeconds += (DateTimeOffset.UtcNow - started).TotalSeconds;
                }
            }
            return result.Path;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ReencodeException or IOException)
        {
            var timedOut = ex is OperationCanceledException;
            _logger.LogWarning("Re-encode failed for job {Id}: {Reason}", job.Id, timedOut ? "timeout" : ex.Message);
            job.SetReencodeResult("failed", timedOut
                ? "Re-encoding took too long, so here is the original file (it may not play on iPhone)."
                : "Re-encoding failed, so here is the original file (it may not play on iPhone).");
            lock (_statsLock) { _reencodeFailed++; _reencodeSeconds += (DateTimeOffset.UtcNow - started).TotalSeconds; }
            // The reencoder removes its temp file; the original is still where the downloader left it.
            return File.Exists(original) ? original : FindOutput(job.Directory) ?? throw new DownloadException("The file disappeared.");
        }
        finally
        {
            _encodeSlot.Release();
        }
    }

    private static string? FindOutput(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault() : null;

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

public sealed record JobRecord(string Id, string Host, string State, DateTimeOffset StartedAt, double? DurationSeconds, long? FileSize, string? Error, string Reencode = "none");

public sealed record DownloadStats(int Total, int Succeeded, int Failed, int Active, long BytesServed, IReadOnlyList<JobRecord> Recent,
    int Reencoded = 0, int ReencodeFailed = 0, double ReencodeSeconds = 0);
