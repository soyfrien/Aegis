using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Aegis.Web.Services;

/// <summary>One cookie from a Netscape-format cookies.txt. The value is secret: it is never logged, returned by the API or shown in the UI.</summary>
public sealed record CookieEntry(string Domain, bool IncludeSubdomains, string Path, bool Secure, long Expires, string Name, string Value, bool HttpOnly)
{
    /// <summary>Domain without the leading dot, lower-cased.</summary>
    public string BareDomain => Domain.TrimStart('.').ToLowerInvariant();
}

public sealed class CookieFormatException(string message) : Exception(message);

/// <summary>What the settings page may show about the stored cookies (no names or values).</summary>
public sealed record CookieSite(string Site, string Label, IReadOnlyList<string> Domains, int Count);

public sealed record CookieInfo(bool Configured, string Source, DateTimeOffset? SavedAt, int TotalCookies, IReadOnlyList<CookieSite> Sites, long MaxBytes);

public sealed record CookieSaveResult(int Added, CookieInfo Info);

/// <summary>
/// Stores a combined cookies.txt (Netscape format) on the server so yt-dlp can use the user's own logged-in sessions
/// (Instagram, Facebook, X, YouTube, ...). The file lives in the state directory (outside the web root, mode 600), is rewritten in a
/// normalised form on every save, and is never logged or returned. For each download only the cookies that belong to the target site
/// are copied into a short-lived per-job file for yt-dlp, so e.g. YouTube cookies are never sent anywhere else.
/// </summary>
public sealed class CookieStore
{
    public const int MaxBytes = 1_048_576;
    public const int MaxCookies = 4000;
    private const string Header = "# Netscape HTTP Cookie File";

    // Hosts that share one login: cookies of any member may be used for every member (e.g. fb.watch redirects to facebook.com).
    private static readonly (string Key, string Label, string[] Domains)[] Families =
    [
        ("youtube", "YouTube", ["youtube.com", "youtu.be", "youtube-nocookie.com", "google.com"]),
        ("facebook", "Facebook", ["facebook.com", "fb.watch", "fb.com", "fb.me"]),
        ("x", "X (Twitter)", ["x.com", "twitter.com", "t.co"]),
        ("instagram", "Instagram", ["instagram.com", "instagr.am"]),
    ];

    private static readonly Regex DomainRegex = new(@"^[a-z0-9]([a-z0-9._-]*[a-z0-9])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NameRegex = new(@"^[^\s;=,""\\\x00-\x1f\x7f]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly AegisOptions _options;
    private readonly ILogger<CookieStore> _logger;
    private readonly object _lock = new();

    public CookieStore(IOptions<AegisOptions> options, ILogger<CookieStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Where uploaded cookies are kept: &lt;StateDir&gt;/cookies.txt.</summary>
    public string StorePath => Path.Combine(_options.ResolvedStateDir, "cookies.txt");

    // ---------- pure helpers (unit tested) ----------

    /// <summary>
    /// Parses a Netscape cookies.txt. Comments and blank lines are skipped (a "#HttpOnly_" prefix is understood). Lines may be tab separated
    /// (normal) or separated by runs of spaces (what some phones/editors turn tabs into). Throws <see cref="CookieFormatException"/> with a
    /// message that never contains cookie data.
    /// </summary>
    public static List<CookieEntry> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new CookieFormatException("The cookies text is empty.");
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new CookieFormatException("That is too large (limit 1 MB). A cookies.txt for a few sites is normally well under 100 KB.");

        var result = new List<CookieEntry>();
        var lineNo = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            lineNo++;
            var line = raw.Trim('\uFEFF', ' ', '\t');
            if (line.Length == 0) continue;

            var httpOnly = false;
            if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal)) { httpOnly = true; line = line["#HttpOnly_".Length..]; }
            else if (line[0] == '#') continue;

            if (line.Length > 8192) throw new CookieFormatException($"Line {lineNo} is too long to be a cookie.");
            foreach (var ch in line)
                if (char.IsControl(ch) && ch != '\t') throw new CookieFormatException($"Line {lineNo} contains control characters; this does not look like a cookies.txt.");

            var f = line.Split('\t');
            if (f.Length < 7) f = Regex.Split(line, @"\s+", RegexOptions.None, TimeSpan.FromSeconds(1)).ToArray();
            if (f.Length < 6) throw new CookieFormatException($"Line {lineNo} does not look like a cookie (expected 7 tab-separated fields). Is this a Netscape-format cookies.txt?");
            // 6 fields = cookie with an empty value.
            var value = f.Length >= 7 ? string.Join("\t", f.Skip(6)) : "";

            var domain = f[0].Trim();
            var bare = domain.TrimStart('.').ToLowerInvariant();
            if (!DomainRegex.IsMatch(bare) || !bare.Contains('.')) throw new CookieFormatException($"Line {lineNo}: invalid cookie domain.");
            if (!TryBool(f[1], out var incl) || !TryBool(f[3], out var secure)) throw new CookieFormatException($"Line {lineNo}: expected TRUE/FALSE in the 2nd and 4th fields.");
            if (!f[2].StartsWith('/')) throw new CookieFormatException($"Line {lineNo}: invalid cookie path.");
            if (!long.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expires) || expires < 0)
                throw new CookieFormatException($"Line {lineNo}: invalid expiry in the 5th field.");
            // Chrome exports microseconds-since-1601-style or ms values for some sites; clamp absurd values to "far future".
            if (expires > 32503680000) expires = 32503680000;
            var name = f[5];
            if (!NameRegex.IsMatch(name) || name.Length > 512) throw new CookieFormatException($"Line {lineNo}: invalid cookie name.");
            if (value.Length > 4096) throw new CookieFormatException($"Line {lineNo}: cookie value is too long.");

            result.Add(new CookieEntry(domain.ToLowerInvariant(), incl, f[2], secure, expires, name, value, httpOnly));
            if (result.Count > MaxCookies) throw new CookieFormatException($"Too many cookies (limit {MaxCookies}). Export only the sites you need.");
        }
        if (result.Count == 0) throw new CookieFormatException("No cookies found. Paste the full contents of a Netscape-format cookies.txt (each line has 7 tab-separated fields).");
        return result;
    }

    private static bool TryBool(string s, out bool b)
    {
        b = false;
        if (s.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) { b = true; return true; }
        return s.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The Netscape text for these cookies, always with the header yt-dlp requires.</summary>
    public static string Serialize(IEnumerable<CookieEntry> cookies)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        foreach (var c in cookies)
        {
            if (c.HttpOnly) sb.Append("#HttpOnly_");
            sb.Append(c.Domain).Append('\t').Append(c.IncludeSubdomains ? "TRUE" : "FALSE").Append('\t').Append(c.Path).Append('\t')
              .Append(c.Secure ? "TRUE" : "FALSE").Append('\t').Append(c.Expires.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(c.Name).Append('\t').Append(c.Value).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>True when <paramref name="host"/> is <paramref name="domain"/> or one of its subdomains.</summary>
    public static bool HostMatches(string host, string domain)
    {
        host = host.ToLowerInvariant().TrimEnd('.');
        domain = domain.ToLowerInvariant().TrimStart('.').TrimEnd('.');
        return domain.Length > 0 && (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
    }

    private static (string Key, string Label, string[] Domains)? FamilyOf(string host)
    {
        foreach (var fam in Families)
            if (fam.Domains.Any(d => HostMatches(host, d))) return fam;
        return null;
    }

    /// <summary>True when a cookie belonging to <paramref name="cookieDomain"/> may be sent when downloading from <paramref name="host"/>.</summary>
    public static bool CookieAppliesToHost(string cookieDomain, string host)
    {
        // Same site or a parent domain of the host (cookie domain .instagram.com for www.instagram.com).
        if (HostMatches(host, cookieDomain)) return true;
        // Sites that share one login (fb.watch <-> facebook.com, youtu.be <-> youtube.com, t.co <-> x.com).
        return FamilyOf(host) is { } fam && fam.Domains.Any(d => HostMatches(cookieDomain.TrimStart('.'), d));
    }

    /// <summary>Settings-page grouping: a well-known site key (youtube, facebook, x, instagram) or the registrable-looking domain.</summary>
    public static (string Key, string Label) SiteOf(string bareDomain)
    {
        if (FamilyOf(bareDomain) is { } fam) return (fam.Key, fam.Label);
        var parts = bareDomain.Split('.');
        var take = parts.Length >= 3 && parts[^1].Length == 2 && (parts[^2] is "co" or "com" or "org" or "net" or "gov" or "ac") ? 3 : 2;
        var d = string.Join('.', parts.Skip(Math.Max(0, parts.Length - take)));
        return (d, d);
    }

    /// <summary>Instagram/Facebook stories always need a login, so there is no point asking yt-dlp without cookies.</summary>
    public static bool AlwaysNeedsLogin(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (!path.StartsWith("/stories/", StringComparison.OrdinalIgnoreCase)) return false;
        return HostMatches(uri.Host, "instagram.com") || HostMatches(uri.Host, "instagr.am") || HostMatches(uri.Host, "facebook.com");
    }

    public static string SiteLabelForHost(string host) => FamilyOf(host)?.Label ?? host;

    // ---------- storage ----------

    private (List<CookieEntry> Cookies, string Source, DateTimeOffset? SavedAt) Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return (Parse(File.ReadAllText(StorePath)), "uploaded", File.GetLastWriteTimeUtc(StorePath));
            if (!string.IsNullOrWhiteSpace(_options.CookiesFile) && File.Exists(_options.CookiesFile))
                return (Parse(File.ReadAllText(_options.CookiesFile)), "server-config", File.GetLastWriteTimeUtc(_options.CookiesFile));
        }
        catch (Exception ex) when (ex is CookieFormatException or IOException or UnauthorizedAccessException)
        {
            // Never include file contents here.
            _logger.LogWarning("The stored cookies file could not be read ({Kind}); ignoring it.", ex.GetType().Name);
        }
        return ([], "none", null);
    }

    public CookieInfo GetInfo()
    {
        lock (_lock) return BuildInfo(Load());
    }

    private static CookieInfo BuildInfo((List<CookieEntry> Cookies, string Source, DateTimeOffset? SavedAt) s)
    {
        var sites = s.Cookies
            .GroupBy(c => SiteOf(c.BareDomain))
            .Select(g => new CookieSite(g.Key.Key, g.Key.Label, g.Select(c => c.BareDomain).Distinct().Order().ToList(), g.Count()))
            .OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new CookieInfo(s.Cookies.Count > 0, s.Source, s.Cookies.Count > 0 ? s.SavedAt : null, s.Cookies.Count, sites, MaxBytes);
    }

    /// <summary>
    /// Saves cookies. By default the new cookies replace existing ones for the sites they contain and other sites are kept,
    /// so you can add Instagram, then X, one export at a time. <paramref name="replaceAll"/> discards everything else.
    /// </summary>
    public CookieSaveResult Save(string? text, bool replaceAll)
    {
        var incoming = Parse(text);
        lock (_lock)
        {
            var current = replaceAll ? [] : Load().Cookies;
            var replacedSites = incoming.Select(c => SiteOf(c.BareDomain).Key).ToHashSet();
            var merged = current.Where(c => !replacedSites.Contains(SiteOf(c.BareDomain).Key)).Concat(incoming)
                .GroupBy(c => (c.BareDomain, c.Path, c.Name), c => c).Select(g => g.Last()).ToList();
            if (merged.Count > MaxCookies) throw new CookieFormatException($"Too many cookies stored (limit {MaxCookies}).");
            WriteStore(merged);
            return new CookieSaveResult(incoming.Count, BuildInfo((merged, "uploaded", DateTimeOffset.UtcNow)));
        }
    }

    /// <summary>Deletes all cookies (<paramref name="site"/> null) or just one site's. A cookie file set through configuration cannot be deleted here.</summary>
    public CookieInfo Delete(string? site)
    {
        lock (_lock)
        {
            var (cookies, _, _) = Load();
            if (string.IsNullOrWhiteSpace(site))
            {
                if (File.Exists(StorePath)) File.Delete(StorePath);
            }
            else
            {
                var kept = cookies.Where(c => !SiteOf(c.BareDomain).Key.Equals(site, StringComparison.OrdinalIgnoreCase)).ToList();
                if (kept.Count == 0) { if (File.Exists(StorePath)) File.Delete(StorePath); }
                else WriteStore(kept);
            }
            return BuildInfo(Load());
        }
    }

    private void WriteStore(List<CookieEntry> cookies)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        WritePrivate(StorePath, Serialize(cookies));
    }

    private static void WritePrivate(string path, string content)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Create with 600 from the start (no window where it is world-readable).
            var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var fs = new FileStream(tmp, opts))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false))) w.Write(content);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>True when any stored cookie may be sent to <paramref name="host"/>.</summary>
    public bool HasCookiesFor(string host)
    {
        lock (_lock) return Load().Cookies.Any(c => CookieAppliesToHost(c.Domain, host));
    }

    /// <summary>
    /// Writes a private (mode 600) cookies file for yt-dlp that contains only the cookies that apply to <paramref name="host"/>.
    /// Returns null when there are none. The caller deletes the file when the download ends.
    /// </summary>
    public string? CreateJobCookieFile(string host, string directoryForFile)
    {
        List<CookieEntry> relevant;
        lock (_lock) relevant = Load().Cookies.Where(c => CookieAppliesToHost(c.Domain, host)).ToList();
        if (relevant.Count == 0) return null;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directoryForFile);
        else Directory.CreateDirectory(directoryForFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directoryForFile, "c-" + Guid.NewGuid().ToString("N") + ".jar");
        WritePrivate(path, Serialize(relevant));
        return path;
    }
}
