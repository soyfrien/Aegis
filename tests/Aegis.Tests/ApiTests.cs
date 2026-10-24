using System.Net;
using System.Net.Http.Json;
using Aegis.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aegis.Tests;

public class ApiTests : IClassFixture<ApiTests.Factory>
{
    private const string Passcode = "test-passcode-123";
    private readonly Factory _factory;

    public ApiTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "aegis-api-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("AEGIS_PASSCODE", Passcode);
            builder.UseSetting("Aegis:WorkDir", Path.Combine(_dir, "work"));
            builder.UseSetting("Aegis:StateDir", Path.Combine(_dir, "state"));
            builder.UseSetting("Aegis:LoginAttemptsPerMinute", "1000");
            builder.ConfigureServices(s =>
            {
                s.Replace(ServiceDescriptor.Singleton<IDownloader, StubDownloader>());
                s.Replace(ServiceDescriptor.Singleton<IReencoder, StubReencoder>());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(_dir, true); } catch { /* ignore */ }
        }
    }

    private sealed class StubDownloader : IDownloader
    {
        public Task<string> DownloadAsync(string url, string directory, Action<double, string?> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(directory);
            var f = Path.Combine(directory, "Test Clip [abc].mp4");
            File.WriteAllText(f, "not really a video");
            return Task.FromResult(f);
        }
    }

    private sealed class StubReencoder : IReencoder
    {
        public Task<ReencodeResult> ProcessAsync(string inputPath, ReencodeMode mode, Action<double, string?> progress, CancellationToken ct) =>
            Task.FromResult(new ReencodeResult(inputPath, ReencodeOutcome.Skipped, "Already iPhone-compatible; no re-encoding needed."));
    }

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    private static async Task Login(HttpClient c)
    {
        var r = await c.PostAsJsonAsync("/api/login", new { passcode = Passcode });
        r.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Protected_endpoints_require_login()
    {
        var c = NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/jobs", new { url = "https://example.com/v" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/jobs/abc")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/jobs/abc/file")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/onedrive/connect", null)).StatusCode);
    }

    [Fact]
    public async Task Wrong_passcode_is_rejected()
    {
        var r = await NewClient().PostAsJsonAsync("/api/login", new { passcode = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Login_download_and_attachment_flow_works()
    {
        var c = NewClient();
        await Login(c);

        var created = await c.PostAsJsonAsync("/api/jobs", new { url = "https://example.com/v" });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var job = await created.Content.ReadFromJsonAsync<JobView>();
        Assert.NotNull(job);

        JobView? view = null;
        for (var i = 0; i < 100; i++)
        {
            view = await c.GetFromJsonAsync<JobView>($"/api/jobs/{job!.Id}");
            if (view!.State == "done") break;
            await Task.Delay(50);
        }
        Assert.Equal("done", view!.State);

        var file = await c.GetAsync($"/api/jobs/{job!.Id}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("attachment", file.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("Test Clip [abc].mp4", file.Content.Headers.ContentDisposition?.FileNameStar ?? file.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("video/mp4", file.Content.Headers.ContentType?.MediaType);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/jobs/{job.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/jobs/{job.Id}/file")).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://example.com/x")]
    [InlineData("http://127.0.0.1/")]
    public async Task Invalid_urls_are_rejected(string url)
    {
        var c = NewClient();
        await Login(c);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/jobs", new { url })).StatusCode);
    }

    [Fact]
    public async Task OneDrive_reports_not_configured()
    {
        var c = NewClient();
        await Login(c);
        var r = await c.PostAsync("/api/onedrive/connect", null);
        Assert.Equal(HttpStatusCode.NotImplemented, r.StatusCode);
        Assert.Contains("not set up", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reencode_option_is_accepted_validated_and_echoed()
    {
        var c = NewClient();
        await Login(c);
        foreach (var mode in new[] { "off", "auto", "always" })
        {
            var r = await c.PostAsJsonAsync("/api/jobs", new { url = "https://example.com/v", reencode = mode });
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
            Assert.Equal(mode, (await r.Content.ReadFromJsonAsync<JobView>())!.Reencode);
        }
        var none = await c.PostAsJsonAsync("/api/jobs", new { url = "https://example.com/v" });
        Assert.Equal("off", (await none.Content.ReadFromJsonAsync<JobView>())!.Reencode);

        var bad = await c.PostAsJsonAsync("/api/jobs", new { url = "https://example.com/v", reencode = "x265" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("off, auto, always", await bad.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/fetch", new { url = "https://example.com/v", reencode = "x265" })).StatusCode);
    }

    [Fact]
    public async Task Fetch_accepts_reencode_and_returns_the_file()
    {
        var c = NewClient();
        await Login(c);
        var r = await c.PostAsJsonAsync("/api/fetch", new { url = "https://example.com/v", reencode = "auto" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("attachment", r.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task Index_page_offers_the_reencode_select_and_it_is_remembered()
    {
        var c = NewClient();
        var html = await c.GetStringAsync("/");
        Assert.Contains("id=\"reencode\"", html);
        Assert.Contains("slow on this small server", html);
        var js = await c.GetStringAsync("/app.js");
        Assert.Contains("localStorage", js);
        Assert.Contains("reencode: reencodeBox.value", js);
    }

    [Fact]
    public async Task Index_page_is_served_with_security_headers()
    {
        var r = await NewClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(r.Headers.Contains("Content-Security-Policy"));
        Assert.Contains("Aegis", await r.Content.ReadAsStringAsync());
    }
}
