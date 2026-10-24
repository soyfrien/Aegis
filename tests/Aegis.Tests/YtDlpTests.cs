using Aegis.Web.Services;

namespace Aegis.Tests;

public class YtDlpTests
{
    [Theory]
    [InlineData("[download]  42.3% of 10.00MiB at 1.00MiB/s ETA 00:05", 42.3)]
    [InlineData("[download] 100% of 10.00MiB in 00:09", 100)]
    [InlineData("[download]   0.0% of ~ 5.00MiB", 0)]
    public void Parses_progress_lines(string line, double expected)
    {
        Assert.True(YtDlpDownloader.TryParseProgress(line, out var pct));
        Assert.Equal(expected, pct, 3);
    }

    [Theory]
    [InlineData("[youtube] abc: Downloading webpage")]
    [InlineData("")]
    [InlineData(null)]
    public void Ignores_other_lines(string? line)
    {
        Assert.False(YtDlpDownloader.TryParseProgress(line, out _));
    }

    [Fact]
    public void Url_is_passed_after_double_dash_so_it_cannot_be_an_option()
    {
        var args = YtDlpDownloader.BuildArguments(new AegisOptions(), "https://example.com/v", "/tmp/x");
        Assert.Equal("--", args[^2]);
        Assert.Equal("https://example.com/v", args[^1]);
        Assert.Contains("--no-playlist", args);
        Assert.Contains("--ignore-config", args);
    }

    [Fact]
    public void Ffmpeg_location_is_only_added_when_configured()
    {
        Assert.DoesNotContain("--ffmpeg-location", YtDlpDownloader.BuildArguments(new AegisOptions(), "https://e.com", "/t"));
        var args = YtDlpDownloader.BuildArguments(new AegisOptions { FfmpegPath = "/opt/ff" }, "https://e.com", "/t");
        Assert.Equal("/opt/ff", args[args.IndexOf("--ffmpeg-location") + 1]);
    }

    [Fact]
    public void Cookies_file_is_only_added_when_it_exists()
    {
        Assert.DoesNotContain("--cookies", YtDlpDownloader.BuildArguments(new AegisOptions { CookiesFile = "/nonexistent/cookies.txt" }, "https://e.com", "/t"));
        var tmp = Path.GetTempFileName();
        try
        {
            var args = YtDlpDownloader.BuildArguments(new AegisOptions { CookiesFile = tmp }, "https://e.com", "/t");
            Assert.Equal(tmp, args[args.IndexOf("--cookies") + 1]);
            Assert.Equal("--", args[^2]);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Summarize_explains_bot_checks_and_truncates()
    {
        Assert.Contains("bot", YtDlpDownloader.Summarize("ERROR: [youtube] x: Sign in to confirm you're not a bot"));
        Assert.Equal("Download failed.", YtDlpDownloader.Summarize(""));
        Assert.True(YtDlpDownloader.Summarize(new string('x', 1000)).Length <= 301);
    }
}
