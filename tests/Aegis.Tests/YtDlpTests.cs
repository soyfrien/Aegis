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
    public void Cookies_argument_is_only_added_when_a_per_site_cookie_file_is_given()
    {
        // The legacy Aegis:CookiesFile setting is never passed straight through any more: the store filters it per site first.
        Assert.DoesNotContain("--cookies", YtDlpDownloader.BuildArguments(new AegisOptions { CookiesFile = "/some/cookies.txt" }, "https://e.com", "/t"));
        var args = YtDlpDownloader.BuildArguments(new AegisOptions(), "https://e.com", "/t", "/work/.cookies/job.jar");
        Assert.Equal("/work/.cookies/job.jar", args[args.IndexOf("--cookies") + 1]);
        Assert.Equal("--", args[^2]);
        Assert.Equal("https://e.com", args[^1]);
    }

    [Fact]
    public void Summarize_explains_bot_checks_and_truncates()
    {
        Assert.Contains("bot", YtDlpDownloader.Summarize("ERROR: [youtube] x: Sign in to confirm you're not a bot"));
        Assert.Equal("Download failed.", YtDlpDownloader.Summarize(""));
        Assert.True(YtDlpDownloader.Summarize(new string('x', 1000)).Length <= 301);
    }

    [Theory]
    [InlineData("ERROR: [instagram:story] 4015100916210158930: This content is unreachable. Use --cookies-from-browser or --cookies for the authentication.", "login_required")]
    [InlineData("ERROR: [Instagram] abc: Requested content is not available, rate-limit reached or login required. Use --cookies", "login_required")]
    [InlineData("ERROR: [Instagram] abc: Instagram sent an empty media response. Check if this post is accessible in your browser without being logged-in.", "login_required")]
    [InlineData("ERROR: [facebook] 1: You must log in to continue. Use --cookies-from-browser", "login_required")]
    [InlineData("ERROR: [twitter] 1: NSFW tweet requires authentication", "login_required")]
    [InlineData("ERROR: [youtube] x: Sign in to confirm you're not a bot", "login_required")]
    [InlineData("ERROR: [twitter] 1: HTTP Error 429: Too Many Requests", "rate_limited")]
    [InlineData("ERROR: Unsupported URL: https://example.com/", "unsupported")]
    [InlineData("ERROR: [generic] something else broke", null)]
    [InlineData("", null)]
    public void Classify_recognises_login_rate_limit_and_unsupported(string stderr, string? expected)
    {
        Assert.Equal(expected, YtDlpDownloader.Classify(stderr));
    }

    [Fact]
    public void Login_messages_point_to_settings_and_mention_whether_cookies_were_used()
    {
        const string err = "ERROR: [instagram:story] 1: This content is unreachable. Use --cookies-from-browser or --cookies for the authentication.";
        var without = YtDlpDownloader.Summarize(err, "www.instagram.com", cookiesUsed: false);
        Assert.Contains("Instagram", without);
        Assert.Contains("Settings", without);
        var with = YtDlpDownloader.Summarize(err, "www.instagram.com", cookiesUsed: true);
        Assert.Contains("saved cookies", with);
        Assert.NotEqual(without, with);
    }
}
