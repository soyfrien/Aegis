using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Aegis.Web.Services;

public sealed record DeviceCodeInfo(string UserCode, string VerificationUri, int ExpiresIn, int Interval);

public sealed record OneDriveStatus(bool Configured, bool Connected);

/// <summary>
/// Optional "Save to OneDrive": Microsoft identity platform device-code flow plus a Microsoft Graph resumable upload.
/// Needs an Entra public-client app registration (Aegis:OneDrive:ClientId); otherwise <see cref="IsConfigured"/> is false.
/// The refresh token is persisted encrypted with ASP.NET data protection so it survives restarts.
/// </summary>
public sealed class OneDriveService
{
    // Multiple of 320 KiB as required by Graph.
    private const int ChunkSize = 320 * 1024 * 32;
    private const string Scope = "Files.ReadWrite offline_access";

    private readonly OneDriveOptions _options;
    private readonly IHttpClientFactory _http;
    private readonly IDataProtector _protector;
    private readonly string _tokenFile;
    private readonly ILogger<OneDriveService> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset _accessExpiry;
    private string? _refreshToken;
    private string? _pendingDeviceCode;
    private DateTimeOffset _pendingExpiry;

    public OneDriveService(IOptions<AegisOptions> options, IHttpClientFactory http, IDataProtectionProvider dp, ILogger<OneDriveService> logger)
    {
        _options = options.Value.OneDrive;
        _http = http;
        _logger = logger;
        _protector = dp.CreateProtector("Aegis.OneDrive.RefreshToken");
        _tokenFile = Path.Combine(options.Value.ResolvedStateDir, "onedrive.token");
        LoadRefreshToken();
    }

    public bool IsConfigured => _options.IsConfigured;
    public bool IsConnected => _refreshToken is not null;
    public OneDriveStatus Status => new(IsConfigured, IsConnected);

    private string Authority => $"https://login.microsoftonline.com/{Uri.EscapeDataString(_options.TenantId)}/oauth2/v2.0";

    public async Task<DeviceCodeInfo> StartDeviceFlowAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("OneDrive is not configured.");
        using var client = _http.CreateClient();
        using var resp = await client.PostAsync($"{Authority}/devicecode", Form(("client_id", _options.ClientId), ("scope", Scope)), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Device code request failed: {Body}", body);
            throw new InvalidOperationException("Microsoft rejected the sign-in request. Check the app registration (public client flows must be enabled).");
        }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        _pendingDeviceCode = root.GetProperty("device_code").GetString();
        var expiresIn = root.GetProperty("expires_in").GetInt32();
        _pendingExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        return new DeviceCodeInfo(
            root.GetProperty("user_code").GetString()!,
            root.GetProperty("verification_uri").GetString()!,
            expiresIn,
            root.TryGetProperty("interval", out var i) ? i.GetInt32() : 5);
    }

    /// <summary>Returns "pending", "connected" or an error string prefixed with "error:".</summary>
    public async Task<string> PollDeviceFlowAsync(CancellationToken ct)
    {
        if (IsConnected) return "connected";
        if (_pendingDeviceCode is null || DateTimeOffset.UtcNow > _pendingExpiry) return "error:Sign-in expired, start again.";

        using var client = _http.CreateClient();
        using var resp = await client.PostAsync($"{Authority}/token", Form(
            ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
            ("client_id", _options.ClientId),
            ("device_code", _pendingDeviceCode)), ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        if (resp.IsSuccessStatusCode)
        {
            await StoreTokensAsync(root);
            _pendingDeviceCode = null;
            return "connected";
        }

        var error = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
        return error is "authorization_pending" or "slow_down" ? "pending" : $"error:{error}";
    }

    public void Disconnect()
    {
        _refreshToken = null;
        _accessToken = null;
        try { File.Delete(_tokenFile); } catch { /* best effort */ }
    }

    /// <summary>Uploads a file to OneDrive using a resumable upload session; returns the item's web URL.</summary>
    public async Task<string?> UploadAsync(string filePath, string fileName, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct) ?? throw new InvalidOperationException("OneDrive is not connected.");
        using var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(30);

        var folder = string.Join('/', _options.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var target = string.IsNullOrEmpty(folder) ? Uri.EscapeDataString(fileName) : $"{folder}/{Uri.EscapeDataString(fileName)}";
        using var create = new HttpRequestMessage(HttpMethod.Post, $"https://graph.microsoft.com/v1.0/me/drive/root:/{target}:/createUploadSession")
        {
            Content = new StringContent("""{"item":{"@microsoft.graph.conflictBehavior":"rename"}}""", Encoding.UTF8, "application/json"),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var created = await client.SendAsync(create, ct);
        if (!created.IsSuccessStatusCode)
        {
            _logger.LogWarning("createUploadSession failed: {Status} {Body}", created.StatusCode, await created.Content.ReadAsStringAsync(ct));
            throw new InvalidOperationException($"OneDrive refused the upload ({(int)created.StatusCode}).");
        }
        var session = await created.Content.ReadFromJsonAsync<UploadSession>(ct) ?? throw new InvalidOperationException("Bad upload session.");

        await using var fs = File.OpenRead(filePath);
        var total = fs.Length;
        var buffer = new byte[ChunkSize];
        long sent = 0;
        string? webUrl = null;
        while (sent < total)
        {
            var read = await fs.ReadAtLeastAsync(buffer, (int)Math.Min(ChunkSize, total - sent), throwOnEndOfStream: false, ct);
            using var put = new HttpRequestMessage(HttpMethod.Put, session.UploadUrl) { Content = new ByteArrayContent(buffer, 0, read) };
            put.Content.Headers.ContentRange = new ContentRangeHeaderValue(sent, sent + read - 1, total);
            put.Content.Headers.ContentLength = read;
            using var resp = await client.SendAsync(put, ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"OneDrive upload failed ({(int)resp.StatusCode}).");
            sent += read;
            if (sent >= total)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                webUrl = doc.RootElement.TryGetProperty("webUrl", out var w) ? w.GetString() : null;
            }
        }
        return webUrl;
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _accessExpiry.AddMinutes(-2)) return _accessToken;
            if (_refreshToken is null) return null;

            using var client = _http.CreateClient();
            using var resp = await client.PostAsync($"{Authority}/token", Form(
                ("grant_type", "refresh_token"),
                ("client_id", _options.ClientId),
                ("refresh_token", _refreshToken),
                ("scope", Scope)), ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Token refresh failed: {Status}", resp.StatusCode);
                Disconnect();
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            await StoreTokensAsync(doc.RootElement);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private Task StoreTokensAsync(JsonElement root)
    {
        _accessToken = root.GetProperty("access_token").GetString();
        _accessExpiry = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32());
        if (root.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } refresh)
        {
            _refreshToken = refresh;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_tokenFile)!);
                File.WriteAllText(_tokenFile, _protector.Protect(refresh));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not persist OneDrive token");
            }
        }
        return Task.CompletedTask;
    }

    private void LoadRefreshToken()
    {
        try
        {
            if (File.Exists(_tokenFile)) _refreshToken = _protector.Unprotect(File.ReadAllText(_tokenFile));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ignoring unreadable OneDrive token file");
        }
    }

    private static FormUrlEncodedContent Form(params (string, string)[] pairs) =>
        new(pairs.Select(p => new KeyValuePair<string, string>(p.Item1, p.Item2)));

    private sealed record UploadSession([property: JsonPropertyName("uploadUrl")] string UploadUrl);
}
