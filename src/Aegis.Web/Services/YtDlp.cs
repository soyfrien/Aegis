using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Aegis.Web.Services;

public interface IDownloader
{
    /// <summary>Downloads <paramref name="url"/> into <paramref name="directory"/> and returns the final file path.</summary>
    Task<string> DownloadAsync(string url, string directory, Action<double, string?> progress, CancellationToken ct);
}

/// <summary>A download failure with a message that is safe to show to the user. <see cref="Code"/> lets the UI offer the right help.</summary>
public sealed class DownloadException(string message, string? code = null) : Exception(message)
{
    /// <summary>Machine readable reason: "login_required", "rate_limited", "unsupported", or null.</summary>
    public string? Code { get; } = code;
}

/// <summary>Runs yt-dlp as a child process (no shell, arguments passed as a list).</summary>
public sealed partial class YtDlpDownloader(IOptions<AegisOptions> options, CookieStore cookies, ILogger<YtDlpDownloader> logger) : IDownloader
{
    private static readonly string[] IgnoredExtensions = [".part", ".ytdl", ".json", ".tmp", ".temp", ".txt", ".vtt", ".jpg", ".png", ".webp"];

    [GeneratedRegex(@"\[download\]\s+(?<pct>\d+(?:\.\d+)?)%")]
    private static partial Regex ProgressRegex();

    /// <summary>Extracts the percentage from a yt-dlp "[download]  42.3% of ..." line.</summary>
    public static bool TryParseProgress(string? line, out double percent)
    {
        percent = 0;
        if (string.IsNullOrEmpty(line)) return false;
        var m = ProgressRegex().Match(line);
        return m.Success && double.TryParse(m.Groups["pct"].Value, System.Globalization.CultureInfo.InvariantCulture, out percent);
    }

    /// <summary>Builds the yt-dlp argument list. Public so the exact (security relevant) arguments can be unit tested.</summary>
    /// <param name="cookiesFile">A cookies.txt that already contains only the cookies for this site (see <see cref="CookieStore.CreateJobCookieFile"/>), or null.</param>
    public static List<string> BuildArguments(AegisOptions o, string url, string directory, string? cookiesFile = null)
    {
        var args = new List<string>
        {
            "--no-playlist",
            "--newline",
            "--no-warnings",
            "--no-mtime",
            "--no-part",
            "--ignore-config",
            "--socket-timeout", "30",
            "--max-filesize", o.MaxFileSize,
            // Prefer H.264/AAC in MP4 so the result plays natively on iOS.
            "-S", $"res:{o.MaxHeight},vcodec:h264,acodec:aac,ext:mp4:m4a",
            "--merge-output-format", "mp4",
            "--windows-filenames",
            "--trim-filenames", "120",
            "-P", directory,
            "-o", "%(title).80B [%(id)s].%(ext)s",
        };
        if (!string.IsNullOrWhiteSpace(o.FfmpegPath))
        {
            args.Add("--ffmpeg-location");
            args.Add(o.FfmpegPath);
        }
        if (!string.IsNullOrWhiteSpace(cookiesFile))
        {
            args.Add("--cookies");
            args.Add(cookiesFile);
        }
        args.Add("--");
        args.Add(url);
        return args;
    }

    public async Task<string> DownloadAsync(string url, string directory, Action<double, string?> progress, CancellationToken ct)
    {
        var o = options.Value;
        Directory.CreateDirectory(directory);

        var host = Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) ? parsedUri.Host : "";
        // Only this site's cookies go to yt-dlp, in a private per-job file that is deleted afterwards (yt-dlp also rewrites its cookie file).
        var cookieFile = host.Length > 0 ? cookies.CreateJobCookieFile(host, Path.Combine(o.ResolvedWorkDir, ".cookies")) : null;
        try
        {
            if (cookieFile is null && parsedUri is not null && CookieStore.AlwaysNeedsLogin(parsedUri))
                throw new DownloadException(Summarize("login required", host, cookiesUsed: false), "login_required");
            return await RunAsync(o, url, directory, host, cookieFile, progress, ct);
        }
        finally
        {
            if (cookieFile is not null) try { File.Delete(cookieFile); } catch { /* best effort */ }
        }
    }

    private async Task<string> RunAsync(AegisOptions o, string url, string directory, string host, string? cookieFile, Action<double, string?> progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(o.YtDlpPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in BuildArguments(o, url, directory, cookieFile)) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var errors = new Queue<string>();
        var stage = "Downloading";

        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (TryParseProgress(line, out var pct)) progress(pct, stage);
            else if (line.StartsWith("[Merger]") || line.StartsWith("[VideoRemuxer]")) { stage = "Merging audio and video"; progress(99, stage); }
            else if (line.StartsWith("[download] Destination")) { stage = "Downloading"; }
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (errors)
            {
                errors.Enqueue(e.Data);
                while (errors.Count > 8) errors.Dequeue();
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not start yt-dlp at {Path}", o.YtDlpPath);
            throw new DownloadException("The downloader is not available on the server.");
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        if (process.ExitCode != 0)
        {
            string detail;
            lock (errors) detail = string.Join(" ", errors);
            logger.LogWarning("yt-dlp exited with {Code}: {Detail}", process.ExitCode, detail);
            var code = Classify(detail);
            throw new DownloadException(Summarize(detail, host, cookieFile is not null), code);
        }

        var file = Directory.EnumerateFiles(directory)
            .Where(f => !IgnoredExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();

        return file?.FullName ?? throw new DownloadException("The download finished but no file was produced.");
    }

    private static readonly string[] LoginMarkers =
    [
        "sign in to confirm", "login required", "log in", "logged in", "log-in", "sign in", "this content is unreachable", "--cookies",
        "empty media response", "use cookies", "cookies for the authentication", "private video", "private account", "members-only",
        "authenticat", "available for registered users", "cannot parse data", "requires login", "nsfw", "age-restricted", "confirm your age",
        "rate-limit reached",
    ];

    /// <summary>"login_required" / "rate_limited" / "unsupported" / null for the tail of yt-dlp's stderr.</summary>
    public static string? Classify(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return null;
        if (stderr.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)) return "unsupported";
        if (LoginMarkers.Any(m => stderr.Contains(m, StringComparison.OrdinalIgnoreCase))) return "login_required";
        if (stderr.Contains("429", StringComparison.Ordinal) || stderr.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("rate-limit", StringComparison.OrdinalIgnoreCase) || stderr.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) return "rate_limited";
        return null;
    }

    /// <summary>Turns yt-dlp's stderr tail into a short, user facing message (never contains cookie data).</summary>
    public static string Summarize(string stderr, string? host = null, bool cookiesUsed = false)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "Download failed.";
        var site = string.IsNullOrEmpty(host) ? "This site" : CookieStore.SiteLabelForHost(host);
        switch (Classify(stderr))
        {
            case "unsupported":
                return "That URL is not supported.";
            case "login_required" when stderr.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase):
                return cookiesUsed
                    ? $"{site} is still asking the server to prove it is not a bot, even with your saved cookies. They may have expired: export them again in Settings."
                    : $"{site} is asking the server to sign in / prove it is not a bot (common for YouTube from datacenter IPs). Saving your cookies in Settings can help, but YouTube may flag the account.";
            case "login_required":
                return cookiesUsed
                    ? $"{site} refused even with your saved cookies. They may have expired, belong to a different account, or the post may be private. Export fresh cookies in Settings and try again."
                    : $"{site} needs a logged-in session for this link (it may be a story, a private or age-restricted post, or the site is blocking the server). Add your {site} cookies in Settings, then try again.";
            case "rate_limited":
                return $"{site} is rate-limiting the server right now. Wait a few minutes and try again; saving cookies in Settings can help.";
        }
        if (stderr.Contains("larger than max-filesize", StringComparison.OrdinalIgnoreCase)) return "The video is larger than the server's size limit.";
        var msg = stderr.Replace("ERROR: ", "", StringComparison.Ordinal).Trim();
        return msg.Length > 300 ? msg[..300] + "…" : msg;
    }
}
