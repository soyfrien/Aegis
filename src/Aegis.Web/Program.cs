using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Aegis.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<AegisOptions>()
    .Bind(builder.Configuration.GetSection(AegisOptions.Section))
    // AEGIS_PASSCODE comes from the environment (systemd EnvironmentFile on the VM), never from a file in the repo.
    .PostConfigure<IConfiguration>((o, c) =>
    {
        o.Passcode = c["AEGIS_PASSCODE"] ?? o.Passcode;
        // Optional: lets an iOS Shortcut call the API headlessly with the X-Aegis-Token header.
        o.ApiToken = c["AEGIS_API_TOKEN"] ?? o.ApiToken;
    });

builder.Services.AddSingleton(sp => new PasscodeService(sp.GetRequiredService<IOptions<AegisOptions>>().Value.Passcode));
builder.Services.AddSingleton<IDownloader, YtDlpDownloader>();
builder.Services.AddSingleton<JobService>();
builder.Services.AddHostedService<CleanupService>();
builder.Services.AddSingleton<OneDriveService>();
builder.Services.AddSingleton<SystemStatsService>();
builder.Services.AddHttpClient();

// Persist data-protection keys so login cookies and the stored OneDrive token survive restarts.
builder.Services.AddDataProtection().SetApplicationName("Aegis");
builder.Services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
    .Configure<IOptions<AegisOptions>>((km, o) =>
    {
        var dir = Path.Combine(o.Value.ResolvedStateDir, "keys");
        Directory.CreateDirectory(dir);
        km.XmlRepository = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(new DirectoryInfo(dir), NullLoggerFactory.Instance);
    });

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "aegis.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenDefaults.Scheme, _ => { });
builder.Services.AddAuthorization(o => o.AddPolicy(ApiTokenDefaults.PolicyName, p => p
    .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, ApiTokenDefaults.Scheme)
    .RequireAuthenticatedUser()));

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Only the local Caddy reverse proxy (loopback, the default trusted proxy) may set these headers.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

builder.Services.AddRateLimiter(_ => { });
builder.Services.AddOptions<RateLimiterOptions>().Configure<IOptions<AegisOptions>>((rl, aegis) =>
{
    var a = aegis.Value;
    rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string Key(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    rl.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(Key(ctx), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = Math.Max(1, a.LoginAttemptsPerMinute),
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    }));
    rl.AddPolicy("api", ctx => RateLimitPartition.GetFixedWindowLimiter(Key(ctx), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = Math.Max(1, a.RequestsPerMinute),
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    }));
});

var app = builder.Build();

{
    var opts = app.Services.GetRequiredService<IOptions<AegisOptions>>().Value;
    if (string.IsNullOrEmpty(opts.Passcode))
        app.Logger.LogWarning("AEGIS_PASSCODE is not set: nobody will be able to log in.");
}

app.UseForwardedHeaders();

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["X-Frame-Options"] = "DENY";
    h["Content-Security-Policy"] = "default-src 'self'; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
    await next();
});

// Share-sheet landing: /share?url=…&text=…&title=… (also /?url=… / ?text=… / ?title=…) -> /?target=<first http(s) link>&auto=1.
// The page itself is static; the login (if needed) happens in the page, which keeps ?target= until it has been used.
// Add auto=0 to only pre-fill the box instead of starting the download.
app.Use(async (ctx, next) =>
{
    var q = ctx.Request.Query;
    var isGet = HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method);
    var isShare = ctx.Request.Path.Equals("/share") || ctx.Request.Path.Equals("/share/");
    var rootWithShareParams = ctx.Request.Path.Equals("/") && (q.ContainsKey("url") || q.ContainsKey("text") || q.ContainsKey("title"));
    if (isGet && (isShare || rootWithShareParams))
    {
        var link = SharedLink.Extract(q["url"].ToString(), q["text"].ToString(), q["title"].ToString());
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Redirect(link is null
            ? "/?shared=none"
            : "/?target=" + Uri.EscapeDataString(link) + (q["auto"].ToString() == "0" ? "" : "&auto=1"));
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = new FileExtensionContentTypeProvider { Mappings = { [".webmanifest"] = "application/manifest+json" } },
    OnPrepareResponse = ctx =>
    {
        // The service worker and manifest must always be revalidated so updates reach phones quickly.
        var name = ctx.File.Name;
        if (name is "sw.js" or "manifest.webmanifest") ctx.Context.Response.Headers.CacheControl = "no-cache";
    },
});
// /stats is a static page; the data it shows comes from the authenticated /api/stats endpoint.
app.MapGet("/stats", () => Results.File(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "stats.html"), "text/html"));
// /shortcut (instructions for the iOS Shortcut) requires login; anonymous visitors are sent to the login page and come back afterwards.
app.MapGet("/shortcut", (HttpContext ctx) => ctx.User.Identity?.IsAuthenticated == true
    ? Results.File(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "shortcut.html"), "text/html")
    : Results.Redirect("/?next=%2Fshortcut"));
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api").RequireRateLimiting("api");

api.MapGet("/session", (HttpContext ctx, OneDriveService od) =>
    Results.Ok(new { authenticated = ctx.User.Identity?.IsAuthenticated == true, oneDrive = od.Status }));

api.MapPost("/login", async (LoginRequest req, HttpContext ctx, PasscodeService passcode) =>
{
    if (!passcode.IsConfigured) return Results.Json(new { error = "The server has no passcode configured." }, statusCode: 503);
    if (!passcode.Verify(req.Passcode)) return Results.Json(new { error = "Wrong passcode." }, statusCode: 401);

    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "aegis")], CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
    return Results.Ok(new { ok = true });
}).RequireRateLimiting("login");

api.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.Ok(new { ok = true });
});

var secured = api.MapGroup("").RequireAuthorization();

// Endpoints a headless client (iOS Shortcut) may use with either the login cookie or the X-Aegis-Token header.
var headless = api.MapGroup("").RequireAuthorization(ApiTokenDefaults.PolicyName);

IResult ServeFile(Job job, JobService jobs)
{
    if (job is not { State: JobState.Done, FilePath: { } path, FileName: { } name } || !File.Exists(path))
        return Results.NotFound(new { error = "File not ready, unknown or expired." });

    if (!new FileExtensionContentTypeProvider().TryGetContentType(name, out var contentType))
        contentType = "application/octet-stream";

    // fileDownloadName makes ASP.NET emit "Content-Disposition: attachment" (with RFC 6266 filename*), so iOS Safari offers Save to Files.
    if (job.FileSize is { } size) jobs.AddBytesServed(size);
    return Results.File(path, contentType, fileDownloadName: name, enableRangeProcessing: true);
}

headless.MapPost("/jobs", (JobRequest req, JobService jobs) =>
{
    if (!UrlValidator.TryValidate(SharedLink.Extract(req.Url) ?? req.Url, out var uri, out var error))
        return Results.BadRequest(new { error });

    var job = jobs.Start(uri!.AbsoluteUri);
    return job is null
        ? Results.Json(new { error = "The server is busy. Try again in a few minutes." }, statusCode: 503)
        : Results.Accepted($"/api/jobs/{job.Id}", job.ToView(DateTimeOffset.UtcNow, jobs.Ttl));
});

headless.MapGet("/jobs/{id}", (string id, JobService jobs) =>
    jobs.Get(id) is { } job ? Results.Ok(job.ToView(DateTimeOffset.UtcNow, jobs.Ttl)) : Results.NotFound(new { error = "Unknown or expired job." }));

secured.MapGet("/stats", (SystemStatsService stats) => Results.Ok(stats.Collect()));

headless.MapGet("/jobs/{id}/file", (string id, JobService jobs) =>
    jobs.Get(id) is { } job ? ServeFile(job, jobs) : Results.NotFound(new { error = "File not ready, unknown or expired." }));

// One call for Shortcuts: POST {"url": "..."} and the response body IS the video file (the server waits for the download to finish).
headless.MapPost("/fetch", async (JobRequest req, JobService jobs, CancellationToken ct) =>
{
    if (!UrlValidator.TryValidate(SharedLink.Extract(req.Url) ?? req.Url, out var uri, out var error))
        return Results.BadRequest(new { error });
    var job = jobs.Start(uri!.AbsoluteUri);
    if (job is null) return Results.Json(new { error = "The server is busy. Try again in a few minutes." }, statusCode: 503);

    await jobs.WaitAsync(job, jobs.JobTimeout + TimeSpan.FromMinutes(1), ct);
    return job.State switch
    {
        JobState.Done => ServeFile(job, jobs),
        JobState.Failed => Results.Json(new { error = job.Error ?? "Download failed." }, statusCode: 502),
        _ => Results.Json(new { error = "The download did not finish in time." }, statusCode: 504),
    };
});

headless.MapDelete("/jobs/{id}", (string id, JobService jobs) => jobs.Remove(id) ? Results.NoContent() : Results.NotFound());


// ---- Optional: Save to OneDrive ----
const string NotSetUp = "OneDrive is not set up on this server (no Entra app registration / Aegis:OneDrive:ClientId configured). See the README.";

secured.MapGet("/onedrive", (OneDriveService od) => Results.Ok(od.Status));

secured.MapPost("/onedrive/connect", async (OneDriveService od, CancellationToken ct) =>
{
    if (!od.IsConfigured) return Results.Json(new { error = NotSetUp }, statusCode: 501);
    try { return Results.Ok(await od.StartDeviceFlowAsync(ct)); }
    catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
});

secured.MapGet("/onedrive/poll", async (OneDriveService od, CancellationToken ct) =>
{
    if (!od.IsConfigured) return Results.Json(new { error = NotSetUp }, statusCode: 501);
    return Results.Ok(new { status = await od.PollDeviceFlowAsync(ct) });
});

secured.MapPost("/onedrive/disconnect", (OneDriveService od) => { od.Disconnect(); return Results.Ok(od.Status); });

secured.MapPost("/jobs/{id}/onedrive", (string id, JobService jobs, OneDriveService od, ILogger<Program> log) =>
{
    if (!od.IsConfigured) return Results.Json(new { error = NotSetUp }, statusCode: 501);
    if (!od.IsConnected) return Results.Json(new { error = "Connect your OneDrive account first." }, statusCode: 409);
    if (jobs.Get(id) is not { } job) return Results.NotFound(new { error = "Unknown or expired job." });
    if (!job.TryBeginUpload()) return Results.Conflict(new { error = "File is not ready or an upload is already running." });

    // Fire and forget: the main download flow never waits on OneDrive.
    _ = Task.Run(async () =>
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var url = await od.UploadAsync(job.FilePath!, job.FileName!, cts.Token);
            job.SetUploadResult(true, "Saved to OneDrive.", url);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "OneDrive upload failed for job {Id}", job.Id);
            job.SetUploadResult(false, ex is InvalidOperationException ? ex.Message : "OneDrive upload failed.");
        }
    });
    return Results.Accepted($"/api/jobs/{id}", job.ToView(DateTimeOffset.UtcNow, jobs.Ttl));
});

app.Run();

public sealed record LoginRequest(string? Passcode);

public sealed record JobRequest(string? Url);

// Exposed for WebApplicationFactory in the test project.
public partial class Program;
