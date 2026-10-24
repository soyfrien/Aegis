using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aegis.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aegis.Tests;

/// <summary>Cookie storage/validation. All cookie values here are obviously fake test strings.</summary>
public class CookieStoreTests : IDisposable
{
    private const string Fake = "FAKE-TEST-VALUE-do-not-use";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aegis-cookies-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* ignore */ } }

    private CookieStore NewStore(string? legacyFile = null) =>
        new(Options.Create(new AegisOptions { StateDir = _dir, CookiesFile = legacyFile ?? "" }), NullLogger<CookieStore>.Instance);

    private static string Line(string domain, string name = "sessionid", string value = Fake, string expires = "1893456000", string sub = "TRUE", string secure = "TRUE") =>
        $"{domain}\t{sub}\t/\t{secure}\t{expires}\t{name}\t{value}";

    private static string File1(params string[] lines) => "# Netscape HTTP Cookie File\n\n" + string.Join("\n", lines) + "\n";

    // ---------- parsing / validation ----------

    [Fact]
    public void Parses_a_normal_cookies_txt_including_httponly_prefix_and_crlf()
    {
        var text = "# Netscape HTTP Cookie File\r\n# comment\r\n\r\n#HttpOnly_" + Line(".instagram.com") + "\r\n" + Line(".x.com", "auth_token") + "\r\n";
        var c = CookieStore.Parse(text);
        Assert.Equal(2, c.Count);
        Assert.True(c[0].HttpOnly);
        Assert.Equal(".instagram.com", c[0].Domain);
        Assert.Equal("auth_token", c[1].Name);
        Assert.Equal(1893456000, c[1].Expires);
    }

    [Fact]
    public void Accepts_space_separated_lines_and_empty_values()
    {
        var c = CookieStore.Parse(".facebook.com TRUE / TRUE 1893456000 c_user " + Fake + "\n.facebook.com\tTRUE\t/\tTRUE\t0\tempty\t");
        Assert.Equal(2, c.Count);
        Assert.Equal(Fake, c[0].Value);
        Assert.Equal("", c[1].Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n")]
    [InlineData("# Netscape HTTP Cookie File\n# only comments\n")]
    [InlineData("this is not a cookie file at all")]
    [InlineData("example.com\tMAYBE\t/\tTRUE\t1\tn\tv")]            // bad boolean
    [InlineData("example.com\tTRUE\t/\tTRUE\tsoon\tn\tv")]          // bad expiry
    [InlineData("example.com\tTRUE\tno-slash\tTRUE\t1\tn\tv")]      // bad path
    [InlineData("localhost\tTRUE\t/\tTRUE\t1\tn\tv")]               // no dot in domain
    [InlineData("exa mple.com\tTRUE\t/\tTRUE\t1\tn\tv")]            // not a domain (after tab split)
    [InlineData("example.com\tTRUE\t/\tTRUE\t1\tbad name\tv")]      // bad cookie name
    [InlineData("example.com\tTRUE\t/\tTRUE\t1\tn\tv\u0001")]       // control char
    public void Rejects_invalid_input_without_echoing_it(string text)
    {
        var ex = Assert.Throws<CookieFormatException>(() => CookieStore.Parse(text));
        Assert.DoesNotContain(Fake, ex.Message);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Rejects_oversized_input_and_too_many_cookies()
    {
        Assert.Throws<CookieFormatException>(() => CookieStore.Parse(new string('x', CookieStore.MaxBytes + 1)));
        var many = string.Join("\n", Enumerable.Range(0, CookieStore.MaxCookies + 1).Select(i => Line(".example.com", "n" + i, "v")));
        Assert.Throws<CookieFormatException>(() => CookieStore.Parse(many));
    }

    [Fact]
    public void Error_messages_never_contain_the_cookie_value()
    {
        var ex = Assert.Throws<CookieFormatException>(() => CookieStore.Parse("example.com\tTRUE\t/\tTRUE\tNOPE\tname\t" + Fake));
        Assert.DoesNotContain(Fake, ex.Message);
    }

    // ---------- domain matching ----------

    [Theory]
    [InlineData("www.instagram.com", ".instagram.com", true)]
    [InlineData("instagram.com", "instagram.com", true)]
    [InlineData("evilinstagram.com", ".instagram.com", false)]
    [InlineData("instagram.com.evil.example", ".instagram.com", false)]
    [InlineData("www.youtube.com", ".instagram.com", false)]
    [InlineData("youtu.be", ".youtube.com", true)]            // same login family
    [InlineData("fb.watch", ".facebook.com", true)]
    [InlineData("t.co", ".twitter.com", true)]
    [InlineData("x.com", ".twitter.com", true)]
    [InlineData("twitter.com", ".x.com", true)]
    [InlineData("example.org", ".youtube.com", false)]
    [InlineData("www.facebook.com", ".youtube.com", false)]    // YouTube cookies are never sent to other sites
    public void Cookies_only_apply_to_their_own_site(string host, string cookieDomain, bool expected)
    {
        Assert.Equal(expected, CookieStore.CookieAppliesToHost(cookieDomain, host));
    }

    [Fact]
    public void Stories_always_need_login()
    {
        Assert.True(CookieStore.AlwaysNeedsLogin(new Uri("https://www.instagram.com/stories/nasa/4015100916210158930/")));
        Assert.True(CookieStore.AlwaysNeedsLogin(new Uri("https://www.facebook.com/stories/123")));
        Assert.False(CookieStore.AlwaysNeedsLogin(new Uri("https://www.instagram.com/reel/DcBx_I1iHZW/")));
        Assert.False(CookieStore.AlwaysNeedsLogin(new Uri("https://x.com/NASA/stories/1")));
    }

    // ---------- storage ----------

    [Fact]
    public void Save_writes_a_private_normalised_file_and_info_has_no_values()
    {
        var store = NewStore();
        var result = store.Save(File1(Line(".instagram.com"), Line(".instagram.com", "csrftoken"), Line(".x.com", "auth_token")), replaceAll: false);

        Assert.Equal(3, result.Added);
        Assert.True(File.Exists(store.StorePath));
        Assert.StartsWith("# Netscape HTTP Cookie File", File.ReadAllText(store.StorePath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.StorePath));

        var info = store.GetInfo();
        Assert.True(info.Configured);
        Assert.Equal("uploaded", info.Source);
        Assert.Equal(3, info.TotalCookies);
        Assert.Equal(["Instagram", "X (Twitter)"], info.Sites.Select(s => s.Label).ToArray());
        Assert.NotNull(info.SavedAt);
        var json = JsonSerializer.Serialize(info);
        Assert.DoesNotContain(Fake, json);
        Assert.DoesNotContain("sessionid", json);
        Assert.DoesNotContain("auth_token", json);
        Assert.DoesNotContain(Directory.EnumerateFiles(_dir), f => f.EndsWith(".tmp"));
    }

    [Fact]
    public void Saving_one_site_replaces_only_that_site_unless_replace_all()
    {
        var store = NewStore();
        store.Save(File1(Line(".instagram.com"), Line(".x.com", "auth_token")), false);
        store.Save(File1(Line(".instagram.com", "newcookie")), false);
        var info = store.GetInfo();
        Assert.Equal(2, info.TotalCookies);                       // 1 new instagram + 1 old x
        Assert.Contains("newcookie", File.ReadAllText(store.StorePath));
        Assert.DoesNotContain("sessionid", File.ReadAllText(store.StorePath));

        store.Save(File1(Line(".facebook.com", "c_user")), replaceAll: true);
        Assert.Equal(["Facebook"], store.GetInfo().Sites.Select(s => s.Label).ToArray());
    }

    [Fact]
    public void Failed_save_leaves_the_existing_cookies_untouched()
    {
        var store = NewStore();
        store.Save(File1(Line(".instagram.com")), false);
        var before = File.ReadAllText(store.StorePath);
        Assert.Throws<CookieFormatException>(() => store.Save("garbage", false));
        Assert.Equal(before, File.ReadAllText(store.StorePath));
    }

    [Fact]
    public void Delete_removes_one_site_or_everything()
    {
        var store = NewStore();
        store.Save(File1(Line(".instagram.com"), Line(".x.com", "auth_token")), false);
        Assert.Equal(["X (Twitter)"], store.Delete("instagram").Sites.Select(s => s.Label).ToArray());
        var gone = store.Delete(null);
        Assert.False(gone.Configured);
        Assert.False(File.Exists(store.StorePath));
        Assert.Equal("none", gone.Source);
    }

    [Fact]
    public void Job_cookie_file_contains_only_the_target_sites_cookies_and_is_private()
    {
        var store = NewStore();
        store.Save(File1(Line(".youtube.com", "yt_secret", "YT-FAKE"), Line(".instagram.com", "sessionid", "IG-FAKE"), Line(".x.com", "auth_token", "X-FAKE")), false);

        var work = Path.Combine(_dir, "jobs");
        var ig = store.CreateJobCookieFile("www.instagram.com", work)!;
        var text = File.ReadAllText(ig);
        Assert.Contains("IG-FAKE", text);
        Assert.DoesNotContain("YT-FAKE", text);
        Assert.DoesNotContain("X-FAKE", text);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ig));

        Assert.Null(store.CreateJobCookieFile("vimeo.com", work));          // no cookies for this site: --cookies is not used
        Assert.False(store.HasCookiesFor("fb.watch"));
        Assert.True(store.HasCookiesFor("youtu.be"));
    }

    [Fact]
    public void Legacy_cookies_file_setting_still_works_until_cookies_are_uploaded()
    {
        var legacy = Path.Combine(_dir, "legacy.txt");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(legacy, File1(Line(".facebook.com", "c_user", "LEGACY-FAKE")));
        var store = NewStore(legacy);

        Assert.Equal("server-config", store.GetInfo().Source);
        Assert.Contains("LEGACY-FAKE", File.ReadAllText(store.CreateJobCookieFile("www.facebook.com", Path.Combine(_dir, "j"))!));
        Assert.Null(store.CreateJobCookieFile("x.com", Path.Combine(_dir, "j")));

        store.Save(File1(Line(".x.com", "auth_token")), false);   // uploading supersedes the file but keeps its other sites
        var info = store.GetInfo();
        Assert.Equal("uploaded", info.Source);
        Assert.Equal(2, info.TotalCookies);
    }
}

public class CookieApiTests : IClassFixture<CookieApiTests.Factory>
{
    private const string Passcode = "test-passcode-123";
    private const string Token = "test-api-token-0123456789-abcdefghij";
    private const string Fake = "API-FAKE-VALUE";
    private readonly Factory _factory;

    public CookieApiTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "aegis-cookie-api-" + Guid.NewGuid().ToString("N"));
        public string StateDir => Path.Combine(_dir, "state");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("AEGIS_PASSCODE", Passcode);
            builder.UseSetting("AEGIS_API_TOKEN", Token);
            builder.UseSetting("Aegis:WorkDir", Path.Combine(_dir, "work"));
            builder.UseSetting("Aegis:StateDir", StateDir);
            builder.UseSetting("Aegis:LoginAttemptsPerMinute", "1000");
            builder.UseSetting("Aegis:RequestsPerMinute", "1000");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(_dir, true); } catch { /* ignore */ }
        }
    }

    private HttpClient Anon(bool redirect = false) => _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = redirect });

    private async Task<HttpClient> LoggedIn()
    {
        var c = Anon();
        (await c.PostAsJsonAsync("/api/login", new { passcode = Passcode })).EnsureSuccessStatusCode();
        return c;
    }

    private static string Cookies(string domain) => $"# Netscape HTTP Cookie File\n{domain}\tTRUE\t/\tTRUE\t1893456000\tsessionid\t{Fake}\n";

    [Fact]
    public async Task Cookie_endpoints_return_401_when_logged_out_and_ignore_the_api_token()
    {
        var c = Anon();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/cookies")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PutAsJsonAsync("/api/cookies", new { content = Cookies(".x.com") })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.DeleteAsync("/api/cookies")).StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/cookies");
        req.Headers.Add("X-Aegis-Token", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anon().SendAsync(req)).StatusCode);   // the headless token cannot manage cookies
    }

    [Fact]
    public async Task Settings_page_requires_login_and_redirects_back()
    {
        var anon = await Anon().GetAsync("/settings");
        Assert.Equal(HttpStatusCode.Redirect, anon.StatusCode);
        Assert.Equal("/?next=%2Fsettings", anon.Headers.Location?.OriginalString);

        var page = await (await LoggedIn()).GetAsync("/settings");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Get cookies.txt LOCALLY", html);
        Assert.Contains("desktop", html);
    }

    [Fact]
    public async Task Save_list_and_delete_roundtrip_never_returns_cookie_contents()
    {
        var c = await LoggedIn();
        var put = await c.PutAsJsonAsync("/api/cookies", new { content = Cookies(".instagram.com") });
        var body = await put.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.DoesNotContain(Fake, body);
        Assert.DoesNotContain("sessionid", body);
        Assert.Contains("Instagram", body);
        Assert.Contains("no-store", put.Headers.CacheControl?.ToString());

        var get = await c.GetAsync("/api/cookies");
        var json = await get.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Fake, json);
        using (var doc = JsonDocument.Parse(json))
        {
            Assert.True(doc.RootElement.GetProperty("configured").GetBoolean());
            Assert.Equal("instagram", doc.RootElement.GetProperty("sites")[0].GetProperty("site").GetString());
            Assert.Contains("instagram.com", doc.RootElement.GetProperty("sites")[0].GetProperty("domains").EnumerateArray().Select(d => d.GetString()));
        }

        var file = Path.Combine(_factory.StateDir, "cookies.txt");
        Assert.True(File.Exists(file));
        // outside the web root: the static file server must not serve it
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/cookies.txt")).StatusCode);

        var del = await c.DeleteAsync("/api/cookies");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.False(File.Exists(file));
        Assert.False(JsonDocument.Parse(await (await c.GetAsync("/api/cookies")).Content.ReadAsStringAsync()).RootElement.GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Invalid_uploads_get_400_without_echoing_the_content()
    {
        var c = await LoggedIn();
        var r = await c.PutAsJsonAsync("/api/cookies", new { content = "example.com\tTRUE\t/\tTRUE\tNOPE\tn\t" + Fake });
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.DoesNotContain(Fake, body);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/cookies", new { content = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsync("/api/cookies", new StringContent("not json", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task Oversized_uploads_are_rejected()
    {
        var c = await LoggedIn();
        var r = await c.PutAsJsonAsync("/api/cookies", new { content = new string('a', 3 * 1024 * 1024) });
        Assert.True(r.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge, r.StatusCode.ToString());
    }

    [Fact]
    public async Task Failed_job_exposes_the_login_error_code_for_instagram_stories_without_calling_yt_dlp()
    {
        var f = _factory.WithWebHostBuilder(b => b.UseSetting("Aegis:YtDlpPath", "/nonexistent/yt-dlp"));
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await c.PostAsJsonAsync("/api/login", new { passcode = Passcode })).EnsureSuccessStatusCode();
        (await c.DeleteAsync("/api/cookies")).EnsureSuccessStatusCode();   // make sure no Instagram cookies are saved
        var created = await c.PostAsJsonAsync("/api/jobs", new { url = "https://www.instagram.com/stories/nasa/4015100916210158930/" });
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
        JsonElement view = default;
        for (var i = 0; i < 50; i++)
        {
            view = JsonDocument.Parse(await (await c.GetAsync("/api/jobs/" + id)).Content.ReadAsStringAsync()).RootElement.Clone();
            if (view.GetProperty("state").GetString() == "failed") break;
            await Task.Delay(100);
        }
        Assert.Equal("failed", view.GetProperty("state").GetString());
        Assert.Equal("login_required", view.GetProperty("errorCode").GetString());
        Assert.Contains("Settings", view.GetProperty("error").GetString());
    }
}
