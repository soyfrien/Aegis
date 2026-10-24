using Aegis.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aegis.Tests;

public class JobServiceTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "aegis-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_work)) Directory.Delete(_work, true);
    }

    private JobService Create(IDownloader downloader, int ttlMinutes = 15, IReencoder? reencoder = null) =>
        new(downloader, reencoder ?? new NoReencoder(), Options.Create(new AegisOptions { WorkDir = _work, FileTtlMinutes = ttlMinutes }), NullLogger<JobService>.Instance);

    private sealed class NoReencoder : IReencoder
    {
        public Task<ReencodeResult> ProcessAsync(string inputPath, ReencodeMode mode, Action<double, string?> progress, CancellationToken ct) =>
            throw new InvalidOperationException("The re-encoder must not run when the mode is Off.");
    }

    private static async Task<Job> WaitFinished(Job job)
    {
        for (var i = 0; i < 200 && job.State is JobState.Queued or JobState.Running; i++) await Task.Delay(25);
        return job;
    }

    [Fact]
    public async Task Successful_download_produces_a_done_job_with_file()
    {
        var svc = Create(new FakeDownloader((dir, p) =>
        {
            p(50, "Downloading");
            var f = Path.Combine(dir, "clip.mp4");
            File.WriteAllText(f, "data");
            return f;
        }));

        var job = svc.Start("https://example.com/v")!;
        await WaitFinished(job);

        Assert.Equal(JobState.Done, job.State);
        Assert.Equal("clip.mp4", job.FileName);
        Assert.Equal(4, job.FileSize);
        Assert.Equal(100, job.Percent);
    }

    [Fact]
    public async Task Failed_download_reports_error_and_cleans_directory()
    {
        var svc = Create(new FakeDownloader((dir, _) =>
        {
            File.WriteAllText(Path.Combine(dir, "partial.bin"), "x");
            throw new DownloadException("boom");
        }));

        var job = svc.Start("https://example.com/v")!;
        await WaitFinished(job);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("boom", job.Error);
        Assert.False(Directory.Exists(job.Directory));
    }

    [Fact]
    public async Task Sweep_removes_expired_jobs_and_their_files()
    {
        var svc = Create(new FakeDownloader((dir, _) =>
        {
            var f = Path.Combine(dir, "a.mp4");
            File.WriteAllText(f, "x");
            return f;
        }), ttlMinutes: 10);

        var job = svc.Start("https://example.com/v")!;
        await WaitFinished(job);

        Assert.Equal(0, svc.Sweep(DateTimeOffset.UtcNow));
        Assert.True(File.Exists(job.FilePath));

        Assert.Equal(1, svc.Sweep(DateTimeOffset.UtcNow.AddMinutes(11)));
        Assert.False(Directory.Exists(job.Directory));
        Assert.Null(svc.Get(job.Id));
    }

    [Fact]
    public async Task Remove_deletes_files_immediately()
    {
        var svc = Create(new FakeDownloader((dir, _) =>
        {
            var f = Path.Combine(dir, "a.mp4");
            File.WriteAllText(f, "x");
            return f;
        }));
        var job = svc.Start("https://example.com/v")!;
        await WaitFinished(job);

        Assert.True(svc.Remove(job.Id));
        Assert.False(Directory.Exists(job.Directory));
        Assert.False(svc.Remove(job.Id));
    }

    private sealed class FakeDownloader(Func<string, Action<double, string?>, string> run) : IDownloader
    {
        public Task<string> DownloadAsync(string url, string directory, Action<double, string?> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(directory);
            return Task.FromResult(run(directory, progress));
        }
    }
}
