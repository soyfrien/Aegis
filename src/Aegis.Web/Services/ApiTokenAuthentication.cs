using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Aegis.Web.Services;

public static class ApiTokenDefaults
{
    public const string Scheme = "ApiToken";
    public const string HeaderName = "X-Aegis-Token";
    public const string PolicyName = "CookieOrToken";

    /// <summary>Tokens shorter than this are ignored (treated as "not configured") so a typo can't create a guessable credential.</summary>
    public const int MinLength = 24;
}

/// <summary>
/// Optional headless credential for iOS Shortcuts / scripts: send the AEGIS_API_TOKEN value in the X-Aegis-Token header.
/// It is only accepted by the endpoints that opt in with the "CookieOrToken" policy (create/read jobs, fetch a file),
/// never for OneDrive or other management endpoints.
/// </summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AegisOptions> aegis) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = aegis.Value.ApiToken;
        if (!Request.Headers.TryGetValue(ApiTokenDefaults.HeaderName, out var supplied) || string.IsNullOrEmpty(supplied))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (string.IsNullOrEmpty(expected) || expected.Length < ApiTokenDefaults.MinLength)
            return Task.FromResult(AuthenticateResult.Fail("API token access is not enabled."));

        if (!CryptographicOperations.FixedTimeEquals(Hash(supplied.ToString()), Hash(expected)))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API token."));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "aegis-token")], ApiTokenDefaults.Scheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), ApiTokenDefaults.Scheme)));
    }
}
