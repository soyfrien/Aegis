using System.Text.RegularExpressions;

namespace Aegis.Web.Services;

/// <summary>
/// Removes everything after the URL that does not identify the content: fragments and tracking/query parameters
/// (?igsh=, ?utm_*, ?fbclid=, ?si=, ?s=20, …). Query strings are stripped completely unless a small per-host rule says the
/// link needs a specific key (YouTube <c>watch?v=</c>, Facebook <c>watch/?v=</c>, <c>story.php?story_fbid=&amp;id=</c>, …).
/// </summary>
public static partial class UrlNormalizer
{
    /// <summary>One host family and the query keys it may keep, optionally depending on the path.</summary>
    private sealed record HostRule(string[] Domains, params PathRule[] Paths)
    {
        public bool Matches(string host) =>
            Domains.Any(d => host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                             host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Keys kept for paths that equal <see cref="Path"/> (case-insensitive, trailing slash ignored). An empty <see cref="Path"/> means "any path".</summary>
    private sealed record PathRule(string Path, params string[] Keep);

    private static readonly HostRule[] Rules =
    [
        new(["youtube.com", "youtube-nocookie.com"],
            new PathRule("/watch", "v"),
            new PathRule("/playlist", "list")),
        new(["facebook.com", "fb.com"],
            new PathRule("/watch", "v"),
            new PathRule("/watch/live", "v"),
            new PathRule("/video.php", "v"),
            new PathRule("/story.php", "story_fbid", "id"),
            new PathRule("/permalink.php", "story_fbid", "id"),
            new PathRule("/photo.php", "fbid"),
            new PathRule("/photo", "fbid"),
            new PathRule("/profile.php", "id")),
        // Unlisted Vimeo videos on the player need the privacy hash (?h=…).
        new(["vimeo.com"], new PathRule("", "h")),
        // ?p= selects the part of a multi-part video.
        new(["bilibili.com"], new PathRule("/video", "p")),
    ];

    /// <summary>Hosts that need no rule: listed only so the README and tests can state them (everything is stripped).</summary>
    public static IReadOnlyList<string> StripAllHostsExamples { get; } =
        ["youtu.be", "instagram.com", "x.com", "twitter.com", "tiktok.com", "fb.watch", "dailymotion.com", "dai.ly", "reddit.com", "twitch.tv"];

    /// <summary>Summary of the retained keys, for documentation.</summary>
    public static IEnumerable<(string Host, string Path, string[] Keys)> RetainedKeys() =>
        Rules.SelectMany(r => r.Paths.Select(p => (string.Join(", ", r.Domains), p.Path.Length == 0 ? "(any path)" : p.Path, p.Keep)));

    // Direct file/stream links often carry signed query parameters (token, expires, …) that are required, so on those
    // only well-known tracking parameters are removed instead of the whole query.
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".webm", ".mkv", ".avi", ".flv", ".wmv", ".3gp", ".ts", ".m3u8", ".mpd", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".flac",
    };

    private static readonly HashSet<string> TrackingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "gbraid", "wbraid", "dclid", "msclkid", "yclid", "twclid", "ttclid", "igshid", "igsh", "si",
        "ref_src", "ref_url", "mc_cid", "mc_eid", "_ga", "_gl", "share_id", "is_from_webapp", "sender_device",
    };

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+(:\d{1,5})?([/?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SchemelessRegex();

    /// <summary>"instagram.com/reel/x" (no scheme) -> "https://instagram.com/reel/x". Anything else is returned unchanged.</summary>
    public static string AddMissingScheme(string input)
    {
        var s = input.Trim();
        return !s.Contains("://") && SchemelessRegex().IsMatch(s) ? "https://" + s : s;
    }

    /// <summary>Returns the URL without its fragment and without query parameters that do not identify the content.</summary>
    public static Uri Normalize(Uri uri)
    {
        var host = uri.Host;
        var path = uri.AbsolutePath;
        var query = uri.Query.TrimStart('?');

        string[]? keep = null;
        var hostRule = Rules.FirstOrDefault(r => r.Matches(host));
        if (hostRule is not null)
        {
            var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
            foreach (var p in hostRule.Paths)
            {
                if (p.Path.Length == 0 ||
                    trimmed.Equals(p.Path, StringComparison.OrdinalIgnoreCase) ||
                    (p.Path == "/video" && trimmed.StartsWith("/video/", StringComparison.OrdinalIgnoreCase)))
                {
                    keep = p.Keep;
                    break;
                }
            }
        }

        var kept = new List<string>();
        if (query.Length > 0)
        {
            var isMediaFile = hostRule is null && MediaExtensions.Contains(Path.GetExtension(path));
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                var key = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
                bool retain;
                if (keep is not null) retain = keep.Contains(key, StringComparer.Ordinal);
                else if (isMediaFile) retain = !IsTracking(key);
                else retain = false;
                if (retain && !kept.Any(k => KeyOf(k) == key)) kept.Add(part);
            }
        }

        var b = new UriBuilder(uri) { Query = string.Join('&', kept), Fragment = string.Empty };
        return b.Uri;
    }

    /// <summary>String overload that never throws: text that is not an absolute http(s) URL is returned unchanged.</summary>
    public static string Normalize(string url)
    {
        var candidate = AddMissingScheme(url);
        return Uri.TryCreate(candidate, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? Normalize(u).AbsoluteUri
            : url;
    }

    private static bool IsTracking(string key) =>
        key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || TrackingKeys.Contains(key);

    private static string KeyOf(string part)
    {
        var eq = part.IndexOf('=');
        return Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
    }
}
