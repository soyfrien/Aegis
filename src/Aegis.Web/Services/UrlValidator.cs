using System.Net;
using System.Net.Sockets;

namespace Aegis.Web.Services;

/// <summary>Validates user supplied URLs before they are handed to yt-dlp.</summary>
public static class UrlValidator
{
    public const int MaxLength = 2048;

    public static bool TryValidate(string? input, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Please paste a video URL.";
            return false;
        }

        input = UrlNormalizer.AddMissingScheme(input);
        if (input.Length > MaxLength)
        {
            error = "That URL is too long.";
            return false;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            error = "Only http(s) URLs are supported.";
            return false;
        }

        if (IsInternalHost(parsed.Host))
        {
            error = "That address is not allowed.";
            return false;
        }

        // Everything after the URL (query string, fragment) is dropped unless the host needs a key to identify the content.
        uri = UrlNormalizer.Normalize(parsed);
        return true;
    }

    /// <summary>Rejects localhost, single-label hosts and private/loopback/link-local IP literals.</summary>
    public static bool IsInternalHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return true;

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 10 || b[0] == 0 ||
                       (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                       (b[0] == 192 && b[1] == 168) ||
                       (b[0] == 169 && b[1] == 254) ||
                       (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            }

            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        }

        return !host.Contains('.');
    }
}
