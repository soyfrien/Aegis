using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Aegis.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aegis.Tests;

/// <summary>The URL handed to the downloader must be the normalized one, whichever endpoint it came in through.</summary>
public class UrlNormalizeApiTests : IClassFixture<UrlNormalizeApiTests.Factory>
{
    private const string Passcode = "test-passcode-1234";
    private const string Token = "test-api-token-abcdefghijklmnop";
    private readonly Factory _factory;

    public UrlNormalizeApiTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "aegis-norm-" + Guid.NewGuid().ToString("N"));
        public ConcurrentQueue<string> Seen { get; } = new();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("AEGIS_PASSCODE", Passcode);
            builder.UseSetting("AEGIS_API_TOKEN", Token);
            builder.UseSetting("Aegis:WorkDir", Path.Combine(_dir, "work"));
            builder.UseSetting("Aegis:StateDir", Path.Combine(_dir, "state"));
            builder.UseSetting("Aegis:LoginAttemptsPerMinute", "1000");
            builder.ConfigureServices(s =>
            {
                s.Replace(ServiceDescriptor.Singleton<IDownloader>(new Recorder(Seen)));
                s.Replace(ServiceDescriptor.Singleton<IReencoder, StubReencoder>());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(_dir, true); } catch { /* ignore */ }
        }
    }

    private sealed class Recorder(ConcurrentQueue<string> seen) : IDownloader
    {
        public Task<string> DownloadAsync(string url, string directory, Action<double, string?> progress, CancellationToken ct)
        {
            seen.Enqueue(url);
            Directory.CreateDirectory(directory);
            var f = Path.Combine(directory, "clip.mp4");
            File.WriteAllText(f, "x");
            return Task.FromResult(f);
        }
    }

    private sealed class StubReencoder : IReencoder
    {
        public Task<ReencodeResult> ProcessAsync(string inputPath, ReencodeMode mode, Action<double, string?> progress, CancellationToken ct) =>
            Task.FromResult(new ReencodeResult(inputPath, ReencodeOutcome.Skipped, "skip"));
    }

    private async Task<HttpClient> LoggedIn()
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await c.PostAsJsonAsync("/api/login", new { passcode = Passcode })).EnsureSuccessStatusCode();
        return c;
    }

    private async Task<string> LastSeenAfter(Func<Task> act)
    {
        while (_factory.Seen.TryDequeue(out _)) { }
        await act();
        for (var i = 0; i < 100 && _factory.Seen.IsEmpty; i++) await Task.Delay(50);
        Assert.True(_factory.Seen.TryDequeue(out var url), "downloader was not called");
        return url!;
    }

    [Fact]
    public async Task Jobs_endpoint_hands_the_downloader_a_clean_url()
    {
        var c = await LoggedIn();
        var url = await LastSeenAfter(async () =>
            Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync("/api/jobs", new { url = "https://www.instagram.com/reel/abc/?igsh=xyz#f" })).StatusCode));
        Assert.Equal("https://www.instagram.com/reel/abc/", url);
    }

    [Fact]
    public async Task Jobs_endpoint_keeps_the_youtube_video_id_only()
    {
        var c = await LoggedIn();
        var url = await LastSeenAfter(async () =>
            Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync("/api/jobs", new { url = "https://www.youtube.com/watch?v=jNQXAC9IVRw&list=PL1&si=a&t=3" })).StatusCode));
        Assert.Equal("https://www.youtube.com/watch?v=jNQXAC9IVRw", url);
    }

    [Fact]
    public async Task Jobs_endpoint_accepts_a_url_without_scheme_and_text_around_it()
    {
        var c = await LoggedIn();
        var url = await LastSeenAfter(async () =>
            Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync("/api/jobs", new { url = "x.com/u/status/1?s=20" })).StatusCode));
        Assert.Equal("https://x.com/u/status/1", url);

        url = await LastSeenAfter(async () =>
            Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync("/api/jobs", new { url = "Look https://youtu.be/abc?si=zz wow" })).StatusCode));
        Assert.Equal("https://youtu.be/abc", url);
    }

    [Fact]
    public async Task Fetch_endpoint_also_normalizes_and_works_with_the_api_token()
    {
        var c = _factory.CreateClient();
        var url = await LastSeenAfter(async () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/fetch") { Content = JsonContent.Create(new { url = "https://cdn.example.com/a.mp4?utm_source=x&token=keep" }) };
            req.Headers.Add("X-Aegis-Token", Token);
            Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(req)).StatusCode);
        });
        Assert.Equal("https://cdn.example.com/a.mp4", url);
    }

    [Fact]
    public async Task Normalize_endpoint_returns_the_cleaned_url_and_requires_login()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/normalize", new { url = "https://x.com/a?s=1" })).StatusCode);

        var c = await LoggedIn();
        var r = await c.PostAsJsonAsync("/api/normalize", new { url = "https://www.facebook.com/watch/?v=123&ref=x#y" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("\"url\":\"https://www.facebook.com/watch/?v=123\"", await r.Content.ReadAsStringAsync());

        var junk = await c.PostAsJsonAsync("/api/normalize", new { url = "hello world?x=1" });
        Assert.Contains("hello world?x=1", await junk.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Normalize_endpoint_is_not_available_to_the_api_token_or_via_get()
    {
        var c = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/normalize") { Content = JsonContent.Create(new { url = "https://x.com/a?s=1" }) };
        req.Headers.Add("X-Aegis-Token", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(req)).StatusCode);
    }

    [Theory]
    [InlineData("/share?url=https%3A%2F%2Fx.com%2Fu%2Fstatus%2F1%3Fs%3D20", "/?target=https%3A%2F%2Fx.com%2Fu%2Fstatus%2F1&auto=1")]
    [InlineData("/share?text=see%20https%3A%2F%2Fyoutu.be%2Fabc%3Fsi%3Dzz%20ok", "/?target=https%3A%2F%2Fyoutu.be%2Fabc&auto=1")]
    [InlineData("/share?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3Dabc%26list%3DPL1", "/?target=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3Dabc&auto=1")]
    public async Task Share_redirect_carries_the_cleaned_link(string path, string expected)
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var r = await c.GetAsync(path);
        Assert.Equal(expected, r.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Page_script_cleans_the_field_on_paste_change_and_submit()
    {
        var js = await _factory.CreateClient().GetStringAsync("/app.js");
        Assert.Contains("/normalize", js);
        Assert.Contains("'paste'", js);
        Assert.Contains("'change'", js);
        Assert.Contains("await cleanUrlField()", js);
    }
}
