using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aegis.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aegis.Tests;

public class ShareAndPwaTests : IClassFixture<ShareAndPwaTests.Factory>
{
    private const string Passcode = "test-passcode-123";
    private const string Token = "test-api-token-0123456789-abcdefghij";
    private readonly Factory _factory;

    public ShareAndPwaTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "aegis-share-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("AEGIS_PASSCODE", Passcode);
            builder.UseSetting("AEGIS_API_TOKEN", Token);
            builder.UseSetting("Aegis:WorkDir", Path.Combine(_dir, "work"));
            builder.UseSetting("Aegis:StateDir", Path.Combine(_dir, "state"));
            builder.UseSetting("Aegis:LoginAttemptsPerMinute", "1000");
            builder.UseSetting("Aegis:RequestsPerMinute", "1000");
            builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IDownloader, StubDownloader>()));
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
            if (url.Contains("fail")) throw new DownloadException("Nope, that failed.");
            Directory.CreateDirectory(directory);
            var f = Path.Combine(directory, "Shared Clip.mp4");
            File.WriteAllText(f, "not really a video");
            return Task.FromResult(f);
        }
    }

    private HttpClient NoRedirectClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    // ---------- PWA ----------

    [Fact]
    public async Task Manifest_is_served_with_icons_and_standalone_display()
    {
        var c = NoRedirectClient();
        var r = await c.GetAsync("/manifest.webmanifest");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/manifest+json", r.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("standalone", root.GetProperty("display").GetString());
        Assert.Equal("Aegis", root.GetProperty("name").GetString());
        Assert.Equal("/", root.GetProperty("start_url").GetString());
        foreach (var icon in root.GetProperty("icons").EnumerateArray())
        {
            var src = icon.GetProperty("src").GetString()!;
            var png = await c.GetAsync(src);
            Assert.Equal(HttpStatusCode.OK, png.StatusCode);
            Assert.Equal("image/png", png.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Index_links_manifest_and_apple_touch_icon_and_service_worker_is_served()
    {
        var c = NoRedirectClient();
        var html = await (await c.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("rel=\"manifest\"", html);
        Assert.Contains("rel=\"apple-touch-icon\"", html);
        Assert.Contains("apple-mobile-web-app-capable", html);

        var apple = await c.GetAsync("/icons/apple-touch-icon.png");
        Assert.Equal(HttpStatusCode.OK, apple.StatusCode);

        var sw = await c.GetAsync("/sw.js");
        Assert.Equal(HttpStatusCode.OK, sw.StatusCode);
        Assert.Contains("javascript", sw.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-cache", sw.Headers.CacheControl?.ToString());
    }

    // ---------- clear button ----------

    [Fact]
    public async Task Index_has_accessible_hidden_clear_button_inside_the_url_field()
    {
        var html = await (await NoRedirectClient().GetAsync("/")).Content.ReadAsStringAsync();
        var i = html.IndexOf("id=\"clear-url\"", StringComparison.Ordinal);
        Assert.True(i > 0, "clear button missing");
        var tag = html.Substring(html.LastIndexOf('<', i), html.IndexOf('>', i) - html.LastIndexOf('<', i));
        Assert.Contains("aria-label=\"Clear URL\"", tag);
        Assert.Contains("type=\"button\"", tag); // must not submit the download form
        Assert.Contains("hidden", tag);            // hidden while the field is empty
        Assert.True(html.IndexOf("id=\"url\"", StringComparison.Ordinal) < i); // sits right after the input
    }

    [Fact]
    public async Task Clear_script_empties_refocuses_and_drops_the_share_prefill_state()
    {
        var c = NoRedirectClient();
        var js = await (await c.GetAsync("/app.js")).Content.ReadAsStringAsync();
        Assert.Contains("clear-url", js);
        Assert.Contains("urlBox.value = ''", js);
        Assert.Contains("pending = null", js);
        Assert.Contains("history.replaceState", js);
        Assert.Contains("urlBox.focus()", js);
        var css = await (await c.GetAsync("/app.css")).Content.ReadAsStringAsync();
        Assert.Contains("button.clear", css);
    }

    [Fact]
    public async Task Service_worker_cache_version_was_bumped_so_installed_app_refreshes()
    {
        var sw = await (await NoRedirectClient().GetAsync("/sw.js")).Content.ReadAsStringAsync();
        Assert.Contains("aegis-shell-v10", sw);
        Assert.DoesNotContain("aegis-shell-v3", sw);
    }

    [Fact]
    public async Task External_links_open_in_a_new_tab_with_noopener_noreferrer_and_internal_links_do_not()
    {
        var c = NoRedirectClient();
        var js = await (await c.GetAsync("/app.js")).Content.ReadAsStringAsync();
        Assert.Contains("a.target = '_blank'", js);
        Assert.Contains("'noopener noreferrer'", js);
        Assert.Contains("externalLink(d.verificationUri", js);   // device-code sign-in link
        Assert.Contains("externalLink(job.oneDriveUrl", js);      // saved-file link
        Assert.DoesNotContain("a.rel = 'noopener';", js);          // old weaker attribute is gone
        Assert.Contains("origin !== location.origin", js);        // only cross-origin links are touched

        foreach (var page in new[] { "/", "/stats.html", "/shortcut.html", "/settings.html" })
        {
            var html = await (await c.GetAsync(page)).Content.ReadAsStringAsync();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, "<a\\b[^>]*>"))
            {
                // Static anchors in the shipped pages are all same-site: they must NOT open new windows.
                Assert.DoesNotContain("target=", m.Value);
            }
        }
    }

    // ---------- /share ----------

    [Theory]
    [InlineData("/share?url=https%3A%2F%2Fyoutu.be%2Fabc", "/?target=https%3A%2F%2Fyoutu.be%2Fabc&auto=1")]
    [InlineData("/share?text=Look%20at%20this%20https%3A%2F%2Fyoutu.be%2Fabc%20wow", "/?target=https%3A%2F%2Fyoutu.be%2Fabc&auto=1")]
    [InlineData("/share?title=Cool&text=no%20link&url=https%3A%2F%2Fexample.com%2Fv.mp4", "/?target=https%3A%2F%2Fexample.com%2Fv.mp4&auto=1")]
    [InlineData("/?url=https%3A%2F%2Fyoutu.be%2Fabc", "/?target=https%3A%2F%2Fyoutu.be%2Fabc&auto=1")]
    [InlineData("/?text=https%3A%2F%2Fyoutu.be%2Fabc", "/?target=https%3A%2F%2Fyoutu.be%2Fabc&auto=1")]
    [InlineData("/share?url=https%3A%2F%2Fyoutu.be%2Fabc&auto=0", "/?target=https%3A%2F%2Fyoutu.be%2Fabc")]
    [InlineData("/share?text=nothing%20useful", "/?shared=none")]
    [InlineData("/share", "/?shared=none")]
    public async Task Share_redirects_to_page_with_extracted_target(string path, string expectedLocation)
    {
        var r = await NoRedirectClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(expectedLocation, r.Headers.Location?.OriginalString);
        Assert.Contains("no-store", r.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Share_does_not_require_login_but_the_download_still_does()
    {
        var c = NoRedirectClient();
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/share?url=https%3A%2F%2Fyoutu.be%2Fabc")).StatusCode);
        // landing page itself is static and loads without a session...
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/?target=https%3A%2F%2Fyoutu.be%2Fabc&auto=1")).StatusCode);
        // ...but nothing is downloaded without auth.
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/jobs", new { url = "https://youtu.be/abc" })).StatusCode);
    }

    // ---------- /shortcut ----------

    [Fact]
    public async Task Shortcut_page_requires_login_and_returns_after_login()
    {
        var anon = await NoRedirectClient().GetAsync("/shortcut");
        Assert.Equal(HttpStatusCode.Redirect, anon.StatusCode);
        Assert.Equal("/?next=%2Fshortcut", anon.Headers.Location?.OriginalString);

        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        (await c.PostAsJsonAsync("/api/login", new { passcode = Passcode })).EnsureSuccessStatusCode();
        var page = await c.GetAsync("/shortcut");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Show in Share Sheet", await page.Content.ReadAsStringAsync());
    }

    // ---------- API token ----------

    private HttpClient TokenClient(string? token = Token)
    {
        var c = NoRedirectClient();
        if (token is not null) c.DefaultRequestHeaders.Add("X-Aegis-Token", token);
        return c;
    }

    [Fact]
    public async Task Token_can_create_poll_and_fetch_a_job()
    {
        var c = TokenClient();
        var created = await c.PostAsJsonAsync("/api/jobs", new { url = "Check this out https://example.com/v wow" });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var job = await created.Content.ReadFromJsonAsync<JobView>();

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
    }

    [Fact]
    public async Task Fetch_waits_and_returns_the_file_in_one_call()
    {
        var r = await TokenClient().PostAsJsonAsync("/api/fetch", new { url = "https://example.com/v" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("attachment", r.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("not really a video", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Fetch_reports_failures_and_bad_input()
    {
        var c = TokenClient();
        var failed = await c.PostAsJsonAsync("/api/fetch", new { url = "https://example.com/fail" });
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Contains("Nope", await failed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/fetch", new { url = "no link" })).StatusCode);
    }

    [Theory]
    [InlineData("wrong-token-wrong-token-wrong-token")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Wrong_or_missing_token_is_rejected(string? token)
    {
        var c = TokenClient(token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/jobs", new { url = "https://example.com/v" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/fetch", new { url = "https://example.com/v" })).StatusCode);
    }

    [Fact]
    public async Task Token_does_not_open_management_endpoints()
    {
        var c = TokenClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/onedrive/connect", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/jobs/abc/onedrive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/shortcut")).StatusCode);
    }

    [Fact]
    public async Task Token_is_ignored_when_not_configured_or_too_short()
    {
        var f = _factory.WithWebHostBuilder(b => b.UseSetting("AEGIS_API_TOKEN", "short"));
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-Aegis-Token", "short");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/jobs", new { url = "https://example.com/v" })).StatusCode);
    }
}
